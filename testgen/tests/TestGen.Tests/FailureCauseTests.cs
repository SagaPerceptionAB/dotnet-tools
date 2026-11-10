using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using TestGen.Core;
using TestGen.Core.Run;

namespace TestGen.Tests;

public class FailureCauseTests
{
    private static readonly StatusExpectation[] Created = { new(201, 201) };
    private static readonly StatusExpectation[] Rejected = { new(400, 400) };

    private static TestInfo Valid(string id = "T-001") =>
        new(id, "Genererade tester", "POST /orders", "POST", "createOrder", "Skapa order", "giltig indata", Created, CaseMeta.Valid);

    private static TestInfo Negative(string id = "T-002") =>
        new(id, "Genererade tester", "POST /orders", "POST", "createOrder", "Skapa order", "x", Rejected,
            new CaseMeta(TestRule.BelowMinimum, "quantity", "1", "0"));

    private static TestInfo Step(string id = "T-003", string status = "Tester!E2", string expected = "Tester!F2") =>
        new(id, "Excel-tester (t.xlsx)", "POST /customers", "POST", "createCustomer", "Skapa kund", "Flöde, steg 1", Created, null,
            new ExcelStepInfo("Flöde", 1, new[] { "name" }, status, expected));

    private static CaseResult Res(int? status, bool ok = false, string verdict = "FEL", params string[] details) =>
        new(status, ok, false, verdict, details.ToList());

    private static Exchange Ex(int? status, params (string Key, string Value)[] headers) =>
        new("POST", "http://x/orders", headers.ToDictionary(h => h.Key, h => h.Value), null, status, null, 5);

    private static TestOutcome Out(TestInfo info, CaseResult r) =>
        new(1, 1, info, r, PlainLanguage.Explain(info, r), r.Skipped ? "HOPPAD" : r.Ok ? "OK" : "FEL", new List<string>(), null);

    private static TestOutcome Out(string id, FailureCause? cause, CaseResult? r = null)
    {
        var info = Valid(id);
        r ??= cause switch
        {
            null => Res(201, true, "OK"),
            FailureCause.System => Res(500),
            FailureCause.TestData => Res(409),
            FailureCause.Auth => Res(401),
            FailureCause.Environment => new CaseResult(null, false, false, "FEL (timeout)", new()) { TimedOut = true },
            FailureCause.Cascade => new CaseResult(null, false, true, "HOPPAD", new()),
            _ => Res(400),
        };
        return Out(info, r);
    }

    // --- Cause ---------------------------------------------------------------------------

    [Test]
    public void OK_has_no_cause()
    {
        Assert.That(PlainLanguage.Cause(Valid(), Res(201, true, "OK")), Is.Null);
        Assert.That(PlainLanguage.Explain(Valid(), Res(201, true, "OK")).Cause, Is.Null);
    }

    [TestCase(500)]
    [TestCase(501)]
    [TestCase(599)]
    public void Status_5xx_is_system_error(int status)
    {
        Assert.That(PlainLanguage.Cause(Valid(), Res(status)), Is.EqualTo(FailureCause.System));
        Assert.That(PlainLanguage.Cause(Negative(), Res(status)), Is.EqualTo(FailureCause.System));
    }

    [TestCase(502)]
    [TestCase(503)]
    [TestCase(504)]
    public void Status_502_503_504_is_environment_not_system(int status)
    {
        Assert.That(PlainLanguage.Cause(Valid(), Res(status)), Is.EqualTo(FailureCause.Environment));
        Assert.That(PlainLanguage.Cause(Negative(), Res(status)), Is.EqualTo(FailureCause.Environment));
        Assert.That(PlainLanguage.Cause(Valid(), new CaseResult(null, false, false, "FEL", new()) { Setup = new SetupFailure("createCustomer", "Skapa kund", status) }), Is.EqualTo(FailureCause.Environment),
            "ett förberedande anrop som får gateway-fel är också miljön");
        var hint = PlainLanguage.Explain(Valid(), Res(status)).Hint;
        Assert.That(hint, Does.Contain("miljön").And.Not.Contain("fel i systemet, inte i testet"));
    }

    [Test]
    public void Wrong_status_per_spec_is_system_error()
    {
        Assert.That(PlainLanguage.Cause(Negative(), Res(201)), Is.EqualTo(FailureCause.System), "borde sagt nej");
        Assert.That(PlainLanguage.Cause(Negative(), Res(422)), Is.EqualTo(FailureCause.System));
        Assert.That(PlainLanguage.Cause(Valid(), Res(400)), Is.EqualTo(FailureCause.System));
        Assert.That(PlainLanguage.Cause(Valid(), Res(404)), Is.EqualTo(FailureCause.System));
    }

