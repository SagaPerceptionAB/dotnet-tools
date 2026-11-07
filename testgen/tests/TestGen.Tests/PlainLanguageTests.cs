using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using TestGen.Core;
using TestGen.Core.Run;

namespace TestGen.Tests;

public class PlainLanguageTests
{
    private static readonly StatusExpectation[] Created = { new(201, 201) };
    private static readonly StatusExpectation[] Rejected = { new(400, 400) };

    private static TestInfo Case(CaseMeta meta, string? summary = "Skapa order", string op = "createOrder", string method = "POST") =>
        new("T-001", "Genererade tester", $"{method} /orders", method, op, summary, "x",
            meta.Rule == TestRule.Valid ? Created : Rejected, meta);

    private static CaseResult Res(int? status, bool ok, string verdict = "", params string[] details) =>
        new(status, ok, false, verdict.Length > 0 ? verdict : ok ? "OK" : "FEL", details.ToList());

    // --- Rubriker --------------------------------------------------------------------------

    private static readonly (CaseMeta Meta, string Phrase)[] Rules =
    {
        (CaseMeta.Valid, "giltig indata"),
        (new(TestRule.Missing, "quantity"), "quantity saknas"),
        (new(TestRule.BelowMinimum, "quantity", "1", "0"), "quantity för lågt (under minimum)"),
        (new(TestRule.AboveMaximum, "quantity", "1000", "1001"), "quantity för högt (över maximum)"),
        (new(TestRule.TooLong, "name", "100", "\"" + new string('a', 101) + "\""), "name för lång (över maxlängd)"),
        (new(TestRule.TooShort, "name", "1", "\"\""), "name för kort (under minlängd)"),
        (new(TestRule.NotInEnum, "status", "\"active\", \"inactive\"", "\"x\""), "status utanför tillåtna värden"),
        (new(TestRule.WrongType, "quantity", "integer", "\"not-a-number\""), "quantity har fel typ"),
        (new(TestRule.PatternMismatch, "sku", "^[A-Z]{3}-[0-9]{4}$", "\"!TI-6293\""), "sku följer inte mönstret"),
    };

    [TestCaseSource(nameof(Rules))]
    public void The_title_uses_summary_when_present((CaseMeta Meta, string Phrase) rule)
    {
        var text = PlainLanguage.Explain(Case(rule.Meta), Res(201, true));
        Assert.That(text.Title, Is.EqualTo($"Skapa order – {rule.Phrase}"));
    }

    [TestCaseSource(nameof(Rules))]
    public void The_title_falls_back_to_operationId_without_summary((CaseMeta Meta, string Phrase) rule)
    {
        var text = PlainLanguage.Explain(Case(rule.Meta, summary: null), Res(201, true));
        Assert.That(text.Title, Is.EqualTo($"createOrder – {rule.Phrase}"));
    }

    [Test]
    public void Title_for_the_example_in_the_task()
    {
        var meta = new CaseMeta(TestRule.BelowMinimum, "quantity", "1", "0");
        Assert.That(PlainLanguage.Explain(Case(meta), Res(500, false)).Title, Is.EqualTo("Skapa order – quantity för lågt (under minimum)"));
    }

    // --- Negativa fall: förklaring per regel (OK) -----------------------------------------

    [Test]
    public void Negative_case_per_rule_describes_what_was_sent()
    {
        string Ex(int i) => PlainLanguage.Explain(Case(Rules[i].Meta), Res(400, true)).Explanation;
        Assert.That(Ex(1), Is.EqualTo("Vi skickade inte med quantity, som är obligatoriskt. Systemet sa nej, som det ska."));
        Assert.That(Ex(2), Is.EqualTo("Vi skickade quantity = 0, men minsta tillåtna är 1. Systemet sa nej, som det ska."));
        Assert.That(Ex(3), Is.EqualTo("Vi skickade quantity = 1001, men största tillåtna är 1000. Systemet sa nej, som det ska."));
        Assert.That(Ex(4), Is.EqualTo("Vi skickade name med 101 tecken, men längsta tillåtna är 100 tecken. Systemet sa nej, som det ska."));
        Assert.That(Ex(5), Is.EqualTo("Vi skickade name med 0 tecken, men minst 1 tecken krävs. Systemet sa nej, som det ska."));
        Assert.That(Ex(6), Is.EqualTo("Vi skickade status = \"x\", som inte är ett av de tillåtna värdena (\"active\", \"inactive\"). Systemet sa nej, som det ska."));
        Assert.That(Ex(7), Is.EqualTo("Vi skickade quantity = \"not-a-number\", men fältet ska vara ett heltal. Systemet sa nej, som det ska."));
        Assert.That(Ex(8), Is.EqualTo("Vi skickade sku = \"!TI-6293\", som inte följer formatet ^[A-Z]{3}-[0-9]{4}$. Systemet sa nej, som det ska."));
        Assert.That(PlainLanguage.Explain(Case(Rules[1].Meta), Res(400, true)).Hint, Is.Null);
    }

