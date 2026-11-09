using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Playwright;
using TestGen.Cli.Web;
using TestGen.Core.Run;
using TestGen.Excel;
using TestGen.Tests;

namespace TestGen.Browser.Tests;

/// <summary>Webbgränssnittet och HTML-rapporten i en riktig webbläsare: tillgänglighet (WCAG 2.1 AA, axe) och att båda lagren syns.</summary>
public class WebPageTests
{
    private string _root = "";
    private WebServer _server = null!;
    private HttpClient _http = null!;
    private string _runId = "";

    [SetUp]
    public async Task SetUp()
    {
        ReportWriter.Register(new ExcelReportFormat());   // som testgen gör vid start; körningar i sidan bygger också Excel-rapporten
        _root = Directory.CreateTempSubdirectory("testgen-webpage-").FullName;
        _server = await WebServer.StartAsync(new WebOptions { Port = 0, Solution = ResultFixture.Create(_root), HandlerFactory = () => new FakeApi() });
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_server.Port}") };
        _http.DefaultRequestHeaders.Add(WebServer.KeyHeader, _server.SessionKey);
    }

    [TearDown]
    public async Task TearDown()
    {
        _http.Dispose();
        await _server.DisposeAsync();
        Directory.Delete(_root, true);
    }

    /// <summary>Svarar 201 på allt utom GET, som svarar 200: några tester får FEL och några OK.</summary>
    private sealed class FakeApi : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(req.Method == HttpMethod.Get ? HttpStatusCode.OK : HttpStatusCode.Created)
            {
                Content = new StringContent("{\"id\":1}", Encoding.UTF8, "application/json"),
            });
    }

    private static string SpecPath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var file = Path.Combine(directory.FullName, "samples", "shop.openapi.yaml");
            if (File.Exists(file)) return file;
        }
        throw new FileNotFoundException("samples/shop.openapi.yaml");
    }

    private async Task RunInThePageAsync()
    {
        var start = await _http.PostAsJsonAsync("/api/runs", new { spec = SpecPath(), api = "http://fake.test", only = "createOrder", seed = "1" });
        Assert.That(start.StatusCode, Is.EqualTo(HttpStatusCode.Accepted), await start.Content.ReadAsStringAsync());
        _runId = JsonNode.Parse(await start.Content.ReadAsStringAsync())!["id"]!.GetValue<string>();
        var events = await _http.GetStringAsync($"/api/runs/{_runId}/events");   // strömmen slutar när körningen är klar
        Assert.That(events, Does.Contain("event: done"), events);
    }

    private async Task<BrowserSession> OpenAsync(string path)
    {
        var session = await BrowserFixture.Host.NewSessionAsync(new SessionOptions($"http://127.0.0.1:{_server.Port}"));
        await session.OpenAsync(path);
        await session.Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        return session;
    }

    private static async Task AssertAccessibleAsync(BrowserSession session)
    {
        var violations = await Accessibility.CheckAsync(session.Page);
        Assert.That(violations.Select(v => v.Describe()), Is.Empty);
        Assert.That(session.Errors.All.Select(e => e.Message), Is.Empty, "inga fel i webbläsarens konsol");
    }

    [Test]
    public async Task Start_page_has_no_accessibility_violations()
    {
        await using var session = await OpenAsync("/");
        await AssertAccessibleAsync(session);
    }

    [Test]
    public async Task A_finished_run_has_no_accessibility_violations()
    {
        await RunInThePageAsync();
        await using var session = await OpenAsync("/");
        await session.Page.WaitForSelectorAsync("#summary:not([hidden])");
        await AssertAccessibleAsync(session);
    }

    [Test]
    public async Task The_results_list_shows_both_layers_and_each_result_opens_without_accessibility_violations()
    {
        await RunInThePageAsync();
        await using var session = await OpenAsync("/#results");
        var rows = session.Page.Locator("#res-body tr");
        await Assertions.Expect(rows).ToHaveCountAsync(4);
        var text = await session.Page.Locator("#res-body").InnerTextAsync();
        Assert.That(text, Does.Contain("API").And.Contain("UI").And.Contain("Shop.Tests").And.Contain("Shop.UiTests"));
        await AssertAccessibleAsync(session);

        // En resultatfil från dotnet test (UI-lagret) och körningen som gjordes i sidan öppnas på samma sätt.
        var formBefore = await session.Page.Locator("#form").EvaluateAsync<string>("f => JSON.stringify(Array.from(f.elements).map(e => e.value))");
        await rows.Filter(new() { HasText = "Shop.UiTests" }).GetByRole(AriaRole.Button, new() { Name = "Öppna" }).ClickAsync();
        await session.Page.WaitForSelectorAsync("#results-slot #summary:not([hidden])");
        Assert.That(await session.Page.Locator("#detail-title").InnerTextAsync(), Does.Contain("UI").And.Contain("Shop.UiTests"));
        Assert.That(await session.Page.Locator("#form").EvaluateAsync<string>("f => JSON.stringify(Array.from(f.elements).map(e => e.value))"), Is.EqualTo(formBefore), "körformuläret påverkas inte");
        await Assertions.Expect(session.Page.Locator("#groups")).ToContainTextAsync("U-001");
        await AssertAccessibleAsync(session);

        // Sammanfattningen från förra resultatet syns redan; vänta på innehållet i det nya i stället för att den visas.
        await session.Page.Locator("#tab-results").ClickAsync();
        await rows.Filter(new() { HasText = "Shop.Tests" }).First.GetByRole(AriaRole.Button, new() { Name = "Öppna" }).ClickAsync();
        await Assertions.Expect(session.Page.Locator("#groups")).ToContainTextAsync("T-001");
        await AssertAccessibleAsync(session);

        await session.Page.Locator("#tab-results").ClickAsync();
        await session.Page.Locator("#cmp-go").ClickAsync();
        await session.Page.WaitForSelectorAsync("#cmp-out:not([hidden])");
        await AssertAccessibleAsync(session);
    }

    [Test]
    public async Task The_html_report_has_no_accessibility_violations()
    {
        await RunInThePageAsync();
        var html = await _http.GetStringAsync($"/api/runs/{_runId}/report?type=html");
        await using var site = await TestApp.StartAsync(app => TestApp.Page(app, "/report", html));
        await using var session = await BrowserFixture.Host.NewSessionAsync(new SessionOptions(site.Url));
        await session.OpenAsync("/report");
        await AssertAccessibleAsync(session);

        // Och med knappen "Visa bara FEL" intryckt.
        await session.Page.Locator("#only-failed").ClickAsync();
        await AssertAccessibleAsync(session);
    }
}