    [Test]
    public void Response_not_matching_spec_is_system_error()
    {
        var r = Res(201, false, "FEL (svaret följer inte specen)", "svar.sku: \"x\" följer inte mönstret ^a$ (enligt specen)");
        Assert.That(PlainLanguage.Cause(Valid(), r), Is.EqualTo(FailureCause.System));
        Assert.That(PlainLanguage.Cause(Step(), r), Is.EqualTo(FailureCause.System), "även i ett Excel-steg");
    }

    [Test]
    public void Status_409_is_test_data()
    {
        Assert.That(PlainLanguage.Cause(Valid(), Res(409)), Is.EqualTo(FailureCause.TestData));
        Assert.That(PlainLanguage.Cause(Step(), Res(409)), Is.EqualTo(FailureCause.TestData));
    }

    [Test]
    public void Expected_409_is_not_a_test_data_error()
    {
        var expects409 = Valid() with { ExpectedStatus = new[] { new StatusExpectation(409, 409) } };
        Assert.That(PlainLanguage.Cause(expects409, Res(409, false, "FEL", "x")), Is.EqualTo(FailureCause.System), "rätt status men fel innehåll");
    }

    [TestCase(401)]
    [TestCase(403)]
    public void Status_401_403_is_auth(int status)
    {
        Assert.That(PlainLanguage.Cause(Valid(), Res(status)), Is.EqualTo(FailureCause.Auth));
        Assert.That(PlainLanguage.Cause(Step(), Res(status)), Is.EqualTo(FailureCause.Auth));
    }

    [Test]
    public void Timeout_and_no_response_is_environment()
    {
        Assert.That(PlainLanguage.Cause(Valid(), new CaseResult(null, false, false, "FEL (timeout)", new()) { TimedOut = true }), Is.EqualTo(FailureCause.Environment));
        Assert.That(PlainLanguage.Cause(Valid(), new CaseResult(null, false, false, "FEL", new())), Is.EqualTo(FailureCause.Environment));
    }

    [Test]
    public void Excel_status_or_response_mismatch_is_expectation()
    {
        Assert.That(PlainLanguage.Cause(Step(), Res(400)), Is.EqualTo(FailureCause.Expectation), "annan status än Excel väntade");
        Assert.That(PlainLanguage.Cause(Step(), Res(201, false, "FEL (svaret stämmer inte)", "svar.name: väntade Fel, fick Anna")), Is.EqualTo(FailureCause.Expectation));
        Assert.That(PlainLanguage.Cause(Step(), new CaseResult(null, false, false, "FEL", new() { "Tester!B2: okänt anrop" })), Is.EqualTo(FailureCause.Expectation), "felet står i Excel-raden");
    }

    [Test]
    public void Skipped_is_cascade()
    {
        Assert.That(PlainLanguage.Cause(Step(), new CaseResult(null, false, true, "HOPPAD", new())), Is.EqualTo(FailureCause.Cascade));
    }

    [Test]
    public void Failed_setup_call_gives_same_cause_as_the_call_itself_otherwise_cascade()
    {
        CaseResult S(int status) => new(null, false, false, "FEL", new()) { Setup = new SetupFailure("createCustomer", "Skapa kund", status) };
        Assert.That(PlainLanguage.Cause(Valid(), S(409)), Is.EqualTo(FailureCause.TestData));
        Assert.That(PlainLanguage.Cause(Valid(), S(401)), Is.EqualTo(FailureCause.Auth));
        Assert.That(PlainLanguage.Cause(Valid(), S(500)), Is.EqualTo(FailureCause.System));
        Assert.That(PlainLanguage.Cause(Valid(), S(400)), Is.EqualTo(FailureCause.Cascade));
        Assert.That(PlainLanguage.Cause(Valid(), S(404)), Is.EqualTo(FailureCause.Cascade));
    }

    [Test]
    public void Explain_sets_cause_on_PlainText_and_TestOutcome()
    {
        var o = Out(Valid(), Res(500));
        Assert.That(o.Text.Cause, Is.EqualTo(FailureCause.System));
        Assert.That(o.Cause, Is.EqualTo(FailureCause.System));
    }

    // --- Namn och förklaring -------------------------------------------------------------