    [Test]
    public void Exclusive_minimum_and_maximum_are_phrased_as_greater_than_and_less_than()
    {
        var below = new CaseMeta(TestRule.BelowMinimum, "price", "0", "0", Exclusive: true);
        var above = new CaseMeta(TestRule.AboveMaximum, "price", "10", "10", Exclusive: true);
        Assert.That(PlainLanguage.Explain(Case(below), Res(400, true)).Explanation, Does.Contain("måste vara större än 0"));
        Assert.That(PlainLanguage.Explain(Case(above), Res(400, true)).Explanation, Does.Contain("måste vara mindre än 10"));
    }

    [TestCase("integer", "ett heltal")]
    [TestCase("number", "ett tal")]
    [TestCase("boolean", "sant eller falskt")]
    [TestCase("array", "en lista")]
    [TestCase("object", "ett objekt")]
    [TestCase("string", "text")]
    public void Wrong_type_names_the_expected_type(string type, string words)
    {
        var meta = new CaseMeta(TestRule.WrongType, "f", type, "12345");
        Assert.That(PlainLanguage.Explain(Case(meta), Res(400, true)).Explanation, Does.Contain($"ska vara {words}."));
    }

    [Test]
    public void Long_values_are_shortened_to_at_most_60_characters()
    {
        var meta = new CaseMeta(TestRule.PatternMismatch, "sku", "^" + new string('x', 200) + "$", "\"" + new string('y', 300) + "\"");
        var text = PlainLanguage.Explain(Case(meta), Res(400, true)).Explanation;
        Assert.That(text, Does.Contain("\"" + new string('y', 57) + "…\""));
        Assert.That(text, Does.Not.Contain(new string('y', 58)));
        Assert.That(text, Does.Not.Contain(new string('x', 60)));
    }

    // --- Negativa fall: FEL -----------------------------------------------------------------

    [Test]
    public void Negative_case_with_500_is_an_error_in_the_system()
    {
        var t = PlainLanguage.Explain(Case(Rules[2].Meta), Res(500, false, "FEL (väntade 400)"));
        Assert.That(t.Explanation, Is.EqualTo("Vi skickade quantity = 0, men minsta tillåtna är 1. " +
            "Systemet borde ha sagt nej till uppgifterna (400), men svarade 500 – internt fel i systemet."));
        Assert.That(t.Hint, Is.EqualTo("Det tyder på ett fel i systemet, inte i testet."));
    }

    [Test]
    public void Accepted_negative_case_hints_at_a_missing_check()
    {
        var t = PlainLanguage.Explain(Case(Rules[2].Meta), Res(201, false));
        Assert.That(t.Explanation, Does.Contain("borde ha sagt nej till uppgifterna (400), men tog emot dem (201 – skapad)."));
        Assert.That(t.Hint, Does.Contain("saknar systemet en kontroll"));
    }

    [Test]
    public void Negative_case_with_422_says_the_system_refused_with_another_code()
    {
        var t = PlainLanguage.Explain(Case(Rules[2].Meta), Res(422, false));
        Assert.That(t.Explanation, Does.Contain("sa nej, men med 422 – sa nej till uppgifterna i stället för 400"));
    }

    // --- Giltiga fall ----------------------------------------------------------------------

    [Test]
    public void Valid_case_ok()
    {
        var t = PlainLanguage.Explain(Case(CaseMeta.Valid), Res(201, true));
        Assert.That(t.Explanation, Is.EqualTo("Vi skickade giltiga uppgifter. Systemet tog emot dem (201 – skapad)."));
        Assert.That(t.Hint, Is.Null);
    }

