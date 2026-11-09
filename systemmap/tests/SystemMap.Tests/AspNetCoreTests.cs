using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SystemMap.AspNetCore;

namespace SystemMap.Tests;

/// <summary>Systemkartan som en sida i en ASP.NET Core-app (src/SystemMap.AspNetCore).</summary>
[NonParallelizable]
public class AspNetCoreTests
{
    private string _dir = "";
    private readonly List<WebApplication> _apps = [];

    [SetUp] public void Up() => _dir = Directory.CreateTempSubdirectory("systemmap-web-").FullName;

    [TearDown]
    public async Task Down()
    {
        foreach (var app in _apps) await app.DisposeAsync();
        _apps.Clear();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static string Page => CodeMapHtml.Write(new CodeMapModel("Test.sln", [], [], [], [], [], []));

    private void WriteBuilt()
    {
        File.WriteAllText(Path.Combine(_dir, "systemmap.html"), Page);
        File.WriteAllText(Path.Combine(_dir, "map.json"), """{"schemaVersion":2}""");
    }

    private void WriteDevJson(string tool, string? solution, string projectDir, string dotnet) =>
        File.WriteAllText(Path.Combine(_dir, "dev.json"), JsonSerializer.Serialize(new { dotnet, tool, projectDir, solution, spec = (string?)null }));

    private async Task<HttpClient> StartAsync(string environment, Action<IEndpointConventionBuilder>? convention = null,
        Action<SystemMapOptions>? configure = null, string pattern = "/systemmap", ILoggerProvider? logger = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        if (logger is not null) builder.Logging.ClearProviders().AddProvider(logger);
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, NoUserHandler>("Test", null);
        builder.Services.AddAuthorization();
        var app = builder.Build();
        _apps.Add(app);
        var group = app.MapSystemMap(pattern, o => { o.Directory = _dir; configure?.Invoke(o); });
        convention?.Invoke(group);
        await app.StartAsync();
        return app.GetTestClient();
    }

    /// <summary>Ett inloggningsschema där ingen är inloggad: skyddade adresser ger 401.</summary>
    private sealed class NoUserHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
    }

