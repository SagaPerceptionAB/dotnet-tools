using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;
using Microsoft.OpenApi.YamlReader;
using TestGen.Core;
using TestGen.Core.Run;

namespace TestGen.Tests;

/// <summary>Hjälpmedel: bygger en spec ur text och genererar fall.</summary>
internal static class SpecText
{
    /// <summary>En spec med en POST /x vars body har det givna schemat (YAML, indenterat valfritt) och svaren i <paramref name="responses"/>.</summary>
    public static string Post(string schemaYaml, string responses = "'201': {description: ok}\n'400': {description: nej}", string contentType = "application/json")
    {
        var schema = string.Join("\n", schemaYaml.Replace("\r", "").Split('\n').Select(l => "              " + l));
        var resp = string.Join("\n", responses.Split('\n').Select(l => "        " + l));
        return $$"""
            openapi: 3.0.3
            info: {title: t, version: 1.0.0}
            paths:
              /x:
                post:
                  operationId: mk
                  requestBody:
                    content:
                      {{contentType}}:
                        schema:
            {{schema}}
                  responses:
            {{resp}}
            """;
    }

    public static List<OperationInfo> Load(string yaml)
    {
        var path = Path.Combine(Path.GetTempPath(), "testgen-spec-" + Guid.NewGuid().ToString("N") + ".yaml");
        File.WriteAllText(path, yaml);
        try { return OpenApiLoader.Load(path); }
        finally { File.Delete(path); }
    }

    public static List<GeneratedCase> Cases(string yaml, int seed, out OperationInfo op)
    {
        var ops = Load(yaml);
        op = ops[0];
        return new ScenarioGenerator(ops, new GeneratorOptions { Seed = seed }).GenerateCases(op);
    }

    /// <summary>Ett schema ur en YAML-text, inläst som en del av ett litet dokument.</summary>
    public static IOpenApiSchema Schema(string yaml)
    {
        var indented = string.Join("\n", yaml.Split('\n').Select(l => "      " + l));
        var settings = new OpenApiReaderSettings();
        settings.AddYamlReader();
        var result = OpenApiDocument.Parse($"openapi: 3.0.1\ninfo: {{title: t, version: '1'}}\npaths: {{}}\ncomponents:\n  schemas:\n    Fragment:\n{indented}\n", "yaml", settings);
        return result.Document!.Components!.Schemas!["Fragment"];
    }
}

public class ResponseStoreListTests
{
    private static ResponseStore Store()
    {
        var s = new ResponseStore();
        s.Save("kund", JsonNode.Parse("""{"id":7,"namn":"Eva"}"""));
        return s;
    }

    [Test]
    public void List_of_strings_throws_no_exception()
    {
        var result = Store().Substitute(JsonNode.Parse("""{"tags":["a","b"]}"""));
        Assert.That(result!.ToJsonString(), Is.EqualTo("""{"tags":["a","b"]}"""));
    }

    [Test]
    public void List_of_objects_throws_no_exception()
    {
        var result = Store().Substitute(JsonNode.Parse("""{"rader":[{"sku":"ABC-1234","antal":2},{"sku":"DEF-0001","antal":3}]}"""));
        Assert.That(result!.ToJsonString(), Is.EqualTo("""{"rader":[{"sku":"ABC-1234","antal":2},{"sku":"DEF-0001","antal":3}]}"""));
    }

    [Test]
    public void Placeholders_in_list_are_replaced_and_type_is_kept()
    {
        var result = Store().Substitute(JsonNode.Parse("""{"kunder":["${kund.id}","x-${kund.namn}",{"id":"${kund.id}"}]}"""));
        Assert.That(result!.ToJsonString(), Is.EqualTo("""{"kunder":[7,"x-Eva",{"id":7}]}"""));
    }

    [Test]
    public void Top_level_list_is_handled()
    {
        var result = Store().Substitute(JsonNode.Parse("""["${kund.namn}",{"k":["${kund.id}"]}]"""));
        Assert.That(result!.ToJsonString(), Is.EqualTo("""["Eva",{"k":[7]}]"""));
    }
}

public class DataRespectsSpecTests
{
    private static readonly Regex PostalCode = new(@"^[0-9]{3} [0-9]{2}$");

    private static JsonNode? Gen(string name, string schemaYaml, int seed)
    {
        var gen = new SchemaDataGenerator(seed, "sv");
        return gen.GenerateField(name, SpecText.Schema(schemaYaml));
    }

    [Test]
    public void Pattern_takes_precedence_over_field_name_rule([Range(1, 25)] int seed)
    {
        foreach (var name in new[] { "postnummer", "zip", "postalCode" })
            Assert.That(Gen(name, "{type: string, pattern: '^[0-9]{3} [0-9]{2}$'}", seed)!.GetValue<string>(), Does.Match(PostalCode), name);
    }

