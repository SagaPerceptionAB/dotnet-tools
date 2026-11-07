using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using NUnit.Framework;
using TestGen.Cli;
using TestGen.Core.Run;

namespace TestGen.Tests;

/// <summary>Körningen: tidsgräns, skydd mot att skriva över indata, stabila id:n, delkörning, flera rapporter och kör-om-kommandot.</summary>
public class RunControlTests
{
    private string _dir = "";

    [SetUp]
    public void SetUp() => _dir = Directory.CreateTempSubdirectory("testgen-kontroll-").FullName;

    [TearDown]
    public void TearDown() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    /// <summary>Svarar 201/200 med en kopia av bodyn de <paramref name="answers"/> första anropen, därefter blir det tidsgräns.</summary>
    private sealed class FlakyApi(int answers) : HttpMessageHandler
    {
        public int Calls;
        public List<string?> Bodies { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref Calls);
            var text = req.Content is null ? null : await req.Content.ReadAsStringAsync(ct);
            lock (Bodies) Bodies.Add(text);
            if (n > answers) throw new TaskCanceledException("timeout", new TimeoutException());
            var body = text is null ? null : JsonNode.Parse(text) as JsonObject;
            if (body is not null) { body["id"] = 7; }
            return new HttpResponseMessage(body is null ? HttpStatusCode.OK : HttpStatusCode.Created)
            { Content = new StringContent(body?.ToJsonString() ?? """{"id":7,"customerId":1,"productId":1,"quantity":1}""", Encoding.UTF8, "application/json") };   // GET /orders/{id}: en order som följer specen
        }
    }


    // --- 1. Tidsgräns ----------------------------------------------------------------------

    [Test]
    public async Task Three_timeouts_in_a_row_without_any_response_give_ApiUnreachable_and_error_report()
    {
        var api = new FlakyApi(0);
        var report = Path.Combine(_dir, "r.html");
        var sw = new StringWriter();
        var runner = new TestRunner(new RunOptions { Spec = ShopSpec.Path, Api = "http://fake.test", Seed = 1, TimeoutSeconds = 2, ReportPath = report }, sw, api);
        var e = Assert.ThrowsAsync<ApiUnreachableException>(() => runner.RunAsync())!;
        Assert.That(api.Calls, Is.EqualTo(3), "inte 34 anrop");
        Assert.That(e.Message, Does.Contain("API:et på http://fake.test svarar inte (inget svar inom 2 s på 3 anrop i rad)"));
        Assert.That(FriendlyErrors.Translate(e).ExitCode, Is.EqualTo(ExitCodes.ApiUnreachable));
        Assert.That(File.ReadAllText(report), Does.Contain("Körningen kunde inte genomföras").And.Contain("svarar inte"));
    }

    [Test]
    public async Task If_API_responds_first_and_then_stops_results_are_kept_with_notice_and_exit_3()
    {
        var api = new FlakyApi(2);
        var report = Path.Combine(_dir, "r.html");
        var sw = new StringWriter();
        var runner = new TestRunner(new RunOptions { Spec = ShopSpec.Path, Api = "http://fake.test", Seed = 1, ReportPath = report }, sw, api);
        var exit = await runner.RunAsync();
        Assert.That(exit, Is.EqualTo(ExitCodes.ApiUnreachable));
        Assert.That(runner.Outcomes, Has.Count.EqualTo(5), "2 med svar + 3 tidsgränser");
        Assert.That(runner.Notice, Does.Contain("avbröts efter 3 anrop i rad").And.Contain("29 av de 34 planerade testerna kördes inte"));
        Assert.That(sw.ToString(), Does.Contain("Obs: Körningen avbröts"));
        var html = WebUtility.HtmlDecode(File.ReadAllText(report));
        Assert.That(html, Does.Contain("Körningen avbröts efter 3 anrop i rad"), "notisen står i rapporten");
        Assert.That(runner.ReportInfo!.Notice, Is.Not.Null);
    }

    [Test]
    public async Task Threshold_can_be_changed_and_turned_off()
    {
        var two = new FlakyApi(0);
        Assert.ThrowsAsync<ApiUnreachableException>(() => new TestRunner(new RunOptions { Spec = ShopSpec.Path, Api = "http://fake.test", Seed = 1, MaxConsecutiveTimeouts = 2 }, TextWriter.Null, two).RunAsync());
        Assert.That(two.Calls, Is.EqualTo(2));
        var off = new FlakyApi(0);
        var exit = await new TestRunner(new RunOptions { Spec = ShopSpec.Path, Api = "http://fake.test", Seed = 1, Only = "createCustomer", MaxConsecutiveTimeouts = 0 }, TextWriter.Null, off).RunAsync();
        Assert.That(exit, Is.EqualTo(ExitCodes.Inconclusive), "bara tidsgränser (miljön) är inget säkert svar om systemet");
        Assert.That(off.Calls, Is.GreaterThan(3), "avstängd: alla tester körs");
    }

    // --- 2. Rapporten får inte skriva över indata ----------------------------------------------

    private static (int Exit, string Out, string Err) Cli(params string[] args)
    {
        var (o, e) = (Console.Out, Console.Error);
        var (so, se) = (new StringWriter(), new StringWriter());
        Console.SetOut(so); Console.SetError(se);
        try { return (Program.Main(args), so.ToString(), se.ToString()); }
        finally { Console.SetOut(o); Console.SetError(e); }
    }

    [Test]
    public void Report_on_same_file_as_excel_gives_exit_2_and_file_is_untouched()
    {
        var x = Path.Combine(_dir, "d2.xlsx");
        File.WriteAllText(x, "tester");
        var (exit, _, err) = Cli("run", "--spec", ShopSpec.Path, "--api", "http://127.0.0.1:1", "--excel", x, "--report", Path.Combine(_dir, ".", "d2.xlsx"));
        Assert.That(exit, Is.EqualTo(2));
        Assert.That(err, Does.Contain("är samma fil som --excel").And.Contain("Välj ett annat namn"));
        Assert.That(File.ReadAllText(x), Is.EqualTo("tester"));
    }

    [Test]
    public void Existing_xlsx_that_is_not_a_report_is_only_overwritten_with_force()
    {
        var x = Path.Combine(_dir, "annan.xlsx");
        using (var wb = new XLWorkbook()) { wb.AddWorksheet("Tester").Cell(1, 1).Value = "min fil"; wb.SaveAs(x); }
        var (exit, _, err) = Cli("run", "--spec", ShopSpec.Path, "--api", "http://127.0.0.1:1", "--only", "getOrder", "--report", x);
        Assert.That(exit, Is.EqualTo(2));
        Assert.That(err, Does.Contain("ser inte ut som en rapport").And.Contain("--force"));
        using (var wb = new XLWorkbook(x)) Assert.That(wb.Worksheets.Contains("Tester"));
        // Med --force skrivs den över (här när API:et inte nås: felrapport).
        var (exit2, _, _) = Cli("run", "--spec", ShopSpec.Path, "--api", "http://127.0.0.1:1", "--only", "getOrder", "--report", x, "--force");
        Assert.That(exit2, Is.EqualTo(3));
        using (var wb = new XLWorkbook(x)) Assert.That(wb.Worksheets.Contains("Sammanfattning"));
        // En riktig rapport får skrivas över utan --force.
        Assert.That(Cli("run", "--spec", ShopSpec.Path, "--api", "http://127.0.0.1:1", "--only", "getOrder", "--report", x).Exit, Is.EqualTo(3));
    }

    // --- 3. Kör om ----------------------------------------------------------------------------

    [Test]
    public void Rerun_includes_all_flags_but_never_secret_values()
    {
        var info = new ReportInfo(new DateTime(2026, 11, 1), "http://localhost:5080", null, 5)
        {
            Spec = "s.yaml", Excel = "t.xlsx", UsesToken = true, TimeoutSeconds = 5, Only = "createOrder", ExcelOnly = true,
            Scenarios = new[] { "Stam kund" }, RerunHeaders = new[] { "X-Api-Key: ${env:X_API_KEY}", "X-Team: qa" },
        };
        var all = ReportWriter.RerunCommand(info)!;
        Assert.That(all, Is.EqualTo("./testgen.sh run --spec s.yaml --api http://localhost:5080 --excel t.xlsx --seed 5 --token '${env:TOKEN}' " +
                                    "--header 'X-Api-Key: ${env:X_API_KEY}' --header 'X-Team: qa' --timeout 5 --bara-excel --scenario 'Stam kund' --only createOrder"));
        Assert.That(ReportWriter.RerunCommand(info, "T-018"), Does.EndWith("--timeout 5 --test T-018").And.Not.Contain("--only").And.Not.Contain("--scenario"));
        var html = WebUtility.HtmlDecode(ReportWriter.Html(info, new List<ReportEntry>
        {
            new("T-018", "G", "POST /orders", "x", new CaseResult(500, false, false, "FEL", new())),
            new("T-020", "G", "POST /orders", "x", new CaseResult(500, false, false, "FEL", new())),
        }, HeaderMask.Default));
        Assert.That(html, Does.Contain("--test T-018 --test T-020").And.Contain("(Windows: testgen.cmd, eller dotnet run --project testgen/src/TestGen.Cli -- …)"));
    }

    [Test]
    public async Task Header_with_secret_name_becomes_env_in_rerun_and_value_never_shows()
    {
        var runner = new TestRunner(new RunOptions
        {
            Spec = ShopSpec.Path, Api = "http://fake.test", Seed = 1, Only = "getOrder",
            Headers = new Dictionary<string, string> { ["X-Api-Key"] = "hemlig123", ["X-Team"] = "qa" },
            HeaderArgs = new[] { "X-Api-Key: hemlig123", "X-Team: qa" }, TimeoutSeconds = 9,
        }, TextWriter.Null, new FlakyApi(100));
        await runner.RunAsync();
        var cmd = ReportWriter.RerunCommand(runner.ReportInfo!)!;
        Assert.That(cmd, Does.Contain("--header 'X-Api-Key: ${env:X_API_KEY}' --header 'X-Team: qa' --timeout 9 --only getOrder").And.Not.Contain("hemlig123"));
    }

    // --- 4/5. Stabila id:n, delkörning -------------------------------------------------------------

    private static async Task<(TestRunner Runner, string Output)> Go(Func<RunOptions, RunOptions> change, FlakyApi? api = null)
    {
        var sw = new StringWriter();
        var runner = new TestRunner(change(new RunOptions { Spec = ShopSpec.Path, Api = "http://fake.test", Seed = 3 }), sw, api ?? new FlakyApi(1000));
        await runner.RunAsync();
        return (runner, sw.ToString());
    }

    private static TestOutcome Of(TestRunner r, string id) => r.Outcomes.Single(o => o.Id == id);
    private static List<string?> Bodies(TestOutcome o) => o.Result.Exchanges.Select(x => x.RequestBody).ToList();

    [Test]
    public async Task Selection_keeps_ids_and_test_data_from_full_run()
    {
        var (full, _) = await Go(o => o);
        var (only, output) = await Go(o => new RunOptions { Spec = o.Spec, Api = o.Api, Seed = o.Seed, Only = "createOrder" });
        var (test, _) = await Go(o => new RunOptions { Spec = o.Spec, Api = o.Api, Seed = o.Seed, Tests = new[] { "T-018", "t-020" } });
        Assert.That(only.Outcomes.Select(o => o.Id), Is.EqualTo(Enumerable.Range(14, 9).Select(i => $"T-{i:000}")));
        Assert.That(Bodies(Of(only, "T-018")), Is.EqualTo(Bodies(Of(full, "T-018"))), "samma förberedande anrop och samma body");
        Assert.That(Bodies(Of(only, "T-018")), Has.Count.EqualTo(3).And.All.Not.Null);
        Assert.That(test.Outcomes.Select(o => o.Id), Is.EqualTo(new[] { "T-018", "T-020" }));
        Assert.That(Bodies(Of(test, "T-018")), Is.EqualTo(Bodies(Of(full, "T-018"))));
        Assert.That(Bodies(Of(test, "T-020")), Is.EqualTo(Bodies(Of(full, "T-020"))));
        Assert.That(output, Does.Contain("Delkörning: bara createOrder (9 av 34 möjliga tester)"));
    }

    [Test]
    public async Task Partial_run_is_exposed_as_data()
    {
        var (full, fullOut) = await Go(o => o);
        Assert.That((full.PossibleTotal, full.Partial, full.FilterDescription), Is.EqualTo((34, false, (string?)null)));
        Assert.That(fullOut, Does.Not.Contain("Delkörning"));
        var (part, _) = await Go(o => new RunOptions { Spec = o.Spec, Api = o.Api, Seed = o.Seed, Only = "createProduct" });
        Assert.That((part.PossibleTotal, part.Partial, part.FilterDescription), Is.EqualTo((34, true, "bara createProduct")));
        Assert.That(part.ReportInfo, Has.Property("PossibleTotal").EqualTo(34).And.Property("Partial").True.And.Property("FilterDescription").EqualTo("bara createProduct"));
        Assert.That(part.Outcomes, Has.Count.EqualTo(11));
        Assert.That(part.Outcomes.First().Total, Is.EqualTo(11));
        var (one, _) = await Go(o => new RunOptions { Spec = o.Spec, Api = o.Api, Seed = o.Seed, Tests = new[] { "T-018" } });
        Assert.That(one.FilterDescription, Is.EqualTo("T-018"));
    }

    [Test]
    public void Unknown_test_id_gives_a_Swedish_error()
    {
        var e = Assert.ThrowsAsync<UsageException>(() => Go(o => new RunOptions { Spec = o.Spec, Api = o.Api, Seed = o.Seed, Tests = new[] { "T-099" } }))!;
        Assert.That(e.Message, Does.Contain("det finns inget test T-099").And.Contain("T-034"));
        Assert.That(Assert.ThrowsAsync<UsageException>(() => Go(o => new RunOptions { Spec = o.Spec, Api = o.Api, Seed = o.Seed, Tests = new[] { "abc" } }))!.Message, Does.Contain("inget test-id"));
    }

    // --- 6. Huvud och sammanfattning ------------------------------------------------------------

    [Test]
    public async Task Header_and_summary_line_have_all_fields()
    {
        var (runner, output) = await Go(o => new RunOptions { Spec = o.Spec, Api = o.Api, Seed = o.Seed, Only = "getOrder", TimeoutSeconds = 12 });
        Assert.That(output, Does.Contain("Kör 1 tester mot http://fake.test (seed 3)")
            .And.Match(@"(?m)^Tid: \d{4}-\d\d-\d\d \d\d:\d\d \(UTC[+-]\d\d:\d\d\)$")
            .And.Contain("Spec: ").And.Contain("Timeout: 12 s per anrop").And.Match(@"(?m)^testgen: version \S+$"));
        Assert.That(output, Does.Match(@"(?m)^Sammanfattning: 1 tester, 1 OK, 0 FEL, 0 HOPPAD, tid \d+,\d s$"));
        Assert.That(runner.Duration, Is.Not.Null);
        Assert.That(runner.ReportInfo!.Duration, Is.EqualTo(runner.Duration));
        Assert.That(runner.ReportInfo.Version, Is.EqualTo(TestRunner.Version));
    }

    [Test]
    public void Excel_is_marked_with_duration_and_format_for_the_excel_row()
    {
        Assert.That(ReportWriter.FormatDuration(TimeSpan.FromSeconds(2.14)), Is.EqualTo("2,1 s"));
        Assert.That(ReportWriter.FormatDuration(TimeSpan.Zero), Is.EqualTo("0,0 s"));
    }

    // --- 7/8. Flera rapporter, slumpat frö ---------------------------------------------------------

    [Test]
    public async Task Multiple_reports_are_all_written()
    {
        var html = Path.Combine(_dir, "r.html");
        var xlsx = Path.Combine(_dir, "r.xlsx");
        var (_, output) = await Go(o => new RunOptions { Spec = o.Spec, Api = o.Api, Seed = o.Seed, Only = "getOrder", ReportPaths = new[] { html, xlsx } });
        Assert.That(File.Exists(html) && File.Exists(xlsx));
        Assert.That(output, Does.Contain("Rapport skriven: " + html).And.Contain("Rapport skriven: " + xlsx));
        using var wb = new XLWorkbook(xlsx);
        var all = string.Join("|", wb.Worksheet("Sammanfattning").CellsUsed().Select(c => c.GetString()));
        Assert.That(all, Does.Contain("Delkörning").And.Contain("bara getOrder (1 av 34 möjliga tester)").And.Contain("Tid"));
    }

    [Test]
    public void Multiple_report_flags_on_the_command_line()
    {
        var html = Path.Combine(_dir, "k.html");
        var xlsx = Path.Combine(_dir, "k.xlsx");
        var (exit, _, _) = Cli("run", "--spec", ShopSpec.Path, "--api", "http://127.0.0.1:1", "--only", "getOrder", "--report", html, "--report", xlsx);
        Assert.That(exit, Is.EqualTo(3));
        Assert.That(File.Exists(html) && File.Exists(xlsx), "felrapport skrivs i båda");
    }

    [Test]
    public async Task Random_seed_picks_a_seed_and_prints_it()
    {
        var (runner, output) = await Go(o => new RunOptions { Spec = o.Spec, Api = o.Api, Only = "getOrder", SeedRandom = true });
        Assert.That(runner.Seed, Is.GreaterThan(0));
        Assert.That(output, Does.Contain($"(seed {runner.Seed})").And.Contain($"Upprepa körningen med --seed {runner.Seed}"));
        Assert.That(runner.ReportInfo!.SeedRandom);
        Assert.That(ReportWriter.RerunCommand(runner.ReportInfo)!, Does.Contain($"--seed {runner.Seed}"));
    }

    [Test]
    public void Seed_slumpa_and_random_are_accepted_on_the_command_line_but_not_other_text()
    {
        var (exit, _, err) = Cli("run", "--spec", ShopSpec.Path, "--api", "http://127.0.0.1:1", "--seed", "hej");
        Assert.That(exit, Is.EqualTo(2));
        Assert.That(err, Does.Contain("heltal, ordet slumpa"));
        var (e2, o2, _) = Cli("run", "--spec", ShopSpec.Path, "--api", "http://127.0.0.1:1", "--only", "getOrder", "--seed", "random");
        Assert.That(e2, Is.EqualTo(3));
        Assert.That(o2, Does.Contain("Upprepa körningen med --seed").Or.Contain("Kör 1 tester"));
    }

    [Test]
    public void Help_text_describes_the_new_flags()
    {
        var help = Cli("run", "--help").Out;
        Assert.That(help, Does.Contain("--test <T-018>").And.Contain("slumpa").And.Contain("--force").And.Contain("--report r.html --report r.xlsx").And.Contain("3 anrop i rad"));
    }
}
