using System.Net;
using System.Text;
using TestGen.Cli;
using TestGen.Core;
using TestGen.Core.Projects;
using TestGen.Core.Run;
using TestGen.Core.Settings;

namespace TestGen.Tests;

/// <summary>testgen.yaml: sökning, modell, validering, miljöval, värdpolicy, skrivskydd och <c>testgen run</c> utan flaggor.</summary>
public class SettingsTests
{
    private string _dir = "";

    [SetUp] public void Up() => _dir = Directory.CreateTempSubdirectory("testgen-settings-").FullName;
    [TearDown] public void Down() => Directory.Delete(_dir, true);

    private string Write(string yaml, string? name = null)
    {
        var path = Path.Combine(_dir, name ?? "testgen.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, yaml);
        return path;
    }

    private static string? Env(string name) => name switch { "SHOP_TOKEN" => "hemlig-token", "TESTGEN_ENV" => null, _ => null };

    private const string Full = """
        seed: 7
        locale: en
        apis:
          ShopApi:
            spec: shop.openapi.yaml
            skipOperations: [getOrder]
            overrides: { createProduct: { price: 10 } }
        hosts:
          default: block
          Api.Example-Payments.com: mock
          legacy.example.com: allow
        environments:
          local: {}
          test:
            readOnly: true
            apis:
              ShopApi:
                url: https://shop-test.example.com
                token: ${env:SHOP_TOKEN}
                headers: { X-Tenant: demo, X-Api-Key: "${env:SHOP_TOKEN}" }
                timeout: 12
            apps:
              ShopWeb: { url: https://shopweb-test.example.com }
        """;

    private static UsageException Invalid(string yaml, string dir)
    {
        var path = Path.Combine(dir, "bad.yaml");
        File.WriteAllText(path, yaml);
        return Assert.Throws<UsageException>(() => TestGenSettings.LoadFile(path))!;
    }

    // --- Sökning ---

    [Test]
    public void The_file_is_found_in_the_solution_directory_and_above_but_not_past_the_repository_root()
    {
        Directory.CreateDirectory(Path.Combine(_dir, ".git"));
        var deep = Path.Combine(_dir, "src", "app");
        Directory.CreateDirectory(deep);
        Assert.That(TestGenSettings.Find(deep), Is.Null);
        var file = Write("seed: 1\n");
        Assert.That(TestGenSettings.Find(deep), Is.EqualTo(file));
        Assert.That(TestGenSettings.Load(deep).Seed, Is.EqualTo(1));
        Assert.That(TestGenSettings.Find(_dir), Is.EqualTo(file));
    }

    [Test]
    public void Without_a_file_the_defaults_and_only_the_local_environment_are_used()
    {
        Directory.CreateDirectory(Path.Combine(_dir, ".git"));
        var settings = TestGenSettings.Load(_dir);
        Assert.That(settings.Path, Is.Null);
        Assert.That((settings.Seed, settings.Locale), Is.EqualTo((12345, "sv")));
        Assert.That(settings.EnvironmentNames, Is.EqualTo(new[] { "local" }));
        var local = settings.Environment(null, Env);
        Assert.That((local.Name, local.ReadOnly, local.Api("ShopApi").Url), Is.EqualTo(("local", false, (string?)null)));
        Assert.That(settings.Hosts.Default, Is.EqualTo(HostAction.Block));
    }

    [Test]
    public void The_sample_file_is_valid_and_has_a_test_environment()
    {
        var settings = TestGenSettings.Load(Path.GetDirectoryName(ShopSolution.Path)!);
        Assert.That(settings.EnvironmentNames, Is.EqualTo(new[] { "local", "test" }));
        Assert.That(settings.IsReadOnly("test"), Is.True);
        Assert.That(settings.Hosts.For("betaltjanst.example"), Is.EqualTo(HostAction.Mock));
    }

    // --- Modellen ---

    [Test]
    public void The_model_gives_url_headers_token_timeout_and_read_only_per_project()
    {
        var settings = TestGenSettings.LoadFile(Write(Full));
        Assert.That(settings.EnvironmentNames, Is.EqualTo(new[] { "local", "test" }));
        var test = settings.Environment("test", Env);
        Assert.That(test.ReadOnly, Is.True);
        var api = test.Api("shopapi");
        Assert.That(api.Url, Is.EqualTo("https://shop-test.example.com"));
        Assert.That(api.Token, Is.EqualTo("hemlig-token"));
        Assert.That(api.TimeoutSeconds, Is.EqualTo(12));
        Assert.That(api.Headers["x-tenant"], Is.EqualTo("demo"));
        Assert.That(api.Headers["X-Api-Key"], Is.EqualTo("hemlig-token"));
        Assert.That(api.SecretHeaderNames, Is.EquivalentTo(new[] { "X-Api-Key" }), "bara värden ur miljövariabler är hemliga");
        Assert.That(test.App("ShopWeb").Url, Is.EqualTo("https://shopweb-test.example.com"));
        Assert.That(test.App("ShopWeb").TimeoutSeconds, Is.EqualTo(RunSettings.DefaultTimeoutSeconds));
        Assert.That(test.Api("OtherApi").Url, Is.Null, "ett projekt som miljön inte nämner får tomma inställningar");
    }

    [Test]
    public void Generator_options_come_from_the_api_and_the_spec_path_is_relative_to_the_file()
    {
        var file = Write(Full, "conf/testgen.yaml");
        var settings = TestGenSettings.LoadFile(file);
        var options = settings.GeneratorFor("ShopApi");
        Assert.That((options.Seed, options.Locale), Is.EqualTo((7, "en")));
        Assert.That(options.SkipOperations, Is.EqualTo(new[] { "getOrder" }));
        Assert.That(options.Overrides["createProduct"]["price"].ToString(), Is.EqualTo("10"));
        Assert.That(settings.GeneratorFor().SkipOperations, Is.EqualTo(new[] { "getOrder" }), "ett enda API i filen används utan namn");
        Assert.That(settings.SpecOf("shopapi"), Is.EqualTo(Path.Combine(_dir, "conf", "shop.openapi.yaml")));
    }

    [Test]
    public void Host_policy_blocks_by_default_and_matches_hosts_without_regard_to_case()
    {
        var hosts = TestGenSettings.LoadFile(Write(Full)).Hosts;
        Assert.That(hosts.Default, Is.EqualTo(HostAction.Block));
        Assert.That(hosts.For("api.example-payments.com"), Is.EqualTo(HostAction.Mock));
        Assert.That(hosts.For("API.EXAMPLE-PAYMENTS.COM"), Is.EqualTo(HostAction.Mock));
        Assert.That(hosts.For("legacy.example.com"), Is.EqualTo(HostAction.Allow));
        Assert.That(hosts.For("elsewhere.example.com"), Is.EqualTo(HostAction.Block));
        Assert.That(TestGenSettings.LoadFile(Write("hosts: { default: allow }")).Hosts.For("x.example.com"), Is.EqualTo(HostAction.Allow));
    }

    [Test]
    public void Ui_checks_and_trace_are_read_and_default_to_none()
    {
        var ui = TestGenSettings.LoadFile(Write("ui: { checks: [accessibility, screenshots], trace: true }"));

        Assert.That(ui.UiChecks, Is.EqualTo(new[] { "accessibility", "screenshots" }));
        Assert.That(ui.UiTrace, Is.True);
        Assert.That(TestGenSettings.Defaults.UiChecks, Is.Empty);
        Assert.That(TestGenSettings.Defaults.UiTrace, Is.False);
    }

    // --- Inställningar för appen i testprocessen ---

    private const string WithSettings = """
        apis:
          ShopApi:
            settings:
              Auth:SigningKey: ${env:SIGNING_KEY}
              Photos:RootPath: /tmp/photos
              Photos:ConnectionString: ""
        apps:
          ShopWeb:
            settings:
              Api:BaseUrl: http://localhost:1
        """;

    [Test]
    public void Settings_for_an_api_and_an_app_are_read_by_key_with_environment_variables_expanded()
    {
        var settings = TestGenSettings.LoadFile(Write(WithSettings));

        var api = settings.ApiProcessSettings("shopapi", name => name == "SIGNING_KEY" ? "hemlig-nyckel-1234" : null);

        Assert.That(api.Values, Is.EqualTo(new Dictionary<string, string>
        {
            ["Auth:SigningKey"] = "hemlig-nyckel-1234",
            ["Photos:ConnectionString"] = "",
            ["Photos:RootPath"] = "/tmp/photos",
        }));
        Assert.That(api.Secrets, Is.EqualTo(new[] { "hemlig-nyckel-1234" }), "bara värden ur miljövariabler är hemliga");
        Assert.That(settings.AppProcessSettings("ShopWeb").Values, Is.EqualTo(new Dictionary<string, string> { ["Api:BaseUrl"] = "http://localhost:1" }));
        Assert.That(settings.ApiProcessSettings("OtherApi").Values, Is.Empty);
    }

    [Test]
    public void A_missing_environment_variable_in_settings_names_the_variable_and_the_key()
    {
        var settings = TestGenSettings.LoadFile(Write(WithSettings));

        var e = Assert.Throws<ArgumentException>(() => settings.ApiProcessSettings("ShopApi", _ => null))!;

        Assert.That(e.Message, Does.Contain("SIGNING_KEY").And.Contain("inte satt").And.Contain("apis.ShopApi.settings, Auth:SigningKey"));
    }

    [Test]
    public void Settings_must_be_text_and_unknown_keys_next_to_them_are_reported()
    {
        Assert.That(Invalid("apis:\n  ShopApi:\n    settings:\n      Key: [a]\n", _dir).Message, Does.Contain("rad 4: apis.ShopApi.settings.Key ska vara text."));
        Assert.That(Invalid("apps:\n  ShopWeb:\n    setting: {}\n", _dir).Message, Does.Contain("rad 3").And.Contain("Menade du settings?"));
        Assert.That(Invalid("apis:\n  ShopApi:\n    settings: text\n", _dir).Message, Does.Contain("apis.ShopApi.settings ska vara en uppsättning"));
    }


    // --- Validering ---

    [Test]
    public void An_unknown_key_gives_a_suggestion_and_the_line()
    {
        var e = Invalid("seed: 1\nenvironments:\n  test:\n    readonly: true\n", _dir);
        Assert.That(e.Message, Does.Contain("bad.yaml").And.Contain("rad 4: okänd nyckel 'readonly' under environments.test. Menade du readOnly?"));
        Assert.That(Invalid("output: x\n", _dir).Message, Does.Contain("rad 1: okänd nyckel 'output' i filen.").And.Contain("Giltiga nycklar: seed, locale, apis, apps, hosts, ui, environments."));
        Assert.That(Invalid("apis:\n  ShopApi:\n    skipOperation: [a]\n", _dir).Message, Does.Contain("rad 3").And.Contain("Menade du skipOperations?"));
    }

    [Test]
    public void All_problems_are_listed_with_their_lines()
    {
        var e = Invalid("seed: abc\nhosts:\n  default: nej\nenvironments:\n  test:\n    apis:\n      ShopApi:\n        timeout: 0\n        url: ftp://x\n", _dir);
        Assert.That(e.Message, Does.Contain("bad.yaml har 4 fel:"));
        Assert.That(e.Message, Does.Contain("rad 1: seed ska vara ett heltal.").And.Contain("rad 3: hosts.default ska vara block, allow eller mock.")
            .And.Contain("rad 8: environments.test.apis.ShopApi.timeout ska vara ett positivt heltal").And.Contain("rad 9:").And.Contain("giltig http/https-adress"));
    }

    [Test]
    public void A_plaintext_secret_is_a_configuration_error_with_a_way_out()
    {
        var token = Invalid("environments:\n  test:\n    apis:\n      ShopApi: { token: abc123 }\n", _dir);
        Assert.That(token.Message, Does.Contain("rad 4").And.Contain("token står i klartext"));
        Assert.That(token.Tip, Does.Contain("${env:NAMN}"));
        Assert.That(token.Message, Does.Not.Contain("abc123"), "själva hemligheten visas aldrig");

        var header = Invalid("environments:\n  test:\n    apps:\n      ShopWeb:\n        headers:\n          X-Api-Key: abc123\n", _dir);
        Assert.That(header.Message, Does.Contain("rad 6").And.Contain("X-Api-Key").And.Contain("klartext").And.Not.Contain("abc123"));

        Assert.DoesNotThrow(() => TestGenSettings.LoadFile(Write("environments:\n  test:\n    apis:\n      ShopApi:\n        headers: { X-Tenant: demo, Accept: application/json }\n")));
    }

    [Test]
    public void The_config_exit_code_is_2()
    {
        var path = Write("seed: abc\n");
        var error = FriendlyErrors.Translate(Assert.Throws<UsageException>(() => TestGenSettings.LoadFile(path))!, new ErrorContext { Config = path });
        Assert.That(error.ExitCode, Is.EqualTo(ExitCodes.Usage));
        Assert.That(error.Action, Is.Not.Empty);
    }

    [Test]
    public void A_file_that_is_not_yaml_gives_the_line()
    {
        var e = Invalid("seed: 1\napis: [unclosed\n", _dir);
        Assert.That(e.Message, Does.Contain("rad ").And.Contain("inte giltig YAML"));
    }

    [Test]
    public void A_missing_environment_variable_names_the_variable_only_for_the_project_asked_for()
    {
        var settings = TestGenSettings.LoadFile(Write(Full));
        var test = settings.Environment("test", _ => null);
        var e = Assert.Throws<ArgumentException>(() => test.Api("ShopApi"))!;
        Assert.That(e.Message, Does.Contain("SHOP_TOKEN").And.Contain("inte satt").And.Contain("miljön test, apis.ShopApi"));
        Assert.That(test.App("ShopWeb").Url, Is.EqualTo("https://shopweb-test.example.com"), "ett annat projekt påverkas inte");
    }

    // --- Miljön väljs ---

    [Test]
    public void An_unknown_environment_lists_the_environments_with_a_suggestion()
    {
        var settings = TestGenSettings.LoadFile(Write(Full));
        var e = Assert.Throws<UsageException>(() => settings.Environment("tset"))!;
        Assert.That(e.Message, Does.Contain("Okänd miljö 'tset'").And.Contain("Menade du test?").And.Contain("Miljöer: local, test."));
        Assert.That(e.Tip, Does.Contain("--env").And.Contain("TESTGEN_ENV"));
    }

    [Test]
    public void TESTGEN_ENV_and_the_flag_give_the_same_environment_and_the_flag_wins()
    {
        var settings = TestGenSettings.LoadFile(Write(Full));
        var viaFlag = settings.Environment("test", Env);
        var viaVariable = settings.Environment(null, n => n == "TESTGEN_ENV" ? "test" : Env(n));
        Assert.That(viaVariable.Name, Is.EqualTo(viaFlag.Name));
        Assert.That(viaVariable.ReadOnly, Is.EqualTo(viaFlag.ReadOnly));
        Assert.That(viaVariable.Api("ShopApi").Url, Is.EqualTo(viaFlag.Api("ShopApi").Url));
        Assert.That(viaVariable.Api("ShopApi").Token, Is.EqualTo(viaFlag.Api("ShopApi").Token));
        Assert.That(settings.Environment("local", n => n == "TESTGEN_ENV" ? "test" : null).Name, Is.EqualTo("local"));
        Assert.That(settings.Environment(null, _ => "  ").Name, Is.EqualTo("local"));
        Assert.That(TestGenSettings.EnvironmentName(null, _ => null), Is.EqualTo("local"));
        Assert.That(settings.Environment("TEST", Env).Name, Is.EqualTo("test"), "skiftläge spelar ingen roll, namnet är filens");
    }

    // --- testgen run ---

    private sealed class Recording(HttpStatusCode code = HttpStatusCode.Created) : HttpMessageHandler
    {
        public List<(string Method, Uri Uri)> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            lock (Requests) Requests.Add((req.Method.Method, req.RequestUri!));
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
        }
    }

