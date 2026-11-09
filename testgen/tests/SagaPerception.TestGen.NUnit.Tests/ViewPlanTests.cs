namespace SagaPerception.TestGen.NUnit.Tests;

/// <summary>Hur testprojektet pekar ut en webbapp med namn: views.json, specen för API:erna och testfallen hittas utan annan konfiguration.</summary>
public class ViewPlanTests
{
    private string _dir = "";

    [SetUp] public void Up() => _dir = Directory.CreateTempSubdirectory("testgen-viewplan-").FullName;
    [TearDown] public void Down() => Directory.Delete(_dir, true);

    private static ViewPlan Shop(Func<string, string?>? getEnv = null, string project = "ShopWeb") =>
        ViewPlan.Create(() => ViewTarget.Resolve(Samples.ShopTests, "Shop.Tests", project, getEnv), typeof(ViewPlanTests).Assembly, project);

    /// <summary>En egen solution med ShopApi, ShopWeb, specen och (om <paramref name="withViews"/>) views.json, så att testgen.yaml kan ändras.</summary>
    private ViewPlan OwnSolution(string yaml, bool withViews = true)
    {
        Directory.CreateDirectory(Path.Combine(_dir, ".git"));
        var shopApi = Path.GetFullPath(Path.Combine(Samples.Directory, "ShopApi", "ShopApi.csproj"));
        var shopWeb = Path.GetFullPath(Path.Combine(Samples.Directory, "ShopWeb", "ShopWeb.csproj"));
        File.WriteAllText(Path.Combine(_dir, "S.slnx"), $"<Solution><Project Path=\"{shopApi}\" /><Project Path=\"{shopWeb}\" /></Solution>");
        File.Copy(Path.Combine(Samples.Directory, "shop.openapi.yaml"), Path.Combine(_dir, "shop.openapi.yaml"));
        if (withViews) File.Copy(Path.Combine(Samples.ShopTests, "views.json"), Path.Combine(_dir, "views.json"));
        File.WriteAllText(Path.Combine(_dir, "testgen.yaml"), yaml);
        return ViewPlan.Create(() => ViewTarget.Resolve(_dir, null, "ShopWeb", testProjectDirectory: _dir), typeof(ViewPlanTests).Assembly, "ShopWeb");
    }

    [Test]
    public void The_views_the_apis_and_the_cases_are_found_from_the_test_project_directory()
    {
        var plan = Shop();

        Assert.That(plan.Error, Is.Null);
        Assert.That(plan.ViewsPath, Does.EndWith("views.json"));
        Assert.That(plan.Target!.InProcess, Is.True, "miljön är local och anger ingen adress för appen");
        Assert.That(plan.Apis.Select(a => (a.Api.Project, a.ConfigKey)), Is.EqualTo(new[] { ("ShopApi", "ShopApi:BaseUrl") }));
        Assert.That(plan.Cases, Has.Count.EqualTo(36));
        Assert.That(plan.Cases.Select(c => c.Id), Is.EqualTo(Enumerable.Range(1, 36).Select(i => $"U-{i:000}")));
        Assert.That(plan.Keys, Has.Count.EqualTo(36).And.All.StartsWith("ui:"));
        Assert.That(plan.Keys.Distinct().Count(), Is.EqualTo(36));
    }

