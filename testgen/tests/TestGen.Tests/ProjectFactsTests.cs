using TestGen.Core.Projects;
using TestGen.Core.Views;

namespace TestGen.Tests;

internal static class ShopSolution
{
    /// <summary>samples/Shop.slnx, hittad uppifrån testernas katalog.</summary>
    public static string Path
    {
        get
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            {
                var f = System.IO.Path.Combine(d.FullName, "samples", "Shop.slnx");
                if (File.Exists(f)) return f;
            }
            throw new FileNotFoundException("samples/Shop.slnx");
        }
    }
}

/// <summary>Projektfakta ur Shop-exemplet.</summary>
public class ShopFactsTests
{
    [Test]
    public void Shop_has_one_api_one_web_app_one_test_project_and_two_libraries()
    {
        var facts = RepositoryFacts.Read(ShopSolution.Path);
        Assert.That(facts.Projects.Select(p => (p.Name, p.Kind)), Is.EqualTo(new[]
        {
            ("Shop.Tests", ProjectKind.Test), ("ShopApi", ProjectKind.Api), ("ShopApplication", ProjectKind.Library), ("ShopInfrastructure", ProjectKind.Library), ("ShopWeb", ProjectKind.Web),
        }));
        Assert.That(facts.ProjectsReferencedBy("ShopApi"), Is.EqualTo(new[] { "ShopApplication", "ShopInfrastructure" }));
    }

    [Test]
    public void Spec_is_found_in_the_solution_folder_by_name()
    {
        var facts = RepositoryFacts.Read(ShopSolution.Path);
        Assert.That(Path.GetFileName(facts.SpecOf(facts.Project("ShopApi")!)), Is.EqualTo("shop.openapi.yaml"));
    }

    [Test]
    public void Local_address_and_settings_are_read_from_json()
    {
        var web = RepositoryFacts.Read(ShopSolution.Path).Project("ShopWeb")!;
        Assert.That(web.LocalUrl, Is.EqualTo("http://localhost:5090"));
        Assert.That(web.Settings["ShopApi:BaseUrl"], Is.EqualTo("http://localhost:5080"));
    }

    [Test]
    public void Reading_twice_gives_the_same_facts()
    {
        var (a, b) = (RepositoryFacts.Read(ShopSolution.Path), RepositoryFacts.Read(ShopSolution.Path));
        Assert.That(a.Projects.Select(p => (p.Name, p.Kind, string.Join(",", p.Packages), string.Join(",", p.Settings))),
            Is.EqualTo(b.Projects.Select(p => (p.Name, p.Kind, string.Join(",", p.Packages), string.Join(",", p.Settings)))));
    }
}

internal static class FactsExtensions
{
    public static IReadOnlyList<string> ProjectsReferencedBy(this RepositoryFacts facts, string name) => facts.Project(name)!.ProjectReferences;
}

/// <summary>Faktaläsningen mot små tillfälliga kataloger.</summary>
public class ProjectFactsTests
{
    private string _dir = "";

    [SetUp] public void Up() => _dir = Directory.CreateTempSubdirectory("testgen-facts-").FullName;
    [TearDown] public void Down() => Directory.Delete(_dir, true);

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private string Project(string name, string sdk = "Microsoft.NET.Sdk", string items = "") =>
        Write($"{name}/{name}.csproj", $"<Project Sdk=\"{sdk}\"><ItemGroup>{items}</ItemGroup></Project>");

    private static string Package(string name) => $"<PackageReference Include=\"{name}\" />";

    // --- Solution ------------------------------------------------------------------------------

    [Test]
    public void A_csproj_alone_is_a_solution_of_one()
    {
        var csproj = Project("A");
        Assert.That(RepositoryFacts.Read(csproj).Projects.Select(p => p.Name), Is.EqualTo(new[] { "A" }));
    }

    [Test]
    public void A_missing_solution_is_a_usage_error()
        => Assert.Throws<UsageException>(() => RepositoryFacts.Read(Path.Combine(_dir, "finns-inte.slnx")));

