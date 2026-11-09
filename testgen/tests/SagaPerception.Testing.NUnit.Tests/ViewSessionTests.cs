using System.Text.Json.Nodes;

namespace SagaPerception.Testing.NUnit.Tests;

/// <summary>ShopWeb körs på Kestrel i testprocessen mot en mock ur ShopApis spec, och Chromium kör testerna: utfall, felmeddelande, bilagor och resultatfil.</summary>
public class ViewSessionTests
{
    private string _out = "";

    [SetUp] public void Up() => _out = Directory.CreateTempSubdirectory("testgen-viewsession-").FullName;
    [TearDown] public void Down() => Directory.Delete(_out, true);

    private ViewPlan Plan(Func<string, string?>? getEnv = null) =>
        ViewPlan.Create(() => ViewTarget.Resolve(Samples.ShopTests, "Shop.Tests", "ShopWeb", getEnv, testProjectDirectory: _out), typeof(ViewSessionTests).Assembly, "ShopWeb");

    private static string IdOf(ViewPlan plan, string key) => plan.Cases[plan.Keys.ToList().IndexOf(key)].Id;

    [Test]
    public async Task Pages_links_and_a_form_pass_against_the_app_with_the_api_mocked_from_the_spec()
    {
        var plan = Plan();
        await using var session = await ViewSession.StartAsync(plan);

        await session.RunTestAsync(IdOf(plan, "ui:/products:page"));
        await session.RunTestAsync(IdOf(plan, "ui:/products:links"));
        await session.RunTestAsync(IdOf(plan, "ui:/products/{id:int}:page"));
        await session.RunTestAsync(IdOf(plan, "ui:/products/create:form"));   // mocken får exakt ett POST /products som klarar specen
    }

    [Test]
    public async Task A_form_that_allows_more_than_the_spec_fails_with_what_the_form_allowed_what_the_api_requires_and_what_to_do()
    {
        var plan = Plan();
        await using var session = await ViewSession.StartAsync(plan);

        var failure = await Caught.Failure(() => session.RunTestAsync(IdOf(plan, "ui:/customers/create:boundary-max-length:Name")));

        Assert.That(failure.Message, Does.StartWith("Formuläret tillåter name med 101 tecken, men API:et tar högst 100 (createCustomer)"));
        Assert.That(failure.Message, Does.Contain("/customers/create").And.Contain("mocken svarade 400").And.Contain("Gör så här").And.Contain("ändra specen"));
    }

    [Test]
    public async Task Forms_that_are_stricter_than_the_spec_and_validate_their_own_rules_pass()
    {
        var plan = Plan();
        await using var session = await ViewSession.StartAsync(plan);

        foreach (var key in plan.Keys.Where(k => k.StartsWith("ui:/products/create:") && (k.Contains(":invalid-") || k.Contains(":boundary-"))))
            await session.RunTestAsync(IdOf(plan, key));
    }

    [Test]
    public async Task A_500_from_the_api_is_shown_to_the_user_without_crashing_the_page()
    {
        var plan = Plan();
        await using var session = await ViewSession.StartAsync(plan);

        await session.RunTestAsync(IdOf(plan, "ui:/customers/create:server-error"));
    }

    [Test]
    public async Task The_result_file_has_the_ui_layer_ui_keys_and_one_entry_per_test_that_ran()
    {
        var plan = Plan();
        await using var session = await ViewSession.StartAsync(plan);
        await session.RunTestAsync(IdOf(plan, "ui:/products:page"));
        await Caught.Failure(() => session.RunTestAsync(IdOf(plan, "ui:/customers/create:boundary-max-length:Name")));

        var path = session.WriteResult();

        Assert.That(path, Is.EqualTo(Path.Combine(_out, "TestResults", "testgen", "ShopWeb.json")));
        var json = (JsonObject)JsonNode.Parse(File.ReadAllText(path!))!;
        Assert.That(json.Select(kv => kv.Key).Take(4), Is.EqualTo(new[] { "schemaVersion", "toolVersion", "layer", "project" }));
        Assert.That(json["layer"]!.GetValue<string>(), Is.EqualTo("ui"));
        Assert.That(json["project"]!.GetValue<string>(), Is.EqualTo("ShopWeb"));
        Assert.That(json["possibleTotal"]!.GetValue<int>(), Is.EqualTo(36));
        Assert.That(json["partial"]!.GetValue<bool>(), Is.True);
        var tests = json["tests"]!.AsArray();
        Assert.That(tests.Select(t => t!["key"]!.GetValue<string>()), Is.EqualTo(new[] { "ui:/customers/create:boundary-max-length:Name", "ui:/products:page" }));
        var failed = tests.Single(t => t!["outcome"]!.GetValue<string>() == "failed")!;
        Assert.That(failed["explanation"]!.GetValue<string>(), Does.Contain("Formuläret tillåter name med 101 tecken"));
    }