    [Test]
    public void Valid_GET_case_is_a_request_not_data()
    {
        var t = PlainLanguage.Explain(Case(CaseMeta.Valid, "Hämta order", "getOrder", "GET"), Res(200, true));
        Assert.That(t.Explanation, Is.EqualTo("Vi skickade en giltig förfrågan. Systemet svarade 200 – OK."));
    }

    [Test]
    public void Rejected_valid_case_is_an_error_in_the_system_or_the_spec()
    {
        var t = PlainLanguage.Explain(Case(CaseMeta.Valid), Res(400, false, "FEL (väntade 201)"));
        Assert.That(t.Explanation, Is.EqualTo("Vi skickade giltiga uppgifter. Systemet sa nej till uppgifter som enligt specen är giltiga (400 – sa nej till uppgifterna)."));
        Assert.That(t.Hint, Does.Contain("fel i systemet").And.Contain("i specen"));
    }

    [Test]
    public void Valid_case_with_another_2xx_and_with_server_error()
    {
        var other = PlainLanguage.Explain(Case(CaseMeta.Valid), Res(200, false));
        Assert.That(other.Explanation, Does.Contain("svarade 200 – OK i stället för 201 – skapad"));
        var server = PlainLanguage.Explain(Case(CaseMeta.Valid), Res(503, false));
        Assert.That(server.Explanation, Does.Contain("Vi väntade oss 201 – skapad, men systemet svarade 503 – systemet är inte tillgängligt."));
        Assert.That(server.Hint, Does.Contain("miljön"), "503 är miljön, inte ett fel i funktionen");
        Assert.That(PlainLanguage.Explain(Case(CaseMeta.Valid), Res(500, false)).Hint, Does.Contain("fel i systemet"));
    }

    [Test]
    public void Valid_case_without_login_hints_at_the_token()
    {
        foreach (var code in new[] { 401, 403 })
        {
            var t = PlainLanguage.Explain(Case(CaseMeta.Valid), Res(code, false));
            Assert.That(t.Hint, Does.Contain("--token").And.Contain("Miljöer"));
        }
    }

    [Test]
    public void Conflict_hints_at_test_data_and_seed()
    {
        var t = PlainLanguage.Explain(Case(CaseMeta.Valid), Res(409, false));
        Assert.That(t.Explanation, Does.Contain("409 – fanns redan / krock"));
        Assert.That(t.Hint, Does.Contain("fanns redan").And.Contain("--seed").And.Contain("Slumpfrö").And.Contain("nollställa"));
    }

    [Test]
    public void GET_returning_404_means_the_created_item_was_not_found()
    {
        var t = PlainLanguage.Explain(Case(CaseMeta.Valid, "Hämta order", "getOrder", "GET"), Res(404, false));
        Assert.That(t.Hint, Is.EqualTo("Det som skapades hittades inte när vi sökte efter det."));
    }

    // --- Svarskontroll ---------------------------------------------------------------------

    [Test]
    public void Response_check_pattern_is_translated()
    {
        var detail = "svar.sku: \"abc\" följer inte mönstret ^[A-Z]{3}-[0-9]{4}$ (enligt specen)";
        var t = PlainLanguage.Explain(Case(CaseMeta.Valid), Res(201, false, "FEL (svaret följer inte specen)", detail));
        Assert.That(t.Explanation, Is.EqualTo("Vi skickade giltiga uppgifter. Systemet svarade 201 – skapad, men svaret följer inte specen. " +
            "Systemet svarade med sku = \"abc\", men specen säger att den ska följa ^[A-Z]{3}-[0-9]{4}$."));
        Assert.That(t.Hint, Does.Contain("specen"));
    }

