using System.Net;
using System.Text;
using ClosedXML.Excel;
using TestGen.Core;
using TestGen.Core.Run;

namespace TestGen.Tests;

/// <summary>Orsakerna visas likadant i terminal, HTML och Excel; kortare utskrift vid massfel; egna hemliga headers maskas.</summary>
public class CauseDisplayTests
{
    private readonly string _dir = Directory.CreateTempSubdirectory("testgen-orsak-").FullName;

    [TearDown]
    public void Clean() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private sealed class Always(HttpStatusCode code) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
    }

    /// <summary>500 för skapa kund, 201 för resten: ett fel i systemet.</summary>
    private sealed class OneBroken : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) =>
            Task.FromResult(req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath == "/customers"
                ? new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent("{}") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
    }

    private async Task<(int Exit, string Out)> Run(HttpMessageHandler h, bool verbose = false, string? report = null, string? only = null, bool plainText = false)
    {
        var sw = new StringWriter { NewLine = "\n" };
        var exit = await new TestRunner(new RunOptions { Spec = ShopSpec.Path, Api = "http://fake.test", Seed = 1, Only = only, Verbose = verbose, ReportPath = report, PlainText = plainText },
            sw, h).RunAsync();
        return (exit, sw.ToString());
    }

    // --- Terminal --------------------------------------------------------------------------

    [Test]
    public async Task Terminal_prints_verdict_cause_lines_and_auth_note_after_summary_line()
    {
        var (exit, o) = await Run(new Always(HttpStatusCode.Unauthorized));
        Assert.That(exit, Is.EqualTo(ExitCodes.Inconclusive));
        var lines = o.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var at = lines.FindIndex(l => l.StartsWith("Sammanfattning: "));
        Assert.That(at, Is.GreaterThan(0));
        Assert.That(lines[at + 1], Does.StartWith("Inget säkert svar: "));
        Assert.That(lines[at + 2], Does.StartWith("  Inloggning (401/403): T-001, T-002").And.Contain("och ").And.EndWith("säger inget om funktionen som testas"));
        Assert.That(lines[at + 3], Does.StartWith("Alla tester fick 401"));
        Assert.That(o, Does.Not.Contain("Tips: 409"));
    }

    [Test]
    public async Task Terminal_shows_cause_on_each_failure_line_and_follows_from()
    {
        var (_, o) = await Run(new OneBroken(), only: "createOrder");
        var row = o.Split('\n').First(l => l.StartsWith("T-") && l.Contains("FEL"));
        Assert.That(row, Does.Contain("– Orsak: Testdata fanns redan"));
        Assert.That(o, Does.Contain("Testdata fanns redan (409): T-").And.Contain("säger inget om systemet"));
        Assert.That(o, Does.Contain("Tips: kör igen med --seed random"));
    }

    [Test]
    public async Task Mass_failures_with_same_cause_and_status_shortened_after_three_but_verbose_shows_all()
    {
        var (_, compact) = await Run(new Always(HttpStatusCode.Unauthorized));
        var (_, full) = await Run(new Always(HttpStatusCode.Unauthorized), verbose: true);
        var failures = compact.Split('\n').Count(l => l.StartsWith("T-") && l.Contains("FEL"));
        Assert.That(failures, Is.GreaterThan(20));
        Assert.That(compact.Split('\n').Count(l => l.TrimStart().StartsWith("curl för anropet")), Is.EqualTo(3), "full detalj för de tre första");
        Assert.That(compact, Does.Contain($"… {failures - 3} till med samma orsak (401). Se rapporten eller kör med --verbose för alla detaljer."));
        Assert.That(compact.Split('\n').Length, Is.LessThan(full.Split('\n').Length / 3));
        Assert.That(full, Does.Not.Contain("till med samma orsak"));
        Assert.That(full.Split('\n').Count(l => l.TrimStart().StartsWith("curl för anropet")), Is.EqualTo(failures));
    }

    [Test]
    public async Task Four_or_five_identical_failures_get_full_detail_after_the_list()
    {
        // Bara getOrder: några få tester som alla får 401 men aldrig fler än fem.
        var (_, o) = await Run(new Always(HttpStatusCode.Unauthorized), only: "getOrder");
        var failures = o.Split('\n').Count(l => l.StartsWith("T-") && l.Contains("FEL"));
        Assert.That(failures, Is.LessThanOrEqualTo(5));
        Assert.That(o, Does.Not.Contain("till med samma orsak"));
        Assert.That(o.Split('\n').Count(l => l.TrimStart().StartsWith("curl för anropet")), Is.EqualTo(failures), "alla detaljer finns med");
    }

    // --- Rapporter --------------------------------------------------------------------------

    [Test]
    public async Task Html_has_verdict_sentence_first_cause_lines_with_links_and_cause_per_test()
    {
        var report = Path.Combine(_dir, "r.html");
        await Run(new OneBroken(), report: report, only: "createCustomer");
        var html = WebUtility.HtmlDecode(File.ReadAllText(report));
        Assert.That(html, Does.Match("<title>Testrapport – inget säkert svar \\(testdata\\) – \\d{4}-\\d\\d-\\d\\d</title>"));
        var verdictAt = html.IndexOf("<p class=\"plain\">", StringComparison.Ordinal);
        Assert.That(html.Substring(verdictAt, 120), Does.Contain("Inget säkert svar: "));
        Assert.That(html, Does.Contain("<li class=\"cause-test-data\"><strong>Testdata fanns redan (409):</strong> <a href=\"#T-001\">T-001</a>"));
        Assert.That(html, Does.Contain("<p class=\"cause\"><strong>Orsak: Testdata fanns redan</strong></p>"));
        Assert.That(html, Does.Contain("<th>Orsak</th>"));
    }

    [Test]
    public async Task Excel_starts_with_verdict_and_cause_lines_has_Cause_and_Follows_from_columns_and_rerun_last()
    {
        var report = Path.Combine(_dir, "r.xlsx");
        await Run(new OneBroken(), report: report, only: "createCustomer");
        using var wb = new XLWorkbook(report);
        var sum = wb.Worksheet("Sammanfattning");
        Assert.That(sum.Cell(1, 1).GetString(), Does.StartWith("Inget säkert svar: "));
        Assert.That(sum.Cell(3, 1).GetString(), Is.EqualTo("Testdata fanns redan (409)"));
        Assert.That(sum.Cell(3, 2).GetString(), Does.StartWith("T-001").And.EndWith("säger inget om systemet"));
        var lastRow = sum.LastRowUsed()!.RowNumber();
        var rerunRow = sum.CellsUsed().First(c => c.Address.ColumnNumber == 1 && c.GetString() == "Kör om allt").Address.RowNumber;
        var failList = sum.CellsUsed().First(c => c.Address.ColumnNumber == 1 && c.GetString() == "Tester med FEL").Address.RowNumber;
        Assert.That(rerunRow, Is.GreaterThan(failList), "kommandona sist");
        Assert.That(rerunRow, Is.LessThanOrEqualTo(lastRow));
        var ws = wb.Worksheet("Resultat");
        Assert.That(ws.Cell(1, 9).GetString(), Is.EqualTo("Orsak"));
        Assert.That(ws.Cell(1, 10).GetString(), Is.EqualTo("Följd av"));
        Assert.That(ws.Cell(2, 9).GetString(), Is.EqualTo("Testdata fanns redan"));
    }

    // --- PlainLanguage -----------------------------------------------------------------------

    [Test]
    public void Labels_and_short_verdicts_come_from_PlainLanguage()
    {
        Assert.That(PlainLanguage.CauseLabel(FailureCause.TestData), Is.EqualTo("Orsak: Testdata fanns redan"));
        Assert.That(PlainLanguage.CauseLabel(null), Is.Null);
        var skipped = new CaseResult(null, false, true, "HOPPAD", new()) { RootCauseTestId = "T-001" };
        Assert.That(PlainLanguage.FollowLabel(skipped), Is.EqualTo("Följd av T-001"));
        Assert.That(PlainLanguage.CauseLabel(FailureCause.Cascade, skipped), Is.EqualTo("Orsak: Följdfel. Följd av T-001"));

        var line = new CauseLine(FailureCause.TestData, "Testdata fanns redan", 2, new[] { "T-001", "T-012" }, 0, "x");
        Assert.That(line.ListText, Is.EqualTo("Testdata fanns redan (409): T-001, T-012 – säger inget om systemet"));
        Assert.That(new CauseLine(FailureCause.System, "Fel i systemet", 1, new[] { "T-018" }, 0, "x").ListText, Is.EqualTo("Fel i systemet: T-018"));

        TestOutcome Out(string id, int status)
        {
            var info = TestInfo.Basic(id, "g", "POST /x", "d") with { ExpectedStatus = new[] { new StatusExpectation(201, 201) } };
            var r = new CaseResult(status, false, false, "FEL", new());
            return new TestOutcome(1, 2, info, r, PlainLanguage.Explain(info, r), "FEL", new List<string>(), null);
        }
        Assert.That(PlainLanguage.VerdictShort(new[] { Out("T-018", 500), Out("T-001", 409), Out("T-012", 409) }),
            Is.EqualTo("1 fel i systemet, 2 kunde inte köras färdigt"));
        Assert.That(PlainLanguage.VerdictShort(new[] { Out("T-001", 409), Out("T-012", 409) }), Is.EqualTo("inget säkert svar (testdata)"));
        Assert.That(PlainLanguage.VerdictShort(Array.Empty<TestOutcome>()), Is.EqualTo("inga tester kördes"));
    }

    // --- Hemliga headers ---------------------------------------------------------------------

    [TestCase("X-Api-Key")]
    [TestCase("x-auth-token")]
    [TestCase("X-Client-Secret")]
    [TestCase("Proxy-Authorization")]
    [TestCase("X-Passphrase")]
    [TestCase("Set-Cookie")]
    [TestCase("X-Signature")]
    [TestCase("X-Credentials")]
    [TestCase("X-Session-Id")]
    public void Header_with_secret_name_is_masked_without_naming_it(string name)
    {
        Assert.That(Secrets.LooksSecret(name));
        var h = new Dictionary<string, string> { [name] = "hemlig123", ["X-Env"] = "test" };
        var mask = HeaderMask.For(h);
        Assert.That(mask.Format(h), Does.Not.Contain("hemlig123").And.Contain("X-Env: test"));
        Assert.That(mask.Scrub("svaret ekar hemlig123"), Is.EqualTo("svaret ekar ***"));
        Assert.That(CurlCommand.Build(new Exchange("GET", "http://x/", mask.Mask(h), null, 200, "{}", 1)),
            Does.Not.Contain("hemlig123"));
    }

    [Test]
    public void Common_headers_are_not_secret_and_curl_gets_variable_for_custom_key()
    {
        foreach (var n in new[] { "Accept", "Content-Type", "X-Env", "X-Correlation-Id", "User-Agent" }) Assert.That(Secrets.LooksSecret(n), Is.False, n);
        var h = new Dictionary<string, string> { ["X-Api-Key"] = "hemlig123" };
        var curl = CurlCommand.Build(new Exchange("GET", "http://x/", HeaderMask.For(h).Mask(h), null, 200, "{}", 1));
        Assert.That(curl, Does.Contain("\"X-Api-Key: $X_API_KEY\""));
    }

    [Test]
    public async Task Custom_key_via_header_never_shows_in_terminal_or_report()
    {
        var report = Path.Combine(_dir, "k.html");
        var sw = new StringWriter { NewLine = "\n" };
        await new TestRunner(new RunOptions
        {
            Spec = ShopSpec.Path,
            Api = "http://fake.test",
            Seed = 1,
            Only = "getOrder",
            Verbose = true,
            ReportPath = report,
            Headers = new Dictionary<string, string> { ["X-Api-Key"] = "hemlig123", ["X-Env"] = "test" },
            HeaderArgs = new[] { "X-Api-Key: hemlig123", "X-Env: test" },
        }, sw, new Always(HttpStatusCode.InternalServerError)).RunAsync();
        Assert.That(sw.ToString(), Does.Contain("X-Api-Key: ***").And.Not.Contain("hemlig123"));
        Assert.That(File.ReadAllText(report), Does.Not.Contain("hemlig123"));
        Assert.That(sw.ToString(), Does.Contain("X_API_KEY"));
    }
}