    [Test]
    public void Name_rule_is_used_when_it_satisfies_the_spec([Range(1, 10)] int seed)
    {
        var email = Gen("email", "{type: string, format: email}", seed)!.GetValue<string>();
        Assert.That(email, Does.Contain("@"));
        var city = Gen("city", "{type: string, maxLength: 40}", seed)!.GetValue<string>();
        Assert.That(city, Has.Length.InRange(1, 40));
    }

    [Test]
    public void Length_limits_are_respected_without_breaking_the_format([Range(1, 10)] int seed)
    {
        // min 30 tecken går inte att nå med en stad: värdet ska ändå vara minst 30 tecken
        var city = Gen("city", "{type: string, minLength: 30}", seed)!.GetValue<string>();
        Assert.That(city.Length, Is.GreaterThanOrEqualTo(30));
        // email med maxLength 8: kapas aldrig till något som saknar @ eller är ogiltigt – annars genereras ur formatet
        var email = Gen("email", "{type: string, format: email, maxLength: 60}", seed)!.GetValue<string>();
        Assert.That(email, Does.Match(@"^[^@\s]+@[^@\s]+\.[^@\s]+$"));
    }

    [Test]
    public void Fit_does_not_break_a_pattern_already_followed()
    {
        var schema = SpecText.Schema("{type: string, pattern: '^[0-9]{3} [0-9]{2}$', minLength: 8}");
        // "123 45" är 6 tecken; en utfyllnad med x skulle bryta mönstret, så värdet lämnas som det är
        Assert.That(FieldRules.Fit("123 45", schema), Is.EqualTo("123 45"));
        Assert.That(FieldRules.Fit("abc", SpecText.Schema("{type: string, minLength: 5}")), Is.EqualTo("abcxx"));
    }

    [Test]
    public void Uuid_is_version_4_with_correct_variant_and_deterministic([Range(1, 40)] int seed)
    {
        var uuid = Gen("id", "{type: string, format: uuid}", seed)!.GetValue<string>();
        Assert.That(uuid, Does.Match("^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$"));
        Assert.That(Gen("id", "{type: string, format: uuid}", seed)!.GetValue<string>(), Is.EqualTo(uuid));
    }

    [Test]
    public void ReadOnly_is_not_sent_and_gets_no_negative_cases()
    {
        var cases = SpecText.Cases(SpecText.Post("""
            type: object
            required: [id, name]
            properties:
              id: {type: integer, readOnly: true}
              name: {type: string}
              inner:
                type: object
                properties:
                  ref: {type: string, format: uuid, readOnly: true}
                  ok: {type: string}
            """), 1, out _);
        var valid = cases[0].Scenario.Body!.AsObject();
        Assert.That(valid.ContainsKey("id"), Is.False);
        Assert.That(valid["inner"]!.AsObject().ContainsKey("ref"), Is.False);
        Assert.That(cases.Select(c => c.Meta.Field), Has.None.EqualTo("id").Or.EqualTo("inner.ref"));
        Assert.That(cases.Select(c => c.Meta.Field), Does.Contain("name"));
    }
}

public class NegativeStatusTests
{
    private static List<int> Statuses(string responses)
    {
        var cases = SpecText.Cases(SpecText.Post("{type: object, required: [a], properties: {a: {type: string}}}", responses), 1, out _);
        return cases.First(c => c.Scenario.Kind == "negative").Scenario.ExpectedStatus;
    }

    [Test]
    public void Without_400_and_422_in_the_spec_it_is_400() =>
        Assert.That(Statuses("'201': {description: ok}\n'404': {description: x}"), Is.EqualTo(new[] { 400 }));

    [Test]
    public void Only_400_gives_400() =>
        Assert.That(Statuses("'201': {description: ok}\n'400': {description: x}"), Is.EqualTo(new[] { 400 }));

    [Test]
    public void Only_422_gives_400_and_422() =>
        Assert.That(Statuses("'201': {description: ok}\n'422': {description: x}"), Is.EqualTo(new[] { 400, 422 }));

    [Test]
    public void Both_listed_gives_both() =>
        Assert.That(Statuses("'201': {description: ok}\n'400': {description: x}\n'422': {description: y}"), Is.EqualTo(new[] { 400, 422 }));

    [Test]
    public void Four_XX_accepts_the_whole_4xx_group()
    {
        var s = Statuses("'201': {description: ok}\n'4XX': {description: x}");
        Assert.That(s, Has.Count.EqualTo(100));
        Assert.That(s.Min(), Is.EqualTo(400));
        Assert.That(s.Max(), Is.EqualTo(499));
    }

