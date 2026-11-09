using System.Text.Json.Nodes;
using TestGen.Core;
using TestGen.Core.Projects;
using TestGen.Core.Run;
using TestGen.Core.Settings;

namespace TestGen.Tests;

/// <summary>Det motorn ger <c>ApiTests</c> i <c>dotnet test</c>: planen utan körning, lager och projekt i resultatet, spec mot spec och Excel i testgen.yaml.</summary>
public class ApiTestsSupportTests
{
    private string _dir = "";

    [SetUp] public void Up() => _dir = Directory.CreateTempSubdirectory("testgen-support-").FullName;
    [TearDown] public void Down() => Directory.Delete(_dir, true);

    private sealed class NeverCalled : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Planeringen ska inte skicka något anrop.");
    }

    private static RunOptions Options(bool listOnly = false, string[]? tests = null, string[]? reports = null) => new()
    {
        Spec = ShopSpec.Path,
        Api = "http://fake.test",
        Seed = 1,
        ListOnly = listOnly,
        Tests = tests,
        Project = "ShopApi",
        ReportPaths = reports ?? [],
    };

    // --- Planen utan körning -------------------------------------------------------------------

    [Test]
    public async Task List_only_plans_every_test_without_sending_anything()
    {
        var runner = new TestRunner(Options(listOnly: true), new Terminal(TextWriter.Null, TextWriter.Null, false), new NeverCalled());
        var exit = await runner.RunAsync();

        Assert.That(exit, Is.Zero);
        Assert.That(runner.PlannedTests, Has.Count.EqualTo(34));
        Assert.That(runner.PlannedTests.Select(t => t.Id), Is.EqualTo(Enumerable.Range(1, 34).Select(i => $"T-{i:000}")));
        Assert.That(runner.Outcomes, Is.Empty);
    }

    [Test]
    public async Task The_plan_is_the_same_whether_or_not_a_selection_is_made()
    {
        var all = new TestRunner(Options(listOnly: true), new Terminal(TextWriter.Null, TextWriter.Null, false));
        await all.RunAsync();
        var one = new TestRunner(Options(listOnly: true, tests: ["T-020"]), new Terminal(TextWriter.Null, TextWriter.Null, false));
        await one.RunAsync();

        Assert.That(one.PlannedTests.Select(t => (t.Id, t.Call, t.Description)), Is.EqualTo(all.PlannedTests.Select(t => (t.Id, t.Call, t.Description))));
        Assert.That(all.PlannedTests.Single(t => t.Id == "T-020").Call, Is.EqualTo("POST /orders"));
    }

    [Test]
    public async Task A_prepared_plan_gives_the_same_tests_without_reading_the_spec_again()
    {
        var first = new TestRunner(Options(listOnly: true), new Terminal(TextWriter.Null, TextWriter.Null, false));
        await first.RunAsync();
        var gone = Path.Combine(_dir, "borta.openapi.yaml");   // specen finns inte: den får inte läsas
        var options = Options(tests: ["T-001"]);

        var second = new TestRunner(new RunOptions { Spec = gone, Api = options.Api, Seed = 1, Tests = ["T-001"], Project = "ShopApi", Prepared = first.Prepared },
            new Terminal(TextWriter.Null, TextWriter.Null, false), new FakeOk());
        await second.RunAsync();

        Assert.That(second.PlannedTests.Select(t => (t.Id, t.Call, t.Description, t.Key)), Is.EqualTo(first.PlannedTests.Select(t => (t.Id, t.Call, t.Description, t.Key))));
        Assert.That(second.Outcomes.Select(o => o.Id), Is.EqualTo(new[] { "T-001" }));
    }


    // --- Lager och projekt i resultatet --------------------------------------------------------

    [Test]
    public async Task The_json_result_names_the_layer_and_the_project_right_after_the_tool_version()
    {
        var path = Path.Combine(_dir, "r.json");
        var handler = new FakeOk();
        await new TestRunner(Options(tests: ["T-001"], reports: [path]), new Terminal(TextWriter.Null, TextWriter.Null, false), handler).RunAsync();

        var json = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
        var keys = json.Select(kv => kv.Key).ToList();
        Assert.That(keys.Take(4), Is.EqualTo(new[] { "schemaVersion", "toolVersion", "layer", "project" }));
        Assert.That(json["layer"]!.GetValue<string>(), Is.EqualTo("api"));
        Assert.That(json["project"]!.GetValue<string>(), Is.EqualTo("ShopApi"));

        var schema = (JsonObject)JsonNode.Parse(File.ReadAllText(SchemaFile()))!;
        Assert.That(schema["properties"]!.AsObject().Select(kv => kv.Key).Take(4), Is.EqualTo(new[] { "schemaVersion", "toolVersion", "layer", "project" }));
        Assert.That(JsonSchemaCheck.Errors(json, schema), Is.Empty);
    }

    [Test]
    public async Task Without_a_known_project_the_result_still_has_a_layer()
    {
        var path = Path.Combine(_dir, "r.json");
        var options = new RunOptions { Spec = ShopSpec.Path, Api = "http://fake.test", Seed = 1, Tests = ["T-001"], ReportPaths = [path] };
        await new TestRunner(options, new Terminal(TextWriter.Null, TextWriter.Null, false), new FakeOk()).RunAsync();

        var json = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
        Assert.That(json["layer"]!.GetValue<string>(), Is.EqualTo("api"));
        Assert.That(json.ContainsKey("project"), Is.False);
    }

    private static string SchemaFile()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var f = Path.Combine(d.FullName, "testgen", "docs", "ci", "testgen-result.schema.json");
            if (File.Exists(f)) return f;
        }
        throw new FileNotFoundException("testgen-result.schema.json");
    }

    /// <summary>Svarar 201 med ett tomt objekt: räcker för ett test som bara ska skrivas till rapporten.</summary>
    private sealed class FakeOk : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Created) { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") });
    }

    [Test]
    public void A_test_with_a_given_key_keeps_it_for_comparisons()
    {
        var info = TestInfo.Basic("T-001", "UI", "GET /products", "smoke") with { Key = "ui:/products:smoke" };
        Assert.That(TestKeys.For([info, info with { Id = "T-002" }]), Is.EqualTo(new[] { "ui:/products:smoke", "ui:/products:smoke#2" }));
    }

    // --- Spec mot spec -------------------------------------------------------------------------

    private static ApiSpec Spec(string yaml)
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("testgen-spec-").FullName, "s.yaml");
        File.WriteAllText(file, yaml);
        try { return OpenApiLoader.LoadSpec(file); }
        finally { Directory.Delete(Path.GetDirectoryName(file)!, true); }
    }

    private const string Small = """
        openapi: 3.0.3
        info: { title: T, version: '1' }
        paths:
          /items:
            post:
              operationId: createItem
              requestBody:
                content:
                  application/json:
                    schema:
                      type: object
                      required: [name]
                      properties:
                        name: { type: string }
                        price: { type: number }
              responses:
                '201':
                  description: ok
                  content:
                    application/json:
                      schema:
                        type: object
                        properties:
                          id: { type: integer }
          /items/{id}:
            get:
              operationId: getItem
              parameters:
                - { name: id, in: path, required: true, schema: { type: integer } }
              responses:
                '200': { description: ok }
        """;

    [Test]
    public void Equal_specs_have_no_differences_even_when_operation_ids_differ()
    {
        var other = Small.Replace("createItem", "skapa").Replace("getItem", "hamta");
        Assert.That(SpecComparison.Diff(Spec(Small), Spec(other)), Is.Empty);
    }

    [Test]
    public void The_real_shop_spec_equals_itself()
    {
        var spec = OpenApiLoader.LoadSpec(ShopSpec.Path);
        Assert.That(SpecComparison.Diff(spec, OpenApiLoader.LoadSpec(ShopSpec.Path)), Is.Empty);
    }

    [Test]
    public void Operations_that_exist_on_one_side_only_are_named()
    {
        var app = Small.Replace("/items/{id}:", "/andra/{id}:");
        var diff = SpecComparison.Diff(Spec(Small), Spec(app));

        Assert.That(diff, Has.Some.EqualTo("GET /items/{id}: finns i specen i repot men inte i appen."));
        Assert.That(diff, Has.Some.EqualTo("GET /andra/{id}: finns i appen men inte i specen i repot."));
    }

    [Test]
    public void Changed_parameters_body_and_response_codes_are_described_briefly()
    {
        var app = Small
            .Replace("{ name: id, in: path, required: true, schema: { type: integer } }", "{ name: id, in: path, required: true, schema: { type: string } }")
            .Replace("required: [name]", "required: [name, price]")
            .Replace("'200': { description: ok }", "'200': { description: ok }\n        '404': { description: saknas }");
        var diff = SpecComparison.Diff(Spec(Small), Spec(app));

        Assert.That(diff, Has.Some.StartsWith("GET /items/{id}: parametrarna skiljer sig"));
        Assert.That(diff, Has.Some.StartsWith("POST /items: bodyn skiljer sig").And.Contain("price:number!"));
        Assert.That(diff, Has.Some.StartsWith("GET /items/{id}: svarskoderna skiljer sig").And.Contain("404"));
    }

    [Test]
    public void A_changed_response_schema_is_found()
    {
        var diff = SpecComparison.Diff(Spec(Small), Spec(Small.Replace("id: { type: integer }", "id: { type: string }")));
        Assert.That(diff, Has.Some.StartsWith("POST /items: svaret 201 skiljer sig"));
    }

    [Test]
    public void A_long_diff_is_cut_short()
    {
        var many = string.Join("\n", Enumerable.Range(0, 30).Select(i => $"  /p{i}:\n    get:\n      responses:\n        '200': {{ description: ok }}"));
        var empty = "openapi: 3.0.3\ninfo: { title: T, version: '1' }\npaths: {}";
        var diff = SpecComparison.Diff(Spec(empty), Spec("openapi: 3.0.3\ninfo: { title: T, version: '1' }\npaths:\n" + many));

        Assert.That(diff, Has.Count.EqualTo(21));
        Assert.That(diff[^1], Is.EqualTo("… och 10 till."));
    }

    // --- Excel i testgen.yaml ------------------------------------------------------------------

    [Test]
    public void Excel_file_is_set_per_api_in_testgen_yaml_and_resolved_from_the_file()
    {
        var file = Path.Combine(_dir, "testgen.yaml");
        File.WriteAllText(file, "apis:\n  ShopApi:\n    excel: tester/shop.xlsx\n");
        var settings = TestGenSettings.LoadFile(file);

        Assert.That(settings.ExcelOf("ShopApi"), Is.EqualTo(Path.GetFullPath(Path.Combine(_dir, "tester", "shop.xlsx"))));
        Assert.That(settings.ExcelOf("shopapi"), Is.Not.Null, "skiftlägesokänsligt");
        Assert.That(settings.ExcelOf("Annat"), Is.Null);
    }

    [Test]
    public void The_run_target_carries_the_excel_file_the_environment_and_the_repository()
    {
        var dir = Directory.CreateTempSubdirectory("testgen-excel-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "testgen.yaml"), "apis:\n  ShopApi:\n    excel: " + Path.GetFileName(ShopExcel()) + "\n");
            File.Copy(ShopExcel(), Path.Combine(dir, Path.GetFileName(ShopExcel())));
            var solution = Path.Combine(dir, "Shop.slnx");
            File.WriteAllText(solution, "<Solution><Project Path=\"ShopApi/ShopApi.csproj\" /></Solution>");
            Directory.CreateDirectory(Path.Combine(dir, "ShopApi", "Controllers"));
            File.WriteAllText(Path.Combine(dir, "ShopApi", "ShopApi.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />");
            File.Copy(ShopSpec.Path, Path.Combine(dir, "ShopApi", "shop.openapi.yaml"));

            var target = RunTarget.Resolve(solution, null, "ShopApi", null, null, startDirectory: dir, localUrl: "http://localhost");

            Assert.That(Path.GetFileName(target.Excel), Is.EqualTo("tester.xlsx"));
            Assert.That(target.Environment.Name, Is.EqualTo("local"));
            Assert.That(target.Settings.Api, Is.EqualTo("http://localhost"));
            Assert.That(target.Repository.Facts!.Apis.Select(a => a.Name), Is.EqualTo(new[] { "ShopApi" }));
        }
        finally { Directory.Delete(dir, true); }
    }

    private static string ShopExcel()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var f = Path.Combine(d.FullName, "samples", "tester.xlsx");
            if (File.Exists(f)) return f;
        }
        throw new FileNotFoundException("samples/tester.xlsx");
    }
}