    [Test]
    public void Causes_have_Swedish_names_and_explanations()
    {
        Assert.That(Enum.GetValues<FailureCause>().Select(PlainLanguage.CauseName), Is.EqualTo(new[]
        {
            "Fel i systemet", "Förväntan i testet stämmer inte", "Inloggning", "Miljön svarar inte", "Testdata fanns redan", "Testdata saknas", "Testanvändaren godtogs inte", "För många anrop", "Fel i testgen", "Stöds inte ännu", "Följdfel", "Skrivskyddad miljö",
        }));
        foreach (var c in Enum.GetValues<FailureCause>()) Assert.That(PlainLanguage.CauseExplanation(c), Is.Not.Empty);
        Assert.That(PlainLanguage.CauseExplanation(FailureCause.Expectation), Does.Contain("antingen i systemet eller i Excel-raden"));
        Assert.That(PlainLanguage.CauseExplanation(FailureCause.Auth, AuthVariant.TokenRejected), Does.Contain("Token skickades men nekades"));
        Assert.That(PlainLanguage.CauseExplanation(FailureCause.Auth, AuthVariant.NoToken), Does.Contain("ingen token skickades"));
    }

    // --- Inloggning: token skickades eller inte ------------------------------------------

    [Test]
    public void Token_sent_is_decided_from_headers_of_the_rejected_call()
    {
        var noToken = Res(401) with { Exchanges = new() { Ex(401, ("Accept", "application/json")) } };
        var bearer = Res(401) with { Exchanges = new() { Ex(401, ("Authorization", "Bearer ***")) } };
        var apiKey = Res(401) with { Exchanges = new() { Ex(401, ("X-Api-Key", "***")) } };
        var empty = Res(401);
        Assert.That(PlainLanguage.TokenWasSent(noToken), Is.False);
        Assert.That(PlainLanguage.TokenWasSent(bearer), Is.True);
        Assert.That(PlainLanguage.TokenWasSent(apiKey), Is.True, "hemlig header (maskad) räknas");
        Assert.That(PlainLanguage.TokenWasSent(empty), Is.False);
        Assert.That(PlainLanguage.AuthVariantOf(noToken), Is.EqualTo(AuthVariant.NoToken));
        Assert.That(PlainLanguage.AuthVariantOf(bearer), Is.EqualTo(AuthVariant.TokenRejected));
    }

    [Test]
    public void Rejected_setup_call_is_judged_on_that_call()
    {
        var prep = new Exchange("POST", "http://x/customers", new Dictionary<string, string> { ["Authorization"] = "Bearer ***" }, null, 401, null, 5, "createCustomer");
        var r = new CaseResult(null, false, false, "FEL", new()) { Setup = new SetupFailure("createCustomer", "Skapa kund", 401), Exchanges = new() { prep } };
        Assert.That(PlainLanguage.TokenWasSent(r), Is.True);
    }

    [Test]
    public void Text_for_401_says_token_was_rejected_when_sent()
    {
        var sent = Res(401) with { Exchanges = new() { Ex(401, ("Authorization", "Bearer ***")) } };
        var t = PlainLanguage.Explain(Valid(), sent);
        Assert.That(t.Hint, Does.StartWith("Token skickades men nekades (401) – den kan vara fel eller ha gått ut."));
        Assert.That(t.Hint, Does.Not.Contain("Ange en token"));

        var none = PlainLanguage.Explain(Valid(), Res(401) with { Exchanges = new() { Ex(401) } });
        Assert.That(none.Hint, Does.StartWith("Ingen token skickades.").And.Contain("--token"));

        var forbidden = PlainLanguage.Explain(Valid(), Res(403) with { Exchanges = new() { Ex(403, ("Authorization", "Bearer ***")) } });
        Assert.That(forbidden.Hint, Does.StartWith("Token skickades men nekades (403) – användaren saknar behörighet"));
    }

    [Test]
    public void AuthNote_says_token_was_rejected_when_sent()
    {
        var all401 = Enumerable.Repeat<int?>(401, 10);
        var sent = PlainLanguage.AuthNote(all401, tokenSent: true)!;
        Assert.That(sent, Does.StartWith("Alla tester fick 401. Token skickades men nekades (401) – den kan vara fel eller ha gått ut."));
        Assert.That(sent, Does.Not.Contain("inloggning saknas"));
        Assert.That(PlainLanguage.AuthNote(all401, tokenSent: true, web: true), Does.Contain("fältet Token").And.Not.Contain("--token"));
        Assert.That(PlainLanguage.AuthNote(all401.Take(9).Append(201), tokenSent: true), Does.StartWith("9 av 10 tester fick 401 eller 403. Token skickades men nekades (401)"));
        Assert.That(PlainLanguage.AuthNote(all401), Does.Contain("inloggning saknas eller är ogiltig"), "utan uppgift om token: som förut");
    }