    [Test]
    public async Task Nothing_is_written_when_no_test_ran()
    {
        await using var session = await ViewSession.StartAsync(Plan());
        Assert.That(session.WriteResult(), Is.Null);
    }

    [Test]
    public async Task A_read_only_environment_ignores_the_forms_without_sending_anything()
    {
        var plan = Plan(name => name == "TESTGEN_ENV" ? "test" : null);
        await using var session = await ViewSession.StartAsync(plan);

        var ignored = (await Assert.ThrowsAsync<IgnoreException>(() => session.RunTestAsync(IdOf(plan, "ui:/customers/create:form"))))!;

        Assert.That(ignored.Message, Does.Contain("skrivskyddad"));
        Assert.That(File.ReadAllText(session.WriteResult()!), Does.Contain("\"environment\": \"test\"").And.Contain("\"skipped\""));
    }

    [Test]
    public async Task Accessibility_and_screenshots_run_when_testgen_yaml_asks_for_them_and_a_new_reference_is_inconclusive()
    {
        var dir = Directory.CreateTempSubdirectory("testgen-viewchecks-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, ".git"));
            var shopApi = Path.GetFullPath(Path.Combine(Samples.Directory, "ShopApi", "ShopApi.csproj"));
            var shopWeb = Path.GetFullPath(Path.Combine(Samples.Directory, "ShopWeb", "ShopWeb.csproj"));
            File.WriteAllText(Path.Combine(dir, "S.slnx"), $"<Solution><Project Path=\"{shopApi}\" /><Project Path=\"{shopWeb}\" /></Solution>");
            File.Copy(Path.Combine(Samples.Directory, "shop.openapi.yaml"), Path.Combine(dir, "shop.openapi.yaml"));
            File.Copy(Path.Combine(Samples.ShopTests, "views.json"), Path.Combine(dir, "views.json"));
            File.WriteAllText(Path.Combine(dir, "testgen.yaml"), "ui:\n  checks: [accessibility, screenshots]\n");
            var plan = ViewPlan.Create(() => ViewTarget.Resolve(dir, null, "ShopWeb", testProjectDirectory: _out), typeof(ViewSessionTests).Assembly, "ShopWeb");
            await using var session = await ViewSession.StartAsync(plan);

            await session.RunTestAsync(IdOf(plan, "ui:/products:accessibility"));
            var first = (await Assert.ThrowsAsync<InconclusiveException>(() => session.RunTestAsync(IdOf(plan, "ui:/products:screenshot"))))!;
            await session.RunTestAsync(IdOf(plan, "ui:/products:screenshot"));   // andra körningen jämför mot referensen som skapades

            Assert.That(first.Message, Does.Contain("saknades och skapades"));
            Assert.That(File.Exists(Path.Combine(_out, "screenshots", "products.png")), Is.True);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public void Settings_under_apps_reach_the_target_with_secrets_marked_and_a_missing_variable_is_a_plan_error()
    {
        var dir = Directory.CreateTempSubdirectory("testgen-viewsettings-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, ".git"));
            var shopApi = Path.GetFullPath(Path.Combine(Samples.Directory, "ShopApi", "ShopApi.csproj"));
            var shopWeb = Path.GetFullPath(Path.Combine(Samples.Directory, "ShopWeb", "ShopWeb.csproj"));
            File.WriteAllText(Path.Combine(dir, "S.slnx"), $"<Solution><Project Path=\"{shopApi}\" /><Project Path=\"{shopWeb}\" /></Solution>");
            File.Copy(Path.Combine(Samples.Directory, "shop.openapi.yaml"), Path.Combine(dir, "shop.openapi.yaml"));
            File.Copy(Path.Combine(Samples.ShopTests, "views.json"), Path.Combine(dir, "views.json"));
            File.WriteAllText(Path.Combine(dir, "testgen.yaml"), "apps:\n  ShopWeb:\n    settings:\n      Min:Nyckel: ${env:WEB_KEY}\n      Min:Annan: vanlig\n");

            var good = ViewPlan.Create(() => ViewTarget.Resolve(dir, null, "ShopWeb", n => n == "WEB_KEY" ? "hemlig-1234" : null, testProjectDirectory: _out), typeof(ViewSessionTests).Assembly, "ShopWeb");
            var bad = ViewPlan.Create(() => ViewTarget.Resolve(dir, null, "ShopWeb", testProjectDirectory: _out), typeof(ViewSessionTests).Assembly, "ShopWeb");

            Assert.That(good.Target!.Config.Values, Is.EqualTo(new Dictionary<string, string> { ["Min:Annan"] = "vanlig", ["Min:Nyckel"] = "hemlig-1234" }));
            Assert.That(good.Target.Config.Secrets, Is.EqualTo(new[] { "hemlig-1234" }));
            Assert.That(bad.Error, Does.Contain("WEB_KEY").And.Contain("apps.ShopWeb.settings, Min:Nyckel"));
        }
        finally { Directory.Delete(dir, true); }
    }
}
