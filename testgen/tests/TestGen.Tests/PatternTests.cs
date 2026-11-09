using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Microsoft.OpenApi;
using TestGen.Core;
using TestGen.Core.Run;

namespace TestGen.Tests;

public class PatternMismatchTests
{
    private const string Sku = "^[A-Z]{3}-[0-9]{4}$";

    [Test]
    public void Candidate_does_not_match_the_pattern_and_is_deterministic()
    {
        var a = PatternMismatch.Find(Sku, "QXT-4821");
        var b = PatternMismatch.Find(Sku, "QXT-4821");
        Assert.That(a, Is.Not.Null.And.EqualTo(b));
        Assert.That(Regex.IsMatch(a!, Sku), Is.False);
        Assert.That(a, Is.EqualTo("!XT-4821"), "minsta ändring: ett tecken byts");
    }

    [Test]
    public void Unanchored_pattern_requires_every_character_to_change()
    {
        var v = PatternMismatch.Find("[A-Z]", "QXT");
        Assert.That(v, Is.Not.Null);
        Assert.That(Regex.IsMatch(v!, "[A-Z]"), Is.False);
    }

    [Test]
    public void Pattern_matching_everything_gives_no_value()
    {
        Assert.That(PatternMismatch.Find(".*", "abc"), Is.Null);
        Assert.That(PatternMismatch.Find("^.*$", "abc", 1, 10), Is.Null);
        Assert.That(PatternMismatch.Find("", "abc"), Is.Null);
    }

    [Test]
    public void Invalid_pattern_gives_no_value() =>
        Assert.That(PatternMismatch.Find("[", "abc"), Is.Null);

    [Test]
    public void Min_and_max_length_are_respected_when_possible()
    {
        var v = PatternMismatch.Find("^[a-z]+$", "abcdef", 6, 6)!;
        Assert.That(v.Length, Is.EqualTo(6));
        Assert.That(Regex.IsMatch(v, "^[a-z]+$"), Is.False);

        // Utan giltigt värde att utgå från: längdkravet möts genom att fylla ut/korta av.
        var w = PatternMismatch.Find("^[a-z]+$", null, 5, 8)!;
        Assert.That(w.Length, Is.InRange(5, 8));
        Assert.That(Regex.IsMatch(w, "^[a-z]+$"), Is.False);
    }

    [Test]
    public void If_length_limits_cannot_be_met_another_candidate_is_used()
    {
        // ^.{3}$ med minLength 3 / maxLength 3: alla 3-teckenvärden matchar, så bara en annan längd bryter mönstret.
        var v = PatternMismatch.Find("^.{3}$", "abc", 3, 3)!;
        Assert.That(Regex.IsMatch(v, "^.{3}$"), Is.False);
    }

    private static IOpenApiSchema Schema(string yaml) => SpecText.Schema(yaml);

    [Test]
    public void Negative_cases_get_pattern_mismatch_only_for_string_fields_with_pattern()
    {
        var schema = Schema("""
            type: object
            required: [sku, name]
            properties:
              sku: { type: string, pattern: '^[A-Z]{3}-[0-9]{4}$' }
              name: { type: string }
              any: { type: string, pattern: '.*' }
              n: { type: integer, pattern: '^1$' }
            """);
        var valid = new JsonObject { ["sku"] = "ABC-1234", ["name"] = "x", ["any"] = "q", ["n"] = 1 };
        var cases = NegativeCaseGenerator.Generate(valid, schema);
        var hit = cases.Where(c => c.Suffix.EndsWith("_pattern_mismatch")).ToList();
        Assert.That(hit.Select(c => c.Suffix), Is.EqualTo(new[] { "sku_pattern_mismatch" }));
        Assert.That(Regex.IsMatch(hit[0].Body["sku"]!.GetValue<string>(), Sku), Is.False);
        Assert.That(hit[0].Body["name"]!.GetValue<string>(), Is.EqualTo("x"), "övriga fält är giltiga");
    }
}

public class ResponseSchemaCheckTests
{
    private static IOpenApiSchema Schema(string yaml) => SpecText.Schema(yaml);

    private static List<string> Errs(JsonNode? body, IOpenApiSchema? schema) => ResponseSchemaCheck.Check(body, schema).Errors;

    private const string Yaml = """
        allOf:
          - type: object
            properties:
              sku: { type: string, pattern: '^[A-Z]{3}-[0-9]{4}$' }
          - type: object
            properties:
              id: { type: integer }
              inner:
                type: object
                properties:
                  code: { type: string, pattern: '^[0-9]+$' }
              items:
                type: array
                items:
                  type: object
                  properties:
                    sku: { type: string, pattern: '^[A-Z]{3}-[0-9]{4}$' }
        """;

    [Test]
    public void Valid_response_gives_no_errors() =>
        Assert.That(Errs(JsonNode.Parse("""{"id":1,"sku":"ABC-1234","inner":{"code":"12"},"items":[{"sku":"XYZ-0001"}]}"""), Schema(Yaml)), Is.Empty);

