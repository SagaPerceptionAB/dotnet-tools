using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Readers;
using NUnit.Framework;
using TestGen.Core;
using TestGen.Core.Run;

namespace TestGen.Tests;

/// <summary>Svarskontrollen mot specens schema: hårda fel, varningar, allOf/oneOf/$ref och meddelandena på svenska.</summary>
public class ResponseSchemaValidationTests
{
    private static OpenApiSchema Schema(string yaml) =>
        new OpenApiStringReader().ReadFragment<OpenApiSchema>(yaml, Microsoft.OpenApi.OpenApiSpecVersion.OpenApi3_0, out _);

    private static SchemaCheckResult Check(string json, string yaml) => ResponseSchemaCheck.Check(JsonNode.Parse(json), Schema(yaml));

    private const string Order = """
        type: object
        required: [id, total, status]
        properties:
          id: { type: integer }
          total: { type: number }
          status: { type: string, enum: [draft, confirmed, shipped] }
          placedAt: { type: string, format: date-time }
          rader:
            type: array
            minItems: 1
            items:
              type: object
              required: [antal]
              properties:
                antal: { type: integer, minimum: 1 }
        """;

    [Test]
    public void Valid_response_gives_neither_errors_nor_warnings()
    {
        var r = Check("""{"id":1,"total":12.5,"status":"draft","placedAt":"2026-11-01T10:00:00Z","rader":[{"antal":2}]}""", Order);
        Assert.That((r.Errors, r.Warnings), Is.EqualTo((new List<string>(), new List<string>())));
    }

    [Test]
    public void Missing_required_field()
    {
        Assert.That(Check("""{"id":1,"status":"draft"}""", Order).Errors, Is.EqualTo(new[] { "svar.total saknas (obligatoriskt enligt specen)" }));
        Assert.That(Check("""{"id":1,"total":1,"status":"draft","rader":[{}]}""", Order).Errors,
            Is.EqualTo(new[] { "svar.rader[0].antal saknas (obligatoriskt enligt specen)" }));
    }

    [Test]
    public void Wrong_type_nested_and_in_lists_with_swedish_type_names()
    {
        Assert.That(Check("""{"id":1,"total":"12.5","status":"draft","rader":[{"antal":"2"}]}""", Order).Errors, Is.EqualTo(new[]
        {
            "svar.total är text men ska vara tal",
            "svar.rader[0].antal är text men ska vara heltal",
        }));
        Assert.That(Check("""{"id":1.5,"total":1,"status":"draft"}""", Order).Errors, Is.EqualTo(new[] { "svar.id är decimaltal men ska vara heltal" }));
        Assert.That(Check("""{"id":2.0,"total":1,"status":"draft"}""", Order).Errors, Has.Count.EqualTo(1), "5.0 är inte ett heltal i JSON-texten");
        Assert.That(Check("""{"id":true,"total":1,"status":"draft"}""", Order).Errors, Is.EqualTo(new[] { "svar.id är sant/falskt men ska vara heltal" }));
        Assert.That(Check("""{"id":1,"total":1,"status":"draft","rader":{}}""", Order).Errors, Is.EqualTo(new[] { "svar.rader är ett objekt men ska vara en lista" }));
        Assert.That(Check("""{"a":{"b":[1]}}""", "type: object\nproperties:\n  a: { type: object, properties: { b: { type: string } } }").Errors,
            Is.EqualTo(new[] { "svar.a.b är en lista men ska vara text" }));
        Assert.That(Check("""{"id":1,"total":1,"status":"draft"}""", "type: array\nitems: { type: string }").Errors,
            Is.EqualTo(new[] { "svar är ett objekt men ska vara en lista" }));
    }

    [Test]
    public void Enum_outside_list_shows_allowed_values()
    {
        Assert.That(Check("""{"id":1,"total":1,"status":"bogus"}""", Order).Errors,
            Is.EqualTo(new[] { "svar.status 'bogus' finns inte bland tillåtna värden (draft, confirmed, shipped)" }));
        Assert.That(Check("""{"level":4}""", "type: object\nproperties:\n  level: { type: integer, enum: [1, 2, 3] }").Errors,
            Is.EqualTo(new[] { "svar.level 4 finns inte bland tillåtna värden (1, 2, 3)" }));
    }