    [Test]
    public void Without_response_list_in_hand_built_operation_it_is_400()
    {
        var op = new OperationInfo("x", "POST", "/x", new List<ParamInfo>(), null, new[] { 201 });
        Assert.That(ScenarioGenerator.NegativeStatuses(op), Is.EqualTo(new[] { 400 }));
    }

    [Test]
    public void Four_XX_is_shown_as_a_range_in_plain_text()
    {
        var info = new TestInfo("T-001", "G", "POST /x", "POST", "mk", null, "x", new[] { new StatusExpectation(400, 499) },
            new CaseMeta(TestRule.Missing, "a"));
        var t = PlainLanguage.Explain(info, new CaseResult(201, false, false, "FEL", new()));
        Assert.That(t.Explanation, Does.Contain("(4xx)").And.Not.Contain("(400)"));
        var both = info with { ExpectedStatus = new[] { new StatusExpectation(400, 400), new StatusExpectation(422, 422) } };
        Assert.That(PlainLanguage.Explain(both, new CaseResult(201, false, false, "FEL", new())).Explanation, Does.Contain("(400 eller 422)"));
    }
}

public class DeepNegativeCaseTests
{
    private const string Order = """
        type: object
        required: [leverans, rader]
        properties:
          leverans:
            type: object
            required: [adress]
            properties:
              adress:
                type: object
                required: [postnummer, gata]
                properties:
                  gata: {type: string, minLength: 2, maxLength: 30}
                  postnummer: {type: string, pattern: '^[0-9]{3} [0-9]{2}$'}
          rader:
            type: array
            minItems: 1
            maxItems: 3
            items:
              type: object
              required: [sku, antal]
              properties:
                sku: {type: string, pattern: '^[A-Z]{3}-[0-9]{4}$'}
                antal: {type: integer, minimum: 1, maximum: 10}
        """;

    private static Dictionary<string, GeneratedCase> ByField(List<GeneratedCase> cases, TestRule rule) =>
        cases.Where(c => c.Meta.Rule == rule && c.Meta.Field is not null).ToDictionary(c => c.Meta.Field!);

    [Test]
    public void Nested_fields_get_negative_cases_with_path_as_field_name()
    {
        var cases = SpecText.Cases(SpecText.Post(Order), 3, out _);
        var pat = ByField(cases, TestRule.PatternMismatch);
        Assert.That(pat.Keys, Is.EquivalentTo(new[] { "leverans.adress.postnummer", "rader[0].sku" }));
        Assert.That(ByField(cases, TestRule.Missing).Keys, Is.SupersetOf(new[] { "leverans", "leverans.adress", "leverans.adress.postnummer", "rader[0].sku", "rader[0].antal" }));
        Assert.That(ByField(cases, TestRule.BelowMinimum).Keys, Is.EqualTo(new[] { "rader[0].antal" }));
        Assert.That(ByField(cases, TestRule.AboveMaximum).Keys, Is.EqualTo(new[] { "rader[0].antal" }));
        Assert.That(ByField(cases, TestRule.TooLong).Keys, Is.EqualTo(new[] { "leverans.adress.gata" }));
        Assert.That(ByField(cases, TestRule.TooShort).Keys, Is.EqualTo(new[] { "leverans.adress.gata" }));
    }

    [Test]
    public void Only_the_deep_field_changes_and_the_rest_is_valid()
    {
        var cases = SpecText.Cases(SpecText.Post(Order), 3, out _);
        var valid = cases[0].Scenario.Body!;
        var bad = ByField(cases, TestRule.PatternMismatch)["leverans.adress.postnummer"].Scenario.Body!;
        Assert.That(bad["leverans"]!["adress"]!["postnummer"]!.GetValue<string>(), Does.Not.Match(@"^[0-9]{3} [0-9]{2}$"));
        Assert.That(bad["leverans"]!["adress"]!["gata"]!.ToJsonString(), Is.EqualTo(valid["leverans"]!["adress"]!["gata"]!.ToJsonString()));
        Assert.That(bad["rader"]!.ToJsonString(), Is.EqualTo(valid["rader"]!.ToJsonString()));
        // det giltiga fallet ändras inte av de negativa
        Assert.That(valid["leverans"]!["adress"]!["postnummer"]!.GetValue<string>(), Does.Match(@"^[0-9]{3} [0-9]{2}$"));

        var missing = ByField(cases, TestRule.Missing)["rader[0].sku"].Scenario.Body!;
        Assert.That(missing["rader"]![0]!.AsObject().ContainsKey("sku"), Is.False);
        Assert.That(missing["rader"]![0]!["antal"]!.ToJsonString(), Is.EqualTo(valid["rader"]![0]!["antal"]!.ToJsonString()));
    }