    private static async Task<(int Exit, string Out, string Err, TestRunner Runner)> RunOn(RunTarget target, Recording handler, string? only = null)
    {
        var (so, se) = (new StringWriter { NewLine = "\n" }, new StringWriter { NewLine = "\n" });
        var runner = new TestRunner(new RunOptions
        {
            Spec = target.Spec,
            Api = target.Settings.Api,
            Headers = target.Settings.Headers,
            Token = target.Settings.Token,
            SecretHeaderNames = target.Settings.SecretHeaderNames,
            EnvName = target.Settings.EnvName,
            Generator = target.Generator,
            ReadOnly = target.Settings.ReadOnly,
            Only = only,
            Seed = 1,
        }, new Terminal(so, se, false), handler);
        return (await runner.RunAsync(), so.ToString(), se.ToString(), runner);
    }

    [Test]
    public async Task Run_without_flags_in_the_sample_uses_the_only_api_its_spec_and_its_local_address()
    {
        var target = RunTarget.Resolve(ShopSolution.Path, null, null, null, null, getEnv: Env);
        Assert.That(target.Project, Is.EqualTo("ShopApi"));
        Assert.That(Path.GetFileName(target.Spec), Is.EqualTo("shop.openapi.yaml"));
        Assert.That((target.Settings.Api, target.Settings.EnvName, target.Settings.ReadOnly), Is.EqualTo(("http://localhost:5080", "local", false)));
        Assert.That(Path.GetFileName(target.ConfigPath), Is.EqualTo("testgen.yaml"));

        var handler = new Recording();
        var (_, output, _, runner) = await RunOn(target, handler, only: "createCustomer");
        Assert.That(runner.Outcomes, Is.Not.Empty);
        Assert.That(handler.Requests.Select(r => r.Uri.GetLeftPart(UriPartial.Authority)).Distinct(), Is.EqualTo(new[] { "http://localhost:5080" }));
        Assert.That(output, Does.Contain("T-001"));
    }

