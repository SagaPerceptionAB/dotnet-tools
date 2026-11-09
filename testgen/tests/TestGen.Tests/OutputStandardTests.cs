using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TestGen.Cli;
using TestGen.Core;
using TestGen.Core.Run;

namespace TestGen.Tests;

/// <summary>Utdatastandarden (plan, avsnitt 4) för testgen: strömmar, exitkoder, hjälptexter, JSON-kontraktet och namnen.</summary>
public class OutputStandardTests
{
    private string _dir = "";

    [SetUp] public void Up() => _dir = Directory.CreateTempSubdirectory("testgen-output-").FullName;
    [TearDown] public void Down() => Directory.Delete(_dir, true);

    private sealed class Always(HttpStatusCode code) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
    }

    private static (int Exit, string Out, string Err) Cli(params string[] args)
    {
        var (o, e) = (Console.Out, Console.Error);
        var (so, se) = (new StringWriter { NewLine = "\n" }, new StringWriter { NewLine = "\n" });
        Console.SetOut(so); Console.SetError(se);
        try { return (Program.Main(args), so.ToString(), se.ToString()); }
        finally { Console.SetOut(o); Console.SetError(e); }
    }

    private static async Task<(int Exit, string Out, string Err)> Run(RunOptions options, bool interactive = false)
    {
        var (so, se) = (new StringWriter { NewLine = "\n" }, new StringWriter { NewLine = "\n" });
        var exit = await new TestRunner(options, new Terminal(so, se, interactive), new Always(HttpStatusCode.Created)).RunAsync();
        return (exit, so.ToString(), se.ToString());
    }

    private static RunOptions Options(string only = "createOrder", string[]? reports = null, string? excel = null) => new()
    {
        Spec = ShopSpec.Path,
        Api = "http://fake.test",
        Seed = 1,
        Only = only,
        ReportPaths = reports ?? [],
        Excel = excel,
        Source = new ExcelTestSource(),
    };

    // --- Strömmar ----------------------------------------------------------------------------

    [Test]
    public async Task Results_and_summary_go_to_stdout_and_header_and_confirmations_to_stderr()
    {
        var html = Path.Combine(_dir, "r.html");
        var (_, o, e) = await Run(Options(reports: [html]));

        Assert.That(o, Does.Contain("T-0").And.Contain("Sammanfattning:").And.Contain("tester"));
        Assert.That(o, Does.Not.Contain(" tester mot ").And.Not.Contain("Skrev ").And.Not.Contain("spec "));
        Assert.That(e, Does.Match(@"^testgen \S+ – \d+ tester mot http://fake\.test \(spec \S+, seed 1\)
").And.Not.Contain("Timeout").And.Contain($"Skrev {Terminal.Relative(html)} (HTML-rapport)"));
        Assert.That(e, Does.Not.Contain("Sammanfattning:"));
    }

    [Test]
    public async Task Progress_is_shown_on_stderr_only_in_a_terminal()
    {
        var (_, quietOut, quietErr) = await Run(Options());
        Assert.That(quietErr, Does.Not.Match(@"\[\s*\d+/\d+\]"));

        var (_, liveOut, liveErr) = await Run(Options(), interactive: true);
        Assert.That(liveErr, Does.Match(@"\[ ?\d+/\d+\] T-0\d\d"));
        Assert.That(liveOut, Does.Not.Match(@"\[ ?\d+/\d+\]"));
        Assert.That(quietOut, Is.EqualTo(liveOut), "stdout är densamma med och utan terminal");
    }

    [Test]
    public async Task Warnings_go_to_stderr_with_the_prefix()
    {
        var excel = Path.Combine(_dir, "w.xlsx");
        using (var wb = new ClosedXML.Excel.XLWorkbook())
        {
            var ws = wb.AddWorksheet("Tester");
            for (var i = 0; i < ExcelLayout.Headers.Length; i++) ws.Cell(1, i + 1).Value = ExcelLayout.Headers[i];
            ws.Cell(1, ExcelLayout.Headers.Length + 1).Value = "Kommentar";
            wb.SaveAs(excel);
        }
        var (_, o, e) = await Run(Options(excel: excel));
        Assert.That(e, Does.Contain("Varning: Tester!G1: rubriken 'Kommentar' känns inte igen"));
        Assert.That(o, Does.Not.Contain("Varning"));
    }

    [Test]
    public void Cli_confirmations_and_errors_go_to_stderr_and_help_to_stdout()
    {
        var xlsx = Path.Combine(_dir, "t.xlsx");
        var (exit, o, e) = Cli("template", "--spec", ShopSpec.Path, "--out", xlsx);
        Assert.That(exit, Is.EqualTo(0));
        Assert.That(o, Is.Empty);
        Assert.That(e, Does.Contain($"Skrev {Terminal.Relative(xlsx)} (Excel-mall med "));

        var (exit2, o2, e2) = Cli("template", "--spec", ShopSpec.Path, "--out", xlsx);
        Assert.That(exit2, Is.EqualTo(2));
        Assert.That(o2, Is.Empty);
        Assert.That(e2, Does.StartWith("Kunde inte köra:"));
    }

    // --- Hjälp --------------------------------------------------------------------------------

    private static readonly string[] Headings = ["Användning:", "Beskrivning:", "Kommandon:", "Flaggor:", "Miljövariabler:", "Exempel:", "Exitkoder:"];

    [TestCase("init")]
    [TestCase("run")]
    [TestCase("template")]
    [TestCase("web")]
    [TestCase("map")]
    [TestCase("mock")]
    [TestCase("views")]
    public void Help_has_the_sections_in_the_fixed_order_and_help_command_gives_the_same(string command)
    {
        var (exit, o, e) = Cli(command, "--help");
        Assert.That(exit, Is.EqualTo(0));
        Assert.That(e, Is.Empty);
        var positions = Headings.Select(h => o.IndexOf("\n" + h, StringComparison.Ordinal) is var p and >= 0 ? p : o.StartsWith(h, StringComparison.Ordinal) ? 0 : -1).Where(p => p >= 0).ToList();
        Assert.That(positions, Is.Ordered);
        Assert.That(o, Does.StartWith("Användning:").And.Contain("Exitkoder:"));
        Assert.That(Cli("help", command).Out, Is.EqualTo(o), "testgen help <kommando> är samma som --help");
    }

    [Test]
    public void Help_flag_and_help_command_print_the_overview_on_stdout_with_exit_0()
    {
        var (exit, o, e) = Cli("--help");
        Assert.That((exit, e), Is.EqualTo((0, "")));
        Assert.That(o, Is.EqualTo(HelpText.General));
        Assert.That(Cli("help").Out, Is.EqualTo(o));
        Assert.That(o, Does.Contain("  web ").And.Not.Contain("testgen karta").And.Not.Contain("  ui "));
        Assert.That(o, Does.Contain("testgen utan argument visar"), "hjälpen nämner statusvyn");
    }

    [Test]
    public void Help_lists_one_line_per_exit_code()
    {
        var help = HelpText.For("run")!;
        foreach (var code in new[] { 0, 1, 2, 3, 4, 70, 130, 143 })
            Assert.That(help.Split('\n').Count(l => l.TrimStart().StartsWith(code + " ") && l.StartsWith("  ")), Is.EqualTo(1), "kod " + code);
    }

    // --- Nya namn, inga alias --------------------------------------------------------------------

    [TestCase("--jamfor")]
    [TestCase("--bara-excel")]
    [TestCase("--bara-nya-fel")]
    [TestCase("--klartext")]
    [TestCase("--losning")]
    public void Old_flags_are_gone(string flag)
    {
        var (exit, _, err) = Cli("run", "--spec", ShopSpec.Path, "--api", "http://127.0.0.1:1", flag, "x");
        Assert.That(exit, Is.EqualTo(2));
        Assert.That(err, Does.Contain("Okänd flagga: " + flag));
    }

    [TestCase("karta")]
    [TestCase("ui")]
    public void Old_commands_are_gone(string command)
    {
        var (exit, _, err) = Cli(command);
        Assert.That(exit, Is.EqualTo(2));
        Assert.That(err, Does.Contain("Okänt kommando: " + command));
    }

    [Test]
    public void Web_and_map_take_solution_and_the_swedish_alias_is_gone()
    {
        Assert.That(Cli("map", "--losning", "x.slnx").Err, Does.Contain("Okänd flagga: --losning"));
        Assert.That(Cli("web", "--losning", "x.slnx").Err, Does.Contain("Okänd flagga: --losning"));
    }

    [Test]
    public void Seed_slumpa_is_not_accepted()
    {
        Assert.That(Cli("run", "--spec", ShopSpec.Path, "--api", "http://127.0.0.1:1", "--seed", "slumpa").Err, Does.Contain("heltal, ordet random eller ordet ci"));
    }

    [TestCase("forvantan")]
    [TestCase("inloggning")]
    [TestCase("miljo")]
    [TestCase("miljö")]
    [TestCase("testdata")]
    [TestCase("verktyg")]
    [TestCase("foljd")]
    public void Swedish_cause_codes_are_rejected(string code) =>
        Assert.Throws<UsageException>(() => CauseCodes.ParseFailOn(code));

    [Test]
    public void Cause_codes_are_english_lowercase_with_hyphens()
    {
        Assert.That(CauseCodes.All.Select(CauseCodes.Code), Is.EqualTo(new[] { "system", "expectation", "auth", "environment", "test-data", "missing-data", "test-user-rejected", "rate-limited", "tool", "dependency", "read-only" }));
        foreach (var code in CauseCodes.All.Select(CauseCodes.Code))
            Assert.That(CauseCodes.ParseFailOn(code), Has.Count.EqualTo(1));
    }

    // --- Exitkoder ----------------------------------------------------------------------------

    [Test]
    public void The_shared_exit_codes_are_used()
    {
        Assert.That(FriendlyErrors.Translate(new InvalidCastException("x")).ExitCode, Is.EqualTo(ExitCodes.Internal).And.EqualTo(70));
        Assert.That(FriendlyErrors.Translate(new OperationCanceledException()).ExitCode, Is.EqualTo(ExitCodes.Interrupted).And.EqualTo(130));
        Assert.That(FriendlyErrors.Translate(new UsageException("x")).ExitCode, Is.EqualTo(ExitCodes.Usage));
        Assert.That(FriendlyErrors.Translate(new ApiUnreachableException("http://x", ApiProblem.Refused, null)).ExitCode, Is.EqualTo(ExitCodes.Unreachable));
    }

    // --- JSON-kontraktet ---------------------------------------------------------------------------

    private async Task<JsonObject> JsonOf(string only = "createOrder")
    {
        var path = Path.Combine(_dir, "r.json");
        await Run(Options(only, [path]));
        return (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
    }

    private static IEnumerable<JsonNode?> Walk(JsonNode? n)
    {
        yield return n;
        if (n is JsonObject o) foreach (var (_, c) in o) foreach (var x in Walk(c)) yield return x;
        if (n is JsonArray a) foreach (var c in a) foreach (var x in Walk(c)) yield return x;
    }

    [Test]
    public async Task Json_starts_with_schemaVersion_and_toolVersion_and_follows_the_schema_key_order()
    {
        var json = await JsonOf();
        var schema = (JsonObject)JsonNode.Parse(File.ReadAllText(SchemaPath()))!;
        var keys = json.Select(kv => kv.Key).ToList();
        Assert.That(keys.Take(2), Is.EqualTo(new[] { "schemaVersion", "toolVersion" }));
        var order = schema["properties"]!.AsObject().Select(kv => kv.Key).ToList();
        Assert.That(keys, Is.EqualTo(order.Where(keys.Contains).ToList()), "nycklarna i schemats ordning");

        var testKeys = json["tests"]![0]!.AsObject().Select(kv => kv.Key).ToList();
        var testOrder = schema["properties"]!["tests"]!["items"]!["properties"]!.AsObject().Select(kv => kv.Key).ToList();
        Assert.That(testKeys, Is.EqualTo(testOrder.Where(testKeys.Contains).ToList()));
    }

    [Test]
    public async Task Json_has_no_null_values_and_english_machine_values()
    {
        var json = await JsonOf();
        Assert.That(Walk(json).Count(n => n is null), Is.Zero, "null utelämnas");
        Assert.That(json["totals"]!.AsObject().Select(kv => kv.Key), Is.EqualTo(new[] { "tests", "passed", "failed", "skipped", "warnings" }));
        Assert.That(json["tests"]!.AsArray().Select(t => t!["outcome"]!.GetValue<string>()), Is.All.AnyOf("passed", "failed", "skipped"));
        Assert.That(json["tests"]!.AsArray().Select(t => t!["id"]!.GetValue<string>()), Is.Ordered, "listan sorterad på id");
        Assert.That(json["toolVersion"]!.GetValue<string>(), Is.Not.Empty);
        Assert.That(json.ToJsonString(), Does.Not.Contain("testgenVersion").And.Not.Contain("\"ok\"").And.Not.Contain("\"fel\"").And.Not.Contain("\"hoppad\""));
    }

    [Test]
    public async Task Json_keys_follow_the_api_operation_rule_field_format()
    {
        var json = await JsonOf("createProduct");
        var keys = json["tests"]!.AsArray().Select(t => t!["key"]!.GetValue<string>()).ToList();
        Assert.That(keys, Does.Contain("api:createProduct:missing:name"));
        Assert.That(keys, Does.Contain("api:createProduct:valid"));
        Assert.That(keys, Has.All.StartsWith("api:createProduct:"));
        Assert.That(keys, Is.Unique);
        Assert.That(json["tests"]!.AsArray().Select(t => t!["rule"]!.GetValue<string>()).Distinct(), Is.All.Matches(@"^[a-z]+(-[a-z]+)*$"));
    }

    [Test]
    public void Keys_without_operation_id_use_method_and_normalized_path()
    {
        var info = new TestInfo("T-001", "g", "GET /Products/{id}/", "GET", "", null, "d", [], new CaseMeta(TestRule.Valid, null, null, null));
        Assert.That(TestKeys.BaseOf(info), Is.EqualTo("api:get/products/{id}:valid"));
        var withField = info with { Case = new CaseMeta(TestRule.Missing, "name", null, null) };
        Assert.That(TestKeys.BaseOf(withField), Is.EqualTo("api:get/products/{id}:missing:name"));
    }

    [Test]
    public async Task Json_is_identical_on_a_second_run_apart_from_times()
    {
        string Strip(JsonObject o)
        {
            foreach (var k in new[] { "startedAt", "finishedAt", "durationSeconds" }) o.Remove(k);
            foreach (var t in o["tests"]!.AsArray()) t!.AsObject().Remove("durationMs");
            return o.ToJsonString();
        }
        var first = Strip(await JsonOf());
        var second = Strip(await JsonOf());
        Assert.That(second, Is.EqualTo(first));
    }

    [Test]
    public void The_failure_report_has_no_null_values_either()
    {
        var info = new ReportInfo(new DateTime(2026, 11, 1, 21, 51, 43), "http://x", null, 1) { Version = "1.0.0", Spec = "s.yaml", Duration = TimeSpan.FromSeconds(1) };
        var json = JsonResult.BuildFailure(info, "Kan inte nå API:et", "Starta det", 0, HeaderMask.Default);
        Assert.That(Walk(json).Count(n => n is null), Is.Zero);
        Assert.That(json.Select(kv => kv.Key).First(), Is.EqualTo("schemaVersion"));
        Assert.That(json["totals"]!.ToJsonString(), Is.EqualTo("{\"tests\":0,\"passed\":0,\"failed\":0,\"skipped\":0,\"warnings\":0}"));
    }

    [Test]
    public void Junit_uses_english_codes_and_toolVersion()
    {
        var info = new ReportInfo(new DateTime(2026, 11, 1, 21, 51, 43), "http://x", null, 1) { Version = "1.0.0", Spec = "s.yaml", Duration = TimeSpan.FromSeconds(1) };
        var xml = JUnitResult.TextFailure(info, "Kan inte nå API:et", "Starta det", HeaderMask.Default);
        Assert.That(xml, Does.Contain("name=\"toolVersion\"").And.Contain("type=\"environment\"").And.Not.Contain("testgen.version").And.Not.Contain("miljo"));
    }

    private static string SchemaPath()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var f = Path.Combine(d.FullName, "testgen", "docs", "ci", "testgen-result.schema.json");
            if (File.Exists(f)) return f;
        }
        throw new FileNotFoundException("testgen/docs/ci/testgen-result.schema.json");
    }
}