    [Test]
    public void AuthNote_from_results_reads_headers()
    {
        var sent = Enumerable.Repeat(Res(401) with { Exchanges = new() { Ex(401, ("Authorization", "Bearer ***")) } }, 5).ToList();
        var none = Enumerable.Repeat(Res(401) with { Exchanges = new() { Ex(401) } }, 5).ToList();
        Assert.That(PlainLanguage.AuthNote(sent), Does.Contain("Token skickades men nekades (401)"));
        Assert.That(PlainLanguage.AuthNote(none), Does.Contain("inloggning saknas").And.Contain("--token"));
        Assert.That(PlainLanguage.AuthNote(Array.Empty<CaseResult>()), Is.Null);
    }

    // --- Excel-cell i förväntansfel --------------------------------------------------------

    [Test]
    public void Expectation_failure_names_the_Excel_cell()
    {
        var body = PlainLanguage.Explain(Step(), Res(201, false, "FEL (svaret stämmer inte)", "svar.name: väntade Fel, fick Anna"));
        Assert.That(body.Cause, Is.EqualTo(FailureCause.Expectation));
        Assert.That(body.Explanation, Does.Contain("men innehållet i svaret stämmer inte (Excel: Tester!F2):"));
        Assert.That(body.Hint, Does.Contain("fel i systemet").And.Contain("Excel"));

        var status = PlainLanguage.Explain(Step(), Res(400));
        Assert.That(status.Explanation, Does.Contain("Vi väntade oss 201 – skapad (Excel: Tester!E2), men systemet svarade 400"));
        Assert.That(status.Hint, Does.Contain("Excel-raden"));
    }

    [Test]
    public void Cell_not_named_for_other_causes_or_without_cell()
    {
        Assert.That(PlainLanguage.Explain(Step(), Res(500)).Explanation, Does.Not.Contain("Excel:"));
        Assert.That(PlainLanguage.Explain(Step(status: null!, expected: null!), Res(400)).Explanation, Does.Not.Contain("Excel:"));
    }

    // --- Rotorsak i texten -----------------------------------------------------------------

    [Test]
    public void Skipped_step_points_out_the_causing_test()
    {
        var skipped = new CaseResult(null, false, true, "HOPPAD", new()) { RootCauseTestId = "T-033", RootCauseText = "steg 1 'Skapa kund' fick FEL" };
        var t = PlainLanguage.Explain(Step("T-034"), skipped);
        Assert.That(t.Explanation, Does.EndWith("Följd av T-033 (steg 1 'Skapa kund' fick FEL)."));
        Assert.That(t.Cause, Is.EqualTo(FailureCause.Cascade));
        Assert.That(PlainLanguage.Explain(Step("T-034"), skipped with { RootCauseText = null }).Explanation, Does.EndWith("Följd av T-033."));
    }

    [Test]
    public void Setup_failure_with_linked_test_names_it()
    {
        var r = new CaseResult(null, false, false, "FEL", new())
        {
            Setup = new SetupFailure("createCustomer", "Skapa kund", 400),
            RootCauseTestId = "T-003",
            RootCauseText = "samma anrop 'Skapa kund' fick FEL",
        };
        var t = PlainLanguage.Explain(Valid(), r);
        Assert.That(t.Explanation, Does.Contain("först behövde vi köra \"Skapa kund\"").And.EndWith("Följd av T-003 (samma anrop 'Skapa kund' fick FEL)."));
        Assert.That(t.Cause, Is.EqualTo(FailureCause.Cascade));
        var without = PlainLanguage.Explain(Valid(), r with { RootCauseTestId = null, RootCauseText = null });
        Assert.That(without.Explanation, Does.Not.Contain("Följd av"));
    }

    // --- CauseSummary ----------------------------------------------------------------------

    [Test]
    public void CauseSummary_gives_one_line_per_cause_in_fixed_order()
    {
        var outcomes = new[]
        {
            Out("T-001", FailureCause.Cascade), Out("T-002", FailureCause.TestData), Out("T-003", null), Out("T-004", FailureCause.System),
            Out("T-005", FailureCause.Environment), Out("T-006", FailureCause.TestData), Out("T-007", FailureCause.Auth),
        };
        var lines = PlainLanguage.CauseSummary(outcomes);
        Assert.That(lines.Select(l => l.Cause), Is.EqualTo(new[]
            { FailureCause.System, FailureCause.Auth, FailureCause.Environment, FailureCause.TestData, FailureCause.Cascade }));
        var testData = lines.Single(l => l.Cause == FailureCause.TestData);
        Assert.That((testData.Name, testData.Count, testData.TestIds, testData.More), Is.EqualTo(("Testdata fanns redan", 2, new[] { "T-002", "T-006" }, 0)));
        Assert.That(testData.IdsText, Is.EqualTo("T-002, T-006"));
        Assert.That(testData.Sentence, Is.EqualTo("2 tester krockade med befintlig testdata (409) – säger inget om systemet."));
    }