    [Test]
    public void The_response_check_translates_real_detail_lines_from_the_checks()
    {
        // Radformaten kommer från ResponseSchemaCheck och BodyMatcher; ändras de här måste översättningen följa med.
        var schema = new Microsoft.OpenApi.Models.OpenApiSchema
        {
            Type = "object",
            Properties = { ["sku"] = new() { Type = "string", Pattern = "^[A-Z]{3}$" } },
        };
        var broken = ResponseSchemaCheck.Check(JsonNode.Parse("{\"sku\":\"abc\"}"), schema).Errors.Single();
        Assert.That(PlainLanguage.DetailSentence(broken), Is.EqualTo("Systemet svarade med sku = \"abc\", men specen säger att den ska följa ^[A-Z]{3}$."));

        var body = JsonNode.Parse("{\"quantity\":2,\"ordernr\":\"X1\"}");
        var want = new[] { KeyValuePair.Create<string, JsonNode?>("quantity", JsonValue.Create(5)), KeyValuePair.Create<string, JsonNode?>("kund", JsonValue.Create("a")) };
        var lines = TestGen.Scenarios.BodyMatcher.Mismatches(want, body).Concat(TestGen.Scenarios.BodyMatcher.RegexMismatches(new[]
            { KeyValuePair.Create("ordernr", "^ORD-\\d{6}$"), KeyValuePair.Create("saknas", "x") }, body)).Select(PlainLanguage.DetailSentence).ToList();
        Assert.That(lines, Is.EqualTo(new[]
        {
            "Förväntat quantity = 5, men systemet svarade 2.",
            "Förväntat kund = \"a\", men svaret saknar fältet.",
            "Systemet svarade med ordernr = \"X1\", men det skulle följa ^ORD-\\d{6}$.",
            "Svaret saknar saknas, som skulle följa x.",
        }));
    }

    [Test]
    public void Unknown_detail_line_is_kept_as_is()
    {
        Assert.That(PlainLanguage.DetailSentence("Tester, rad 3: okänt anrop 'x'. Välj ett i listan."), Is.EqualTo("Tester, rad 3: okänt anrop 'x'. Välj ett i listan."));
    }

    // --- Excel-steg ------------------------------------------------------------------------

    private static TestInfo Step(string? summary = "Skapa order", int number = 3, StatusExpectation? expected = null, params string[] fields) =>
        new("T-040", "Excel-tester", "POST /orders", "POST", "createOrder", summary, "Stamkund, steg " + number,
            new[] { expected ?? new StatusExpectation(201, 201) }, null, new ExcelStepInfo("Stamkund", number, fields));

    [Test]
    public void Excel_step_title_uses_summary_or_operationId()
    {
        Assert.That(PlainLanguage.Explain(Step(), Res(201, true)).Title, Is.EqualTo("Stamkund, steg 3: Skapa order"));
        Assert.That(PlainLanguage.Explain(Step(summary: null), Res(201, true)).Title, Is.EqualTo("Stamkund, steg 3: createOrder"));
    }

    [Test]
    public void Excel_step_ok_names_the_expected_response()
    {
        var t = PlainLanguage.Explain(Step(fields: new[] { "quantity", "ordernr" }), Res(201, true));
        Assert.That(t.Explanation, Is.EqualTo("Steget körde \"Skapa order\". Systemet svarade 201 – skapad, som förväntat. " +
            "Svaret innehöll också det som förväntades (quantity, ordernr)."));
        Assert.That(t.Hint, Is.Null);
    }

    [Test]
    public void Excel_step_with_wrong_status_compares_expected_and_actual()
    {
        var t = PlainLanguage.Explain(Step(), Res(500, false, "FEL (väntade 201)"));
        Assert.That(t.Explanation, Is.EqualTo("Steget körde \"Skapa order\". Vi väntade oss 201 – skapad, men systemet svarade 500 – internt fel i systemet."));
        Assert.That(t.Hint, Does.Contain("fel i systemet"));
        var any = PlainLanguage.Explain(Step(expected: StatusExpectation.AnySuccess), Res(404, false)).Explanation;
        Assert.That(any, Does.Contain("Vi väntade oss valfri lyckad kod (2xx), men systemet svarade 404 – fanns inte."));
        var neg = PlainLanguage.Explain(Step(expected: new StatusExpectation(400, 499)), Res(201, false)).Explanation;
        Assert.That(neg, Does.Contain("Vi väntade oss ett nej från systemet (4xx), men systemet svarade 201 – skapad."));
    }

    [Test]
    public void Excel_step_with_wrong_response_translates_the_details()
    {
        var t = PlainLanguage.Explain(Step(), Res(201, false, "FEL (svaret stämmer inte)", "svar.quantity: väntade 5, fick 2"));
        Assert.That(t.Explanation, Is.EqualTo("Steget körde \"Skapa order\". Systemet svarade 201 – skapad som förväntat, men innehållet i svaret stämmer inte: " +
            "Förväntat quantity = 5, men systemet svarade 2."));
        Assert.That(t.Hint, Does.Contain("Excel"));
    }