    [Test]
    public void Top_level_via_allOf_with_exact_detail_line() =>
        Assert.That(Errs(JsonNode.Parse("""{"sku":"abc"}"""), Schema(Yaml)),
            Is.EqualTo(new[] { "svar.sku: \"abc\" följer inte mönstret ^[A-Z]{3}-[0-9]{4}$ (enligt specen)" }));

    [Test]
    public void Nested_object_and_array()
    {
        var errors = Errs(
            JsonNode.Parse("""{"sku":"ABC-1234","inner":{"code":"x1"},"items":[{"sku":"OK"},{"sku":"ABC-1234"},{"sku":"nej"}]}"""), Schema(Yaml));
        Assert.That(errors, Is.EqualTo(new[]
        {
            "svar.inner.code: \"x1\" följer inte mönstret ^[0-9]+$ (enligt specen)",
            "svar.items[0].sku: \"OK\" följer inte mönstret ^[A-Z]{3}-[0-9]{4}$ (enligt specen)",
            "svar.items[2].sku: \"nej\" följer inte mönstret ^[A-Z]{3}-[0-9]{4}$ (enligt specen)",
        }));
    }

    [Test]
    public void Top_level_array_of_objects()
    {
        var schema = Schema("""
            type: array
            items: { type: object, properties: { sku: { type: string, pattern: '^A$' } } }
            """);
        Assert.That(Errs(JsonNode.Parse("""[{"sku":"A"},{"sku":"B"}]"""), schema),
            Is.EqualTo(new[] { "svar[1].sku: \"B\" följer inte mönstret ^A$ (enligt specen)" }));
    }

    [Test]
    public void Null_and_missing_optional_fields_give_no_pattern_errors()
    {
        Assert.That(Errs(JsonNode.Parse("""{"id":1}"""), Schema(Yaml)), Is.Empty);
        Assert.That(Errs(null, Schema(Yaml)), Is.Empty);
        Assert.That(Errs(JsonNode.Parse("""{"sku":"zzz"}"""), null), Is.Empty);
    }

    [Test]
    public void Spec_without_pattern_is_not_checked() =>
        Assert.That(Errs(JsonNode.Parse("""{"name":"?!"}"""),
            Schema("type: object\nproperties:\n  name: { type: string }")), Is.Empty);

    [Test]
    public void Response_schemas_are_read_per_2xx_in_the_shop_spec()
    {
        var op = OpenApiLoader.Load(ShopSpec.Path).Single(o => o.OperationId == "createProduct");
        Assert.That(op.ResponseSchemas!.Keys, Does.Contain(201));
        Assert.That(Errs(JsonNode.Parse("""{"id":1,"name":"x","price":5,"sku":"abc"}"""), op.ResponseSchemas[201]),
            Is.EqualTo(new[] { "svar.sku: \"abc\" följer inte mönstret ^[A-Z]{3}-[0-9]{4}$ (enligt specen)" }));
    }
}

internal static class ShopSpec
{
    public static string Path
    {
        get
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            {
                var f = System.IO.Path.Combine(d.FullName, "samples", "shop.openapi.yaml");
                if (File.Exists(f)) return f;
            }
            throw new FileNotFoundException("samples/shop.openapi.yaml");
        }
    }
}