    [Test]
    public void CauseSummary_shows_at_most_10_ids_and_N_more()
    {
        var outcomes = Enumerable.Range(1, 32).Select(i => Out($"T-{i:000}", FailureCause.Environment)).ToList();
        var line = PlainLanguage.CauseSummary(outcomes).Single();
        Assert.That(line.Count, Is.EqualTo(32));
        Assert.That(line.TestIds, Has.Count.EqualTo(10));
        Assert.That(line.More, Is.EqualTo(22));
        Assert.That(line.IdsText, Is.EqualTo("T-001, T-002, T-003, T-004, T-005, T-006, T-007, T-008, T-009, T-010 och 22 till"));
    }

    [Test]
    public void CauseSummary_without_failures_is_empty()
    {
        Assert.That(PlainLanguage.CauseSummary(new[] { Out("T-001", null) }), Is.Empty);
        Assert.That(PlainLanguage.CauseSummary(Array.Empty<TestOutcome>()), Is.Empty);
    }

    [Test]
    public void Auth_line_distinguishes_no_token_from_token_rejected()
    {
        var withToken = Res(401) with { Exchanges = new() { Ex(401, ("Authorization", "Bearer ***")) } };
        var without = Res(401) with { Exchanges = new() { Ex(401) } };
        string S(params CaseResult[] rs) => PlainLanguage.CauseSummary(rs.Select((r, i) => Out($"T-{i + 1:000}", FailureCause.Auth, r))).Single().Sentence;
        Assert.That(S(without, without), Is.EqualTo("2 tester nekades (401/403) – ingen token skickades. Ange en token och kör igen."));
        Assert.That(S(withToken), Is.EqualTo("1 test nekades (401/403) – token skickades men nekades, den kan vara fel eller ha gått ut. Byt token och kör igen."));
        Assert.That(S(withToken, without), Is.EqualTo("2 tester nekades (401/403): 1 utan token och 1 där token nekades. Kontrollera token och kör igen."));
    }

    // --- Verdict ---------------------------------------------------------------------------

    [Test]
    public void Verdict_system_and_test_data()
    {
        var outcomes = new[] { Out("T-017", null), Out("T-018", FailureCause.System), Out("T-019", FailureCause.TestData), Out("T-020", FailureCause.TestData) };
        Assert.That(PlainLanguage.Verdict(outcomes), Is.EqualTo(
            "1 fel i systemet (T-018). Dessutom kunde 2 tester inte köras färdigt (testdata fanns redan)."));
    }

    [Test]
    public void Verdict_environment_without_system_errors()
    {
        var outcomes = Enumerable.Range(1, 32).Select(i => Out($"T-{i:000}", FailureCause.Environment)).ToList();
        Assert.That(PlainLanguage.Verdict(outcomes), Is.EqualTo(
            "Inget säkert svar: 32 tester fick inget svar – miljön svarade inte. Kör igen när den är uppe."));
    }

    [Test]
    public void Verdict_all_passed()
    {
        var outcomes = Enumerable.Range(1, 37).Select(i => Out($"T-{i:000}", null)).ToList();
        Assert.That(PlainLanguage.Verdict(outcomes), Is.EqualTo("Alla 37 tester gick som väntat."));
        Assert.That(PlainLanguage.Verdict(outcomes.Take(1)), Is.EqualTo("Testet gick som väntat."));
        Assert.That(PlainLanguage.Verdict(Array.Empty<TestOutcome>()), Is.EqualTo("Inga tester kördes."));
    }

    [Test]
    public void Verdict_partial_run_starts_with_count()
    {
        var outcomes = Enumerable.Range(1, 11).Select(i => Out($"T-{i:000}", null)).ToList();
        Assert.That(PlainLanguage.Verdict(outcomes, possibleTotal: 32), Is.EqualTo("Delkörning: 11 av 32 möjliga tester kördes. Alla 11 tester gick som väntat."));
        Assert.That(PlainLanguage.Verdict(outcomes, possibleTotal: 11), Is.EqualTo("Alla 11 tester gick som väntat."));
        Assert.That(PlainLanguage.Verdict(outcomes, partial: true), Does.StartWith("Delkörning: bara en del av testerna kördes. "));
        var fail = new[] { Out("T-001", FailureCause.System) };
        Assert.That(PlainLanguage.Verdict(fail, 32), Is.EqualTo("Delkörning: 1 av 32 möjliga tester kördes. 1 fel i systemet (T-001)."));
    }