    [Test]
    public void Api_can_be_named_by_project_and_takes_its_address_from_the_environment()
    {
        var target = RunTarget.Resolve(ShopSolution.Path, null, "ShopApi", null, "test", getEnv: n => n == "SHOP_TEST_TOKEN" ? "tok-123" : null);
        Assert.That(target.Settings.Api, Is.EqualTo("https://shop-test.example.com"));
        Assert.That((target.Settings.EnvName, target.Settings.ReadOnly, target.Settings.Token), Is.EqualTo(("test", true, "tok-123")));
        Assert.That(target.Settings.Headers["X-Tenant"], Is.EqualTo("demo"));

        var viaVariable = RunTarget.Resolve(ShopSolution.Path, null, "shopapi", null, null, getEnv: n => n switch { "TESTGEN_ENV" => "test", "SHOP_TEST_TOKEN" => "tok-123", _ => null });
        Assert.That(viaVariable.Settings.Api, Is.EqualTo(target.Settings.Api));
        Assert.That(viaVariable.Settings.ReadOnly, Is.True);

        var missing = Assert.Throws<ArgumentException>(() => RunTarget.Resolve(ShopSolution.Path, null, "ShopApi", null, "test", getEnv: _ => null))!;
        Assert.That(missing.Message, Does.Contain("SHOP_TEST_TOKEN"));
    }