    [Test]
    public void Skipped_step_is_explained()
    {
        var skipped = new CaseResult(null, false, true, "HOPPAD (ett tidigare steg i scenariot misslyckades)", new());
        var t = PlainLanguage.Explain(Step(), skipped);
        Assert.That(t.Explanation, Is.EqualTo("Hoppades över eftersom ett tidigare steg i flödet misslyckades."));
    }

    // --- Förberedande fel, timeout, övrigt -------------------------------------------------

    [Test]
    public void Failing_setup_call_is_explained()
    {
        var r = new CaseResult(null, false, false, "FEL (förberedande anrop createCustomer gav 500)", new())
        { Setup = new SetupFailure("createCustomer", "Skapa kund", 500) };
        var t = PlainLanguage.Explain(Case(Rules[2].Meta), r);
        Assert.That(t.Explanation, Is.EqualTo("Testet kunde inte köras: först behövde vi köra \"Skapa kund\", men det misslyckades (svar 500 – internt fel i systemet)."));
        Assert.That(t.Hint, Does.Contain("fel i systemet"));

        var refused = PlainLanguage.Explain(Case(Rules[2].Meta), r with { Setup = new SetupFailure("createCustomer", "Skapa kund", 400) });
        Assert.That(refused.Hint, Does.Contain("förberedande"));
        var conflict = PlainLanguage.Explain(Case(Rules[2].Meta), r with { Setup = new SetupFailure("createCustomer", "Skapa kund", 409) });
        Assert.That(conflict.Hint, Does.Contain("--seed"));
    }

    [Test]
    public void Timeout_and_unknown_cause_are_explained()
    {
        var timeout = new CaseResult(null, false, false, "FEL (timeout)", new()) { TimedOut = true };
        var t = PlainLanguage.Explain(Case(CaseMeta.Valid), timeout);
        Assert.That(t.Explanation, Is.EqualTo("Systemet svarade inte inom tidsgränsen, så testet fick inget svar."));
        Assert.That(t.Hint, Does.Contain("--timeout"));

        var other = new CaseResult(null, false, false, "FEL", new() { "Tester, rad 4: okänt anrop 'x'. Välj ett i listan." });
        Assert.That(PlainLanguage.Explain(Step(), other).Explanation, Is.EqualTo("Testet kunde inte köras. Tester, rad 4: okänt anrop 'x'. Välj ett i listan."));
    }

    [Test]
    public void Test_without_metadata_is_explained_from_the_technical_line()
    {
        var info = TestInfo.Basic("T-009", "Genererade tester", "POST /orders", "quantity under minimum");
        var t = PlainLanguage.Explain(info, Res(500, false));
        Assert.That(t.Title, Is.EqualTo("POST /orders – quantity under minimum"));
        Assert.That(t.Explanation, Does.Contain("svarade 500 – internt fel i systemet"));
    }

    // --- Statusordlista --------------------------------------------------------------------

    [TestCase(200, "OK")]
    [TestCase(201, "skapad")]
    [TestCase(204, "klart utan innehåll")]
    [TestCase(400, "sa nej till uppgifterna")]
    [TestCase(401, "inte inloggad")]
    [TestCase(403, "saknar behörighet")]
    [TestCase(404, "fanns inte")]
    [TestCase(409, "fanns redan / krock")]
    [TestCase(422, "sa nej till uppgifterna")]
    [TestCase(429, "för många anrop")]
    [TestCase(500, "internt fel i systemet")]
    [TestCase(502, "systemet svarar inte")]
    [TestCase(503, "systemet är inte tillgängligt")]
    [TestCase(504, "systemet svarar inte i tid")]
    [TestCase(202, "lyckades")]
    [TestCase(301, "hänvisade vidare")]
    [TestCase(418, "fel i anropet")]
    [TestCase(599, "fel i systemet")]
    public void The_status_glossary(int status, string meaning)
    {
        Assert.That(PlainLanguage.StatusMeaning(status), Is.EqualTo(meaning));
        Assert.That(PlainLanguage.StatusText(status), Is.EqualTo($"{status} – {meaning}"));
    }

    [Test]
    public void Summary_in_plain_language()
    {
        Assert.That(PlainLanguage.Summary(31, 1, 0), Is.EqualTo("1 av 32 tester visar ett problem."));
        Assert.That(PlainLanguage.Summary(29, 3, 0), Is.EqualTo("3 av 32 tester visar problem."));
        Assert.That(PlainLanguage.Summary(32, 0, 0), Is.EqualTo("Alla 32 tester gick bra."));
        Assert.That(PlainLanguage.Summary(1, 1, 2), Does.Contain("2 tester hoppades över"));
        Assert.That(PlainLanguage.Summary(0, 0, 0), Is.EqualTo("Inga tester kördes."));
    }