    // --- csproj --------------------------------------------------------------------------------

    [TestCase("NUnit", "nunit")]
    [TestCase("xunit.v3", "xunit")]
    [TestCase("MSTest.TestFramework", "mstest")]
    public void Test_framework_packages_make_a_test_project(string package, string framework)
    {
        var facts = ProjectFacts.Read(Project("T", items: Package(package)));
        Assert.That((facts.Kind, facts.TestFramework), Is.EqualTo((ProjectKind.Test, framework)));
    }

    [Test]
    public void Web_sdk_with_controllers_is_an_api_and_without_is_a_web_app()
    {
        var api = Project("Butik", "Microsoft.NET.Sdk.Web");
        Directory.CreateDirectory(Path.Combine(_dir, "Butik", "Controllers"));
        var web = Project("Skyltfonster", "Microsoft.NET.Sdk.Web");
        Assert.That(ProjectFacts.Read(api).Kind, Is.EqualTo(ProjectKind.Api));
        Assert.That(ProjectFacts.Read(web).Kind, Is.EqualTo(ProjectKind.Web));
    }

    [Test]
    public void An_openapi_package_makes_a_minimal_api_an_api()
        => Assert.That(ProjectFacts.Read(Project("Mini", "Microsoft.NET.Sdk.Web", Package("Microsoft.AspNetCore.OpenApi"))).Kind, Is.EqualTo(ProjectKind.Api));

    [Test]
    public void References_and_packages_that_matter_are_read()
    {
        var facts = ProjectFacts.Read(Project("T", items: "<ProjectReference Include=\"..\\Zeta\\Zeta.csproj\" /><ProjectReference Include=\"../Alfa/Alfa.csproj\" />" +
            Package("Microsoft.Playwright") + Package("SagaPerception.TestGen.NUnit") + Package("Microsoft.AspNetCore.Mvc.Testing")));
        Assert.That(facts.ProjectReferences, Is.EqualTo(new[] { "Alfa", "Zeta" }));
        Assert.That((facts.UsesPlaywright, facts.UsesTestGen), Is.EqualTo((true, true)));
        Assert.That(facts.Packages, Does.Contain("Microsoft.AspNetCore.Mvc.Testing"));
    }

    [Test]
    public void A_project_file_with_a_doctype_is_not_read()
    {
        var path = Write("D/D.csproj", "<!DOCTYPE Project [<!ENTITY x \"y\">]><Project Sdk=\"Microsoft.NET.Sdk.Web\" />");
        Assert.That(ProjectFacts.Read(path).Kind, Is.EqualTo(ProjectKind.Library));
    }

    // --- launchSettings och appsettings --------------------------------------------------------

    [Test]
    public void Local_url_is_the_first_http_address_and_skips_https_only_profiles()
    {
        var csproj = Project("A", "Microsoft.NET.Sdk.Web");
        Write("A/Properties/launchSettings.json", """
            {
              // kommentar
              "profiles": {
                "https": { "commandName": "Project", "applicationUrl": "https://localhost:7001" },
                "http": { "commandName": "Project", "applicationUrl": "https://localhost:7002;http://localhost:5123", },
                "iis": { "commandName": "IISExpress" }
              }
            }
            """);
        Assert.That(ProjectFacts.Read(csproj).LocalUrl, Is.EqualTo("http://localhost:5123"));
    }

    [Test]
    public void Local_url_is_unknown_without_launch_settings_or_with_broken_ones()
    {
        var csproj = Project("A", "Microsoft.NET.Sdk.Web");
        Assert.That(ProjectFacts.Read(csproj).LocalUrl, Is.Null);
        Write("A/Properties/launchSettings.json", "{ inte json");
        Assert.That(ProjectFacts.Read(csproj).LocalUrl, Is.Null);
    }