    [Test]
    public void Lists_get_rules_for_too_few_and_too_many_items()
    {
        var cases = SpecText.Cases(SpecText.Post(Order), 3, out _);
        var few = ByField(cases, TestRule.TooFewItems)["rader"];
        var many = ByField(cases, TestRule.TooManyItems)["rader"];
        Assert.That(few.Scenario.Body!["rader"]!.AsArray(), Is.Empty);
        Assert.That(few.Meta.Limit, Is.EqualTo("1"));
        Assert.That(many.Scenario.Body!["rader"]!.AsArray(), Has.Count.EqualTo(4));
        Assert.That(many.Meta.Limit, Is.EqualTo("3"));
        Assert.That(PlainLanguage.RulePhrase(few.Meta), Is.EqualTo("rader har för få element (under minItems)"));
        Assert.That(PlainLanguage.RulePhrase(many.Meta), Is.EqualTo("rader har för många element (över maxItems)"));
        Assert.That(few.Scenario.Name, Is.EqualTo("mk_rader_too_few_items"));
    }

    [Test]
    public void Plain_text_for_nested_fields()
    {
        var cases = SpecText.Cases(SpecText.Post(Order), 3, out _);
        Assert.That(PlainLanguage.RulePhrase(ByField(cases, TestRule.PatternMismatch)["leverans.adress.postnummer"].Meta), Is.EqualTo("leverans.adress.postnummer följer inte mönstret"));
        Assert.That(PlainLanguage.RulePhrase(ByField(cases, TestRule.Missing)["rader[0].sku"].Meta), Is.EqualTo("rader[0].sku saknas"));
    }

    [Test]
    public void Test_names_are_unique_and_without_brackets()
    {
        var names = SpecText.Cases(SpecText.Post(Order), 3, out _).Select(c => c.Scenario.Name).ToList();
        Assert.That(names, Is.Unique);
        Assert.That(names, Has.None.Contain("["));
        Assert.That(names, Does.Contain("mk_missing_rader_0.sku"));
    }

    [Test]
    public void Depth_over_four_levels_is_not_tested()
    {
        var cases = SpecText.Cases(SpecText.Post("""
            type: object
            properties:
              a:
                type: object
                properties:
                  b:
                    type: object
                    properties:
                      c:
                        type: object
                        properties:
                          d: {type: integer, minimum: 1}
                          e:
                            type: object
                            properties:
                              f: {type: integer, minimum: 1}
            """), 1, out _);
        var fields = cases.Select(c => c.Meta.Field).Where(f => f is not null).ToList();
        Assert.That(fields, Does.Contain("a.b.c.d"));
        Assert.That(fields, Has.None.EqualTo("a.b.c.e.f"));
    }

    [Test]
    public void More_than_60_cases_are_capped_and_the_deepest_are_dropped_first()
    {
        var props = string.Join("\n", Enumerable.Range(1, 30).Select(i => $"  f{i:00}: {{type: integer, minimum: 1, maximum: 5}}"));
        var yaml = $"type: object\nproperties:\n{props}\n  nested:\n    type: object\n    properties:\n      deep: {{type: integer, minimum: 1}}";
        var op = SpecText.Load(SpecText.Post(yaml))[0];
        var cases = new ScenarioGenerator(new[] { op }, new GeneratorOptions { Seed = 1 }).GenerateCases(op);
        var negatives = cases.Where(c => c.Scenario.Kind == "negative").ToList();
        Assert.That(negatives, Has.Count.EqualTo(NegativeCaseGenerator.MaxCases));
        Assert.That(negatives.Select(c => c.Meta.Field), Does.Not.Contain("nested.deep"), "det nästlade faller bort före toppnivåfälten");
        var valid = cases[0].Scenario.Body!;
        NegativeCaseGenerator.Generate(valid, op.BodySchema!, out var capped);
        Assert.That(capped, Is.True);
        Assert.That(NegativeCaseGenerator.Generate(JsonNode.Parse("""{"a":5}""")!, SpecText.Schema("{type: object, properties: {a: {type: integer, minimum: 1}}}"), out var notCapped), Is.Not.Empty);
        Assert.That(notCapped, Is.False);
    }

    [Test]
    public void Top_level_list_gets_negative_cases_named_after_the_list()
    {
        var cases = SpecText.Cases(SpecText.Post("""
            type: array
            minItems: 1
            maxItems: 2
            items:
              type: object
              required: [n]
              properties:
                n: {type: integer, minimum: 1}
            """), 1, out _);
        Assert.That(cases[0].Scenario.Body, Is.InstanceOf<JsonArray>());
        var fields = cases.Select(c => c.Meta.Field).ToList();
        Assert.That(fields, Is.SupersetOf(new[] { "listan", "listan[0]", "listan[0].n" }));
        var many = cases.Single(c => c.Meta.Rule == TestRule.TooManyItems);
        Assert.That(many.Scenario.Body, Is.InstanceOf<JsonArray>().And.Count.EqualTo(3));
        var low = cases.Single(c => c.Meta.Rule == TestRule.BelowMinimum);
        Assert.That(low.Scenario.Body![0]!["n"]!.GetValue<long>(), Is.EqualTo(0));
    }
}