    [TestCase("2026-11-01T10:00:00Z", true)]
    [TestCase("2026-11-01T10:00:00.123+01:00", true)]
    [TestCase("2026-11-01t10:00:00z", true)]
    [TestCase("2026-11-01 10:00", false)]
    [TestCase("yesterday", false)]
    [TestCase("2026-11-01T10:00:00", false)]
    [TestCase("2026-13-01T10:00:00Z", false)]
    [TestCase("2026-11-01T25:00:00Z", false)]
    public void Date_time_per_RFC_3339(string value, bool ok)
    {
        var r = Check($$"""{"at":"{{value}}"}""", "type: object\nproperties:\n  at: { type: string, format: date-time }");
        Assert.That(r.Errors, ok ? Is.Empty : Is.EqualTo(new[] { $"svar.at '{value}' är inte ett giltigt datum med tid" }));
    }

    [TestCase("date", "2026-02-28", true)]
    [TestCase("date", "2026-02-30", false)]
    [TestCase("date", "28/02/2026", false)]
    [TestCase("uuid", "585b88a3-20c9-4b16-ac5d-71778d6158ab", true)]
    [TestCase("uuid", "nej", false)]
    [TestCase("email", "ann@example.se", true)]
    [TestCase("email", "ann.example.se", false)]
    [TestCase("uri", "https://example.se/a?b=1", true)]
    [TestCase("uri", "inte en adress", false)]
    public void Format_that_cannot_be_parsed(string format, string value, bool ok)
    {
        var r = Check($$"""{"x":"{{value}}"}""", "type: object\nproperties:\n  x: { type: string, format: " + format + " }");
        Assert.That(r.Errors.Count == 0, Is.EqualTo(ok), string.Join("; ", r.Errors));
        if (!ok) Assert.That(r.Errors.Single(), Does.StartWith($"svar.x '{value}' är inte "));
    }

    [Test]
    public void Unknown_format_is_not_checked() =>
        Assert.That(Check("""{"x":"vad som helst"}""", "type: object\nproperties:\n  x: { type: string, format: hostname }").Errors, Is.Empty);

    [Test]
    public void Null_is_error_if_field_is_not_nullable()
    {
        const string yaml = "type: object\nproperties:\n  a: { type: string }\n  b: { type: string, nullable: true }\n  c: { type: object, properties: { x: { type: integer } } }";
        Assert.That(Check("""{"a":null,"b":null,"c":null}""", yaml).Errors, Is.EqualTo(new[]
        {
            "svar.a är null men fältet får inte vara null (enligt specen)",
            "svar.c är null men fältet får inte vara null (enligt specen)",
        }));
        Assert.That(Check("""{"b":null}""", yaml).Errors, Is.Empty, "saknade fält utan required är ok");
    }

    [Test]
    public void WriteOnly_in_response_is_error_and_not_part_of_required()
    {
        const string yaml = """
            type: object
            required: [id, password]
            properties:
              id: { type: integer }
              password: { type: string, writeOnly: true, maxLength: 4 }
            """;
        Assert.That(Check("""{"id":1}""", yaml).Errors, Is.Empty, "writeOnly kan inte krävas i svaret");
        Assert.That(Check("""{"id":1,"password":"hemligt"}""", yaml).Errors,
            Is.EqualTo(new[] { "svar.password skickas tillbaka – fältet är writeOnly och ska aldrig finnas i svaret" }));
    }

    [Test]
    public void Bounds_for_list_length_text_and_numbers()
    {
        Assert.That(Check("""{"id":1,"total":1,"status":"draft","rader":[]}""", Order).Errors, Is.EqualTo(new[] { "svar.rader har 0 poster men ska ha minst 1" }));
        Assert.That(Check("""{"id":1,"total":1,"status":"draft","rader":[{"antal":0}]}""", Order).Errors, Is.EqualTo(new[] { "svar.rader[0].antal är 0 men ska vara minst 1" }));
        const string yaml = """
            type: object
            properties:
              tags: { type: array, maxItems: 1, items: { type: string, minLength: 2, maxLength: 3 } }
              pct: { type: number, maximum: 100, exclusiveMaximum: true }
            """;
        Assert.That(Check("""{"tags":["a","bbbb"],"pct":100}""", yaml).Errors, Is.EqualTo(new[]
        {
            "svar.tags har 2 poster men ska ha högst 1",
            "svar.tags[0] 'a' är för kort (minst 2 tecken, fick 1)",
            "svar.tags[1] 'bbbb' är för långt (högst 3 tecken, fick 4)",
            "svar.pct är 100 men ska vara mindre än 100",
        }));
    }

