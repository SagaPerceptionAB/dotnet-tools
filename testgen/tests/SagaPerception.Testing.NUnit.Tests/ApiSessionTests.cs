using System.Text.Json.Nodes;

namespace SagaPerception.Testing.NUnit.Tests;

/// <summary>ShopApi körs i testprocessen och testerna går genom motorn: utfall, felmeddelande, resultatfil och miljöer.</summary>
public class ApiSessionTests
{
    private string _out = "";

    [SetUp] public void Up() => _out = Directory.CreateTempSubdirectory("testgen-session-").FullName;
    [TearDown] public void Down() => Directory.Delete(_out, true);

    private ApiPlan Plan(Func<string, string?>? getEnv = null) =>
        ApiPlan.Create(() => TestGenTarget.Resolve(Samples.ShopTests, "Shop.Tests", "ShopApi", getEnv, testProjectDirectory: _out), "ShopApi");

    [Test]
    public async Task A_valid_test_passes_against_the_app_in_the_test_process()
    {
        await using var session = await ApiSession.StartAsync(Plan());

        Assert.That(session.NotReady, Is.Null);
        await session.RunTestAsync("T-001", CancellationToken.None);
        await session.RunTestAsync("T-014", CancellationToken.None);   // skapar en order: Betaltjänsten mockas ur betaltjanst.openapi.yaml
    }

    [Test]
    public async Task A_bug_in_the_app_fails_the_test_with_the_same_explanation_as_the_terminal()
    {
        await using var session = await ApiSession.StartAsync(Plan());

        var failure = await Caught.Failure(() => session.RunTestAsync("T-020", CancellationToken.None));

        Assert.That(failure.Message, Does.StartWith("Skapa order – quantity för lågt (under minimum)"));
        Assert.That(failure.Message, Does.Contain("svarade 500").And.Contain("Det tyder på ett fel i systemet"));
        Assert.That(failure.Message, Does.Contain("FEL (väntade 400)").And.Contain("förberedande 1: POST /customers → 201").And.Contain("anrop: POST http://localhost/orders"));
        Assert.That(failure.Message, Does.Contain("curl för anropet:").And.Contain("curl -X POST 'http://localhost/orders'"));
    }

    [Test]
    public async Task The_result_file_has_the_layer_the_project_and_one_entry_per_test_that_ran()
    {
        await using var session = await ApiSession.StartAsync(Plan());
        await session.RunTestAsync("T-001", CancellationToken.None);
        await Caught.Failure(() => session.RunTestAsync("T-020", CancellationToken.None));

        var path = session.WriteResult();

        Assert.That(path, Is.EqualTo(Path.Combine(_out, "TestResults", "testgen", "ShopApi.json")));
        var json = (JsonObject)JsonNode.Parse(File.ReadAllText(path!))!;
        Assert.That(json.Select(kv => kv.Key).Take(4), Is.EqualTo(new[] { "schemaVersion", "toolVersion", "layer", "project" }));
        Assert.That(json["layer"]!.GetValue<string>(), Is.EqualTo("api"));
        Assert.That(json["project"]!.GetValue<string>(), Is.EqualTo("ShopApi"));
        Assert.That(json["environment"]!.GetValue<string>(), Is.EqualTo("local"));
        Assert.That(json["partial"]!.GetValue<bool>(), Is.True, "bara två av 34 kördes");
        Assert.That(json["possibleTotal"]!.GetValue<int>(), Is.EqualTo(34));
        var tests = json["tests"]!.AsArray();
        Assert.That(tests.Select(t => t!["id"]!.GetValue<string>()), Is.EqualTo(new[] { "T-001", "T-020" }));
        var failed = tests.Single(t => t!["outcome"]!.GetValue<string>() == "failed")!;
        Assert.That((failed["id"]!.GetValue<string>(), failed["cause"]!.GetValue<string>(), failed["key"]!.GetValue<string>()),
            Is.EqualTo(("T-020", "system", "api:createOrder:below-minimum:quantity")));
    }

    [Test]
    public async Task Nothing_is_written_when_no_test_ran()
    {
        await using var session = await ApiSession.StartAsync(Plan());
        Assert.That(session.WriteResult(), Is.Null);
        Assert.That(Directory.Exists(Path.Combine(_out, "TestResults")), Is.False);
    }

    [Test]
    public async Task A_read_only_environment_ignores_the_tests_that_write_without_sending_anything()
    {
        string? Env(string name) => name switch { "TESTGEN_ENV" => "test", "SHOP_TEST_TOKEN" => "hemlig-token-1234", _ => null };
        await using var session = await ApiSession.StartAsync(Plan(Env));

        var ignored = Assert.ThrowsAsync<IgnoreException>(() => session.RunTestAsync("T-001", CancellationToken.None))!;

        Assert.That(ignored.Message, Does.Contain("Miljön är skrivskyddad").And.Contain("POST"));
        Assert.That(session.NotReady, Is.Null, "ingen app startas för en miljö med adress");
    }

    [Test]
    public async Task The_secret_of_an_environment_is_never_in_the_result_file()
    {
        string? Env(string name) => name switch { "TESTGEN_ENV" => "test", "SHOP_TEST_TOKEN" => "hemlig-token-1234", _ => null };
        await using var session = await ApiSession.StartAsync(Plan(Env));
        Assert.ThrowsAsync<IgnoreException>(() => session.RunTestAsync("T-001", CancellationToken.None));

        var text = File.ReadAllText(session.WriteResult()!);

        Assert.That(text, Does.Not.Contain("hemlig-token-1234"));
        Assert.That(text, Does.Contain("\"environment\": \"test\"").And.Contain("\"skipped\""));
    }

    [Test]
    public async Task Excel_tests_from_testgen_yaml_are_cases_and_a_scenario_runs_as_a_whole()
    {
        // En egen solution som pekar på ShopApi, med testgen.yaml som anger Excel-filen för API:et.
        var dir = Directory.CreateTempSubdirectory("testgen-excel-session-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, ".git"));
            var shopApi = Path.GetFullPath(Path.Combine(Samples.Directory, "ShopApi", "ShopApi.csproj"));
            File.WriteAllText(Path.Combine(dir, "S.slnx"), $"<Solution><Project Path=\"{shopApi}\" /></Solution>");
            File.Copy(Path.Combine(Samples.Directory, "shop.openapi.yaml"), Path.Combine(dir, "shop.openapi.yaml"));
            File.Copy(Path.Combine(Samples.Directory, "tester.xlsx"), Path.Combine(dir, "tester.xlsx"));
            File.Copy(Samples.Betaltjanst, Path.Combine(dir, "betaltjanst.openapi.yaml"));
            File.WriteAllText(Path.Combine(dir, "testgen.yaml"), "apis:\n  ShopApi:\n    excel: tester.xlsx\nhosts:\n  betaltjanst.example: mock\n");
            var plan = ApiPlan.Create(() => TestGenTarget.Resolve(dir, null, "ShopApi", testProjectDirectory: _out), "ShopApi");

            Assert.That(plan.Error, Is.Null);
            var excel = plan.Tests.Where(t => t.Step is not null).ToList();
            Assert.That(plan.Tests, Has.Count.EqualTo(39));
            Assert.That(excel, Has.Count.EqualTo(5));
            Assert.That(plan.Keys.Count(k => k.Contains(":excel:")), Is.EqualTo(5));

            await using var session = await ApiSession.StartAsync(plan);
            foreach (var step in excel)
                await session.RunTestAsync(step.Id, CancellationToken.None);   // går bara om scenariot kördes i ordning och delade svar
        }
        finally { Directory.Delete(dir, true); }
    }
}
