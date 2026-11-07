using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using TestGen.Cli.Ui;
using TestGen.Core.Run;

namespace TestGen.Tests;

/// <summary>Webbgränssnittet startas i processen på en slumpad port; API:et som testas är en fejkad HTTP-handler.</summary>
public class UiTests
{
    private const string Token = "tok-ui-4c2b9a";
    private UiServer _server = null!;
    private HttpClient _http = null!;
    private Func<HttpMessageHandler?> _handler = () => new FakeApi();

    [SetUp]
    public async Task SetUp()
    {
        _handler = () => new FakeApi();
        await StartServer();
    }

    private async Task StartServer(string? excel = null)
    {
        _server = await UiServer.StartAsync(new UiOptions { Port = 0, Spec = Spec(), Excel = excel, HandlerFactory = () => _handler() });
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_server.Port}"), Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.Add(UiServer.KeyHeader, _server.SessionKey);
    }

    [TearDown]
    public async Task TearDown()
    {
        _http.Dispose();
        await _server.DisposeAsync();
    }

    private static string Spec()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var f = Path.Combine(d.FullName, "samples", "shop.openapi.yaml");
            if (File.Exists(f)) return f;
        }
        throw new FileNotFoundException("samples/shop.openapi.yaml");
    }

    /// <summary>Fejk-API: order med quantity 0 ger 500 (som ekar token), övrig POST 201, annars 200.</summary>
    private sealed class FakeApi : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            var body = req.Content is null ? null : JsonNode.Parse(await req.Content.ReadAsStringAsync(ct)) as JsonObject;
            if (path == "/orders" && body?["quantity"]?.GetValue<int>() == 0)
                return Json(HttpStatusCode.InternalServerError, $"{{\"error\":\"internal error\",\"auth\":\"{req.Headers.Authorization}\"}}");
            if (req.Method == HttpMethod.Post && body is not null)
            {
                body["id"] = 7;
                return Json(HttpStatusCode.Created, body.ToJsonString());
            }
            return Json(HttpStatusCode.OK, "{}");
        }
    }

    /// <summary>Svarar aldrig (tills anropet avbryts), så att en körning kan hållas igång.</summary>
    private sealed class HangingApi(TaskCompletionSource reached) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            reached.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return Json(HttpStatusCode.OK, "{}");
        }
    }

    private sealed class DeniedApi : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) =>
            Task.FromResult(Json(HttpStatusCode.Unauthorized, "{}"));
    }

    private sealed class DownApi : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) =>
            throw new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused");
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static object Body(string only = "createOrder", string? token = Token, string? env = null, string? api = "http://fake.test",
        string? excel = null, string? upload = null) =>
        new { spec = Spec(), api, token, only, seed = "1", env, excel, upload };

    private async Task<string> Start(object? body = null)
    {
        var res = await _http.PostAsJsonAsync("/api/runs", body ?? Body());
        var text = await res.Content.ReadAsStringAsync();
        Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.Accepted), text);
        return JsonNode.Parse(text)!["id"]!.GetValue<string>();
    }

    private sealed record Ev(string Name, JsonNode Data, string Raw);

    private async Task<(List<Ev> Events, string Raw)> Events(string id)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var res = await _http.GetAsync($"/api/runs/{id}/events", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(res.Content.Headers.ContentType!.MediaType, Is.EqualTo("text/event-stream"));
        var raw = await res.Content.ReadAsStringAsync(cts.Token);   // strömmen avslutas när körningen är slut
        var list = new List<Ev>();
        foreach (var block in raw.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var name = Regex.Match(block, @"^event: (\w+)", RegexOptions.Multiline);
            var data = Regex.Match(block, @"^data: (.*)$", RegexOptions.Multiline);
            if (name.Success && data.Success) list.Add(new Ev(name.Groups[1].Value, JsonNode.Parse(data.Groups[1].Value)!, data.Groups[1].Value));
        }
        return (list, raw);
    }

    private static string TempExcel(string dir, bool withToken = true)
    {
        var path = Path.Combine(dir, "tester.xlsx");
        using var wb = new XLWorkbook();
        wb.AddWorksheet("Tester").Cell(1, 1).Value = "Scenario";
        var ws = wb.AddWorksheet("Miljöer");
        ws.Cell(1, 1).Value = "Miljö"; ws.Cell(1, 2).Value = "Inställning"; ws.Cell(1, 3).Value = "Värde";
        var rows = new List<string[]> { new[] { "lokal", "api", "http://fake.test" }, new[] { "test", "api", "http://test.fake" } };
        if (withToken) rows.Add(new[] { "test", "token", Token });
        for (var i = 0; i < rows.Count; i++) for (var c = 0; c < 3; c++) ws.Cell(i + 2, c + 1).Value = rows[i][c];
        wb.SaveAs(path);
        return path;
    }

    /// <summary>Excel med bladet Tester (rubriker + rader) och ev. bladet Miljöer.</summary>
    private static string ExcelRows(string dir, string[] headers, string[][] rows, string[][]? envs = null)
    {
        var path = Path.Combine(dir, "egna.xlsx");
        using var wb = new XLWorkbook();
        var testSheet = wb.AddWorksheet("Tester");
        for (var c = 0; c < headers.Length; c++) testSheet.Cell(1, c + 1).Value = headers[c];
        for (var r = 0; r < rows.Length; r++) for (var c = 0; c < rows[r].Length; c++) testSheet.Cell(r + 2, c + 1).Value = rows[r][c];
        if (envs is not null)
        {
            var ws = wb.AddWorksheet("Miljöer");
            ws.Cell(1, 1).Value = "Miljö"; ws.Cell(1, 2).Value = "Inställning"; ws.Cell(1, 3).Value = "Värde";
            for (var r = 0; r < envs.Length; r++) for (var c = 0; c < 3; c++) ws.Cell(r + 2, c + 1).Value = envs[r][c];
        }
        wb.SaveAs(path);
        return path;
    }

    private static readonly string[] TestSheetHeaders = { "Scenario", "Anrop", "Indata", "Spara som", "Förväntad status", "Förväntat svar" };

    [Test]
    public async Task Excel_errors_give_one_error_event_listing_all_errors_and_no_calls()
    {
        var dir = Directory.CreateTempSubdirectory("testgen-uitest-").FullName;
        try
        {
            var calls = 0;
            _handler = () => new CountingApi(() => calls++);
            var excel = ExcelRows(dir, TestSheetHeaders, new[]
            {
                new[] { "Kund", "createCustomerr", "", "", "201", "" },
                new[] { "Kund", "createCustomer", "", "", "bananer", "" },
            });
            var id = await Start(Body(only: "", excel: excel));
            var (events, raw) = await Events(id);
            var error = events.Single(e => e.Name == "error").Data;
            var items = error["items"]!.AsArray().Select(item => item!.GetValue<string>()).ToList();
            Assert.That(items, Has.Count.EqualTo(2));
            Assert.That(items[0], Does.StartWith("Tester!B2 (Anrop):").And.Contain("createCustomer"));
            Assert.That(items[1], Does.StartWith("Tester!E3 (Förväntad status):"));
            Assert.That(error["message"]!.GetValue<string>(), Does.Contain("har 2 fel").And.Not.Contain("\n").And.Not.Contain("Tester!B2"));
            Assert.That(error["tip"]!.GetValue<string>(), Does.StartWith("Rätta cellerna ovan i egna.xlsx"));
            Assert.That(events.Any(e => e.Name == "result"), Is.False, "inga tester ska ha körts");
            Assert.That(calls, Is.EqualTo(0), "inga anrop mot API:et");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public async Task Excel_warnings_are_sent_as_a_warning_event_and_do_not_stop_the_run()
    {
        var dir = Directory.CreateTempSubdirectory("testgen-uitest-").FullName;
        try
        {
            var excel = ExcelRows(dir, TestSheetHeaders.Append("Status").ToArray(),
                new[] { new[] { "Kund", "createCustomer", "", "", "201", "", "x" } },
                new[] { new[] { "test", "api", "http://fake.test" }, new[] { "test", "token", Token } });
            var id = await Start(Body(only: "", excel: excel, env: "test", token: null));
            var (events, raw) = await Events(id);
            var v = events.Single(e => e.Name == "warning").Data;
            var items = v["items"]!.AsArray().Select(item => item!.GetValue<string>()).ToList();
            Assert.That(items, Has.Some.Contain("Miljöer!C3").And.Some.Contain("klartext"), "token i klartext");
            Assert.That(items, Has.Some.Contain("Status"), "okänd rubrik");
            Assert.That(v["title"]!.GetValue<string>(), Does.Contain("körningen fortsätter"));
            Assert.That(raw, Does.Not.Contain(Token), "varningen får aldrig visa själva token");
            Assert.That(events.Any(e => e.Name == "result"), Is.True);
            Assert.That(events.Last().Name, Is.EqualTo("done"));
            Assert.That(events.Select(e => e.Name).ToList().IndexOf("warning"), Is.LessThan(events.FindIndex(e => e.Name == "result")), "varningen kommer före resultaten");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public async Task Errors_in_the_environment_sheet_give_400_with_a_bullet_list()
    {
        var dir = Directory.CreateTempSubdirectory("testgen-uitest-").FullName;
        try
        {
            var excel = ExcelRows(dir, TestSheetHeaders, Array.Empty<string[]>(),
                new[] { new[] { "test", "api", "http://fake.test" }, new[] { "test", "header", "saknar-kolon" }, new[] { "test", "timeout", "snabbt" } });
            var res = await _http.PostAsJsonAsync("/api/runs", Body(only: "", excel: excel, env: "test", token: null));
            var j = JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
            Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            var items = j["items"]!.AsArray().Select(item => item!.GetValue<string>()).ToList();
            Assert.That(items, Has.Count.EqualTo(2));
            Assert.That(items, Has.All.StartWith("Miljöer!C"));
            Assert.That(j["tip"]!.GetValue<string>(), Does.StartWith("Rätta cellerna ovan"));
        }
        finally { Directory.Delete(dir, true); }
    }

    private sealed class CountingApi(Action onCall) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            onCall();
            return Task.FromResult(Json(HttpStatusCode.OK, "{}"));
        }
    }

    // --- Sidan och skydd -----------------------------------------------------------------

    [Test]
    public async Task Page_is_served_without_external_resources()
    {
        var res = await _http.GetAsync("/");
        var html = await res.Content.ReadAsStringAsync();
        Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(res.Content.Headers.ContentType!.MediaType, Is.EqualTo("text/html"));
        Assert.That(html, Does.Contain("Kör tester"));
        Assert.That(html, Does.Contain(_server.SessionKey));
        Assert.That(html, Does.Not.Contain("__SESSION_KEY__").And.Not.Contain("__PREFILL__"));
        Assert.That(Regex.IsMatch(html, @"(src|href)\s*=\s*[""']https?:", RegexOptions.IgnoreCase), Is.False, "inga externa resurser");
        Assert.That(html, Does.Not.Contain("@import").And.Not.Contain("url(http"));
        Assert.That(res.Headers.GetValues("Content-Security-Policy").Single(), Does.Contain("connect-src 'self'"));
        Assert.That(html, Does.Contain(Path.GetFileName(Spec())), "spec är förifylld");
    }

    [Test]
    public async Task Page_has_a_tablist_with_the_tests_and_map_tabs()
    {
        var html = await _http.GetStringAsync("/");
        Assert.That(html, Does.Contain("role=\"tablist\""));
        foreach (var (tab, panel, selected) in new[] { ("tab-tests", "panel-tests", "true"), ("tab-map", "panel-map", "false") })
        {
            Assert.That(html, Does.Contain($"role=\"tab\" id=\"{tab}\" aria-selected=\"{selected}\" aria-controls=\"{panel}\""), tab);
            Assert.That(html, Does.Contain($"role=\"tabpanel\" id=\"{panel}\" aria-labelledby=\"{tab}\""), panel);
        }
        // Formuläret hör till fliken Tester och kartans fält till fliken Systemkarta.
        Assert.That(html.IndexOf("id=\"form\"", StringComparison.Ordinal), Is.GreaterThan(html.IndexOf("id=\"panel-tests\"", StringComparison.Ordinal)).And.LessThan(html.IndexOf("id=\"panel-map\"", StringComparison.Ordinal)));
        Assert.That(html.IndexOf("id=\"map-retry\"", StringComparison.Ordinal), Is.GreaterThan(html.IndexOf("id=\"panel-map\"", StringComparison.Ordinal)));
        Assert.That(html, Does.Not.Contain("id=\"solution\"").And.Not.Contain("id=\"map-go\""), "kartfliken har inget fält för solutionen");
    }

    [Test]
    public async Task Map_libraries_are_served_by_the_server()
    {
        foreach (var name in new[] { "cytoscape.min.js", "layout-base.js", "cose-base.js", "cytoscape-fcose.js", "systemmap.js", "systemmap.css" })
        {
            var res = await _http.GetAsync("/lib/" + name);
            Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.OK), name);
            Assert.That(res.Content.Headers.ContentType!.MediaType, Is.EqualTo(name.EndsWith(".css") ? "text/css" : "text/javascript"), name);
            Assert.That((await res.Content.ReadAsStringAsync()).Length, Is.GreaterThan(2000), name);
        }
        Assert.That((await _http.GetAsync("/lib/annat.js")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound), "bara kartans egna filer");
        Assert.That((await _http.GetAsync("/lib/page.html")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await _http.GetAsync("/lib/..%2Findex.html")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        var page = await _http.GetAsync("/");
        var csp = page.Headers.GetValues("Content-Security-Policy").Single();
        Assert.That(csp, Does.Contain("script-src 'self' 'unsafe-inline'").And.Contain("style-src 'self' 'unsafe-inline'"),
            "skripten och kartans stil hämtas från testgen själv, aldrig utifrån");
        Assert.That(await page.Content.ReadAsStringAsync(), Does.Contain("<script src=\"/lib/systemmap.js\"></script>").And.Contain("href=\"/lib/systemmap.css\""));
    }

    [Test]
    public async Task Server_listens_only_on_loopback()
    {
        Assert.That(_server.Url, Does.StartWith("http://localhost:"));
        var port = _server.Port;
        var nonLoopback = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
        if (nonLoopback is null) Assert.Pass("Ingen annan nätverksadress att pröva.");
        using var client = new System.Net.Sockets.TcpClient();
        Assert.That(async () => await client.ConnectAsync(nonLoopback!, port), Throws.InstanceOf<System.Net.Sockets.SocketException>());
    }

    [Test]
    public async Task Api_requires_a_session_key()
    {
        using var open = new HttpClient { BaseAddress = _http.BaseAddress };
        Assert.That((await open.GetAsync("/api/environments")).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That((await open.PostAsJsonAsync("/api/runs", Body())).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That((await open.GetAsync("/api/runs/x/events")).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That((await open.GetAsync("/api/runs/x/report?type=html")).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        open.DefaultRequestHeaders.Add(UiServer.KeyHeader, "fel-nyckel");
        var res = await open.GetAsync("/api/environments");
        Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(await res.Content.ReadAsStringAsync(), Does.Contain("sessionsnyckel"));
    }

    [Test]
    public async Task Wrong_host_and_wrong_origin_are_rejected()
    {
        foreach (var host in new[] { "evil.example.com", $"evil.example.com:{_server.Port}", "localhost", "localhost:1", $"192.168.1.5:{_server.Port}" })
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/");
            req.Headers.Host = host;
            Assert.That((await _http.SendAsync(req)).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden), host);
        }
        using var ok = new HttpRequestMessage(HttpMethod.Get, "/");
        ok.Headers.Host = $"localhost:{_server.Port}";
        Assert.That((await _http.SendAsync(ok)).StatusCode, Is.EqualTo(HttpStatusCode.OK));

        using var foreign = new HttpRequestMessage(HttpMethod.Get, "/api/environments");
        foreign.Headers.Add("Origin", "https://evil.example.com");
        Assert.That((await _http.SendAsync(foreign)).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        using var own = new HttpRequestMessage(HttpMethod.Get, "/api/environments");
        own.Headers.Add("Origin", $"http://localhost:{_server.Port}");
        Assert.That((await _http.SendAsync(own)).StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    // --- Körning -------------------------------------------------------------------------

    [Test]
    public async Task Run_streams_events_and_ends_with_a_summary()
    {
        var id = await Start();
        var (events, _) = await Events(id);

        var results = events.Where(e => e.Name == "result").ToList();
        Assert.That(results, Is.Not.Empty);
        Assert.That(results.Select(r => r.Data["index"]!.GetValue<int>()), Is.EqualTo(Enumerable.Range(1, results.Count)));
        Assert.That(results.All(r => r.Data["total"]!.GetValue<int>() == results.Count));
        Assert.That(events.Last().Name, Is.EqualTo("done"));
        Assert.That(events.Count(e => e.Name == "done"), Is.EqualTo(1));

        var end = events.Last().Data;
        int ok = results.Count(r => r.Data["outcome"]!.GetValue<string>() == "OK"), fail = results.Count(r => r.Data["outcome"]!.GetValue<string>() == "FEL");
        Assert.That(end["total"]!.GetValue<int>(), Is.EqualTo(results.Count));
        Assert.That(end["ok"]!.GetValue<int>(), Is.EqualTo(ok));
        Assert.That(end["fail"]!.GetValue<int>(), Is.EqualTo(fail));
        Assert.That(fail, Is.GreaterThan(0), "quantity 0 -> 500 ska bli FEL");
        Assert.That(end["summary"]!.GetValue<string>(), Is.EqualTo(PlainLanguage.Summary(ok, fail, 0)));
        Assert.That(end["cancelled"]!.GetValue<bool>(), Is.False);
        Assert.That(end["warnings"]!.GetValue<int>(), Is.EqualTo(results.Sum(r => r.Data["warnings"]!.AsArray().Count)), "antal varningar i sammanfattningen");
        Assert.That(results.All(r => r.Data["warnings"] is JsonArray), "varningar per kort");

        var bad = results.First(r => r.Data["outcome"]!.GetValue<string>() == "FEL").Data;
        Assert.That(bad["id"]!.GetValue<string>(), Does.Match(@"^T-\d{3}$"));
        Assert.That(bad["title"]!.GetValue<string>(), Is.Not.Empty);
        Assert.That(bad["explanation"]!.GetValue<string>(), Is.Not.Empty);
        Assert.That(bad["hint"]!.GetValue<string>(), Is.Not.Empty);
        Assert.That(bad["curl"]!.GetValue<string>(), Does.Contain("\ncurl ").Or.StartWith("curl "));
        Assert.That(bad["technical"]!.AsArray(), Is.Not.Empty);
        Assert.That(bad["group"]!.GetValue<string>(), Is.EqualTo("Genererade tester"));
    }

    [Test]
    public async Task Token_never_appears_in_events_reports_or_responses()
    {
        var dir = Directory.CreateTempSubdirectory("testgen-uitest-").FullName;
        try
        {
            var excel = TempExcel(dir);
            var seen = new StringBuilder();

            var env = await _http.GetStringAsync($"/api/environments?excel={Uri.EscapeDataString(excel)}");
            Assert.That(JsonNode.Parse(env)!["environments"]!.AsArray().Select(environment => environment!["name"]!.GetValue<string>()), Is.EqualTo(new[] { "lokal", "test" }));
            seen.Append(env);

            var start = await _http.PostAsJsonAsync("/api/runs", Body());
            seen.Append(await start.Content.ReadAsStringAsync());
            var id = JsonNode.Parse(seen.ToString()[env.Length..])!["id"]!.GetValue<string>();
            var (_, raw) = await Events(id);
            seen.Append(raw);
            Assert.That(raw, Does.Contain("***"), "fejk-API:et ekar token; den ska ha maskats");

            var html = await _http.GetStringAsync($"/api/runs/{id}/report?type=html");
            seen.Append(html);
            var xlsx = await _http.GetByteArrayAsync($"/api/runs/{id}/report?type=xlsx");
            using (var zip = new ZipArchive(new MemoryStream(xlsx)))
                foreach (var e in zip.Entries)
                    using (var r = new StreamReader(e.Open())) seen.Append(await r.ReadToEndAsync());

            // även körning via en miljö vars token står i Excel
            var id2 = await Start(Body(token: null, env: "test", api: null, excel: excel));
            var (_, raw2) = await Events(id2);
            seen.Append(raw2).Append(await _http.GetStringAsync($"/api/runs/{id2}/report?type=html"));

            Assert.That(seen.ToString(), Does.Not.Contain(Token));
            Assert.That(seen.ToString(), Does.Not.Contain(Convert.ToBase64String(Encoding.UTF8.GetBytes(Token))));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public async Task Concurrent_run_gives_409_and_cancel_works()
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler = () => new HangingApi(reached);
        var id = await Start();
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(20));

        var second = await _http.PostAsJsonAsync("/api/runs", Body());
        var json = JsonNode.Parse(await second.Content.ReadAsStringAsync())!;
        Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(json["message"]!.GetValue<string>(), Does.Contain("pågår redan"));

        var early = await _http.GetAsync($"/api/runs/{id}/report?type=html");
        Assert.That(early.StatusCode, Is.EqualTo(HttpStatusCode.Conflict), "rapport finns inte förrän körningen är klar");

        var cancel = await _http.PostAsync($"/api/runs/{id}/cancel", null);
        Assert.That(cancel.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        var (events, _) = await Events(id);
        Assert.That(events.Last().Name, Is.EqualTo("done"));
        Assert.That(events.Last().Data["cancelled"]!.GetValue<bool>(), Is.True);
        Assert.That(events.Last().Data["summary"]!.GetValue<string>(), Does.Contain("avbröt"));
        Assert.That(events.Any(e => e.Name == "error"), Is.False);

        // efter avbrott går det att starta en ny körning
        _handler = () => new FakeApi();
        var third = await _http.PostAsJsonAsync("/api/runs", Body());
        Assert.That(third.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        await Events(JsonNode.Parse(await third.Content.ReadAsStringAsync())!["id"]!.GetValue<string>());
    }

    [Test]
    public async Task Report_download_html_and_xlsx()
    {
        var id = await Start();
        await Events(id);

        var html = await _http.GetAsync($"/api/runs/{id}/report?type=html");
        Assert.That(html.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(html.Content.Headers.ContentType!.MediaType, Is.EqualTo("text/html"));
        Assert.That(html.Content.Headers.ContentDisposition!.FileName, Does.EndWith(".html"));
        Assert.That(await html.Content.ReadAsStringAsync(), Does.Contain("visar ett problem").And.Contain("Tekniska detaljer"));

        var x = await _http.GetAsync($"/api/runs/{id}/report?type=xlsx");
        Assert.That(x.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(x.Content.Headers.ContentDisposition!.FileName, Does.EndWith(".xlsx"));
        using var wb = new XLWorkbook(new MemoryStream(await x.Content.ReadAsByteArrayAsync()));
        Assert.That(wb.Worksheets.Select(w => w.Name), Does.Contain("Resultat").And.Contain("Sammanfattning"));

        Assert.That((await _http.GetAsync($"/api/runs/{id}/report?type=pdf")).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await _http.GetAsync("/api/runs/finnsinte/report?type=html")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Events_can_be_reread_and_replayed_from_the_start()
    {
        var id = await Start();
        var (first, _) = await Events(id);
        var (again, _) = await Events(id);   // t.ex. efter omladdning av sidan
        Assert.That(again.Select(e => e.Raw), Is.EqualTo(first.Select(e => e.Raw)));
        Assert.That(again.Last().Name, Is.EqualTo("done"));
    }

    [Test]
    public async Task New_reader_gets_finished_results_and_follows_the_rest_of_an_ongoing_run()
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler = () => new HangingApi(reached);
        var id = await Start();
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(20));

        var cur = JsonNode.Parse(await _http.GetStringAsync("/api/runs/latest"))!;
        Assert.That(cur["exists"]!.GetValue<bool>(), Is.True);
        Assert.That(cur["id"]!.GetValue<string>(), Is.EqualTo(id));
        Assert.That(cur["running"]!.GetValue<bool>(), Is.True);
        Assert.That(cur["api"]!.GetValue<string>(), Is.EqualTo("http://fake.test"));
        Assert.That(cur["only"]!.GetValue<string>(), Is.EqualTo("createOrder"));
        Assert.That(cur.ToJsonString(), Does.Not.Contain(Token), "token får aldrig skickas tillbaka");

        // två läsare samtidigt (den gamla sidan och den omladdade) får båda strömmen och slutet
        var one = Events(id);
        var two = Events(id);
        await Task.Delay(300);
        Assert.That((await _http.PostAsync($"/api/runs/{id}/cancel", null)).StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        var (e1, _) = await one;
        var (e2, _) = await two;
        Assert.That(e1.Last().Name, Is.EqualTo("done"));
        Assert.That(e2.Select(e => e.Raw), Is.EqualTo(e1.Select(e => e.Raw)));

        var after = JsonNode.Parse(await _http.GetStringAsync("/api/runs/latest"))!;
        Assert.That(after["running"]!.GetValue<bool>(), Is.False, "avslutad körning går fortfarande att hämta");
    }

    [Test]
    public async Task No_run_exists_before_one_is_started()
    {
        var cur = JsonNode.Parse(await _http.GetStringAsync("/api/runs/latest"))!;
        Assert.That(cur["exists"]!.GetValue<bool>(), Is.False);
    }

    [Test]
    public async Task Already_running_gives_the_ongoing_run_id_and_cancel_releases_hanging_calls()
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler = () => new HangingApi(reached);
        var id = await Start();
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(20));

        var second = await _http.PostAsJsonAsync("/api/runs", Body());
        var json = JsonNode.Parse(await second.Content.ReadAsStringAsync())!;
        Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(json["runningId"]!.GetValue<string>(), Is.EqualTo(id));

        var started = DateTime.UtcNow;
        await _http.PostAsync($"/api/runs/{id}/cancel", null);
        var (events, _) = await Events(id);
        Assert.That(DateTime.UtcNow - started, Is.LessThan(TimeSpan.FromSeconds(5)), "ett hängande HTTP-anrop ska avbrytas direkt");
        Assert.That(events.Last().Data["cancelled"]!.GetValue<bool>(), Is.True);
    }

    [Test]
    public async Task Cancel_before_the_first_result_gives_its_own_text()
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler = () => new HangingApi(reached);
        var id = await Start();
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await _http.PostAsync($"/api/runs/{id}/cancel", null);
        var (events, _) = await Events(id);
        var end = events.Last().Data;
        Assert.That(end["total"]!.GetValue<int>(), Is.EqualTo(0));
        Assert.That(end["cancelled"]!.GetValue<bool>(), Is.True);
        Assert.That(end["summary"]!.GetValue<string>(), Is.EqualTo("Du avbröt körningen innan något test hann bli klart."));
    }

    [Test]
    public async Task Invalid_fields_point_out_which_field_is_wrong()
    {
        async Task<string?> Field(object body)
        {
            var res = await _http.PostAsJsonAsync("/api/runs", body);
            Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            return JsonNode.Parse(await res.Content.ReadAsStringAsync())!["field"]?.GetValue<string>();
        }
        Assert.That(await Field(new { spec = "", api = "http://fake.test" }), Is.EqualTo("spec"));
        Assert.That(await Field(new { spec = "finns-inte.yaml", api = "http://fake.test" }), Is.EqualTo("spec"));
        Assert.That(await Field(new { spec = Spec(), api = "http://fake.test", seed = "abc" }), Is.EqualTo("seed"));
        Assert.That(await Field(new { spec = Spec() }), Is.EqualTo("api"));
    }

    // --- Fel i klartext ------------------------------------------------------------------

    [Test]
    public async Task Unresponsive_api_gives_error_event_in_plain_language()
    {
        _handler = () => new DownApi();
        var id = await Start(Body(api: "http://localhost:5999"));
        var (events, _) = await Events(id);
        var error = events.Single(e => e.Name == "error").Data;
        Assert.That(error["message"]!.GetValue<string>(), Does.Contain("Kan inte nå API:et på http://localhost:5999").And.Contain("är det startat?").And.Not.Contain("Connection refused"));
        Assert.That(error["tip"]!.GetValue<string>(), Does.Contain("startat"));
        Assert.That(events.Any(e => e.Name == "done"), Is.False);
    }

    [Test]
    public async Task Error_event_has_the_same_text_as_the_command_line_and_technical_details()
    {
        _handler = () => new DownApi();
        var (events, raw) = await Events(await Start(Body(api: "http://localhost:5999")));
        var error = events.Single(e => e.Name == "error").Data;
        var cli = FriendlyErrors.Translate(new ApiUnreachableException("http://localhost:5999", ApiProblem.Refused, "x"));
        Assert.That(error["message"]!.GetValue<string>(), Is.EqualTo(cli.What));
        Assert.That(error["tip"]!.GetValue<string>(), Is.EqualTo(cli.Action));
        Assert.That(error["technical"]!.GetValue<string>(), Does.Contain("Connection refused"));
        Assert.That(raw, Does.Not.Contain(Token));
    }

    [Test]
    public async Task Error_event_replaces_flag_names_with_field_names()
    {
        var (events, _) = await Events(await Start(Body(only: "finnsInte")));
        var error = events.Single(e => e.Name == "error").Data;
        Assert.That(error["message"]!.GetValue<string>(), Does.StartWith("Bara ett anrop: okänd operation").And.Not.Contain("--"));
        Assert.That(error["tip"]!.GetValue<string>(), Does.Not.Contain("--"));
    }

    [Test]
    public void UiErrors_replaces_flags_and_the_word_spec_but_keeps_the_rest_of_the_text()
    {
        var f = UiErrors.Translate(new MissingSourceException(SourceKind.Spec, "/x/y.yaml"));
        Assert.That(f.What, Is.EqualTo("Hittar inte API-beskrivningen: /x/y.yaml"));
        Assert.That(f.ExitCode, Is.EqualTo(ExitCodes.Usage));
        Assert.That(UiErrors.FieldNames("Öka med --timeout, ange --api och --token."), Is.EqualTo("Öka med Timeout, ange API-adress och Token."));
        var unexpected = UiErrors.Translate(new NotSupportedException("boom"));
        Assert.That(unexpected.Action, Does.Not.Contain("--verbose"));
        Assert.That(unexpected.Technical, Does.Contain("boom"));
    }

    [Test]
    public async Task End_contains_a_login_note_when_almost_all_responses_are_401()
    {
        _handler = () => new DeniedApi();
        var (denied, _) = await Events(await Start(Body(only: null!)));
        var note = denied.Last().Data["authNote"]?.GetValue<string>();
        Assert.That(note, Does.StartWith("Alla tester fick 401").And.Contain("fältet Token").And.Not.Contain("--token"));

        TearDownRun();
        await StartServer();
        var (normal, _) = await Events(await Start());
        Assert.That(normal.Last().Data["authNote"], Is.Null);
    }

    [Test]
    public async Task End_has_verdict_cause_lines_and_cause_per_result()
    {
        _handler = () => new DeniedApi();
        var (denied, _) = await Events(await Start(Body(only: null!)));
        var end = denied.Last().Data;
        Assert.That(end["verdict"]!.GetValue<string>(), Does.StartWith("Inget säkert svar: "));
        var causes = end["causes"]!.AsArray();
        Assert.That(causes, Has.Count.EqualTo(1));
        Assert.That(causes[0]!["code"]!.GetValue<string>(), Is.EqualTo("inloggning"));
        Assert.That(causes[0]!["head"]!.GetValue<string>(), Is.EqualTo("Inloggning (401/403)"));
        Assert.That(causes[0]!["count"]!.GetValue<int>(), Is.GreaterThan(5));
        Assert.That(end["envNote"], Is.Null);
        var first = denied.First(e => e.Name == "result").Data;
        Assert.That(first["cause"]!.GetValue<string>(), Is.EqualTo("Inloggning"));
        Assert.That(first["followsFrom"], Is.Null);
    }

    private void TearDownRun() { _http.Dispose(); _server.DisposeAsync().AsTask().GetAwaiter().GetResult(); _handler = () => new FakeApi(); }

    [Test]
    public async Task Invalid_input_gives_400_with_explanation()
    {
        async Task<(HttpStatusCode, JsonNode)> Post(object body)
        {
            var r = await _http.PostAsJsonAsync("/api/runs", body);
            return (r.StatusCode, JsonNode.Parse(await r.Content.ReadAsStringAsync())!);
        }

        var (code, j) = await Post(new { spec = "", api = "http://x" });
        Assert.That(code, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(j["message"]!.GetValue<string>(), Does.Contain("API-beskrivning"));

        (code, j) = await Post(new { spec = "/finns/inte.yaml", api = "http://x" });
        Assert.That(j["message"]!.GetValue<string>(), Does.Contain("Hittar inte API-beskrivningen"));

        (code, j) = await Post(new { spec = Spec() });
        Assert.That(j["message"]!.GetValue<string>(), Does.Contain("API-adressen saknas"));

        (code, j) = await Post(new { spec = Spec(), api = "inte-en-adress" });
        Assert.That(j["message"]!.GetValue<string>(), Does.Contain("inte en giltig"));

        (code, j) = await Post(new { spec = Spec(), api = "http://x", token = "${env:TESTGEN_FINNS_INTE_9921}" });
        Assert.That(j["message"]!.GetValue<string>(), Does.Contain("TESTGEN_FINNS_INTE_9921").And.Contain("inte satt"));
        Assert.That(j["tip"]!.GetValue<string>(), Does.Contain("Token"));

        (code, j) = await Post(new { spec = Spec(), api = "http://x", seed = "abc" });
        Assert.That(j["message"]!.GetValue<string>(), Does.Contain("heltal"));

        var dir = Directory.CreateTempSubdirectory("testgen-uitest-").FullName;
        try
        {
            (code, j) = await Post(new { spec = Spec(), excel = TempExcel(dir), env = "produktion" });
            Assert.That(j["message"]!.GetValue<string>(), Does.Contain("Okänd miljö 'produktion'").And.Contain("lokal, test"));
            Assert.That(code, Is.EqualTo(HttpStatusCode.BadRequest));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public async Task Invalid_spec_gives_error_event()
    {
        var dir = Directory.CreateTempSubdirectory("testgen-uitest-").FullName;
        try
        {
            var bad = Path.Combine(dir, "trasig.yaml");
            File.WriteAllText(bad, "detta: är inte: en spec: [");
            var res = await _http.PostAsJsonAsync("/api/runs", new { spec = bad, api = "http://fake.test" });
            Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
            var (events, _) = await Events(JsonNode.Parse(await res.Content.ReadAsStringAsync())!["id"]!.GetValue<string>());
            Assert.That(events.Single().Name, Is.EqualTo("error"));
            Assert.That(events.Single().Data["tip"]!.GetValue<string>(), Is.Not.Empty);
        }
        finally { Directory.Delete(dir, true); }
    }

    // --- Uppladdning ---------------------------------------------------------------------

    [Test]
    public async Task Uploaded_excel_can_be_used_and_is_cleaned_up_when_the_server_stops()
    {
        var dir = Directory.CreateTempSubdirectory("testgen-uitest-").FullName;
        try
        {
            var bytes = File.ReadAllBytes(TempExcel(dir));
            var up = await _http.PostAsync("/api/uploads?name=" + Uri.EscapeDataString("../../mina tester.xlsx"), new ByteArrayContent(bytes));
            var j = JsonNode.Parse(await up.Content.ReadAsStringAsync())!;
            Assert.That(up.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(j["name"]!.GetValue<string>(), Is.EqualTo("mina tester.xlsx"), "sökvägsdelar i namnet tas bort");
            var uploadId = j["id"]!.GetValue<string>();

            var envs = JsonNode.Parse(await _http.GetStringAsync($"/api/environments?upload={uploadId}"))!;
            Assert.That(envs["environments"]!.AsArray().Count, Is.EqualTo(2));

            var id = await Start(Body(upload: uploadId, env: "lokal", api: null, token: null));
            var (events, _) = await Events(id);
            Assert.That(events.Last().Name, Is.EqualTo("done"));
            Assert.That(events.Last().Data["env"]!.GetValue<string>(), Is.EqualTo("lokal"));

            Assert.That(Directory.Exists(_server.TempDir), Is.True);
            var temp = _server.TempDir;
            await _server.DisposeAsync();
            Assert.That(Directory.Exists(temp), Is.False, "uppladdningar städas när servern avslutas");
            await StartServer();   // så att TearDown har något att stänga
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public async Task Upload_is_limited_and_checked()
    {
        var notExcel = await _http.PostAsync("/api/uploads?name=x.xlsx", new ByteArrayContent(Encoding.UTF8.GetBytes("det här är text")));
        Assert.That(notExcel.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var wrongExt = await _http.PostAsync("/api/uploads?name=x.exe", new ByteArrayContent(new byte[] { (byte)'P', (byte)'K' }));
        Assert.That(wrongExt.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        var big = await _http.PostAsync("/api/uploads?name=stor.xlsx", new ByteArrayContent(new byte[(int)UiServer.MaxUploadBytes + 10]));
        Assert.That(big.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
        Assert.That(await big.Content.ReadAsStringAsync(), Does.Contain("för stor"));

        // utan Content-Length (chunked) stoppas den också
        var chunked = new HttpRequestMessage(HttpMethod.Post, "/api/uploads?name=stor.xlsx") { Content = new StreamContent(new ZeroStream(UiServer.MaxUploadBytes + 2048)) };
        chunked.Headers.TransferEncodingChunked = true;
        Assert.That((await _http.SendAsync(chunked)).StatusCode, Is.AnyOf(HttpStatusCode.RequestEntityTooLarge, HttpStatusCode.BadRequest));
    }

    // --- Varv 3: slumpfrö/409, uppladdad API-beskrivning, scenarier, rapportnamn ---------------------

    /// <summary>Alla POST ger 409 ("fanns redan"), övrigt 200.</summary>
    private sealed class ConflictApi : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) =>
            Task.FromResult(req.Method == HttpMethod.Post ? Json(HttpStatusCode.Conflict, "{\"error\":\"exists\"}") : Json(HttpStatusCode.OK, "{}"));
    }

    [Test]
    public async Task Conflict_409_is_marked_on_the_card_with_UI_text_and_the_end_has_the_seed()
    {
        _handler = () => new ConflictApi();
        var (events, raw) = await Events(await Start(Body(only: "createOrder")));
        var card = events.First(e => e.Name == "result" && e.Data["outcome"]!.GetValue<string>() == "FEL").Data;
        Assert.That(card["conflict"]!.GetValue<bool>(), Is.True);
        Assert.That(card["preparing"]!.GetValue<string>(), Is.EqualTo("POST /customers"), "förberedande anrop som fick 409");
        Assert.That(card["hint"]!.GetValue<string>(), Does.Contain("Kör igen med nytt slumpfrö").And.Not.Contain("--"));
        Assert.That(events.Last().Data["seed"]!.GetValue<int>(), Is.EqualTo(1), "använt slumpfrö skickas med slutet");
        Assert.That(raw, Does.Not.Contain("--seed"));
    }

    [Test]
    public async Task Ordinary_failure_is_no_conflict_and_cards_without_preparing_steps_have_no_marker()
    {
        var (events, _) = await Events(await Start(Body(only: "createOrder")));
        var cards = events.Where(e => e.Name == "result").Select(e => e.Data).ToList();
        Assert.That(cards, Is.Not.Empty);
        Assert.That(cards.All(c => !c["conflict"]!.GetValue<bool>()), Is.True);
        Assert.That(cards.Where(c => c["outcome"]!.GetValue<string>() == "FEL").All(c => c["preparing"] is null), Is.True);
    }

    [Test]
    public async Task Default_seed_is_shown_at_the_end_when_none_is_given()
    {
        var (events, _) = await Events(await Start(new { spec = Spec(), api = "http://fake.test", only = "createOrder" }));
        Assert.That(events.Last().Data["seed"]!.GetValue<int>(), Is.EqualTo(12345));
    }

    private async Task<string> UploadId(string name, byte[] bytes, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var up = await _http.PostAsync("/api/uploads?name=" + Uri.EscapeDataString(name), new ByteArrayContent(bytes));
        var text = await up.Content.ReadAsStringAsync();
        Assert.That(up.StatusCode, Is.EqualTo(expected), text);
        return expected == HttpStatusCode.OK ? JsonNode.Parse(text)!["id"]!.GetValue<string>() : "";
    }

    [Test]
    public async Task Uploaded_api_description_can_be_run_and_is_shown_as_a_copy_after_reload()
    {
        var specId = await UploadId("../butik.yaml", File.ReadAllBytes(Spec()));
        var id = await Start(new { spec = "", specUpload = specId, api = "http://fake.test", only = "createOrder", seed = "1" });
        var (events, _) = await Events(id);
        Assert.That(events.Last().Name, Is.EqualTo("done"));
        Assert.That(events.Count(e => e.Name == "result"), Is.GreaterThan(0));

        var now = JsonNode.Parse(await _http.GetStringAsync("/api/runs/latest"))!;
        Assert.That(now["spec"]!.GetValue<string>(), Is.EqualTo("butik.yaml"), "bara filnamnet, aldrig sökvägen på disken");
        Assert.That(now["specUpload"]!.GetValue<string>(), Is.EqualTo(specId));
    }

    [Test]
    public async Task Api_description_upload_is_checked_and_types_are_not_mixed()
    {
        await UploadId("x.json", Encoding.UTF8.GetBytes("{\"openapi\":\"3.0.0\"}"));
        await UploadId("x.yml", Encoding.UTF8.GetBytes("openapi: 3.0.0"));
        await UploadId("x.xlsx", new byte[] { 1, 2, 3 }, HttpStatusCode.BadRequest);              // fel innehåll för Excel
        await UploadId("x.json", new byte[] { (byte)'{', 0, 0, 1 }, HttpStatusCode.BadRequest);    // binärt, inte text
        await UploadId("x.json", Array.Empty<byte>(), HttpStatusCode.BadRequest);
        await UploadId("x.txt", Encoding.UTF8.GetBytes("hej"), HttpStatusCode.BadRequest);

        var specId = await UploadId("spec.yaml", File.ReadAllBytes(Spec()));
        var asExcel = await _http.GetAsync($"/api/environments?upload={specId}");
        Assert.That(asExcel.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), "en API-beskrivning kan inte användas som Excel-fil");

        var dir = Directory.CreateTempSubdirectory("testgen-uitest-").FullName;
        try
        {
            var excelId = await UploadId("t.xlsx", File.ReadAllBytes(TempExcel(dir)));
            var res = await _http.PostAsJsonAsync("/api/runs", new { spec = "", specUpload = excelId, api = "http://fake.test" });
            var j = JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
            Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), "en Excel-fil kan inte användas som API-beskrivning");
            Assert.That(j["field"]!.GetValue<string>(), Is.EqualTo("spec"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public async Task Uploaded_api_description_is_size_limited()
    {
        var big = await _http.PostAsync("/api/uploads?name=stor.json", new ByteArrayContent(Enumerable.Repeat((byte)'a', (int)UiServer.MaxUploadBytes + 10).ToArray()));
        Assert.That(big.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
    }

    [Test]
    public async Task Old_uploads_are_cleaned_up_instead_of_giving_429()
    {
        var first = await UploadId("a.yaml", File.ReadAllBytes(Spec()));
        for (var i = 0; i < 25; i++) await UploadId($"f{i}.json", Encoding.UTF8.GetBytes("{}"));
        var gone = await _http.PostAsJsonAsync("/api/runs", new { spec = "", specUpload = first, api = "http://fake.test" });
        Assert.That(gone.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), "den äldsta är borttagen");
        Assert.That(await gone.Content.ReadAsStringAsync(), Does.Contain("finns inte längre"));
    }

    private static string ScenarioExcel(string dir) => ExcelRows(dir, TestSheetHeaders, new[]
    {
        new[] { "Kund A", "createCustomer", "", "", "201", "" },
        new[] { "Kund B", "createCustomer", "", "", "201", "" },
        new[] { "Kund B", "createProduct", "", "", "201", "" },
    });

    [Test]
    public async Task Scenarios_are_listed_from_excel_via_path_and_upload()
    {
        var dir = Directory.CreateTempSubdirectory("testgen-uitest-").FullName;
        try
        {
            var excel = ScenarioExcel(dir);
            var byPath = JsonNode.Parse(await _http.GetStringAsync("/api/scenarios?excel=" + Uri.EscapeDataString(excel)))!;
            Assert.That(byPath["scenarios"]!.AsArray().Select(n => n!.GetValue<string>()), Is.EqualTo(new[] { "Kund A", "Kund B" }));
            var id = await UploadId("s.xlsx", File.ReadAllBytes(excel));
            var byUpload = JsonNode.Parse(await _http.GetStringAsync("/api/scenarios?upload=" + id))!;
            Assert.That(byUpload["scenarios"]!.AsArray().Count, Is.EqualTo(2));
            var none = JsonNode.Parse(await _http.GetStringAsync("/api/scenarios"))!;
            Assert.That(none["scenarios"]!.AsArray(), Is.Empty);
            Assert.That((await _http.GetAsync("/api/scenarios?excel=/finns/inte.xlsx")).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            using var anon = new HttpClient { BaseAddress = _http.BaseAddress };
            Assert.That((await anon.GetAsync("/api/scenarios")).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized), "kräver sessionsnyckel");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public async Task Excel_only_and_scenarios_are_passed_to_the_run_and_restored_after_reload()
    {
        var dir = Directory.CreateTempSubdirectory("testgen-uitest-").FullName;
        try
        {
            var excel = ScenarioExcel(dir);
            object Req(string[]? scenarios, bool excelOnly) => new { spec = Spec(), api = "http://fake.test", excel, seed = "1", scenarios, excelOnly };

            var (all, _) = await Events(await Start(Req(null, true)));
            var onlyExcel = all.Count(e => e.Name == "result");
            Assert.That(onlyExcel, Is.GreaterThan(0).And.LessThan(10), "bara Excel-testerna, inte de genererade");

            var (withGenerated, _) = await Events(await Start(Req(null, false)));
            Assert.That(withGenerated.Count(e => e.Name == "result"), Is.GreaterThan(onlyExcel));

            var (one, _) = await Events(await Start(Req(new[] { "kund a" }, false)));   // skiftlägesokänsligt som på kommandoraden
            var scenarioOnly = one.Count(e => e.Name == "result");
            Assert.That(scenarioOnly, Is.LessThan(onlyExcel), "ett scenario av två");
            Assert.That(scenarioOnly, Is.GreaterThan(0));

            var now = JsonNode.Parse(await _http.GetStringAsync("/api/runs/latest"))!;
            Assert.That(now["scenarios"]!.AsArray().Select(n => n!.GetValue<string>()), Is.EqualTo(new[] { "kund a" }));
            Assert.That(now["excelOnly"]!.GetValue<bool>(), Is.False);

            var (unknown, _) = await Events(await Start(Req(new[] { "Finns inte" }, false)));
            Assert.That(unknown.Single(e => e.Name == "error").Data["message"]!.GetValue<string>(), Does.Contain("Scenarier:").And.Not.Contain("--"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public async Task Scenarios_and_excel_only_without_excel_give_400_on_the_excel_field()
    {
        foreach (var body in new object[]
        {
            new { spec = Spec(), api = "http://fake.test", scenarios = new[] { "A" } },
            new { spec = Spec(), api = "http://fake.test", excelOnly = true },
        })
        {
            var res = await _http.PostAsJsonAsync("/api/runs", body);
            var j = JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
            Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(j["field"]!.GetValue<string>(), Is.EqualTo("excel"));
            Assert.That(j["message"]!.GetValue<string>(), Does.Not.Contain("--"));
        }
    }

    [Test]
    public async Task Report_file_name_has_environment_and_date_with_seconds_in_local_time()
    {
        var dir = Directory.CreateTempSubdirectory("testgen-uitest-").FullName;
        try
        {
            var excel = TempExcel(dir);
            var before = DateTime.Now.AddSeconds(-2);
            var id = await Start(Body(excel: excel, env: "lokal", api: null, token: null));
            await Events(id);
            var html = await _http.GetAsync($"/api/runs/{id}/report?type=html");
            var name = html.Content.Headers.ContentDisposition!.FileName!.Trim('"');
            Assert.That(name, Does.Match(@"^testrapport-lokal-\d{8}-\d{6}\.html$"));
            var stamp = DateTime.ParseExact(name["testrapport-lokal-".Length..^".html".Length], "yyyyMMdd-HHmmss", null);
            Assert.That(stamp, Is.GreaterThanOrEqualTo(before).And.LessThanOrEqualTo(DateTime.Now.AddSeconds(2)), "lokal tid");

            var noEnv = await Start(Body());
            await Events(noEnv);
            var x = await _http.GetAsync($"/api/runs/{noEnv}/report?type=xlsx");
            Assert.That(x.Content.Headers.ContentDisposition!.FileName!.Trim('"'), Does.Match(@"^testrapport-\d{8}-\d{6}\.xlsx$"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public void ReportFileStem_sanitizes_the_environment_name()
    {
        var t = new DateTime(2026, 11, 1, 17, 38, 5);
        Assert.That(UiServer.ReportFileStem(null, t), Is.EqualTo("testrapport-20261101-173805"));
        Assert.That(UiServer.ReportFileStem("  ", t), Is.EqualTo("testrapport-20261101-173805"));
        Assert.That(UiServer.ReportFileStem("Test", t), Is.EqualTo("testrapport-Test-20261101-173805"));
        Assert.That(UiServer.ReportFileStem("Stage Ö/åäö:1", t), Is.EqualTo("testrapport-Stage-O-aao-1-20261101-173805"));
        Assert.That(UiServer.ReportFileStem("../../etc", t), Does.Not.Contain("/").And.Not.Contain(".."));
    }

    [Test]
    public void UiErrors_removes_cli_help_duplicated_label_and_flags_in_card_text()
    {
        var bad = UiErrors.Translate(new ArgumentException("API-adressen 'localhost:5091' (--api) är inte en giltig http/https-adress, t.ex. http://localhost:5080."));
        Assert.That(bad.What, Is.EqualTo("API-adressen 'localhost:5091' är inte en giltig http/https-adress, t.ex. http://localhost:5080."));
        Assert.That(UiErrors.FieldNames("Kontrollera inställningarna och försök igen. Se: testgen --help"), Is.EqualTo("Kontrollera inställningarna och försök igen."));
        Assert.That(UiErrors.FieldNames("Rätta flaggan. Se: testgen run --help"), Is.EqualTo("Rätta flaggan."));
        var card = UiErrors.CardText("Uppgifterna fanns redan. Välj ett annat slumpfrö (--seed, eller Slumpfrö under Fler inställningar) eller be någon nollställa testsystemet.");
        Assert.That(card, Is.EqualTo("Uppgifterna fanns redan. Tryck på Kör igen med nytt slumpfrö eller be någon nollställa testsystemet."));
        Assert.That(UiErrors.CardText("Ange --token eller fyll i bladet Miljöer."), Is.EqualTo("Ange Token eller fyll i bladet Miljöer."));
    }

    [Test]
    public async Task Bad_api_address_gives_error_without_cli_help_or_duplicated_label()
    {
        var res = await _http.PostAsJsonAsync("/api/runs", new { spec = Spec(), api = "localhost:5091" });
        var j = JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
        Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var text = j["message"]!.GetValue<string>() + " " + j["tip"]!.GetValue<string>();
        Assert.That(text, Does.Not.Contain("testgen --help").And.Not.Contain("(API-adress)"));
    }

    private sealed class ZeroStream(long length) : Stream
    {
        private long _pos;
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, length - _pos);
            Array.Clear(buffer, offset, n);
            _pos += n;
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // --- Systemkarta -------------------------------------------------------------------------

    [Test]
    public async Task Result_has_the_call_chain_linked_to_the_spec()
    {
        var (events, _) = await Events(await Start());
        // Testet med quantity 0: fejk-API:et svarar 500 på själva ordern (övriga FEL är 201 där 400 väntades).
        var bad = events.Where(e => e.Name == "result").Select(e => e.Data)
            .First(d => d["chain"]!.AsArray().LastOrDefault()?["status"]?.GetValue<int>() == 500);
        Assert.That(bad["outcome"]!.GetValue<string>(), Is.EqualTo("FEL"));
        var chain = bad["chain"]!.AsArray().Select(step => step!).ToList();

        Assert.That(chain.Select(step => step["operationId"]?.GetValue<string>()), Is.EqualTo(new[] { "createCustomer", "createProduct", "createOrder" }));
        Assert.That(chain.Select(step => step["preparation"]!.GetValue<bool>()), Is.EqualTo(new[] { true, true, false }));
        Assert.That(chain.Select(step => step["name"]!.GetValue<string>()), Is.EqualTo(new[] { "Skapa kund", "Skapa produkt", "Skapa order" }));
        var order = chain[2];
        Assert.That((order["method"]!.GetValue<string>(), order["path"]!.GetValue<string>(), order["status"]!.GetValue<int>()), Is.EqualTo(("POST", "/orders", 500)));
        Assert.That(order["uses"]!.AsArray().Select(u => u!["field"]!.GetValue<string>()), Is.SupersetOf(new[] { "customerId", "productId" }));
        Assert.That(order["sent"]!.GetValue<string>(), Does.Contain("\"quantity\":0"));
    }

    [Test]
    public async Task Map_requires_an_existing_solution()
    {
        var res = await _http.PostAsJsonAsync("/api/map", new { solution = "finns-inte/Butik.slnx" });
        var j = JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
        Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(j["message"]!.GetValue<string>(), Does.StartWith("Hittar inte solutionen: ").And.Contain("Butik.slnx"));

        using var open = new HttpClient { BaseAddress = _http.BaseAddress };
        Assert.That((await open.PostAsJsonAsync("/api/map", new { solution = "x.slnx" })).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Map_for_the_sample_is_cached_until_next_time()
    {
        using var http = new HttpClient { BaseAddress = _http.BaseAddress, Timeout = TimeSpan.FromMinutes(5) };   // restore och MSBuild kan ta tid
        http.DefaultRequestHeaders.Add(UiServer.KeyHeader, _server.SessionKey);
        var body = new { solution = Path.Combine(Path.GetDirectoryName(Spec())!, "Shop.slnx"), spec = Spec() };

        var res = await http.PostAsJsonAsync("/api/map", body);
        var text = await res.Content.ReadAsStringAsync();
        Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.OK), text);
        var map = JsonNode.Parse(text)!;
        Assert.That(map["projects"]!.AsArray().Select(p => p!.GetValue<string>()), Is.EqualTo(new[] { "ShopApi", "ShopApplication", "ShopInfrastructure" }));
        Assert.That(map["entryPoints"]!.AsArray().Select(e => e!["operationId"]!.GetValue<string>()),
            Is.EquivalentTo(new[] { "createCustomer", "createOrder", "getOrder", "createProduct" }));
        Assert.That(map["differences"]!.AsArray(), Is.Empty);
        // Översikten som kartan ritas från: databasen en gång, med läsningar och skrivningar som egna rader.
        var nodes = map["overview"]!["nodes"]!.AsArray();
        Assert.That(nodes.Count(n => n!["kind"]!.GetValue<string>() == "integration" && n["name"]!.GetValue<string>() == "ShopDatabase"), Is.EqualTo(1));
        Assert.That(nodes.Count(n => n!["parent"]?.GetValue<string>() == "int:db:ShopDatabase"), Is.EqualTo(6));
        Assert.That(map["overview"]!["links"]!.AsArray(), Is.Not.Empty);

        var timer = System.Diagnostics.Stopwatch.StartNew();
        var again = await http.PostAsJsonAsync("/api/map", body);
        Assert.That(await again.Content.ReadAsStringAsync(), Is.EqualTo(text));
        Assert.That(timer.Elapsed, Is.LessThan(TimeSpan.FromSeconds(3)), "koden har inte ändrats: den sparade kartan används");

        // Samma karta som Mermaid, med sidans nivå, riktning och valda anrop.
        var mermaid = await http.PostAsJsonAsync("/api/map/mermaid", new { body.solution, body.spec, level = "classes", direction = "TB", entry = "POST /orders" });
        var diagram = await mermaid.Content.ReadAsStringAsync();
        Assert.That(mermaid.StatusCode, Is.EqualTo(HttpStatusCode.OK), diagram);
        Assert.That(mermaid.Content.Headers.ContentType!.MediaType, Is.EqualTo("text/plain"));
        Assert.That(diagram, Does.StartWith("flowchart TB\n")
            .And.Contain("int_ShopDatabase[(\"ShopDatabase<br/>databas, EF Core (ShopDbContext) och SQL\")]")
            .And.Contain("-->|\"skicka OrderPlaced\"| int_order_lagd")
            .And.Not.Contain("GET /orders"));
        var wrong = await http.PostAsJsonAsync("/api/map/mermaid", new { body.solution, body.spec, level = "hela" });
        Assert.That(wrong.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await wrong.Content.ReadAsStringAsync(), Does.Contain("--level måste vara projects, classes eller all"));
    }

    [Test]
    public async Task Solution_from_the_flag_is_used_without_the_page_sending_it()
    {
        await using var server = await UiServer.StartAsync(new UiOptions { Port = 0, Solution = "finns-inte/Butik.slnx" });
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };
        http.DefaultRequestHeaders.Add(UiServer.KeyHeader, server.SessionKey);
        Assert.That(await http.GetStringAsync("/"), Does.Not.Contain("finns-inte/Butik.slnx"), "solutionen förifylls inte på sidan");
        var res = await http.PostAsJsonAsync("/api/map", new { });
        Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await res.Content.ReadAsStringAsync(), Does.Contain("Hittar inte solutionen: ").And.Contain("Butik.slnx"));
    }
}

public class UiCancelTests
{
    [Test]
    public async Task TestRunner_is_cancelled_by_CancellationToken_and_keeps_finished_results()
    {
        using var cts = new CancellationTokenSource();
        var runner = default(TestRunner);
        var count = 0;
        runner = new TestRunner(new RunOptions
        {
            Spec = SpecPath(), Api = "http://fake.test", Only = "createCustomer", Seed = 1,
            OnResult = _ => { if (++count == 2) cts.Cancel(); },
        }, TextWriter.Null, new OkApi());
        Assert.ThrowsAsync<OperationCanceledException>(async () => await runner.RunAsync(cts.Token));
        Assert.That(runner.Outcomes.Count, Is.EqualTo(2));
    }

    private sealed class OkApi : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{\"id\":1}", Encoding.UTF8, "application/json") });
    }

    private static string SpecPath()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var f = Path.Combine(d.FullName, "samples", "shop.openapi.yaml");
            if (File.Exists(f)) return f;
        }
        throw new FileNotFoundException("samples/shop.openapi.yaml");
    }
}