    [Test]
    public void Settings_are_flattened_and_development_wins()
    {
        var csproj = Project("A", "Microsoft.NET.Sdk.Web");
        Write("A/appsettings.json", """{ "ShopApi": { "BaseUrl": "http://a", "Retries": 3 }, "Hosts": ["x", "y"], "Nothing": null, "Flag": true }""");
        Write("A/appsettings.Development.json", """{ "ShopApi": { "BaseUrl": "http://dev" } }""");
        var settings = ProjectFacts.Read(csproj).Settings;
        Assert.That(settings, Is.EqualTo(new Dictionary<string, string>
        {
            ["Flag"] = "True",
            ["Hosts:0"] = "x",
            ["Hosts:1"] = "y",
            ["ShopApi:BaseUrl"] = "http://dev",
            ["ShopApi:Retries"] = "3",
        }));
        Assert.That(settings.Keys, Is.EqualTo(settings.Keys.Order(StringComparer.OrdinalIgnoreCase)), "nycklarna i stabil ordning");
    }

    [Test]
    public void A_huge_settings_file_is_ignored()
    {
        var csproj = Project("A", "Microsoft.NET.Sdk.Web");
        Write("A/appsettings.json", "{\"A\":\"" + new string('x', 3 * 1024 * 1024) + "\"}");
        Assert.That(ProjectFacts.Read(csproj).Settings, Is.Empty);
    }

    // --- Spec ----------------------------------------------------------------------------------

    private RepositoryFacts Repository(params string[] projects)
    {
        var entries = string.Concat(projects.Select(p => $"<Project Path=\"{p}/{p}.csproj\" />"));
        return RepositoryFacts.Read(Write("r.slnx", $"<Solution>{entries}</Solution>"));
    }

    [Test]
    public void A_spec_next_to_the_api_project_wins_over_one_in_the_solution_folder()
    {
        Project("ShopApi", "Microsoft.NET.Sdk.Web");
        Write("ShopApi/api.openapi.json", "{}");
        Write("shop.openapi.yaml", "");
        var facts = Repository("ShopApi");
        Assert.That(facts.SpecOf(facts.Project("ShopApi")!), Is.EqualTo(Path.Combine(_dir, "ShopApi", "api.openapi.json")));
    }

    [Test]
    public void A_solution_spec_is_matched_by_project_name_case_insensitively_without_the_api_suffix()
    {
        Project("ShopApi", "Microsoft.NET.Sdk.Web"); Project("BillingApi", "Microsoft.NET.Sdk.Web");
        Directory.CreateDirectory(Path.Combine(_dir, "ShopApi", "Controllers")); Directory.CreateDirectory(Path.Combine(_dir, "BillingApi", "Controllers"));
        Write("SHOP.openapi.yml", ""); Write("billing.openapi.yaml", "");
        var facts = Repository("ShopApi", "BillingApi");
        Assert.That(Path.GetFileName(facts.SpecOf(facts.Project("ShopApi")!)), Is.EqualTo("SHOP.openapi.yml"));
        Assert.That(Path.GetFileName(facts.SpecOf(facts.Project("BillingApi")!)), Is.EqualTo("billing.openapi.yaml"));
    }

    [Test]
    public void The_only_api_gets_the_only_spec_but_with_two_apis_a_name_is_needed()
    {
        Project("Orders", "Microsoft.NET.Sdk.Web"); Directory.CreateDirectory(Path.Combine(_dir, "Orders", "Controllers"));
        Write("whatever.openapi.yaml", "");
        var one = Repository("Orders");
        Assert.That(Path.GetFileName(one.SpecOf(one.Project("Orders")!)), Is.EqualTo("whatever.openapi.yaml"));

        Project("Stock", "Microsoft.NET.Sdk.Web"); Directory.CreateDirectory(Path.Combine(_dir, "Stock", "Controllers"));
        var two = Repository("Orders", "Stock");
        Assert.That(two.SpecOf(two.Project("Orders")!), Is.Null);
    }