    private sealed class ListLogger : ILoggerProvider, ILogger
    {
        public List<string> Warnings { get; } = [];
        public ILogger CreateLogger(string categoryName) => categoryName == "SystemMap" ? this : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) lock (Warnings) Warnings.Add(formatter(state, exception));
        }
    }

    private static async Task<string> StatusAsync(HttpClient client, string path = "/systemmap/status") =>
        JsonDocument.Parse(await client.GetStringAsync(path)).RootElement.GetProperty("state").GetString()!;

    private static async Task<string> WaitWhileRunningAsync(HttpClient client, TimeSpan limit)
    {
        var end = DateTime.UtcNow + limit;
        while (true)
        {
            var state = await StatusAsync(client);
            if (state != "running" || DateTime.UtcNow > end) return state;
            await Task.Delay(500);
        }
    }

    // --- Släppbygget ------------------------------------------------------------------------------

    [Test]
    public async Task Development_serves_the_built_page_with_security_headers()
    {
        WriteBuilt();
        var client = await StartAsync("Development");

        var response = await client.GetAsync("/systemmap");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType!.ToString(), Is.EqualTo("text/html; charset=utf-8"));
        Assert.That(response.Headers.GetValues("Content-Security-Policy").Single(), Does.StartWith("default-src 'none'; script-src 'unsafe-inline';").And.Not.Contain("connect-src"));
        Assert.That(response.Headers.GetValues("X-Content-Type-Options").Single(), Is.EqualTo("nosniff"));
        Assert.That(response.Headers.GetValues("Referrer-Policy").Single(), Is.EqualTo("no-referrer"));
        Assert.That(response.Headers.CacheControl!.NoCache, Is.True);
        Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo(Page));
    }

    [Test]
    public async Task Map_json_is_served_with_the_json_content_type()
    {
        WriteBuilt();
        var client = await StartAsync("Development");

        var response = await client.GetAsync("/systemmap/map.json");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType!.ToString(), Is.EqualTo("application/json; charset=utf-8"));
        Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("""{"schemaVersion":2}"""));
    }

    [Test]
    public async Task Custom_pattern_and_trailing_slash()
    {
        WriteBuilt();
        var client = await StartAsync("Development", pattern: "/docs/karta");

        foreach (var path in new[] { "/docs/karta", "/docs/karta/" })
            Assert.That((await client.GetAsync(path)).StatusCode, Is.EqualTo(HttpStatusCode.OK), path);
        Assert.That((await client.GetAsync("/docs/karta/map.json")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await client.GetAsync("/systemmap")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Development_without_files_shows_instructions()
    {
        var client = await StartAsync("Development");

        var response = await client.GetAsync("/systemmap");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("GenerateSystemMap").And.Contain("SagaPerception.SystemMap.AspNetCore"));
        Assert.That((await client.GetAsync("/systemmap/map.json")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await client.GetAsync("/systemmap/status")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    // --- Säker standard ---------------------------------------------------------------------------

    [Test]
    public async Task Production_without_authorization_gives_404_and_one_warning()
    {
        WriteBuilt();
        var logger = new ListLogger();
        var client = await StartAsync("Production", logger: logger);

        foreach (var path in new[] { "/systemmap", "/systemmap/", "/systemmap/map.json", "/systemmap/status" })
            Assert.That((await client.GetAsync(path)).StatusCode, Is.EqualTo(HttpStatusCode.NotFound), path);
        Assert.That(logger.Warnings, Has.Count.EqualTo(1));
        Assert.That(logger.Warnings[0], Does.Contain("Development").And.Contain("RequireAuthorization").And.Contain("AllowWithoutAuthorization"));
    }

    [Test]
    public async Task Production_with_required_authorization_gives_401_to_anonymous()
    {
        WriteBuilt();
        var client = await StartAsync("Production", g => ((RouteGroupBuilder)g).RequireAuthorization());

        Assert.That((await client.GetAsync("/systemmap")).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That((await client.GetAsync("/systemmap/map.json")).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Production_with_AllowWithoutAuthorization_serves_the_page()
    {
        WriteBuilt();
        var client = await StartAsync("Production", configure: o => o.AllowWithoutAuthorization = true);

        Assert.That((await client.GetAsync("/systemmap")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await client.GetAsync("/systemmap/map.json")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Production_with_authorization_but_without_a_built_page_gives_404()
    {
        var client = await StartAsync("Production", configure: o => o.AllowWithoutAuthorization = true);
        Assert.That((await client.GetAsync("/systemmap")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    // --- Utvecklingsbygget ------------------------------------------------------------------------

    /// <summary>
    /// Verktyget där det byggdes. Kopian i testernas mapp saknar Microsoft.Extensions.Logging.Abstractions, som testprojektet
    /// får från ASP.NET Core:s ramverk, och verktyget (en vanlig konsolapp) behöver filen bredvid sig.
    /// </summary>
    private static string ToolInOwnFolder { get; } = Path.Combine(
        AppContext.BaseDirectory.Replace(Path.Combine("tests", "SystemMap.Tests"), Path.Combine("src", "SystemMap.Cli")), "SystemMap.Cli.dll");

    private static string? DotnetHost => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");

    [Test]
    public async Task Failed_run_shows_the_error_and_only_runs_again_on_retry()
    {
        if (DotnetHost is null) Assert.Ignore("DOTNET_HOST_PATH saknas (sätts av dotnet test).");
        WriteDevJson(typeof(Cli.Program).Assembly.Location, Path.Combine(_dir, "finns-inte.slnx"), _dir, DotnetHost!);
        var client = await StartAsync("Development");

        var waiting = await client.GetAsync("/systemmap");
        Assert.That(waiting.Headers.GetValues("Content-Security-Policy").Single(), Does.Contain("connect-src 'self'"));
        Assert.That(await waiting.Content.ReadAsStringAsync(), Does.Contain("Kartan ritas").And.Contain("upp till en minut"));
        Assert.That(await StatusAsync(client), Is.EqualTo("running"));

        Assert.That(await WaitWhileRunningAsync(client, TimeSpan.FromSeconds(60)), Is.EqualTo("failed"));
        var failed = await (await client.GetAsync("/systemmap")).Content.ReadAsStringAsync();
        Assert.That(failed, Does.Contain("<pre>").And.Contain("finns-inte.slnx").And.Contain("Försök igen").And.Contain("?retry=1"));

        Assert.That(await client.GetStringAsync("/systemmap"), Is.EqualTo(failed), "en ny sidladdning startar ingen ny körning");
        Assert.That(await StatusAsync(client), Is.EqualTo("failed"));

        var retry = await client.GetAsync("/systemmap?retry=1");
        Assert.That(retry.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        Assert.That(retry.Headers.Location!.OriginalString, Is.EqualTo("/systemmap"));
        Assert.That(await StatusAsync(client), Is.EqualTo("running"));
        Assert.That(await WaitWhileRunningAsync(client, TimeSpan.FromSeconds(60)), Is.EqualTo("failed"));
    }

    [Test]
    public async Task Missing_tool_gives_an_error_page_not_an_exception()
    {
        WriteDevJson(Path.Combine(_dir, "finns-inte.dll"), null, _dir, Path.Combine(_dir, "finns-inte-dotnet.exe"));
        var client = await StartAsync("Development");

        await client.GetAsync("/systemmap");
        Assert.That(await WaitWhileRunningAsync(client, TimeSpan.FromSeconds(30)), Is.EqualTo("failed"));
        Assert.That(await client.GetStringAsync("/systemmap"), Does.Contain("Försök igen"));
    }

    [Test]
    public async Task Development_draws_the_map_on_request()
    {
        if (DotnetHost is null) Assert.Ignore("DOTNET_HOST_PATH saknas (sätts av dotnet test).");
        WriteDevJson(ToolInOwnFolder, null, Path.Combine(TestPaths.Samples, "ShopApi"), DotnetHost!);
        var client = await StartAsync("Development");

        Assert.That(await client.GetStringAsync("/systemmap"), Does.Contain("Kartan ritas"));
        Assert.That(await WaitWhileRunningAsync(client, TimeSpan.FromMinutes(3)), Is.EqualTo("ready"), await client.GetStringAsync("/systemmap/status"));
        Assert.That(await client.GetStringAsync("/systemmap"), Does.Contain("POST /orders"));
        Assert.That(await client.GetStringAsync("/systemmap/map.json"), Does.Contain("\"/Orders\""));
    }
}
