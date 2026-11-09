using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using TestGen.Core;
using TestGen.Core.Run;

namespace TestGen.Tests;

/// <summary>OpenAPI 3.1: null som alternativ, falska förväntningar i testdatan och 404 på värden som testgen hittat på.</summary>
public class SpecRobustnessTests
{
    private string _dir = "";

    [SetUp]
    public void Up() => _dir = Directory.CreateTempSubdirectory("testgen-robust-").FullName;

    [TearDown]
    public void Clean() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private const string Spec = """
        openapi: 3.1.1
        info: { title: t, version: '1' }
        paths:
          /sets/{set}:
            get:
              operationId: getThing
              parameters: [ { name: set, in: path, required: true, schema: { type: string } } ]
              responses:
                '200': { description: ok }
                '404': { description: finns inte }
          /things:
            post:
              operationId: createThing
              requestBody:
                required: true
                content:
                  application/json:
                    schema:
                      type: object
                      required: [name, device, count]
                      properties:
                        name: { type: string }
                        device:
                          oneOf:
                            - { type: 'null' }
                            - { $ref: '#/components/schemas/Device' }
                        note: { type: [string, 'null'] }
                        count: { type: [integer, string], pattern: '^-?(?:0|[1-9]\d*)$' }
                        tag: { type: [integer, string] }
              responses:
                '201': { description: skapad }
                '400': { description: nej }
        components:
          schemas:
            Device:
              type: object
              required: [model]
              properties:
                model: { type: string, minLength: 2 }
        """;

    private string SpecFile()
    {
        var path = Path.Combine(_dir, "t.openapi.yaml");
        File.WriteAllText(path, Spec);
        return path;
    }

    [Test]
    public void A_null_alternative_is_skipped_so_the_field_gets_an_object_and_no_null_variant()
    {
        var ops = OpenApiLoader.Load(SpecFile());
        var create = ops.Single(o => o.OperationId == "createThing");
        var cases = new ScenarioGenerator(ops, new GeneratorOptions { Seed = 3 }).GenerateCases(create);

        var valid = cases.Single(c => c.Meta.Rule == TestRule.Valid).Scenario.Body!;
        Assert.That(valid["device"], Is.InstanceOf<JsonObject>(), "inte en slumpad text");
        Assert.That(valid["device"]!["model"]!.GetValue<string>(), Has.Length.AtLeast(2));
        Assert.That(cases.Select(c => c.Scenario.Name), Has.None.Contain("alternativ"), "inget alternativ är bara null");
        Assert.That(cases.Where(c => c.Meta.Field == "device" || c.Meta.Field?.StartsWith("device.") == true).Select(c => c.Meta.Rule),
            Does.Contain(TestRule.Missing).And.Contain(TestRule.TooShort));
    }

    [Test]
    public void Empty_is_not_tested_for_a_plain_string_and_null_or_text_alternatives_get_a_value_the_schema_forbids()
    {
        var ops = OpenApiLoader.Load(SpecFile());
        var cases = new ScenarioGenerator(ops, new GeneratorOptions { Seed = 3 }).GenerateCases(ops.Single(o => o.OperationId == "createThing"));

        Assert.That(cases.Where(c => c.Meta.Rule == TestRule.Empty).Select(c => c.Meta.Field), Is.EqualTo(new[] { "device.model" }), "name får vara tom enligt schemat, model har minLength 2");
        Assert.That(cases.Where(c => c.Meta.Field == "note").Select(c => c.Meta.Rule), Is.All.Not.EqualTo(TestRule.Missing), "note är valfri");
        var wrong = cases.Where(c => c.Meta.Rule == TestRule.WrongType).ToDictionary(c => c.Meta.Field!, c => c.Scenario.Body![c.Meta.Field!]);
        Assert.That(wrong["count"]!.GetValue<string>(), Is.EqualTo("not-a-number"), "mönstret förbjuder texten");
        Assert.That(wrong["tag"], Is.InstanceOf<JsonObject>(), "[integer, string] utan mönster tillåter text");
    }

    private sealed class Answer(HttpStatusCode code) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(req.Method == HttpMethod.Get ? code : HttpStatusCode.Created) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
    }

    private async Task<TestRunner> RunAsync(HttpStatusCode code)
    {
        var runner = new TestRunner(new RunOptions { Spec = SpecFile(), Api = "http://fake.test", Seed = 1 }, new StringWriter(), new Answer(code));
        await runner.RunAsync();
        return runner;
    }

    [Test]
    public async Task A_documented_404_on_an_invented_id_is_inconclusive_with_missing_data_as_the_cause()
    {
        var runner = await RunAsync(HttpStatusCode.NotFound);

        var outcome = runner.Outcomes.Single(o => o.Test.Call.StartsWith("GET /sets/"));
        Assert.That(outcome.Outcome, Is.EqualTo("HOPPAD"));
        Assert.That(outcome.Cause, Is.EqualTo(FailureCause.MissingData));
        Assert.That(outcome.Text.Explanation, Does.Contain("bindings").And.Contain("testgen.yaml"));
        Assert.That(outcome.Text.Explanation, Does.Not.Contain("skapades"));
        Assert.That(CauseCodes.Code(FailureCause.MissingData), Is.EqualTo("missing-data"));
    }

    [Test]
    public async Task An_undocumented_404_is_still_a_failure()
    {
        File.WriteAllText(Path.Combine(_dir, "t.openapi.yaml"), Spec.Replace("'404': { description: finns inte }", "'409': { description: krock }"));
        var runner = new TestRunner(new RunOptions { Spec = Path.Combine(_dir, "t.openapi.yaml"), Api = "http://fake.test", Seed = 1 }, new StringWriter(), new Answer(HttpStatusCode.NotFound));
        await runner.RunAsync();
        Assert.That(runner.Outcomes.Single(o => o.Test.Call.StartsWith("GET /sets/")).Outcome, Is.EqualTo("FEL"));
    }
}