public class FormatNegativeTests
{
    private static List<GeneratedCase> Cases(string props, string required = "[]") =>
        SpecText.Cases(SpecText.Post($"type: object\nrequired: {required}\nproperties:\n{props}"), 1, out _);

    [TestCase("email", "abc")]
    [TestCase("date", "2023-13-45")]
    [TestCase("uuid", "inte-ett-uuid")]
    [TestCase("uri", "http://")]
    public void Bad_format_sends_an_invalid_value(string format, string expected)
    {
        var c = Cases($"  f: {{type: string, format: {format}}}").Single(x => x.Meta.Rule == TestRule.BadFormat);
        Assert.That(c.Scenario.Body!["f"]!.GetValue<string>(), Is.EqualTo(expected));
        Assert.That(c.Meta.Limit, Is.EqualTo(format));
        Assert.That(PlainLanguage.RulePhrase(c.Meta), Is.EqualTo("f har fel format"));
    }

    [Test]
    public void Date_time_gets_an_invalid_value()
    {
        var c = Cases("  f: {type: string, format: date-time}").Single(x => x.Meta.Rule == TestRule.BadFormat);
        Assert.That(DateTimeOffset.TryParse(c.Scenario.Body!["f"]!.GetValue<string>(), out _), Is.False);
    }

    [Test]
    public void Unknown_formats_and_formats_with_pattern_get_no_format_case()
    {
        Assert.That(Cases("  f: {type: string, format: int64x}"), Has.None.Matches<GeneratedCase>(c => c is { Meta.Rule: TestRule.BadFormat }));
        Assert.That(Cases("  f: {type: string, format: email, pattern: '^a.*@b.se$'}"), Has.None.Matches<GeneratedCase>(c => c is { Meta.Rule: TestRule.BadFormat }));
    }

    [Test]
    public void String_field_gets_an_object_as_wrong_type()
    {
        var c = Cases("  f: {type: string}").Single(x => x.Meta.Rule == TestRule.WrongType);
        Assert.That(c.Scenario.Body!["f"], Is.InstanceOf<JsonObject>());
        Assert.That(c.Meta.SentValue, Is.EqualTo("{}"));
    }

    [Test]
    public void Empty_text_is_tested_only_for_required_strings_whose_schema_forbids_it()
    {
        var cases = Cases("  a: {type: string}\n  b: {type: string, minLength: 3}\n  c: {type: string, minLength: 1}\n  d: {type: string, pattern: '^[a-z]*$'}\n  e: {type: string, enum: [x, y]}\n"
            + "  f: {type: string, pattern: '^[a-z]+$'}\n  g: {type: string, format: email}\n  h: {type: string, enum: ['', x]}\n  i: {type: string, minLength: 3}", "[a, b, c, d, e, f, g, h]");
        var empty = cases.Where(c => c.Meta.Rule == TestRule.Empty).Select(c => c.Meta.Field).ToList();
        Assert.That(empty, Is.EqualTo(new[] { "b", "e", "f", "g" }),
            "a tillåter tom text, c får \"\" som för kort, d:s mönster matchar tom text, h har \"\" i enum, i är valfritt");
        var b = cases.Single(c => c.Meta.Rule == TestRule.Empty && c.Meta.Field == "b");
        Assert.That(b.Scenario.Body!["b"]!.GetValue<string>(), Is.Empty);
        Assert.That(PlainLanguage.RulePhrase(b.Meta), Is.EqualTo("b är tom"));
    }
}

public class OneOfTests
{
    [Test]
    public void One_valid_case_per_top_level_alternative()
    {
        var cases = SpecText.Cases(SpecText.Post("""
            oneOf:
              - {type: object, required: [card], properties: {card: {type: string, minLength: 4}}}
              - {type: object, required: [iban], properties: {iban: {type: string}}}
            """), 1, out _);
        Assert.That(cases[0].Scenario.Body!.AsObject().ContainsKey("card"), Is.True, "första alternativet är grundfallet");
        var second = cases.Single(c => c.Meta.Rule == TestRule.Valid && c.Meta.Limit is not null);
        Assert.That(second.Scenario.Body!.AsObject().ContainsKey("iban"), Is.True);
        Assert.That(second.Scenario.Kind, Is.EqualTo("valid"));
        Assert.That(second.Scenario.ExpectedStatus, Is.EqualTo(new[] { 201 }));
        Assert.That(PlainLanguage.RulePhrase(second.Meta), Is.EqualTo("giltig indata – alternativ 2"));
    }