    // --- Hemligheter -----------------------------------------------------------------------

    [Test]
    public void Secrets_do_not_leak_into_the_plain_text()
    {
        const string token = "tok-9f8e7d";
        var mask = HeaderMask.For(new Dictionary<string, string> { ["Authorization"] = "Bearer " + token });
        var meta = new CaseMeta(TestRule.PatternMismatch, "sku", "^" + token + "$", "\"" + token + "\"");
        var r = Res(201, false, "FEL (svaret följer inte specen)", $"svar.x: \"{token}\" följer inte mönstret ^a$ (enligt specen)");
        var info = Case(meta, summary: "Skapa " + token);
        foreach (var t in new[] { PlainLanguage.Explain(info, r, mask), PlainLanguage.Explain(Case(CaseMeta.Valid), r, mask), PlainLanguage.Explain(Step(), r, mask) })
        {
            var all = t.Title + t.Explanation + t.Hint;
            Assert.That(all, Does.Not.Contain(token));
        }
        Assert.That(PlainLanguage.Explain(info, Res(400, true), mask).Explanation, Does.Contain("***"));
    }
}

public class CaseMetaTests
{
    private static OperationInfo Op(string id) => OpenApiLoader.Load(Spec()).Single(o => o.OperationId == id);

    private static string Spec()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var f = Path.Combine(d.FullName, "samples", "shop.openapi.yaml");
            if (File.Exists(f)) return f;
        }
        throw new FileNotFoundException("samples/shop.openapi.yaml");
    }

    [Test]
    public void Spec_summary_is_read()
    {
        Assert.That(Op("createOrder").Summary, Is.EqualTo("Skapa order"));
    }

    [Test]
    public void Generated_cases_have_rule_field_limit_and_sent_value()
    {
        var gen = new ScenarioGenerator(OpenApiLoader.Load(Spec()), new TestGenConfig { Seed = 1 });
        CaseMeta M(string op, string name) => gen.GenerateCases(Op(op)).Single(c => c.Scenario.Name == $"{op}_{name}").Meta;

        Assert.That(M("createOrder", "valid"), Is.EqualTo(CaseMeta.Valid));
        Assert.That(M("createOrder", "quantity_below_min"), Is.EqualTo(new CaseMeta(TestRule.BelowMinimum, "quantity", "1", "0")));
        Assert.That(M("createOrder", "quantity_above_max"), Is.EqualTo(new CaseMeta(TestRule.AboveMaximum, "quantity", "1000", "1001")));
        Assert.That(M("createOrder", "missing_quantity"), Is.EqualTo(new CaseMeta(TestRule.Missing, "quantity")));
        var wrong = M("createOrder", "quantity_wrong_type");
        Assert.That((wrong.Rule, wrong.Limit, wrong.SentValue), Is.EqualTo((TestRule.WrongType, "integer", "\"not-a-number\"")));
        var tooLong = M("createCustomer", "name_too_long");
        Assert.That((tooLong.Rule, tooLong.Limit), Is.EqualTo((TestRule.TooLong, "100")));
        Assert.That(tooLong.SentValue, Has.Length.EqualTo(103));
        Assert.That(M("createCustomer", "name_too_short"), Is.EqualTo(new CaseMeta(TestRule.TooShort, "name", "1", "\"\"")));
        var notInEnum = M("createCustomer", "status_not_in_enum");
        Assert.That((notInEnum.Rule, notInEnum.Limit, notInEnum.SentValue), Is.EqualTo((TestRule.NotInEnum, "\"active\", \"inactive\"", "\"__not_in_enum__\"")));
        var pat = M("createProduct", "sku_pattern_mismatch");
        Assert.That((pat.Rule, pat.Limit), Is.EqualTo((TestRule.PatternMismatch, "^[A-Z]{3}-[0-9]{4}$")));
        Assert.That(System.Text.RegularExpressions.Regex.IsMatch(System.Text.Json.JsonSerializer.Deserialize<string>(pat.SentValue!)!, pat.Limit!), Is.False);
    }

    [Test]
    public void Scenario_files_are_not_affected_by_the_metadata()
    {
        var gen = new ScenarioGenerator(OpenApiLoader.Load(Spec()), new TestGenConfig { Seed = 1 });
        var op = Op("createOrder");
        var json = TestGen.Scenarios.ScenarioJson.Serialize(gen.Generate(op));
        Assert.That(json, Does.Not.Contain("meta").And.Not.Contain("rule").And.Not.Contain("limit"));
        Assert.That(TestGen.Scenarios.ScenarioJson.Serialize(gen.GenerateCases(op).Select(c => c.Scenario)), Is.EqualTo(json));
    }
}

