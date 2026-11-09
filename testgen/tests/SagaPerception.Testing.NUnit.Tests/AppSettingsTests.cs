namespace SagaPerception.Testing.NUnit.Tests;

/// <summary>Appen i testprocessen får sin konfiguration ur testgen.yaml, och startar den inte fäller testerna med vad som saknas.</summary>
public class AppSettingsTests
{
    private string _dir = "";
    private string _out = "";

    [SetUp]
    public void Up()
    {
        _dir = Directory.CreateTempSubdirectory("testgen-appsettings-").FullName;
        _out = Directory.CreateTempSubdirectory("testgen-appsettings-out-").FullName;
        Directory.CreateDirectory(Path.Combine(_dir, ".git"));
        var app = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "ConfigApp"));
        File.WriteAllText(Path.Combine(_dir, "S.slnx"), $"<Solution><Project Path=\"{Path.Combine(app, "ConfigApp.csproj")}\" /></Solution>");
        File.Copy(Path.Combine(app, "configapp.openapi.yaml"), Path.Combine(_dir, "configapp.openapi.yaml"));
    }

    [TearDown]
    public void Down()
    {
        Directory.Delete(_dir, true);
        Directory.Delete(_out, true);
    }

    private ApiPlan Plan(string yaml, Func<string, string?>? getEnv = null)
    {
        File.WriteAllText(Path.Combine(_dir, "testgen.yaml"), "apis:\n  ConfigApp:\n    spec: configapp.openapi.yaml\n" + yaml);
        return ApiPlan.Create(() => TestGenTarget.Resolve(_dir, null, "ConfigApp", getEnv, testProjectDirectory: _out), "ConfigApp");
    }

    private static string IdOf(ApiPlan plan, string call) => plan.Tests.First(t => t.Call == call).Id;

    [Test]
    public async Task An_app_that_needs_configuration_to_start_fails_with_what_it_said_and_where_to_set_it()
    {
        var plan = Plan("");
        Assert.That(plan.Error, Is.Null);

        await using var session = await ApiSession.StartAsync(plan);

        Assert.That(session.NotReady, Is.Null, "det är ett fel, inte något som saknas i miljön");
        Assert.That(session.StartFailure, Is.EqualTo(
            "Appen ConfigApp kunde inte startas i testprocessen: App:RequiredKey is not configured" + Environment.NewLine +
            "Gör så här: ange det appen saknar under apis.ConfigApp.settings i testgen.yaml."));
        var failure = await Caught.Failure(() => session.RunTestAsync(IdOf(plan, "GET /ping"), CancellationToken.None));
        Assert.That(failure.Message, Is.EqualTo(session.StartFailure));
    }

    [Test]
    public async Task Settings_in_testgen_yaml_let_the_app_start_and_reach_it()
    {
        var plan = Plan("    settings:\n      App:RequiredKey: ett-varde\n");

        await using var session = await ApiSession.StartAsync(plan);

        Assert.That(session.StartFailure, Is.Null);
        await session.RunTestAsync(IdOf(plan, "GET /ping"), CancellationToken.None);
    }

    [Test]
    public async Task A_secret_from_an_environment_variable_is_masked_in_failures_and_in_the_result_file()
    {
        const string secret = "hemlig-nyckel-1234";
        var plan = Plan("    settings:\n      App:RequiredKey: ${env:CONFIG_KEY}\n", name => name == "CONFIG_KEY" ? secret : null);
        await using var session = await ApiSession.StartAsync(plan);

        var failure = await Caught.Failure(() => session.RunTestAsync(IdOf(plan, "GET /echo"), CancellationToken.None));   // appen svarar 500 med nyckeln

        Assert.That(failure.Message, Does.Not.Contain(secret).And.Contain("***"));
        Assert.That(File.ReadAllText(session.WriteResult()!), Does.Not.Contain(secret));
    }

    [Test]
    public async Task The_app_runs_in_Development_unless_testgen_yaml_says_another_environment()
    {
        var plain = Plan("    settings:\n      App:RequiredKey: x\n");
        Assert.That(plain.Target!.HostEnvironment, Is.EqualTo("Development"), "ingen appsettings.Testing.json och ingen launch-profil");
        await using (var session = await ApiSession.StartAsync(plain))
        {
            var failure = await Caught.Failure(() => session.RunTestAsync(IdOf(plain, "GET /env"), CancellationToken.None));
            Assert.That(failure.Message, Does.Contain("Development"));
        }

        var staging = Plan("    environment: Staging\n    settings:\n      App:RequiredKey: x\n");
        Assert.That(staging.Target!.HostEnvironment, Is.EqualTo("Staging"));
        await using (var session = await ApiSession.StartAsync(staging))
        {
            var failure = await Caught.Failure(() => session.RunTestAsync(IdOf(staging, "GET /env"), CancellationToken.None));
            Assert.That(failure.Message, Does.Contain("Staging"));
        }
    }

    [Test]
    public async Task In_Development_a_body_of_the_wrong_type_still_gets_400_as_in_production()
    {
        var plan = Plan("    settings:\n      App:RequiredKey: x\n");
        await using var session = await ApiSession.StartAsync(plan);

        // Minimal API:er kastar annars BadHttpRequestException i Development, som appens felhanterare kan göra till 500.
        var wrongType = plan.Tests.First(t => t.Call == "POST /typed" && t.Description.Contains("fel typ", StringComparison.Ordinal));
        await session.RunTestAsync(wrongType.Id, CancellationToken.None);
    }

    [Test]
    public void A_missing_environment_variable_in_settings_is_an_error_with_the_variable_and_the_key()
    {
        var plan = Plan("    settings:\n      App:RequiredKey: ${env:CONFIG_KEY}\n");

        Assert.That(plan.Error, Does.Contain("CONFIG_KEY").And.Contain("apis.ConfigApp.settings, App:RequiredKey"));
    }

    [Test]
    public void Settings_are_not_used_when_the_environment_has_an_address()
    {
        var plan = Plan("    settings:\n      App:RequiredKey: ${env:CONFIG_KEY}\nenvironments:\n  test:\n    apis:\n      ConfigApp: { url: 'https://config-test.example.com' }\n",
            name => name == "TESTGEN_ENV" ? "test" : null);

        Assert.That(plan.Error, Is.Null, "variabeln behövs inte när appen inte körs i testprocessen");
        Assert.That(plan.Target!.InProcess, Is.False);
        Assert.That(plan.Target.Config.Values, Is.Empty);
    }

    [Test]
    public void The_plan_keeps_what_is_expensive_to_prepare_for_the_runs_of_single_tests()
    {
        var plan = Plan("    settings:\n      App:RequiredKey: x\n");

        Assert.That(plan.Prepared, Is.Not.Null);
        Assert.That(plan.Tests, Is.Not.Empty);
    }
}
