using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using TestGen.Core.Run;

namespace TestGen.Tests;

/// <summary>Svarshuvuden, tidpunkt, korrelations-id, förberedande anrop och skriptet som återskapar ett fel.</summary>
public class ReproTests : IDisposable
{
    private const string Token = "tok-9f8e7d";
    private readonly string _dir = Directory.CreateTempSubdirectory("testgen-repro-").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    private static string Spec()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var f = Path.Combine(d.FullName, "samples", "shop.openapi.yaml");
            if (File.Exists(f)) return f;
        }
        throw new FileNotFoundException("samples/shop.openapi.yaml");
    }

    /// <summary>Skapa ger 201 med id 42; order med quantity 0 ger 500. Svaren har korrelations-id, Set-Cookie och en header som ekar token.</summary>
    private sealed class Api : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var body = req.Content is null ? null : JsonNode.Parse(await req.Content.ReadAsStringAsync(ct)) as JsonObject;
            HttpResponseMessage R(HttpStatusCode c, string json)
            {
                var r = new HttpResponseMessage(c) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
                r.Headers.TryAddWithoutValidation("X-Request-Id", "req-123");
                r.Headers.TryAddWithoutValidation("Set-Cookie", "session=hemlig-kaka; Path=/");
                r.Headers.TryAddWithoutValidation("X-Echo", "Bearer " + Token);
                return r;
            }
            if (req.RequestUri!.AbsolutePath == "/orders" && body?["quantity"]?.GetValue<int>() == 0) return R(HttpStatusCode.InternalServerError, "{\"error\":\"internal error\"}");
            if (body is not null) { body["id"] = 42; return R(HttpStatusCode.Created, body.ToJsonString()); }
            return R(HttpStatusCode.OK, "{}");
        }
    }

    private async Task<TestRunner> RunOrders(string? token = Token)
    {
        var runner = new TestRunner(new RunOptions { Spec = Spec(), Api = "http://fake.test", Token = token, Only = "createOrder", Seed = 1, TimeoutSeconds = 20 },
            new StringWriter(), new Api());
        await runner.RunAsync();
        return runner;
    }

    private static TestOutcome Failing(TestRunner runner) =>
        runner.Outcomes.First(o => o.Test.Call.Contains("quantity under minimum", StringComparison.Ordinal) || o.Result.Exchanges.Any(e => e.Status == 500));

    [Test]
    public async Task Response_headers_timestamp_and_correlation_id_are_saved_without_secrets()
    {
        var o = Failing(await RunOrders());
        var main = ExchangeText.Main(o.Result.Exchanges)!;
        Assert.That(main.CorrelationId, Is.EqualTo("req-123"));
        Assert.That(main.ResponseHeaders!["Set-Cookie"], Is.EqualTo("***"));
        Assert.That(main.ResponseHeaders!["X-Echo"], Does.Not.Contain(Token));
        Assert.That(main.StartedAtText, Does.Match(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}[+-]\d\d:\d\d$"));
        Assert.That(main.TimeoutSeconds, Is.EqualTo(20));
        Assert.That(o.Result.Exchanges.Take(2).All(e => e.Preparation is not null && e.TestId == o.Id));
        var text = string.Join("\n", o.TechnicalLines);
        Assert.That(text, Does.Contain("korrelations-id: req-123").And.Contain("tid: 20").And.Contain("svarshuvuden:"));
        Assert.That(text, Does.Not.Contain("hemlig-kaka").And.Not.Contain(Token));
    }

    [Test]
    public async Task Preparatory_calls_are_shown_with_body_response_and_captured_id()
    {
        var o = Failing(await RunOrders());
        var lines = o.TechnicalLines;
        Assert.That(lines, Does.Contain("förberedande 1: POST /customers → 201, gav id=42 (används som customerId)"));
        Assert.That(lines, Does.Contain("förberedande 2: POST /products → 201, gav id=42 (används som productId)"));
        var text = string.Join("\n", lines);
        Assert.That(text, Does.Contain("använder: createCustomer.id = 42 från förberedande 1, används som customerId"));
        Assert.That(text, Does.Contain("\"id\": 42"));   // svaret på det förberedande anropet
    }

    [Test]
    public async Task Script_has_shell_requirements_and_no_secrets()
    {
        var o = Failing(await RunOrders());
        var script = ExchangeText.ReproScript(o.Result.Exchanges)!;
        Assert.That(script, Does.StartWith("#!/usr/bin/env bash"));
        Assert.That(script, Does.Contain("set -euo pipefail").And.Contain("BASE=${BASE:-http://fake.test}"));
        Assert.That(script, Does.Contain("[ -n \"${TOKEN:-}\" ]").And.Contain("export TOKEN='din token'"));
        Assert.That(script, Does.Contain("Authorization: Bearer ${TOKEN}"));
        Assert.That(script.Split('\n').Count(l => l.Contains("curl -sS")), Is.EqualTo(3));
        Assert.That(script.Split('\n').Where(l => l.Contains("curl -sS")).All(l => l.Contains("--max-time 20")));
        Assert.That(script, Does.Contain("\"customerId\":'\"${createcustomer_id}\"'").Or.Contain("'\"${createcustomer_id}\"'"));
        Assert.That(script, Does.Not.Contain("\"customerId\":42"));
        Assert.That(script, Does.Not.Contain(Token).And.Not.Contain("***").And.Not.Contain("hemlig-kaka"));
        Assert.That(script, Does.Contain("echo \"HTTP $status\""));
    }

    [Test]
    public async Task Without_token_no_variable_is_required()
    {
        var o = Failing(await RunOrders(token: null));
        var script = ExchangeText.ReproScript(o.Result.Exchanges)!;
        Assert.That(script, Does.Not.Contain("TOKEN"));
    }

    [Test]
    public void Ok_test_without_calls_gives_no_script()
    {
        Assert.That(ExchangeText.ReproScript(Array.Empty<Exchange>()), Is.Null);
    }

    [Test]
    public void Curl_has_max_time_and_real_characters_in_body()
    {
        var e = new Exchange("POST", "http://x/c", new Dictionary<string, string>(), "{\"name\":\"Åsa Ödman\"}", 500, "{}", 5, TimeoutSeconds: 45);
        var curl = ExchangeText.Curl(new[] { e })!;
        Assert.That(curl, Does.Contain("--data-raw '{\"name\":\"Åsa Ödman\"}'").And.EndWith("--max-time 45"));
        Assert.That(curl, Does.Not.Contain("\\u00"));
        Assert.That(curl, Does.Not.StartWith("curl: "));
    }

    [Test]
    public void Correlation_id_is_taken_from_common_headers_and_traceparent()
    {
        static string? Id(params (string, string)[] h) => ExchangeCorrelation.Find(h.ToDictionary(x => x.Item1, x => x.Item2));
        Assert.That(Id(("x-request-id", "a1")), Is.EqualTo("a1"));
        Assert.That(Id(("X-Correlation-Id", "b2")), Is.EqualTo("b2"));
        Assert.That(Id(("Request-Id", "c3")), Is.EqualTo("c3"));
        Assert.That(Id(("X-Amzn-Trace-Id", "Root=1-abc")), Is.EqualTo("Root=1-abc"));
        Assert.That(Id(("traceparent", "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01")), Is.EqualTo("4bf92f3577b34da6a3ce929d0e0e4736"));
        Assert.That(Id(("Content-Type", "x")), Is.Null);
    }

    [Test]
    public void Excel_step_shows_value_from_earlier_step_and_script_includes_the_chain()
    {
        var h = new Dictionary<string, string> { ["Content-Type"] = "application/json" };
        var step1 = new Exchange("POST", "http://x/customers", h, "{\"name\":\"Åsa\"}", 201, "{\"Id\":3}", 4, Preparation: "createCustomer",
            TestId: "T-033", Step: 1, EarlierStep: true, SavedAs: new[] { "kund", "createCustomer" }, BaseUrl: "http://x/", TimeoutSeconds: 30);
        var step2 = new Exchange("GET", "http://x/customers/3", h, null, 500, "{}", 4, TestId: "T-034", Step: 2,
            UrlTemplate: "http://x/customers/${kund.id}", Uses: new[] { new ValueUse("kund.id", "3", false, "sökvägen") }, BaseUrl: "http://x/", TimeoutSeconds: 30);
        var all = new[] { step1, step2 };
        var text = string.Join("\n", ExchangeText.Describe(all, full: true));
        Assert.That(text, Does.Contain("steg 1 (T-033): POST /customers → 201, gav id=3 (används som sökvägen)"));
        Assert.That(text, Does.Contain("använder: kund.id = 3 från steg 1 (T-033), används som sökvägen"));
        var script = ExchangeText.ReproScript(all)!;
        Assert.That(script, Does.Contain("# Steg 1 (T-033): POST /customers"));
        Assert.That(script, Does.Contain("kund_id=$(printf '%s' \"$body\" | pick '.Id' 'Id')"), "skiftläge som i svaret");
        Assert.That(script, Does.Contain("\"${BASE}/customers/${kund_id}\""));
        Assert.That(script, Does.Contain("--data-raw '{\"name\":\"Åsa\"}'"));
    }

    // --- Kör skriptet på riktigt mot en liten server ------------------------------------------------

    private static bool OnPath(string tool)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("bash", $"-c \"command -v {tool}\"") { RedirectStandardOutput = true, UseShellExecute = false })!;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    private static (int Exit, string Output) RunBash(string scriptPath, string baseUrl, string? token = null)
    {
        var psi = new ProcessStartInfo("bash", $"\"{scriptPath}\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.Environment["BASE"] = baseUrl;
        if (token is not null) psi.Environment["TOKEN"] = token;
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(30000);
        return (p.ExitCode, output);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task Script_runs_in_bash_against_empty_system_with_and_without_jq(bool withJq)
    {
        if (!OnPath("bash") || !OnPath("curl") || !OnPath("grep") || !OnPath("sed")) Assert.Ignore("bash/curl/grep/sed saknas");
        if (withJq && !OnPath("jq")) Assert.Ignore("jq saknas");
        var o = Failing(await RunOrders());
        var script = ExchangeText.ReproScript(o.Result.Exchanges)!;
        if (!withJq) script = script.Replace("command -v jq >/dev/null 2>&1", "false");   // tvinga grep/sed-varianten
        var path = Path.Combine(_dir, withJq ? "jq.sh" : "grep.sh");
        File.WriteAllText(path, script, new UTF8Encoding(false));

        // Ett tomt "API": /customers och /products ger 201 med id 42, /orders ger 500 om kund- och produkt-id är 42.
        var port = Ports.Free();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        var seen = new List<string>();
        var serve = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext c;
                try { c = await listener.GetContextAsync(); } catch (Exception e) when (e is HttpListenerException or ObjectDisposedException) { return; }
                var req = new StreamReader(c.Request.InputStream, Encoding.UTF8).ReadToEnd();
                seen.Add($"{c.Request.HttpMethod} {c.Request.Url!.AbsolutePath} {req} auth={c.Request.Headers["Authorization"]}");
                var ok = c.Request.Url!.AbsolutePath != "/orders" || (JsonNode.Parse(req)!["customerId"]?.GetValue<int>() == 42 && JsonNode.Parse(req)!["productId"]?.GetValue<int>() == 42);
                var (code, json) = c.Request.Url.AbsolutePath == "/orders" ? (ok ? 500 : 404, ok ? "{\"error\":\"internal error\"}" : "{\"error\":\"kunden finns inte\"}") : (201, "{\"id\":42}");
                var bytes = Encoding.UTF8.GetBytes(json);
                c.Response.StatusCode = code;
                c.Response.ContentType = "application/json";
                c.Response.OutputStream.Write(bytes);
                c.Response.Close();
            }
        });
        try
        {
            var (exit, output) = RunBash(path, $"http://localhost:{port}", Token);
            Assert.That(output, Does.Contain("HTTP 500").And.Contain("internal error"), output);
            Assert.That(exit, Is.EqualTo(0), output);
            Assert.That(seen, Has.Count.EqualTo(3));
            Assert.That(seen.All(s => s.EndsWith("auth=Bearer " + Token)));

            // Utan TOKEN stannar skriptet med en förklaring i stället för att skicka tom token.
            var (exit2, output2) = RunBash(path, $"http://localhost:{port}");
            Assert.That(exit2, Is.Not.EqualTo(0));
            Assert.That(output2, Does.Contain("Sätt TOKEN först: export TOKEN='din token'"));
            Assert.That(seen, Has.Count.EqualTo(3), "inga anrop utan token");
        }
        finally { listener.Stop(); await serve; }
    }
}

internal static class Ports
{
    public static int Free()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