    private const string Payment = """
        components:
          schemas:
            Faktura:
              type: object
              required: [kind, referens]
              properties:
                kind: {type: string, enum: [faktura]}
                referens: {type: string, minLength: 5}
            Kort:
              type: object
              required: [kind, last4]
              properties:
                kind: {type: string, enum: [kort]}
                last4: {type: string, pattern: '^[0-9]{4}$'}
        """;

    private static string PaymentSpec() => $$$"""
        openapi: 3.0.3
        info: {title: t, version: 1.0.0}
        paths:
          /x:
            post:
              operationId: mk
              requestBody:
                content:
                  application/json:
                    schema:
                      type: object
                      required: [betalning]
                      properties:
                        betalning:
                          oneOf:
                            - $ref: '#/components/schemas/Kort'
                            - $ref: '#/components/schemas/Faktura'
                          discriminator: {propertyName: kind}
              responses: {'201': {description: ok}}
        {{{Payment}}}
        """;

    [Test]
    public void Alternatives_in_a_field_get_a_case_with_the_discriminator_value()
    {
        var cases = SpecText.Cases(PaymentSpec(), 1, out _);
        Assert.That(cases[0].Scenario.Body!["betalning"]!["kind"]!.GetValue<string>(), Is.EqualTo("kort"));
        var alt = cases.Single(c => c.Meta.Rule == TestRule.Valid && c.Meta.Field is not null);
        Assert.That(alt.Scenario.Body!["betalning"]!["kind"]!.GetValue<string>(), Is.EqualTo("faktura"));
        Assert.That(alt.Scenario.Body!["betalning"]!["referens"]!.GetValue<string>(), Has.Length.GreaterThanOrEqualTo(5));
        Assert.That(PlainLanguage.RulePhrase(alt.Meta), Is.EqualTo("giltig indata – betalning faktura"));
        Assert.That(alt.Scenario.Name, Is.EqualTo("mk_valid_betalning_faktura"));
    }

    [Test]
    public void Primitive_alternatives_give_the_first_alternatives_type_not_an_empty_object()
    {
        var cases = SpecText.Cases(SpecText.Post("""
            type: object
            required: [v]
            properties:
              v:
                oneOf:
                  - {type: integer, minimum: 5, maximum: 9}
                  - {type: string}
            """), 1, out _);
        var v = cases[0].Scenario.Body!["v"]!;
        Assert.That(v.GetValueKind(), Is.EqualTo(System.Text.Json.JsonValueKind.Number));
        Assert.That(v.GetValue<long>(), Is.InRange(5, 9));
        var alt = cases.Single(c => c.Meta.Rule == TestRule.Valid && c.Meta.Field == "v");
        Assert.That(alt.Scenario.Body!["v"]!.GetValueKind(), Is.EqualTo(System.Text.Json.JsonValueKind.String));
        Assert.That(cases.Single(c => c.Meta.Rule == TestRule.BelowMinimum).Meta.Field, Is.EqualTo("v"));
    }

    [Test]
    public void Without_negative_cases_no_alternative_cases_are_created()
    {
        var ops = SpecText.Load(PaymentSpec());
        var gen = new ScenarioGenerator(ops, new GeneratorOptions { Seed = 1 });
        Assert.That(gen.Generate(ops[0], includeNegatives: false), Has.Count.EqualTo(1));
    }
}

public class BodyKindTests
{
    [Test]
    public void Form_is_sent_with_its_media_type_and_encoded_as_field_pairs()
    {
        var cases = SpecText.Cases(SpecText.Post("type: object\nrequired: [a]\nproperties:\n  a: {type: string}\n  n: {type: integer}\n  l: {type: array, items: {type: string}}",
            contentType: "application/x-www-form-urlencoded"), 1, out var op);
        Assert.That(op.BodyContentType, Is.EqualTo("application/x-www-form-urlencoded"));
        Assert.That(op.UnsupportedBody, Is.Null);
        Assert.That(cases.Select(c => c.Scenario.ContentType), Has.All.EqualTo("application/x-www-form-urlencoded"));
        var body = JsonNode.Parse("""{"a":"x y","l":["p","q&r"],"n":3}""")!;
        Assert.That(TestRunner.BodyText(body, "application/x-www-form-urlencoded"), Is.EqualTo("a=x%20y&l=p&l=q%26r&n=3"));
        Assert.That(TestRunner.BodyText(body, null), Is.EqualTo("""{"a":"x y","l":["p","q&r"],"n":3}""").Or.EqualTo("""{"a":"x y","l":["p","q&r"],"n":3}"""));
    }

    [Test]
    public void Json_has_no_own_media_type_in_the_scenario()
    {
        var cases = SpecText.Cases(SpecText.Post("type: object\nproperties:\n  a: {type: string}"), 1, out _);
        Assert.That(cases.Select(c => c.Scenario.ContentType), Has.All.Null);
    }