    [Test]
    public void Verdict_expectation_and_cascade()
    {
        var expectation = new[] { Out(Step("T-005"), Res(400)) };
        var v = PlainLanguage.Verdict(expectation);
        Assert.That(v, Does.StartWith("1 test väntade sig något annat än systemet svarade (T-005)."));
        Assert.That(v, Does.Contain("antingen i systemet eller i Excel-raden").And.Not.Contain("Inget fel i systemet"));

        var onlyFollow = PlainLanguage.Verdict(new[] { Out("T-001", FailureCause.Cascade) });
        Assert.That(onlyFollow, Is.EqualTo("Inget säkert svar: 1 test är följdfel av ett tidigare fel – de säger inget eget om systemet."));
    }

    [Test]
    public void Verdict_inconclusive_for_test_data_auth_and_ok()
    {
        var testData = Enumerable.Range(1, 13).Select(i => Out($"T-{i:000}", FailureCause.TestData)).ToList();
        Assert.That(PlainLanguage.Verdict(testData), Is.EqualTo(
            "Inget säkert svar: 13 tester kunde inte testa systemet eftersom testdata redan fanns (409). Kör igen med nytt slumpfrö."));
        Assert.That(PlainLanguage.Verdict(testData), Does.Not.Contain("Inget fel i systemet"));

        testData.Add(Out("T-100", null));
        testData.Add(Out("T-101", null));
        Assert.That(PlainLanguage.Verdict(testData), Does.EndWith(" 2 tester kördes färdigt utan fel."));

        var auth = Enumerable.Range(1, 32).Select(i => Out($"T-{i:000}", FailureCause.Auth)).ToList();
        Assert.That(PlainLanguage.Verdict(auth), Does.StartWith("Inget säkert svar: 32 tester nekades (401) – ingen token skickades."));
    }

    [Test]
    public void Verdict_system_with_unfinished_tests()
    {
        var outcomes = new[] { Out("T-001", FailureCause.System), Out("T-002", FailureCause.Auth), Out("T-003", FailureCause.Environment), Out("T-004", FailureCause.Cascade) };
        Assert.That(PlainLanguage.Verdict(outcomes), Is.EqualTo(
            "1 fel i systemet (T-001). Dessutom kunde 3 tester inte köras färdigt (inloggningen nekades, miljön svarade inte, ett tidigare test föll)."));
    }

    [Test]
    public void Verdict_order_system_expectation_auth_environment_test_data_cascade()
    {
        var outcomes = new[]
        {
            Out("T-001", FailureCause.Cascade), Out("T-002", FailureCause.TestData), Out("T-003", FailureCause.Environment),
            Out("T-004", FailureCause.Auth), Out(Step("T-005"), Res(400)), Out("T-006", FailureCause.System),
        };
        var v = PlainLanguage.Verdict(outcomes);
        var order = new[] { "fel i systemet", "väntade sig", "inloggningen nekades", "miljön svarade inte", "testdata fanns redan", "ett tidigare test föll" };
        var at = order.Select(o => v.IndexOf(o, StringComparison.Ordinal)).ToList();
        Assert.That(at, Is.Ordered.And.All.GreaterThanOrEqualTo(0));
    }

    // --- EnvironmentNote -------------------------------------------------------------------

    private static CaseResult Timeout() => new(null, false, false, "FEL (timeout)", new()) { TimedOut = true };

    [Test]
    public void EnvironmentNote_when_almost_all_timed_out()
    {
        var all = Enumerable.Range(0, 10).Select(_ => Timeout()).ToList();
        Assert.That(PlainLanguage.EnvironmentNote(all), Is.EqualTo(
            "Alla 10 tester fick inget svar – miljön svarade inte. Kontrollera att systemet är igång och att --api stämmer, eller öka --timeout. Kör sedan igen."));
        var nine = all.Take(9).Append(Res(201, true, "OK")).ToList();
        Assert.That(PlainLanguage.EnvironmentNote(nine), Does.StartWith("9 av 10 tester fick inget svar"));
        Assert.That(PlainLanguage.EnvironmentNote(all.Take(8).Concat(new[] { Res(201, true, "OK"), Res(201, true, "OK") })), Is.Null);
        Assert.That(PlainLanguage.EnvironmentNote(all, web: true), Does.Contain("öka Timeout").And.Not.Contain("--timeout"));
        Assert.That(PlainLanguage.EnvironmentNote(new[] { Timeout() }), Does.StartWith("Testet fick inget svar"));
    }