    [Test]
    public void An_address_flag_wins_over_the_environment_and_an_unknown_project_gives_suggestions()
    {
        var withUrl = RunTarget.Resolve(ShopSolution.Path, null, "http://localhost:5175", null, "test", getEnv: n => n == "SHOP_TEST_TOKEN" ? "t" : null);
        Assert.That(withUrl.Settings.Api, Is.EqualTo("http://localhost:5175"));
        Assert.That(withUrl.Project, Is.EqualTo("ShopApi"), "det enda API:et ger spec och inställningar också när adressen anges");

        var e = Assert.Throws<UsageException>(() => RunTarget.Resolve(ShopSolution.Path, null, "ShopAp", null, null))!;
        Assert.That(e.Message, Does.Contain("Okänt API 'ShopAp'").And.Contain("Menade du ShopApi?").And.Contain("API:er i solutionen: ShopApi."));
        Assert.That(Assert.Throws<ArgumentException>(() => RunTarget.Resolve(ShopSolution.Path, null, "ShopApi", null, "test", getEnv: n => n == "SHOP_TEST_TOKEN" ? "t" : null, timeout: "abc"))!.Message, Does.Contain("--timeout"));
    }

    [Test]
    public void Full_flags_need_no_solution_or_file()
    {
        Directory.CreateDirectory(Path.Combine(_dir, ".git"));
        var old = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_dir);
        try
        {
            var target = RunTarget.Resolve(null, null, "http://x.test", ShopSpec.Path, null);
            Assert.That((target.Settings.Api, target.Project, target.ConfigPath, target.Spec), Is.EqualTo(("http://x.test", (string?)null, (string?)null, ShopSpec.Path)));
            var e = Assert.Throws<UsageException>(() => RunTarget.Resolve(null, null, null, ShopSpec.Path, null))!;
            Assert.That(e.Message, Does.Contain("API:et som ska testas är inte utpekat"));
        }
        finally { Directory.SetCurrentDirectory(old); }
    }

    // --- Skrivskydd ---

    [Test]
    public async Task A_read_only_environment_skips_tests_that_would_write_and_sends_nothing()
    {
        var target = RunTarget.Resolve(ShopSolution.Path, null, "http://localhost:5175", null, "test", getEnv: n => n == "SHOP_TEST_TOKEN" ? "t" : null);
        var handler = new Recording();
        var (exit, output, _, runner) = await RunOn(target, handler, only: "createCustomer");

        Assert.That(handler.Requests, Is.Empty, "inget skrivande anrop skickas");
        Assert.That(runner.Outcomes, Is.Not.Empty);
        Assert.That(runner.Outcomes.Select(o => o.Result.Verdict), Has.All.EqualTo("HOPPAD (miljön är skrivskyddad)"));
        Assert.That(runner.Outcomes.Select(o => PlainLanguage.Cause(o.Test, o.Result)), Has.All.EqualTo(FailureCause.ReadOnly));
        Assert.That(CauseCodes.Code(FailureCause.ReadOnly), Is.EqualTo("read-only"));
        Assert.That(exit, Is.EqualTo(ExitCodes.Ok), "hoppade tester är inget fel");
        Assert.That(output, Does.Contain("skrivskyddad"));
    }

    [Test]
    public async Task A_read_only_environment_still_runs_reads_but_skips_a_read_that_needs_a_write_to_set_up()
    {
        var spec = Path.Combine(_dir, "ping.openapi.yaml");
        File.WriteAllText(spec, """
            openapi: 3.0.1
            info: { title: Ping, version: '1' }
            paths:
              /ping:
                get:
                  operationId: ping
                  responses:
                    '200': { description: ok }
            """);
        var target = RunTarget.Resolve(ShopSolution.Path, null, "http://localhost:5175", spec, "test", getEnv: n => n == "SHOP_TEST_TOKEN" ? "t" : null);
        var handler = new Recording(HttpStatusCode.OK);
        var (_, _, _, runner) = await RunOn(target, handler, only: "ping");
        Assert.That(handler.Requests.Select(r => r.Method), Has.All.EqualTo("GET"));
        Assert.That(handler.Requests, Is.Not.Empty);
        Assert.That(runner.Outcomes.Count(o => o.Result.Skipped), Is.EqualTo(0));

        var shop = RunTarget.Resolve(ShopSolution.Path, null, "http://localhost:5175", null, "test", getEnv: n => n == "SHOP_TEST_TOKEN" ? "t" : null);
        var (_, _, _, withSetup) = await RunOn(shop, new Recording(HttpStatusCode.OK), only: "getOrder");
        Assert.That(withSetup.Outcomes.Where(o => o.Test.Method == "GET").Select(o => PlainLanguage.Cause(o.Test, o.Result)), Has.All.EqualTo(FailureCause.ReadOnly),
            "getOrder behöver createOrder som förberedande anrop, som inte får skickas");
    }

    [Test]
    public async Task A_run_in_the_local_environment_is_not_read_only()
    {
        var target = RunTarget.Resolve(ShopSolution.Path, null, null, null, null);
        var handler = new Recording();
        await RunOn(target, handler, only: "createCustomer");
        Assert.That(handler.Requests.Select(r => r.Method), Has.Some.EqualTo("POST"));
    }

    // --- Statusvyn och mock ---

    [Test]
    public void The_status_view_shows_the_environments_from_the_file()
    {
        var facts = TestGen.Core.Projects.RepositoryFacts.Read(ShopSolution.Path);
        var (so, se) = (new StringWriter { NewLine = "\n" }, new StringWriter { NewLine = "\n" });
        StatusView.Render(facts, new Terminal(so, se, false), true, TestGenSettings.Load(facts.SolutionDirectory));
        Assert.That(so.ToString(), Does.Contain("  Miljöer  local, test (skrivskyddad)\n"));
        var (o2, e2) = (new StringWriter { NewLine = "\n" }, new StringWriter { NewLine = "\n" });
        StatusView.Render(facts, new Terminal(o2, e2, false), true);
        Assert.That(o2.ToString(), Does.Contain("  Miljöer  local (ingen konfiguration behövs)\n"));
    }

    [Test]
    public void Mock_without_spec_starts_one_mock_per_api_in_the_solution_under_the_project_name()
    {
        var repo = RepositoryContext.Load(ShopSolution.Path, null, needFacts: true);
        var sources = MockCommand.FromSolution(repo, new Terminal(new StringWriter(), new StringWriter(), false));
        Assert.That(sources.Select(s => (Path.GetFileName(s.Spec), s.Name)), Is.EqualTo(new[] { ("shop.openapi.yaml", (string?)"ShopApi") }));
    }
}