    [Test]
    public void Unsupported_media_types_are_marked_on_the_operation_and_get_no_body()
    {
        var cases = SpecText.Cases(SpecText.Post("type: object\nproperties:\n  f: {type: string, format: binary}", contentType: "multipart/form-data"), 1, out var op);
        Assert.That(op.UnsupportedBody, Is.EqualTo("multipart/form-data"));
        Assert.That(cases, Has.Count.EqualTo(1));
        Assert.That(cases[0].Scenario.Body, Is.Null);
    }

    [Test]
    public void Top_level_list_is_generated_as_a_list_of_the_right_type()
    {
        var cases = SpecText.Cases(SpecText.Post("type: array\nminItems: 2\nitems: {type: string, format: email}"), 1, out _);
        var list = cases[0].Scenario.Body!.AsArray();
        Assert.That(list, Has.Count.EqualTo(2));
        Assert.That(list.Select(x => x!.GetValue<string>()), Has.All.Contain("@"));
    }
}

public class RunnerBodyTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("testgen-body-").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    private sealed class Capture(Func<HttpRequestMessage, string?, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(string? ContentType, string? Body)> Requests { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var text = req.Content is null ? null : await req.Content.ReadAsStringAsync(ct);
            lock (Requests) Requests.Add((req.Content?.Headers.ContentType?.MediaType, text));
            return answer(req, text);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body = "{}") =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private async Task<(TestRunner Runner, Capture Api)> Run(string yaml, Func<HttpRequestMessage, string?, HttpResponseMessage> answer)
    {
        var spec = Path.Combine(_dir, "s.yaml");
        File.WriteAllText(spec, yaml);
        var api = new Capture(answer);
        var runner = new TestRunner(new RunOptions { Spec = spec, Api = "http://fake.test", Seed = 1 }, new StringWriter { NewLine = "\n" }, api);
        await runner.RunAsync();
        return (runner, api);
    }

    [Test]
    public async Task Body_with_lists_of_strings_and_objects_does_not_crash_but_is_sent()
    {
        var (runner, api) = await Run(SpecText.Post("""
            type: object
            required: [tags, rader]
            properties:
              tags: {type: array, minItems: 2, items: {type: string}}
              rader:
                type: array
                items:
                  type: object
                  required: [n]
                  properties:
                    n: {type: integer, minimum: 1}
            """), (r, b) => b is not null && b.Contains("\"n\":0") ? Json(HttpStatusCode.BadRequest) : Json(HttpStatusCode.Created));
        Assert.That(runner.Outcomes, Has.None.Matches<TestOutcome>(o => o is { Result.Status: null }), "alla tester skickade något");
        var valid = runner.Outcomes.First(o => o.Test.Case?.Rule == TestRule.Valid);
        Assert.That(valid.Outcome, Is.EqualTo("OK"));
        Assert.That(api.Requests[0].Body, Does.Contain("\"tags\":[").And.Contain("\"rader\":[{\"n\":"));
    }

    [Test]
    public async Task Top_level_list_is_sent_as_a_json_array()
    {
        var (runner, api) = await Run(SpecText.Post("type: array\nminItems: 1\nitems: {type: object, required: [n], properties: {n: {type: integer}}}"),
            (r, b) => Json(HttpStatusCode.Created));
        Assert.That(api.Requests[0].ContentType, Is.EqualTo("application/json"));
        Assert.That(api.Requests[0].Body, Does.StartWith("[{\"n\":"));
        Assert.That(runner.Outcomes[0].Outcome, Is.EqualTo("OK"));
    }

    [Test]
    public async Task Form_is_sent_as_urlencoded()
    {
        var (_, api) = await Run(SpecText.Post("type: object\nrequired: [a]\nproperties:\n  a: {type: string}", contentType: "application/x-www-form-urlencoded"),
            (r, b) => Json(HttpStatusCode.Created));
        Assert.That(api.Requests[0].ContentType, Is.EqualTo("application/x-www-form-urlencoded"));
        Assert.That(api.Requests[0].Body, Does.Match("^a=[^&]+$"));
    }

    [Test]
    public async Task Unsupported_body_is_skipped_with_clear_text_and_sends_nothing()
    {
        var (runner, api) = await Run(SpecText.Post("type: object\nproperties:\n  f: {type: string, format: binary}", contentType: "multipart/form-data"),
            (r, b) => Json(HttpStatusCode.Created));
        Assert.That(api.Requests, Is.Empty);
        var o = runner.Outcomes.Single();
        Assert.That(o.Outcome, Is.EqualTo("HOPPAD"));
        Assert.That(o.Text.Explanation, Does.Contain("stöder inte bodyn (multipart/form-data) ännu").And.Contain("känd begränsning").And.Not.Contain("Rapportera"));
        Assert.That(o.Cause, Is.EqualTo(FailureCause.Unsupported), "en känd begränsning, inte Fel i testgen");
    }

    [Test]
    public async Task Internal_error_while_building_or_sending_the_test_is_skipped_with_report_text()
    {
        var (runner, _) = await Run(SpecText.Post("type: object\nproperties:\n  a: {type: string}"),
            (r, b) => throw new NullReferenceException("boom"));
        var o = runner.Outcomes[0];
        Assert.That(o.Outcome, Is.EqualTo("HOPPAD"));
        Assert.That(o.Text.Explanation, Does.StartWith("Verktyget kunde inte bygga testet (internt fel): boom").And.Contain("Rapportera till den som underhåller testgen."));
        Assert.That(o.Text.Cause, Is.Not.EqualTo(FailureCause.Expectation));
    }

    [Test]
    public async Task Rejection_with_422_is_accepted_when_the_spec_lists_422()
    {
        var (runner, _) = await Run(SpecText.Post("type: object\nrequired: [a]\nproperties:\n  a: {type: string, maxLength: 3}",
            "'201': {description: ok}\n'422': {description: nej}"),
            (r, b) => b is not null && b.Contains("\"a\":\"aaaa\"") ? Json((HttpStatusCode)422) : b is not null && b.Contains("\"a\":") && !b.Contains("\"a\":\"") ? Json((HttpStatusCode)422) : Json(HttpStatusCode.Created));
        var tooLong = runner.Outcomes.Single(o => o.Test.Case?.Rule == TestRule.TooLong);
        Assert.That(tooLong.Outcome, Is.EqualTo("OK"));
    }

    [Test]
    public async Task Rejection_with_400_is_accepted_even_when_the_spec_only_lists_422()
    {
        var (runner, _) = await Run(SpecText.Post("type: object\nrequired: [a]\nproperties:\n  a: {type: string, maxLength: 3}",
            "'201': {description: ok}\n'422': {description: nej}"),
            (r, b) => b is not null && b.Contains("\"a\":\"aaaa\"") ? Json(HttpStatusCode.BadRequest) : Json(HttpStatusCode.Created));
        var tooLong = runner.Outcomes.Single(o => o.Test.Case?.Rule == TestRule.TooLong);
        Assert.That(tooLong.Outcome, Is.EqualTo("OK"));
        Assert.That(tooLong.Test.ExpectedStatus.Select(e => e.ToString()), Is.EqualTo(new[] { "400", "422" }));
    }
}