    [Test]
    public void Extra_field_is_warning_but_error_with_additionalProperties_false()
    {
        var r = Check("""{"id":1,"total":1,"status":"draft","internalCost":3,"rader":[{"antal":1,"x":1},{"antal":1,"x":2}]}""", Order);
        Assert.That(r.Errors, Is.Empty);
        Assert.That(r.Warnings, Is.EquivalentTo(new[]
        {
            "svar har extra fält som inte finns i specen: internalCost (varning)",
            "svar.rader[0] har extra fält som inte finns i specen: x (varning)",   // [1] är samma varning och slås ihop
        }));

        var strict = Check("""{"id":1,"hemlig":2}""", "type: object\nadditionalProperties: false\nproperties:\n  id: { type: integer }");
        Assert.That(strict.Warnings, Is.Empty);
        Assert.That(strict.Errors, Is.EqualTo(new[] { "svar har extra fält som inte får finnas (additionalProperties är false i specen): hemlig" }));
    }

    [Test]
    public void Free_objects_and_additionalProperties_with_schema_give_no_warnings()
    {
        Assert.That(Check("""{"meta":{"a":1,"b":"x"}}""", "type: object\nproperties:\n  meta: { type: object }").Warnings, Is.Empty);
        const string map = "type: object\nproperties:\n  m: { type: object, additionalProperties: { type: integer } }";
        var r = Check("""{"m":{"a":1,"b":"x"}}""", map);
        Assert.That((r.Warnings, r.Errors), Is.EqualTo((new List<string>(), new List<string> { "svar.m.b är text men ska vara heltal" })));
    }

    [Test]
    public void AllOf_is_merged_and_extra_fields_count_against_all_parts()
    {
        const string yaml = """
            allOf:
              - type: object
                required: [a]
                properties: { a: { type: integer } }
              - type: object
                required: [b]
                properties: { b: { type: string } }
            """;
        var ok = Check("""{"a":1,"b":"x"}""", yaml);
        Assert.That((ok.Errors, ok.Warnings), Is.EqualTo((new List<string>(), new List<string>())));
        Assert.That(Check("""{"a":1,"b":"x","c":1}""", yaml).Warnings, Is.EqualTo(new[] { "svar har extra fält som inte finns i specen: c (varning)" }));
        Assert.That(Check("""{"a":"1"}""", yaml).Errors, Is.EqualTo(new[] { "svar.b saknas (obligatoriskt enligt specen)", "svar.a är text men ska vara heltal" }));
    }

    [Test]
    public void OneOf_and_AnyOf_pass_if_any_alternative_matches()
    {
        const string yaml = """
            type: object
            properties:
              pay:
                oneOf:
                  - type: object
                    required: [card]
                    properties: { card: { type: string } }
                  - type: object
                    required: [iban]
                    properties: { iban: { type: string } }
              v:
                anyOf:
                  - { type: integer }
                  - { type: string, format: uuid }
            """;
        var ok = Check("""{"pay":{"iban":"SE12"},"v":"585b88a3-20c9-4b16-ac5d-71778d6158ab"}""", yaml);
        Assert.That((ok.Errors, ok.Warnings), Is.EqualTo((new List<string>(), new List<string>())), "iban finns i det andra alternativet: inget extra fält");
        Assert.That(Check("""{"pay":{"iban":"SE12"},"v":7}""", yaml).Errors, Is.Empty);
        var bad = Check("""{"pay":{"x":1},"v":true}""", yaml);
        Assert.That(bad.Errors, Is.Not.Empty);
        Assert.That(bad.Errors, Has.Some.Contains("svar.v är"));
        Assert.That(bad.Errors, Has.Some.Contains("svar.pay."));
    }

