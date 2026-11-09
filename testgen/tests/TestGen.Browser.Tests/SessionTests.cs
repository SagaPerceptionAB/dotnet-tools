using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace TestGen.Browser.Tests;

public class SessionTests
{
    private const string ErrorsPage = """
        <!doctype html><html lang="sv"><head><title>Fel</title><link rel="icon" href="/favicon.ico"></head>
        <body><h1>Fel</h1><img src="/nope.png" alt="">
        <script>
          console.log('hej');
          console.error('boom');
          setTimeout(() => { throw new Error('oops'); }, 0);
          fetch('/missing');
          fetch('/broken');
        </script></body></html>
        """;

    private static Task<TestApp> StartAsync() => TestApp.StartAsync(app =>
    {
        TestApp.Page(app, "/errors", ErrorsPage);
        TestApp.Page(app, "/quiet", "<!doctype html><title>Tyst</title><h1>Tyst</h1><img src=\"/nope.png\" alt=\"\"><link rel=\"icon\" href=\"/favicon.ico\">");
        app.MapGet("/broken", () => Results.StatusCode(500));
    });

    [Test]
    public async Task Collects_console_errors_page_errors_and_failed_responses_in_swedish()
    {
        await using var site = await StartAsync();
        await using var session = await BrowserFixture.Host.NewSessionAsync(new SessionOptions(site.Url));

        await session.OpenAsync("/errors");

        Assert.That(() => session.Errors.All.Select(e => e.Kind).Distinct().Count(), Is.EqualTo(3).After(5000, 25));
        var messages = session.Errors.All.Select(e => e.Message).ToList();
        Assert.That(messages, Has.Some.StartsWith("Konsolfel: boom"));
        Assert.That(messages, Has.Some.StartsWith("Sidfel (oupptäckt undantag):").And.Contain("oops"));
        Assert.That(messages, Has.Some.StartsWith("Svar 404 på GET ").And.EndsWith("/missing"));
        Assert.That(messages, Has.Some.StartsWith("Svar 500 på GET ").And.EndsWith("/broken"));
        Assert.That(session.Errors.Describe(), Does.Contain("boom"));
    }

    [Test]
    public async Task Missing_favicon_and_missing_images_are_not_errors()
    {
        await using var site = await StartAsync();
        await using var session = await BrowserFixture.Host.NewSessionAsync(new SessionOptions(site.Url));

        await session.OpenAsync("/quiet");
        await session.Page.WaitForLoadStateAsync(Microsoft.Playwright.LoadState.NetworkIdle);

        Assert.That(session.Errors.All, Is.Empty);
    }

    [Test]
    public async Task Extra_ignored_requests_are_not_counted()
    {
        await using var site = await StartAsync();
        await using var session = await BrowserFixture.Host.NewSessionAsync(new SessionOptions(site.Url, IsIgnored: r => r.Url.EndsWith("/broken", StringComparison.Ordinal)));

        await session.OpenAsync("/errors");

        Assert.That(() => session.Errors.All.Count(e => e.Kind == PageErrorKind.Response), Is.EqualTo(1).After(5000, 25));
        Assert.That(session.Errors.All.Select(e => e.Message), Has.None.EndsWith("/broken"));
    }

    [Test]
    public async Task Headers_are_sent_to_the_app_and_only_to_the_app()
    {
        var seenByOther = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var other = await TestApp.StartAsync(app => app.MapGet("/seen", (HttpContext http) =>
        {
            seenByOther.Enqueue(http.Request.Headers.Authorization.ToString());
            return Results.Text("");
        }));
        await using var site = await TestApp.StartAsync(app =>
        {
            app.MapGet("/seen", (HttpContext http) => Results.Text(http.Request.Headers.Authorization.ToString()));
            TestApp.Page(app, "/page", $"<!doctype html><title>x</title><script>fetch('/seen').then(r => r.text()).then(t => document.title = 'egen:' + t);" +
                $"fetch('{other.Url}/seen', {{ mode: 'no-cors' }});</script>");
        });
        var headers = new Dictionary<string, string> { ["Authorization"] = "Bearer hemlig-token-1234" };
        await using var session = await BrowserFixture.Host.NewSessionAsync(new SessionOptions(site.Url, Headers: headers));

        await session.OpenAsync("/page");

        Assert.That(async () => await session.Page.TitleAsync(), Is.EqualTo("egen:Bearer hemlig-token-1234").After(5000, 25));
        Assert.That(() => seenByOther.ToArray(), Is.EqualTo(new[] { "" }).After(5000, 25), "ett annat ursprung får inget sidhuvud");
    }

    [Test]
    public async Task Clear_empties_the_list()
    {
        await using var site = await StartAsync();
        await using var session = await BrowserFixture.Host.NewSessionAsync(new SessionOptions(site.Url));

        await session.OpenAsync("/errors");
        Assert.That(() => session.Errors.All, Is.Not.Empty.After(5000, 25));
        session.Errors.Clear();

        Assert.That(session.Errors.All, Is.Empty);
    }

    [Test]
    public async Task Attachments_are_available_on_request()
    {
        await using var site = await StartAsync();
        await using var session = await BrowserFixture.Host.NewSessionAsync(new SessionOptions(site.Url));

        await session.OpenAsync("/quiet");

        Assert.That(await session.HtmlAsync(), Does.Contain("<h1>Tyst</h1>"));
        Assert.That((await session.ScreenshotAsync()).Take(4), Is.EqualTo(new byte[] { 137, 80, 78, 71 }));
    }

    [Test]
    public async Task Console_log_keeps_every_line()
    {
        await using var site = await StartAsync();
        await using var session = await BrowserFixture.Host.NewSessionAsync(new SessionOptions(site.Url));

        await session.OpenAsync("/errors");

        Assert.That(() => session.ConsoleLog, Does.Contain("[log] hej").After(5000, 25));
    }

    [Test]
    public async Task Trace_is_only_saved_when_it_was_turned_on()
    {
        await using var site = await StartAsync();
        var path = Path.Combine(Path.GetTempPath(), "testgen-trace-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            await using (var off = await BrowserFixture.Host.NewSessionAsync(new SessionOptions(site.Url)))
            {
                await off.OpenAsync("/quiet");
                Assert.That(await off.SaveTraceAsync(path), Is.False);
                Assert.That(File.Exists(path), Is.False);
            }
            await using (var on = await BrowserFixture.Host.NewSessionAsync(new SessionOptions(site.Url, Trace: true)))
            {
                await on.OpenAsync("/quiet");
                Assert.That(await on.SaveTraceAsync(path), Is.True);
                Assert.That(new FileInfo(path).Length, Is.GreaterThan(0));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Missing_chromium_gives_a_clear_error_with_the_install_command()
    {
        var empty = Path.Combine(Path.GetTempPath(), "testgen-no-browsers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        var before = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH");
        Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", empty);
        try
        {
            var error = await Assert.ThrowsAsync<ChromiumMissingException>(async () => await BrowserHost.StartAsync());
            Assert.That(error!.Message, Does.Contain("Chromium är inte installerat").And.Contain("playwright.ps1 install chromium"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", before);
            Directory.Delete(empty, recursive: true);
        }
        await Task.CompletedTask;
    }
}