public class RulePhraseTests
{
    [Test]
    public void New_rules_have_phrases_and_explanations()
    {
        Assert.That(PlainLanguage.RulePhrase(new CaseMeta(TestRule.BadFormat, "email", "email", "\"abc\"")), Is.EqualTo("email har fel format"));
        Assert.That(PlainLanguage.RulePhrase(new CaseMeta(TestRule.Empty, "namn", null, "\"\"")), Is.EqualTo("namn är tom"));
        Assert.That(PlainLanguage.RulePhrase(CaseMeta.ValidAlternative("betalning", "faktura")), Is.EqualTo("giltig indata – betalning faktura"));
        Assert.That(PlainLanguage.RulePhrase(CaseMeta.ValidAlternative("", "faktura")), Is.EqualTo("giltig indata – faktura"));
        Assert.That(PlainLanguage.RulePhrase(CaseMeta.Valid), Is.EqualTo("giltig indata"));

        var info = new TestInfo("T-001", "G", "POST /x", "POST", "mk", null, "x", new[] { new StatusExpectation(400, 400) }, new CaseMeta(TestRule.BadFormat, "email", "email", "\"abc\""));
        var res = new CaseResult(201, false, false, "FEL", new());
        Assert.That(PlainLanguage.Explain(info, res).Explanation, Does.Contain("Vi skickade email = \"abc\"").And.Contain("en e-postadress"));
        var empty = info with { Case = new CaseMeta(TestRule.Empty, "namn", null, "\"\"") };
        Assert.That(PlainLanguage.Explain(empty, res).Explanation, Does.Contain("namn som tom text"));
        var few = info with { Case = new CaseMeta(TestRule.TooFewItems, "rader", "1", "[]") };
        Assert.That(PlainLanguage.Explain(few, res).Explanation, Does.Contain("rader med 0 element").And.Contain("minst 1"));
        var many = info with { Case = new CaseMeta(TestRule.TooManyItems, "rader", "3", "[1,2,3,4]") };
        Assert.That(PlainLanguage.Explain(many, res).Explanation, Does.Contain("rader med 4 element").And.Contain("högst 3"));
    }
}