public class PlainRunTests
{
    private const string Token = "tok-9f8e7d";

    private static string Spec()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var f = Path.Combine(d.FullName, "samples", "shop.openapi.yaml");
            if (File.Exists(f)) return f;
        }
        throw new FileNotFoundException("samples/shop.openapi.yaml");
    }

    /// <summary>Fejk-API: kunder skapas med 201 (eller 500 om failCustomers), order med quantity 0 ger 500, GET ger 200.</summary>
    private sealed class Api(bool failCustomers = false) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            var body = req.Content is null ? null : JsonNode.Parse(await req.Content.ReadAsStringAsync(ct)) as JsonObject;
            if (path == "/customers" && failCustomers) return Json(HttpStatusCode.InternalServerError, "{\"error\":\"boom\"}");
            if (path == "/orders" && body?["quantity"]?.GetValue<int>() == 0) return Json(HttpStatusCode.InternalServerError, "{}");
            if (req.Method == HttpMethod.Post && body is not null) { body["id"] = 7; return Json(HttpStatusCode.Created, body.ToJsonString()); }
            return Json(HttpStatusCode.OK, "{}");
        }

        private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
            new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private static async Task<(string Output, List<TestOutcome> Outcomes)> Run(bool plainText, string only = "createOrder", bool failCustomers = false)
    {
        var sw = new StringWriter();
        var seen = new List<TestOutcome>();
        var runner = new TestRunner(new RunOptions
        {
            Spec = Spec(), Api = "http://fake.test", Token = Token, Only = only, Seed = 1, PlainText = plainText, OnResult = seen.Add,
        }, sw, new Api(failCustomers));
        await runner.RunAsync();
        Assert.That(runner.Outcomes, Is.EqualTo(seen));
        return (sw.ToString(), seen);
    }

    [Test]
    public async Task Without_plain_text_the_terminal_shows_the_technical_line_as_before()
    {
        var (output, _) = await Run(plainText: false, only: "createCustomer");
        Assert.That(output, Does.Match(@"T-001\s+POST /customers\s+giltig indata\s+201\s+OK"));
        Assert.That(output, Does.Match(@"T-002\s+POST /customers\s+email saknas"));
        Assert.That(output, Does.Match(@"status utanför tillåtna värden"));
        Assert.That(output, Does.Not.Contain("Vi skickade").And.Not.Contain("Tolkning:"));
    }

    [Test]
    public async Task Terminal_descriptions_are_unchanged_for_all_rules()
    {
        var (output, _) = await Run(plainText: false, only: "createProduct");
        foreach (var d in new[] { "name saknas", "name för lång (över maxlängd)", "name för kort (under minlängd)", "name har fel typ", "price för lågt (under minimum)", "price för högt (över maximum)", "sku följer inte mönstret" })
            Assert.That(output, Does.Match(@"POST /products\s+" + System.Text.RegularExpressions.Regex.Escape(d) + @"\s+\d{3}\s"));
    }

    [Test]
    public async Task Plain_text_writes_title_and_explanation_and_keeps_details_for_failures()
    {
        var (output, _) = await Run(plainText: true);
        Assert.That(output, Does.Match(@"T-\d{3}\s+FEL\s+Skapa order – quantity för lågt \(under minimum\)"));
        Assert.That(output, Does.Contain("Vi skickade quantity = 0, men minsta tillåtna är 1."));
        Assert.That(output, Does.Contain("Tolkning: Det tyder på ett fel i systemet, inte i testet."));
        Assert.That(output, Does.Contain("anrop: POST http://fake.test/orders").And.Contain("curl -X POST").And.Not.Contain("curl: curl"));
        Assert.That(output, Does.Not.Contain("quantity under minimum"), "samma rubrikfras i alla kanaler");
        Assert.That(output, Does.Contain("av ").And.Contain("tester visar"));
        Assert.That(output, Does.Not.Contain(Token));
    }

    [Test]
    public async Task OnResult_is_called_once_per_test_with_plain_text_and_technical_details()
    {
        var (_, seen) = await Run(plainText: false);
        Assert.That(seen.Select(s => s.Index), Is.EqualTo(Enumerable.Range(1, seen.Count)));
        Assert.That(seen.All(s => s.Total == seen.Count));
        var failed = seen.Single(s => s.Test.Case?.Rule == TestRule.BelowMinimum);
        Assert.That(failed.Outcome, Is.EqualTo("FEL"));
        Assert.That(failed.Group, Is.EqualTo("Genererade tester"));
        Assert.That(failed.Id, Does.Match(@"^T-\d{3}$"));
        Assert.That(failed.Test.Case!.Rule, Is.EqualTo(TestRule.BelowMinimum));
        Assert.That(failed.Text.Title, Is.EqualTo("Skapa order – quantity för lågt (under minimum)"));
        Assert.That(failed.TechnicalLines, Does.Contain("svar: 500 (" + failed.Result.Exchanges.Last().ElapsedMs + " ms)"));
        Assert.That(failed.Curl, Does.Contain("\nexport TOKEN='din token'\ncurl -X POST").And.Contain("-H \"Authorization: Bearer $TOKEN\""));
        Assert.That(string.Join("\n", failed.TechnicalLines) + failed.Curl, Does.Not.Contain(Token));
    }

    [Test]
    public async Task Setup_failure_gives_plain_text_for_the_test_that_could_not_run()
    {
        var (output, seen) = await Run(plainText: true, failCustomers: true);
        var first = seen.First();
        Assert.That(first.Text.Explanation, Does.StartWith("Testet kunde inte köras: först behövde vi köra \"Skapa kund\", men det misslyckades (svar 500 – internt fel i systemet)."));
        Assert.That(output, Does.Contain("Testet kunde inte köras: först behövde vi köra \"Skapa kund\""));
    }

    [Test]
    public async Task Excel_steps_and_skipped_steps_get_plain_text()
    {
        var dir = Directory.CreateTempSubdirectory("testgen-klartext-").FullName;
        try
        {
            var xlsx = Path.Combine(dir, "t.xlsx");
            ExcelTemplate.Create(OpenApiLoader.Load(Spec()), xlsx);
            using (var wb = new XLWorkbook(xlsx))
            {
                var ws = wb.Worksheet(ExcelScenarioReader.SheetName);
                foreach (var r in ws.RowsUsed().Skip(1).ToList()) r.Clear();
                string[][] rows =
                {
                    new[] { "Flöde", "createCustomer", "", "", "201", "" },
                    new[] { "Flöde", "createOrder", "quantity=0", "", "2xx", "" },
                    new[] { "Flöde", "getOrder", "", "", "200", "" },
                };
                for (var i = 0; i < rows.Length; i++)
                    for (var c = 0; c < 6; c++) ws.Cell(2 + i, c + 1).Value = rows[i][c];
                wb.Save();
            }
            var seen = new List<TestOutcome>();
            await new TestRunner(new RunOptions { Spec = Spec(), Api = "http://fake.test", Only = "getOrder", Excel = xlsx, Seed = 1, OnResult = seen.Add },
                new StringWriter(), new Api()).RunAsync();
            var steps = seen.Where(s => s.Test.Step is not null).ToList();
            Assert.That(steps.Select(s => s.Text.Title), Is.EqualTo(new[] { "Flöde, steg 1: Skapa kund", "Flöde, steg 2: Skapa order", "Flöde, steg 3: Hämta order" }));
            Assert.That(steps[0].Text.Explanation, Does.Contain("Systemet svarade 201 – skapad, som förväntat."));
            Assert.That(steps[1].Text.Explanation, Does.Contain("Vi väntade oss valfri lyckad kod (2xx), men systemet svarade 500 – internt fel i systemet."));
            Assert.That(steps[2].Outcome, Is.EqualTo("HOPPAD"));
            Assert.That(steps[2].Text.Explanation, Is.EqualTo("Hoppades över eftersom ett tidigare steg i flödet misslyckades. Följd av " + steps[1].Id + " (steg 2 'Skapa order' fick FEL)."));
            Assert.That(steps[2].Result.RootCauseTestId, Is.EqualTo(steps[1].Id));
        }
        finally { Directory.Delete(dir, true); }
    }
}