    [Test]
    public void A_spec_in_a_folder_between_the_project_and_the_solution_is_matched_by_name()
    {
        Write("samples/ShopApi/ShopApi.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />");
        Write("samples/shop.openapi.yaml", "");
        var facts = RepositoryFacts.Read(Write("r.slnx", "<Solution><Project Path=\"samples/ShopApi/ShopApi.csproj\" /></Solution>"));
        Assert.That(Path.GetFileName(facts.SpecOf(facts.Project("ShopApi")!)), Is.EqualTo("shop.openapi.yaml"));
        Write("samples/other.openapi.yaml", "");
        Assert.That(Path.GetFileName(facts.SpecOf(facts.Project("ShopApi")!)), Is.EqualTo("shop.openapi.yaml"));
    }

    [Test]
    public void No_spec_gives_null()
    {
        Project("ShopApi", "Microsoft.NET.Sdk.Web"); Directory.CreateDirectory(Path.Combine(_dir, "ShopApi", "Controllers"));
        var facts = Repository("ShopApi");
        Assert.That(facts.SpecOf(facts.Project("ShopApi")!), Is.Null);
    }

    private const string GeneratingCsproj = """
        <Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup>
        <OpenApiGenerateDocuments>true</OpenApiGenerateDocuments>
        <OpenApiDocumentsDirectory>$(MSBuildProjectDirectory)/../contract</OpenApiDocumentsDirectory>
        <OpenApiGenerateDocumentsOptions>--file-name openapi</OpenApiGenerateDocumentsOptions>
        </PropertyGroup></Project>
        """;

    [Test]
    public void A_spec_written_by_the_build_is_found_from_the_csproj_once_it_exists()
    {
        Write("Api/Api.csproj", GeneratingCsproj);
        var facts = Repository("Api");
        var api = facts.Project("Api")!;
        Assert.That(api.GeneratedSpec, Is.EqualTo(Path.GetFullPath(Path.Combine(_dir, "contract", "openapi.json"))));
        Assert.That(facts.SpecOf(api), Is.Null, "bygget har inte körts");
        Write("contract/openapi.json", "{}");
        Assert.That(facts.SpecOf(api), Is.EqualTo(api.GeneratedSpec));
        Assert.That(api.IsGenerated(facts.SpecOf(api)!), Is.True);
        Assert.That(SpecLocator.MissingAdvice(api), Does.StartWith("Bygg projektet").And.Contain("contract/openapi.json"));
    }

    [Test]
    public void A_checked_in_spec_wins_over_the_one_the_build_writes()
    {
        Write("Api/Api.csproj", GeneratingCsproj);
        Write("Api/api.openapi.json", "{}");
        Write("contract/openapi.json", "{}");
        var facts = Repository("Api");
        Assert.That(facts.SpecOf(facts.Project("Api")!), Is.EqualTo(Path.Combine(_dir, "Api", "api.openapi.json")));
        Assert.That(facts.Project("Api")!.IsGenerated(Path.Combine(_dir, "Api", "api.openapi.json")), Is.False);
    }

    [Test]
    public void Build_spec_defaults_are_obj_and_the_project_name_and_unresolvable_directories_are_not_used()
    {
        Write("Api/Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><ItemGroup><PackageReference Include=\"Microsoft.Extensions.ApiDescription.Server\" /></ItemGroup></Project>");
        Assert.That(ProjectFacts.Read(Path.Combine(_dir, "Api", "Api.csproj")).GeneratedSpec, Is.EqualTo(Path.Combine(_dir, "Api", "obj", "Api.json")));
        Write("Api/Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><OpenApiGenerateDocuments>true</OpenApiGenerateDocuments><OpenApiDocumentsDirectory>$(Out)/x</OpenApiDocumentsDirectory></PropertyGroup></Project>");
        Assert.That(ProjectFacts.Read(Path.Combine(_dir, "Api", "Api.csproj")).GeneratedSpec, Is.EqualTo(Path.Combine(_dir, "Api", "obj", "Api.json")));
        Write("Api/Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><OpenApiGenerateDocuments>false</OpenApiGenerateDocuments></PropertyGroup></Project>");
        Assert.That(ProjectFacts.Read(Path.Combine(_dir, "Api", "Api.csproj")).GeneratedSpec, Is.Null);
    }