    [Test]
    public void Ref_is_resolved_via_spec_and_circularity_is_harmless()
    {
        var yaml = """
            openapi: 3.0.3
            info: { title: t, version: '1' }
            paths:
              /nodes:
                get:
                  operationId: getNode
                  responses:
                    '200':
                      description: ok
                      content:
                        application/json:
                          schema: { $ref: '#/components/schemas/Node' }
                    '404':
                      description: nej
                      content:
                        application/json:
                          schema: { $ref: '#/components/schemas/Err' }
            components:
              schemas:
                Node:
                  type: object
                  required: [name]
                  properties:
                    name: { type: string }
                    child: { $ref: '#/components/schemas/Node' }
                Err:
                  type: object
                  required: [message]
                  properties: { message: { type: string } }
            """;
        var path = Path.Combine(Path.GetTempPath(), $"testgen-ref-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, yaml);
        try
        {
            var op = OpenApiLoader.Load(path).Single();
            Assert.That(op.ResponseSchemas!.Keys, Is.EquivalentTo(new[] { 200, 404 }));
            Assert.That(ResponseSchemaCheck.Check(JsonNode.Parse("""{"name":"a","child":{"name":"b","child":{"name":5}}}"""), op.ResponseSchemas[200]).Errors,
                Is.EqualTo(new[] { "svar.child.child.name är heltal men ska vara text" }));
            Assert.That(ResponseSchemaCheck.Check(JsonNode.Parse("""{"fel":1}"""), op.ResponseSchemas[404]).Errors,
                Is.EqualTo(new[] { "svar.message saknas (obligatoriskt enligt specen)" }));
        }
        finally { File.Delete(path); }
    }

    [Test]
    public void At_most_ten_messages_then_and_N_more()
    {
        var items = string.Join(",", Enumerable.Range(0, 14).Select(_ => "\"x\""));
        var r = Check($$"""{"v":[{{items}}]}""", "type: object\nproperties:\n  v: { type: array, items: { type: integer } }");
        Assert.That(r.Errors, Has.Count.EqualTo(11));
        Assert.That(r.Errors[0], Is.EqualTo("svar.v[0] är text men ska vara heltal"));
        Assert.That(r.Errors[10], Is.EqualTo("och 4 till"));
        Assert.That(PlainLanguage.DetailSentence(r.Errors[10]), Is.EqualTo("Och 4 avvikelser till."));
    }

    [Test]
    public void No_response_or_no_schema_is_not_checked()
    {
        Assert.That(ResponseSchemaCheck.Check(null, Schema(Order)).HasErrors, Is.False);
        Assert.That(ResponseSchemaCheck.Check(JsonNode.Parse("{}"), null).HasErrors, Is.False);
    }

    // --- Genom TestRunner ----------------------------------------------------------------------

    private sealed class Api(string createdBody) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(createdBody, Encoding.UTF8, "application/json") });
    }

    private static async Task<TestRunner> Run(string body)
    {
        var runner = new TestRunner(new RunOptions { Spec = ShopSpec.Path, Api = "http://fake.test", Seed = 3, Only = "createProduct" }, new StringWriter(), new Api(body));
        await runner.RunAsync();
        return runner;
    }

    [Test]
    public async Task Warnings_show_separately_but_do_not_change_outcome()
    {
        var runner = await Run("""{"id":7,"name":"x","price":5,"sku":"ABC-1234","hemlig":1}""");
        var valid = runner.Outcomes.Single(o => o.Test.Case?.Rule == TestRule.Valid);
        Assert.That(valid.Result.Ok, Is.True);
        Assert.That(valid.Result.Warnings, Is.EqualTo(new[] { "svar har extra fält som inte finns i specen: hemlig (varning)" }));
        Assert.That(valid.Text.Explanation, Does.Not.Contain("Varning"), "varningen visas på egen rad, inte i förklaringen");
        Assert.That(PlainLanguage.WarningText(valid.Result.Warnings[0]), Is.EqualTo("svar har extra fält som inte finns i specen: hemlig"));
        Assert.That(valid.Outcome, Is.EqualTo("OK"));
    }

    [Test]
    public async Task Schema_error_gives_FEL_with_cause_System_and_with_wrong_status_as_extra_info()
    {
        var runner = await Run("""{"id":7,"name":"x","sku":"ABC-1234"}""");
        var valid = runner.Outcomes.Single(o => o.Test.Case?.Rule == TestRule.Valid);
        Assert.That(valid.Result.Ok, Is.False);
        Assert.That(valid.Result.Verdict, Is.EqualTo("FEL (svaret följer inte specen)"));
        Assert.That(valid.Result.Details, Is.EqualTo(new[] { "svar.price saknas (obligatoriskt enligt specen)" }));
        Assert.That(valid.Cause, Is.EqualTo(FailureCause.System));

        var negative = runner.Outcomes.First(o => o.Test.Case is { Rule: not TestRule.Valid });   // förväntar 400 men får 201
        Assert.That(negative.Result.Status, Is.EqualTo(201));
        Assert.That(negative.Result.Verdict, Does.StartWith("FEL (väntade 400"));
        Assert.That(negative.Result.Details, Is.EqualTo(new[] { "Svaret (201) följer dessutom inte specen:", "svar.price saknas (obligatoriskt enligt specen)" }));
        Assert.That(negative.Text.Explanation, Does.Contain("Svaret (201) följer dessutom inte specen: svar.price saknas (obligatoriskt enligt specen)."));
    }
}