    [Test]
    public void EnvironmentNote_ignores_skipped_and_failures_with_response()
    {
        var skipped = new CaseResult(null, false, true, "HOPPAD", new());
        Assert.That(PlainLanguage.EnvironmentNote(new[] { Timeout(), skipped, skipped }), Does.StartWith("Testet fick inget svar"));
        Assert.That(PlainLanguage.EnvironmentNote(new[] { Res(500), Res(500) }), Is.Null);
        Assert.That(PlainLanguage.EnvironmentNote(Array.Empty<CaseResult>()), Is.Null);
        Assert.That(PlainLanguage.EnvironmentNote(new[] { skipped }), Is.Null);
    }

    // --- Rubriken: en enda källa -----------------------------------------------------------

    [Test]
    public void RulePhrase_is_the_single_source_of_the_title_phrase()
    {
        var meta = new CaseMeta(TestRule.BelowMinimum, "quantity", "1", "0");
        Assert.That(PlainLanguage.RulePhrase(meta), Is.EqualTo("quantity för lågt (under minimum)"));
        Assert.That(PlainLanguage.TitleOf(Negative()), Is.EqualTo("Skapa order – " + PlainLanguage.RulePhrase(meta)));
    }

    // --- Riktiga körningar mot ett fejk-API ------------------------------------------------