    [Test]
    public void The_suggested_spec_name_is_the_project_name_with_openapi_json()
    {
        var api = ProjectFacts.Read(Project("Orders.Api", "Microsoft.NET.Sdk.Web"));
        Assert.That(SpecLocator.SuggestedName(api), Is.EqualTo("Orders.Api.openapi.json"));
        Assert.That(SpecLocator.MissingAdvice(api), Does.Contain("Orders.Api.openapi.json").And.Not.Contain(".."));
        Write("Orders.Api/Orders.Api.openapi.json", "{}");
        var facts = Repository("Orders.Api");
        Assert.That(Path.GetFileName(facts.SpecOf(facts.Project("Orders.Api")!)), Is.EqualTo("Orders.Api.openapi.json"));
    }

    [Test]
    public void Test_targets_skip_bin_obj_node_modules_and_dot_folders()
    {
        var path = Project("Tests", items: Package("SagaPerception.TestGen.NUnit"));
        var dir = Path.GetDirectoryName(path)!;
        foreach (var skipped in new[] { "bin", "obj", "node_modules", ".git" })
            Write($"Tests/{skipped}/X.cs", "public sealed class X() : ApiTests(\"Hoppas\");");
        Write("Tests/Under/Y.cs", "public sealed class Y() : ApiTests(\"Med\");");
        Assert.That(ProjectFacts.Read(path).TestTargets(), Is.EqualTo(new[] { new TestTarget("api", "Med") }));
        Assert.That(dir, Is.Not.Empty);
    }

    // --- views.json ----------------------------------------------------------------------------

    private const string ViewsJsonText = """
        { "schemaVersion": 1, "apps": [ { "project": "W", "apis": [ { "project": "Annat" }, { "project": "ShopApi", "configKey": "ShopApi:BaseUrl" } ],
          "views": [
            { "id": "ui:/", "route": "/", "kind": "blazor", "source": "W/A.razor", "parameters": [], "forms": [], "links": [], "apiCalls": [], "requiresAuth": false },
            { "id": "ui:/about", "route": "/about", "kind": "razor-page", "source": "W/B.cshtml", "parameters": [], "forms": [], "links": [], "apiCalls": [], "requiresAuth": false } ] } ] }
        """;

    [Test]
    public void Views_summary_is_null_without_the_file_and_read_from_the_solution_folder_when_present()
    {
        var web = ProjectFacts.Read(Project("W", "Microsoft.NET.Sdk.Web"));
        var facts = Repository("W");
        Assert.That(facts.ViewsOf(web), Is.Null);
        Write("views.json", ViewsJsonText);
        var summary = facts.ViewsOf(web)!;
        Assert.That((summary.ViewCount, summary.Kind), Is.EqualTo((2, "blazor, razor-page")));
        Assert.That(summary.Apis, Is.EqualTo(new[] { new ViewsApi("Annat", null), new ViewsApi("ShopApi", "ShopApi:BaseUrl") }));
    }

    [Test]
    public void The_views_file_in_a_testgen_test_project_wins_and_is_the_default_place_to_write()
    {
        Project("W", "Microsoft.NET.Sdk.Web");
        Project("W.Tests", items: Package("NUnit") + Package("SagaPerception.TestGen.NUnit"));
        var facts = Repository("W", "W.Tests");
        Assert.That(ViewsLocator.Default(facts, "elsewhere"), Is.EqualTo(Path.Combine(_dir, "W.Tests", "views.json")));
        Write("views.json", ViewsJsonText);
        Write("W.Tests/views.json", ViewsJsonText.Replace("\"/about\"", "\"/other\""));
        Assert.That(facts.ViewsFilePath, Is.EqualTo(Path.Combine(_dir, "W.Tests", "views.json")));
    }

    [Test]
    public void Default_views_path_is_the_given_folder_without_a_testgen_test_project()
    {
        Project("W", "Microsoft.NET.Sdk.Web");
        Assert.That(ViewsLocator.Default(Repository("W"), "elsewhere"), Is.EqualTo(Path.Combine("elsewhere", "views.json")));
    }