public class ExpectedRegexTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("testgen-").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    [Test]
    public void Parser_distinguishes_equality_and_regex_and_the_first_operator_decides()
    {
        var (values, patterns) = FieldList.ParseExpected("quantity=5; ordernr~^ORD-\\d{6}$; note=a~b; re~a=b");
        Assert.That(values.Select(v => v.Key), Is.EqualTo(new[] { "quantity", "note" }));
        Assert.That(values[1].Value!.GetValue<string>(), Is.EqualTo("a~b"));
        Assert.That(patterns.Select(p => (p.Key, p.Value)), Is.EqualTo(new[] { ("ordernr", "^ORD-\\d{6}$"), ("re", "a=b") }));
    }

    [Test]
    public void Regex_with_semicolon_must_be_quoted()
    {
        var (_, patterns) = FieldList.ParseExpected("x~\"^a;b$\"; y~z");
        Assert.That(patterns.Select(p => p.Value), Is.EqualTo(new[] { "^a;b$", "z" }));
    }

    [Test]
    public void Invalid_regex_gives_clear_error_with_where()
    {
        var e = Assert.Throws<FieldListException>(() => FieldList.ParseExpected("x~[", "Tester!F4: "))!;
        Assert.That(e.Message, Is.EqualTo("Tester!F4: Mönstret efter ~ för x går inte att läsa: en [ är inte stängd med ]. Vill du jämföra exakt, skriv = i stället för ~."));
        Assert.That(e.Technical, Does.Contain("Unterminated"), "den råa .NET-texten finns bara som teknisk information");
        Assert.Throws<FieldListException>(() => FieldList.ParseExpected("x~"));
        Assert.Throws<FieldListException>(() => FieldList.ParseExpected("~abc"));
        Assert.Throws<FieldListException>(() => FieldList.ParseExpected("bara text"));
    }

    [Test]
    public void Match_mismatch_missing_and_numbers()
    {
        var body = JsonNode.Parse("""{"ordernr":"ORD-12","n":42,"kund":{"kod":"A1"}}""");
        string[] Run(string p, string f = "ordernr") => BodyMatcher.RegexMismatches(new[] { KeyValuePair.Create(f, p) }, body).ToArray();
        Assert.That(Run("^ORD-\\d+$"), Is.Empty);
        Assert.That(Run("^ORD-\\d{6}$"), Is.EqualTo(new[] { "svar.ordernr: \"ORD-12\" matchar inte ~^ORD-\\d{6}$" }));
        Assert.That(Run("^\\d+$", "n"), Is.Empty, "tal jämförs som text");
        Assert.That(Run("^A", "kund.kod"), Is.Empty);
        Assert.That(Run("x", "saknas"), Is.EqualTo(new[] { "svar.saknas: väntade ~x, fick (saknas)" }));
    }

    // --- Runner med fejk-API ---

    private sealed class Api(string sku) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var ok = new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent($$"""{"id":1,"name":"Pryl","sku":"{{sku}}","price":5}""", Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(ok);
        }
    }

    private string Workbook(string expected)
    {
        var path = Path.Combine(_dir, "t.xlsx");
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Tester");
        for (var i = 0; i < ExcelLayout.Headers.Length; i++) ws.Cell(1, i + 1).Value = ExcelLayout.Headers[i];
        ws.Cell(2, 1).Value = "S";
        ws.Cell(2, 2).Value = "createProduct";
        ws.Cell(2, 5).Value = "201";
        ws.Cell(2, 6).Value = expected;
        wb.SaveAs(path);
        return path;
    }

    private async Task<(int Exit, string Output)> Run(string sku, string? excel = null, string only = "createProduct")
    {
        var sw = new StringWriter { NewLine = "\n" };
        var exit = await new TestRunner(new RunOptions { Spec = ShopSpec.Path, Api = "http://fake.test", Only = only, Seed = 1, Excel = excel, Source = new ExcelTestSource() },
            sw, new Api(sku)).RunAsync();
        return (exit, sw.ToString());
    }

    [Test]
    public async Task Response_breaking_the_spec_pattern_fails_with_detail_line()
    {
        var (exit, output) = await Run("abc");
        Assert.That(exit, Is.EqualTo(1));
        Assert.That(output, Does.Match(@"T-\d{3}\s+POST /products\s+giltig indata\s+201\s+FEL \(svaret följer inte specen\)"));
        Assert.That(output, Does.Contain("      svar.sku: \"abc\" följer inte mönstret ^[A-Z]{3}-[0-9]{4}$ (enligt specen)"));
    }

    [Test]
    public async Task Response_following_the_pattern_passes_for_the_valid_case()
    {
        var (_, output) = await Run("ABC-1234");
        Assert.That(output, Does.Match(@"T-\d{3}\s+POST /products\s+giltig indata\s+201\s+OK"));
        Assert.That(output, Does.Contain("sku följer inte mönstret"), "det nya negativa fallet beskrivs");
        Assert.That(output, Does.Not.Contain("(enligt specen)"));
    }

    [Test]
    public async Task Excel_regex_match_passes_and_mismatch_fails()
    {
        var (exit, output) = await Run("ABC-1234", Workbook("sku~^[A-Z]{3}-\\d{4}$; name=Pryl"), only: "createProduct");
        Assert.That(output, Does.Match(@"S, steg 1\s+201\s+OK"));

        var (_, bad) = await Run("ABC-1234", Workbook("sku~^ORD-\\d{6}$"));
        Assert.That(bad, Does.Match(@"S, steg 1\s+201\s+FEL \(svaret stämmer inte\)"));
        Assert.That(bad, Does.Contain("svar.sku: \"ABC-1234\" matchar inte ~^ORD-\\d{6}$"));
        Assert.That(exit, Is.EqualTo(1), "de genererade negativa fallen får 201 från fejk-API:et");
    }

    [Test]
    public void Excel_with_invalid_regex_gives_error_with_row_number()
    {
        var path = Workbook("sku~(");
        var e = Assert.Throws<ExcelValidationException>(() => ExcelScenarioReader.Read(path))!;
        Assert.That(e.Message, Does.Contain("Tester!F2 (Förväntat svar): Mönstret efter ~ för sku går inte att läsa: en ( är inte stängd med )"));
    }

    [Test]
    public async Task Excel_response_breaking_the_spec_pattern_fails_even_without_expected_response()
    {
        var (_, output) = await Run("abc", Workbook(""));
        Assert.That(output, Does.Match(@"S, steg 1\s+201\s+FEL \(svaret följer inte specen\)"));
    }
}