    private static string Spec()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var f = Path.Combine(d.FullName, "samples", "shop.openapi.yaml");
            if (File.Exists(f)) return f;
        }
        throw new FileNotFoundException("samples/shop.openapi.yaml");
    }

    private sealed class Api(Func<HttpRequestMessage, JsonObject?, HttpResponseMessage?> special) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var body = req.Content is null ? null : JsonNode.Parse(await req.Content.ReadAsStringAsync(ct)) as JsonObject;
            if (special(req, body) is { } r) return r;
            if (req.Method == HttpMethod.Post && body is not null) { body["id"] = 7; return Json(HttpStatusCode.Created, body.ToJsonString()); }
            return Json(HttpStatusCode.OK, "{}");
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static async Task<List<TestOutcome>> RunAll(Api api, string? token = null, string? only = null, string? excel = null, string[]? scenarios = null)
    {
        var seen = new List<TestOutcome>();
        await new TestRunner(new RunOptions { Spec = Spec(), Api = "http://fake.test", Token = token, Only = only, Seed = 1, Excel = excel, Source = new ExcelTestSource(), Scenarios = scenarios, OnResult = seen.Add },
            new StringWriter { NewLine = "\n" }, api).RunAsync();
        return seen;
    }

    [Test]
    public async Task Setup_failure_is_linked_to_the_test_running_the_same_call()
    {
        // Kundskapande anrop ger 500 överallt: createCustomer-testet fallerar och order-testerna kan inte förberedas.
        var seen = await RunAll(new Api((req, _) => req.RequestUri!.AbsolutePath == "/customers" ? Json(HttpStatusCode.InternalServerError, "{}") : null));
        var customerValid = seen.First(o => o.Test.OperationId == "createCustomer" && o.Test.Case?.Rule == TestRule.Valid);
        var orderTests = seen.Where(o => o.Test.OperationId == "createOrder" && o.Result.Setup is not null).ToList();
        Assert.That(orderTests, Is.Not.Empty);
        foreach (var o in orderTests)
        {
            Assert.That(o.Result.RootCauseTestId, Is.EqualTo(customerValid.Id));
            Assert.That(o.Text.Explanation, Does.EndWith($"Följd av {customerValid.Id} (samma anrop 'Skapa kund' fick FEL)."));
            Assert.That(o.Cause, Is.EqualTo(FailureCause.System), "förberedande 500 är ett fel i systemet");
        }
        Assert.That(customerValid.Cause, Is.EqualTo(FailureCause.System));
        Assert.That(customerValid.Result.RootCauseTestId, Is.Null);
    }

    [Test]
    public async Task Setup_failure_without_matching_test_is_described_but_not_linked()
    {
        var seen = await RunAll(new Api((req, _) => req.RequestUri!.AbsolutePath == "/customers" ? Json(HttpStatusCode.Conflict, "{}") : null), only: "createOrder");
        var setup = seen.Where(o => o.Result.Setup is not null).ToList();
        Assert.That(setup, Is.Not.Empty);
        foreach (var o in setup)
        {
            Assert.That(o.Result.RootCauseTestId, Is.Null);
            Assert.That(o.Cause, Is.EqualTo(FailureCause.TestData));
            Assert.That(o.Text.Explanation, Does.Contain("först behövde vi köra \"Skapa kund\"").And.Not.Contain("Följd av"));
        }
    }

    [Test]
    public async Task Test_data_conflict_is_test_data_and_auth_depends_on_sent_token()
    {
        var conflict = await RunAll(new Api((req, _) => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath == "/customers" ? Json(HttpStatusCode.Conflict, "{}") : null), only: "createCustomer");
        Assert.That(conflict.Where(o => o.Outcome == "FEL").Select(o => o.Cause).Distinct(), Is.EqualTo(new FailureCause?[] { FailureCause.TestData }));

        Api Denied() => new((_, _) => Json(HttpStatusCode.Unauthorized, "{}"));
        var noToken = await RunAll(Denied(), only: "createCustomer");
        Assert.That(noToken.All(o => o.Cause is FailureCause.Auth or null));
        Assert.That(noToken.Where(o => o.Cause is not null).All(o => PlainLanguage.AuthVariantOf(o.Result) == AuthVariant.NoToken));
        var withToken = await RunAll(Denied(), token: "fel-token", only: "createCustomer");
        var failed = withToken.Where(o => o.Cause is not null).ToList();
        Assert.That(failed, Is.Not.Empty);
        Assert.That(failed.All(o => PlainLanguage.AuthVariantOf(o.Result) == AuthVariant.TokenRejected));
        Assert.That(failed[0].Text.Hint, Does.StartWith("Token skickades men nekades (401)"));
        Assert.That(string.Join(" ", withToken.Select(o => o.Text.Explanation + o.Text.Hint)), Does.Not.Contain("fel-token"));
        Assert.That(PlainLanguage.AuthNote(withToken.Select(o => o.Result)), Does.Contain("Token skickades men nekades (401)"));
    }

    private static string MakeExcel(string dir, string[][] rows)
    {
        var xlsx = Path.Combine(dir, "t.xlsx");
        ExcelTemplate.Create(OpenApiLoader.Load(Spec()), xlsx);
        using var wb = new XLWorkbook(xlsx);
        var ws = wb.Worksheet(ExcelLayout.SheetName);
        foreach (var r in ws.RowsUsed().Skip(1).ToList()) r.Clear();
        for (var i = 0; i < rows.Length; i++)
            for (var c = 0; c < 6; c++) ws.Cell(2 + i, c + 1).Value = rows[i][c];
        wb.Save();
        return xlsx;
    }

    [Test]
    public async Task Excel_with_wrong_expected_name_is_expectation_with_cell_and_next_step_cascade()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var xlsx = MakeExcel(dir, new[]
            {
                new[] { "Flöde", "createCustomer", "name=Anna", "", "201", "name=Fel" },
                new[] { "Flöde", "createCustomer", "name=Bo", "", "201", "name=Bo" },
            });
            var seen = await RunAll(new Api((_, _) => null), excel: xlsx, scenarios: new[] { "Flöde" });
            var steps = seen.Where(o => o.Test.Step is not null).ToList();
            Assert.That(steps[0].Cause, Is.EqualTo(FailureCause.Expectation));
            Assert.That(steps[0].Text.Explanation, Does.Contain("(Excel: Tester!F2)"));
            Assert.That(steps[1].Outcome, Is.EqualTo("HOPPAD"));
            Assert.That(steps[1].Cause, Is.EqualTo(FailureCause.Cascade));
            Assert.That(steps[1].Result.RootCauseTestId, Is.EqualTo(steps[0].Id));
            Assert.That(steps[1].Text.Explanation, Does.EndWith($"Följd av {steps[0].Id} (steg 1 'Skapa kund' fick FEL)."));
            Assert.That(PlainLanguage.Verdict(seen, possibleTotal: 40), Does.StartWith("Delkörning: 2 av 40 möjliga tester kördes. 1 test väntade sig något annat"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public async Task Timeout_gives_environment_for_all_and_note()
    {
        var seen = new List<TestOutcome>();
        var runner = new TestRunner(new RunOptions { Spec = Spec(), Api = "http://fake.test", Only = "createCustomer", Seed = 1, OnResult = seen.Add, MaxConsecutiveTimeouts = 0 },
            new StringWriter { NewLine = "\n" }, new TimeoutApi());
        await runner.RunAsync();
        Assert.That(seen, Is.Not.Empty);
        Assert.That(seen.All(o => o.Cause == FailureCause.Environment));
        Assert.That(PlainLanguage.EnvironmentNote(seen.Select(o => o.Result)), Does.StartWith($"Alla {seen.Count} tester fick inget svar"));
        Assert.That(PlainLanguage.Verdict(seen), Does.StartWith($"Inget säkert svar: {seen.Count} tester fick inget svar"));
    }

    private sealed class TimeoutApi : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) => throw new TaskCanceledException("timeout");
    }
}
