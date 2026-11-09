using TestGen.Core.Settings;

namespace SagaPerception.Testing.NUnit.Tests;

/// <summary>Hur testprojektet pekar ut ett API med namn: solutionen, specen, miljön och testfallen hittas utan annan konfiguration.</summary>
public class ApiPlanTests
{
    private string _dir = "";

    [SetUp] public void Up() => _dir = Directory.CreateTempSubdirectory("testgen-plan-").FullName;
    [TearDown] public void Down() => Directory.Delete(_dir, true);

    private static ApiPlan Shop(Func<string, string?>? getEnv = null, string project = "ShopApi") =>
        ApiPlan.Create(() => TestGenTarget.Resolve(Samples.ShopTests, "Shop.Tests", project, getEnv), project);

    // --- Hitta allt ----------------------------------------------------------------------------

    [Test]
    public void The_solution_the_spec_and_the_cases_are_found_from_the_test_project_directory()
    {
        var plan = Shop();

        Assert.That(plan.Error, Is.Null);
        Assert.That(plan.Target!.Facts.SolutionPath, Does.EndWith("Shop.slnx"));
        Assert.That(Path.GetFileName(plan.Target.Run.Spec), Is.EqualTo("shop.openapi.yaml"));
        Assert.That(plan.Target.InProcess, Is.True, "miljön är local och anger ingen adress");
        Assert.That(plan.Target.TestProjectDirectory, Is.EqualTo(Samples.ShopTests).IgnoreCase.Or.EqualTo(Path.GetFullPath(Samples.ShopTests)).IgnoreCase);
        Assert.That(plan.Tests, Has.Count.EqualTo(34));
        Assert.That(plan.Keys, Has.Count.EqualTo(34).And.All.StartsWith("api:"));
        Assert.That(plan.Keys.Distinct().Count(), Is.EqualTo(34));
        Assert.That(plan.HasSpecCheck, Is.False, "ShopApi serverar ingen spec");
    }

    [Test]
    public void Cases_are_named_with_id_call_and_what_is_tested_and_categorised()
    {
        var cases = Shop().CaseData().ToList();

        Assert.That(cases, Has.Count.EqualTo(34));
        Assert.That(cases[0].TestName, Is.EqualTo("T-001 POST /customers giltig indata"));
        Assert.That(cases.Select(c => c.TestName), Does.Contain("T-020 POST /orders quantity för lågt (under minimum)"));
        Assert.That(cases.Select(c => c.TestName), Is.Unique);
        var categories = cases.ToDictionary(c => c.TestName!, c => c.Properties["Category"].Cast<string>().ToArray());
        Assert.That(categories["T-001 POST /customers giltig indata"], Is.EqualTo(new[] { "api", "mutating" }));
        var get = cases.Single(c => c.TestName!.Contains("GET /orders/{id}"));
        Assert.That(get.Properties["Category"].Cast<string>(), Is.EqualTo(new[] { "api" }), "GET är inte skrivande");
        Assert.That(cases.Select(c => c.Properties.Get("key")), Is.All.StartsWith("api:"));
    }

    [Test]
    public void The_plan_does_not_depend_on_how_many_times_it_is_made()
    {
        var first = Shop().Tests.Select(t => (t.Id, t.Description)).ToList();
        var second = Shop().Tests.Select(t => (t.Id, t.Description)).ToList();
        Assert.That(second, Is.EqualTo(first));
    }

    // --- Miljön --------------------------------------------------------------------------------

    [Test]
    public void The_environment_comes_from_testgen_env_and_points_at_the_address_instead_of_the_process()
    {
        string? Env(string name) => name switch { "TESTGEN_ENV" => "test", "SHOP_TEST_TOKEN" => "hemlig-token-1234", _ => null };
        var plan = Shop(Env);

        Assert.That(plan.Error, Is.Null);
        Assert.That(plan.Target!.Environment.Name, Is.EqualTo("test"));
        Assert.That(plan.Target.InProcess, Is.False);
        Assert.That(plan.Target.Run.Settings.Api, Is.EqualTo("https://shop-test.example.com"));
        Assert.That(plan.Target.Run.Settings.ReadOnly, Is.True);
        Assert.That(plan.Target.Run.Settings.Token, Is.EqualTo("hemlig-token-1234"));
        Assert.That(plan.Target.Run.Settings.Headers["X-Tenant"], Is.EqualTo("demo"));
        Assert.That(plan.Target.Run.Settings.TimeoutSeconds, Is.EqualTo(30));
        Assert.That(plan.Tests, Has.Count.EqualTo(34), "samma testfall i alla miljöer");
        Assert.That(plan.Keys, Is.EqualTo(Shop().Keys));
        Assert.That(plan.Target.RunOptions().ReadOnly, Is.True);
    }