    [Test]
    public void Cases_are_named_with_id_route_and_what_is_tested_and_categorised()
    {
        var cases = Shop().CaseData().ToList();
        var names = cases.Select(c => c.TestName).ToList();

        Assert.That(names[0], Is.EqualTo("U-001 / sida"));
        Assert.That(names, Does.Contain("U-016 /customers/create gränsvärde Name (max-length)"));
        Assert.That(names, Does.Contain("U-015 /customers/create formulär"));
        Assert.That(names, Is.Unique);
        var categories = cases.ToDictionary(c => c.TestName!, c => c.Properties["Category"].Cast<string>().ToArray());
        Assert.That(categories["U-001 / sida"], Is.EqualTo(new[] { "ui" }));
        Assert.That(categories["U-015 /customers/create formulär"], Is.EqualTo(new[] { "ui", "form", "mutating" }));
        Assert.That(categories["U-016 /customers/create gränsvärde Name (max-length)"], Is.EqualTo(new[] { "ui", "boundary" }));
        Assert.That(Shop().Keys.Where(k => k.Contains("boundary")), Is.EqualTo(new[] { "ui:/customers/create:boundary-max-length:Name", "ui:/products/create:boundary-required:Price" }),
            "gränserna där formuläret inte själv har samma regel som specen; bara den första släpper igenom värdet");
        Assert.That(Shop().Keys, Does.Contain("ui:/products/{id:int}:page").And.Contain("ui:/products/create:server-error"));
    }

    [Test]
    public void The_plan_is_the_same_every_time()
    {
        var first = Shop().Cases.Select(c => (c.Id, c.Key, c.Name)).ToList();
        var second = Shop().Cases.Select(c => (c.Id, c.Key, c.Name)).ToList();
        Assert.That(second, Is.EqualTo(first));
    }

    [Test]
    public void Accessibility_and_screenshots_are_extra_cases_per_view_only_when_testgen_yaml_asks_for_them()
    {
        var without = OwnSolution("seed: 12345\n");
        Assert.That(without.Cases.Any(c => c.Kind is UiKind.Accessibility or UiKind.Screenshot), Is.False);
        Down();
        Up();

        var with = OwnSolution("ui:\n  checks: [accessibility, screenshots]\n");

        Assert.That(with.Cases.Count(c => c.Kind == UiKind.Accessibility), Is.EqualTo(6));
        Assert.That(with.Cases.Count(c => c.Kind == UiKind.Screenshot), Is.EqualTo(6));
        Assert.That(with.Keys, Does.Contain("ui:/customers/create:accessibility").And.Contain("ui:/customers/create:screenshot"));
        Assert.That(with.CaseData().Select(c => c.Properties["Category"].Cast<string>().Last()), Does.Contain("accessibility").And.Contain("screenshot"));
    }

    [Test]
    public void A_missing_views_json_says_to_run_testgen_views()
    {
        var plan = OwnSolution("seed: 1\n", withViews: false);

        Assert.That(plan.Error, Does.Contain("views.json").And.Contain("testgen views"));
        Assert.That(plan.CaseData().Single().TestName, Is.EqualTo(ViewPlan.ErrorName));
    }

    [Test]
    public void An_api_is_not_a_web_app_and_an_unknown_name_lists_the_web_apps()
    {
        Assert.That(Shop(project: "ShopApi").Error, Does.Contain("ShopApi").And.Contain("ingen webbapp").And.Contain("ShopWeb"));
        Assert.That(Shop(project: "ButikWeb").Error, Does.Contain("ButikWeb").And.Contain("ShopWeb"));
    }

    [Test]
    public void The_environment_with_an_app_address_tests_that_address_instead_of_the_process()
    {
        var plan = Shop(name => name == "TESTGEN_ENV" ? "test" : null);

        Assert.That(plan.Error, Is.Null);
        Assert.That(plan.Target!.InProcess, Is.False);
        Assert.That(plan.Target.App.Url, Is.EqualTo("https://shopweb-test.example.com"));
        Assert.That(plan.Target.Environment.ReadOnly, Is.True);
        Assert.That(plan.Keys, Is.EqualTo(Shop().Keys), "samma testfall i alla miljöer");
    }

    [Test]
    public void The_ui_section_of_testgen_yaml_is_validated()
    {
        var plan = OwnSolution("ui:\n  checks: [snabbhet]\n");

        Assert.That(plan.Error, Does.Contain("ui.checks").And.Contain("accessibility").And.Contain("screenshots"));
    }
}