    [Test]
    public void A_broken_or_other_version_views_file_gives_null()
    {
        var web = ProjectFacts.Read(Project("W", "Microsoft.NET.Sdk.Web"));
        var facts = Repository("W");
        Write("views.json", "{ trasig");
        Assert.That(facts.ViewsOf(web), Is.Null);
        Write("views.json", ViewsJsonText.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2"));
        Assert.That(facts.ViewsOf(web), Is.Null);
    }

    // --- Chromium ------------------------------------------------------------------------------

    [Test]
    public void Chromium_is_found_in_the_browsers_directory_from_the_environment()
    {
        var browsers = Path.Combine(_dir, "browsers");
        Func<string, string?> env = name => name == "PLAYWRIGHT_BROWSERS_PATH" ? browsers : null;
        Assert.That(ChromiumLocator.IsInstalled(env, _dir, _dir), Is.False, "katalogen finns inte");
        Directory.CreateDirectory(Path.Combine(browsers, "firefox-1"));
        Assert.That(ChromiumLocator.IsInstalled(env, _dir, _dir), Is.False, "bara Firefox");
        Directory.CreateDirectory(Path.Combine(browsers, "chromium-1181"));
        Assert.That(ChromiumLocator.IsInstalled(env, _dir, _dir), Is.True);
    }

    [Test]
    public void Chromium_falls_back_to_the_default_directory()
    {
        var dir = ChromiumLocator.BrowsersDirectory(_ => null, "/home/x", "D:\\Home\\x\\AppData\\Local");
        Assert.That(dir.Replace('\\', '/'), Does.EndWith("ms-playwright"));
    }

    [Test]
    public void Chromium_is_needed_only_when_a_project_uses_playwright_or_has_ui_tests_from_testgen()
    {
        Project("Plain", items: Package("NUnit"));
        Assert.That(Repository("Plain").NeedsChromium, Is.False);
        Project("Ui", items: Package("Microsoft.Playwright"));
        Assert.That(Repository("Plain", "Ui").NeedsChromium, Is.True);
        var api = Project("ApiOnly", items: Package("NUnit") + "<ProjectReference Include=\"..\\SagaPerception.TestGen.NUnit\\SagaPerception.TestGen.NUnit.csproj\" />");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(api)!, "T.cs"), "public sealed class T() : ApiTests(\"ShopApi\");");
        Assert.That(ProjectFacts.Read(api).UsesTestGen, Is.True, "ett projekt i repot räknas som paketet");
        Assert.That(Repository("Plain", "ApiOnly").NeedsChromium, Is.False);
        var ui = Project("UiToo", items: Package("NUnit") + Package("SagaPerception.TestGen.NUnit"));
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(ui)!, "V.cs"), "public sealed class V() : ViewTests(\"ShopWeb\");");
        Assert.That(Repository("Plain", "UiToo").NeedsChromium, Is.True);
    }

    [Test]
    public void Test_classes_point_out_their_projects_by_name()
    {
        var path = Project("Tests", items: Package("SagaPerception.TestGen.NUnit"));
        var dir = Path.GetDirectoryName(path)!;
        File.WriteAllText(Path.Combine(dir, "A.cs"), "public sealed class A() : ApiTests(\"ShopApi\");\npublic sealed class B() : ViewTests( \"ShopWeb\" );");
        File.WriteAllText(Path.Combine(dir, "Dubbel.cs"), "public sealed class C() : ApiTests(\"ShopApi\");");
        Directory.CreateDirectory(Path.Combine(dir, "obj"));
        File.WriteAllText(Path.Combine(dir, "obj", "Genererad.cs"), "public sealed class D() : ApiTests(\"Annat\");");

        Assert.That(ProjectFacts.Read(path).TestTargets(), Is.EqualTo(new[] { new TestTarget("api", "ShopApi"), new TestTarget("ui", "ShopWeb") }));
    }
}