    [Test]
    public void A_missing_secret_for_the_chosen_environment_is_an_error_with_what_to_do()
    {
        var plan = Shop(name => name == "TESTGEN_ENV" ? "test" : null);

        Assert.That(plan.Error, Does.Contain("SHOP_TEST_TOKEN"));
        Assert.That(plan.CaseData().Single().TestName, Is.EqualTo(ApiPlan.ErrorName));
    }

    [Test]
    public void An_unknown_environment_lists_the_known_ones()
    {
        var plan = Shop(name => name == "TESTGEN_ENV" ? "prodd" : null);
        Assert.That(plan.Error, Does.Contain("Okänd miljö 'prodd'").And.Contain("local").And.Contain("test"));
    }

    // --- Fel som ska synas som ett test --------------------------------------------------------

    [Test]
    public void An_unknown_project_becomes_one_failing_case_that_lists_the_apis()
    {
        var plan = Shop(project: "ButikApi");

        Assert.That(plan.Error, Does.Contain("ButikApi").And.Contain("ShopApi").And.Contain("Gör så här"));
        var only = plan.CaseData().Single();
        Assert.That((only.TestName, only.Arguments), Is.EqualTo((ApiPlan.ErrorName, new object[] { ApiTests.ErrorKind, "" })));
    }

    private void Solution(bool withSpec, string packages = "")
    {
        File.WriteAllText(Path.Combine(_dir, "S.slnx"), "<Solution><Project Path=\"Min/Min.csproj\" /></Solution>");
        Directory.CreateDirectory(Path.Combine(_dir, "Min", "Controllers"));
        File.WriteAllText(Path.Combine(_dir, "Min", "Min.csproj"), $"<Project Sdk=\"Microsoft.NET.Sdk.Web\"><ItemGroup>{packages}</ItemGroup></Project>");
        if (withSpec) File.Copy(Path.Combine(Samples.Directory, "shop.openapi.yaml"), Path.Combine(_dir, "Min", "min.openapi.yaml"));
        Directory.CreateDirectory(Path.Combine(_dir, ".git"));
    }

    [Test]
    public void A_missing_spec_says_where_to_put_it()
    {
        Solution(withSpec: false);
        var plan = ApiPlan.Create(() => TestGenTarget.Resolve(_dir, null, "Min"), "Min");

        Assert.That(plan.Error, Does.Contain("Hittar ingen spec för Min").And.Contain("Min.openapi.json"));
        Assert.That(plan.Error, Does.Not.Contain("code-first"));
    }

    [Test]
    public void A_missing_spec_for_a_code_first_app_says_that_init_fetches_it()
    {
        Solution(withSpec: false, packages: "<PackageReference Include=\"Microsoft.AspNetCore.OpenApi\" />");
        var plan = ApiPlan.Create(() => TestGenTarget.Resolve(_dir, null, "Min"), "Min");

        Assert.That(plan.Error, Does.Contain("serverar sin spec själv").And.Contain("testgen init").And.Contain("i testprojektet"));
    }

    [Test]
    public void A_code_first_app_also_gets_a_test_that_compares_the_spec_in_the_repo_with_the_app()
    {
        Solution(withSpec: true, packages: "<PackageReference Include=\"Microsoft.AspNetCore.OpenApi\" />");
        var plan = ApiPlan.Create(() => TestGenTarget.Resolve(_dir, null, "Min"), "Min");

        Assert.That(plan.Error, Is.Null);
        Assert.That(plan.HasSpecCheck, Is.True);
        Assert.That(plan.CaseData().Last().TestName, Is.EqualTo(ApiPlan.SpecCheckName));
        Assert.That(plan.CaseData().Count(), Is.EqualTo(35));
    }

    [Test]
    public void No_spec_check_is_made_against_an_environment_with_an_address()
    {
        File.WriteAllText(Path.Combine(_dir, "testgen.yaml"), "environments:\n  test:\n    apis:\n      Min:\n        url: https://min.example.com\n");
        Solution(withSpec: true, packages: "<PackageReference Include=\"Microsoft.AspNetCore.OpenApi\" />");
        var plan = ApiPlan.Create(() => TestGenTarget.Resolve(_dir, null, "Min", name => name == "TESTGEN_ENV" ? "test" : null), "Min");

        Assert.That(plan.HasSpecCheck, Is.False);
    }

    [Test]
    public void Settings_with_a_mistake_become_the_message_of_the_failing_case()
    {
        File.WriteAllText(Path.Combine(_dir, "testgen.yaml"), "enviroments: {}\n");
        Solution(withSpec: true);
        var plan = ApiPlan.Create(() => TestGenTarget.Resolve(_dir, null, "Min"), "Min");

        Assert.That(plan.Error, Does.Contain("enviroments").And.Contain("environments"));
    }

    [Test]
    public void The_settings_type_exists_for_the_environment_variable_name()
    {
        // Namnet på miljövariabeln är en del av det publika löftet (README): TESTGEN_ENV.
        Assert.That(TestGenSettings.EnvironmentVariable, Is.EqualTo("TESTGEN_ENV"));
    }
}
