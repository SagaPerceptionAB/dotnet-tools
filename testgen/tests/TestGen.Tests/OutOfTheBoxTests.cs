using TestGen.Cli;
using TestGen.Core.Projects;
using TestGen.Core.Settings;

namespace TestGen.Tests;

/// <summary>
/// En vanlig .NET-app ska fungera utan testgen.yaml: miljön läses ur appen, compose-filen ger avbildningarna, och testgens filer
/// (testgen.yaml, specen) ligger i testprojektet.
/// </summary>
public class OutOfTheBoxTests
{
    private string _dir = "";

    [SetUp] public void Up() => _dir = Directory.CreateTempSubdirectory("testgen-oob-").FullName;
    [TearDown] public void Down() => Directory.Delete(_dir, true);

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return Path.GetFullPath(path);
    }

    private string Api(string name = "Api") =>
        Write($"{name}/{name}.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><ItemGroup><PackageReference Include=\"Microsoft.AspNetCore.OpenApi\" /></ItemGroup></Project>");

    private const string TestCsproj = "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"NUnit\" /><PackageReference Include=\"SagaPerception.Testing.NUnit\" /></ItemGroup></Project>";

    /// <summary>Solution med ett API och ett testprojekt som använder testgen.</summary>
    private RepositoryFacts Repository(string api = "Api")
    {
        Directory.CreateDirectory(Path.Combine(_dir, ".git"));
        Api(api);
        var slnx = Write("S.slnx", $"<Solution><Project Path=\"{api}/{api}.csproj\" /><Project Path=\"tests/S.Tests/S.Tests.csproj\" /></Solution>");
        Write("tests/S.Tests/S.Tests.csproj", TestCsproj);
        return RepositoryFacts.Read(slnx);
    }

    // --- Miljön ---

    [Test]
    public void The_environment_and_the_address_come_from_the_same_launch_profile()
    {
        Write("Api/Properties/launchSettings.json", """
            {
              "profiles": {
                "https": { "applicationUrl": "https://localhost:7001", "environmentVariables": { "ASPNETCORE_ENVIRONMENT": "Staging" } },
                "http": { "applicationUrl": "http://localhost:5123", "environmentVariables": { "ASPNETCORE_ENVIRONMENT": "Local" } },
                "other": { "applicationUrl": "http://localhost:5124", "environmentVariables": { "ASPNETCORE_ENVIRONMENT": "Production" } }
              }
            }
            """);
        Assert.That(ProjectFacts.LaunchProfileOf(Path.Combine(_dir, "Api")), Is.EqualTo(new LaunchProfile("http://localhost:5123", "Local")));
        var facts = ProjectFacts.Read(Api());
        Assert.That((facts.LocalUrl, facts.LaunchEnvironment), Is.EqualTo(("http://localhost:5123", "Local")));
    }

    [Test]
    public void Without_an_http_profile_the_first_environment_is_used_and_without_a_file_nothing()
    {
        Assert.That(ProjectFacts.LaunchProfileOf(Path.Combine(_dir, "Api")), Is.EqualTo(new LaunchProfile(null, null)));
        Write("Api/Properties/launchSettings.json", """
            { "profiles": { "iis": { "commandName": "IISExpress" }, "https": { "applicationUrl": "https://localhost:7001", "environmentVariables": { "ASPNETCORE_ENVIRONMENT": "Staging" } } } }
            """);
        Assert.That(ProjectFacts.LaunchProfileOf(Path.Combine(_dir, "Api")), Is.EqualTo(new LaunchProfile(null, "Staging")));
    }

    [Test]
    public void The_environment_of_the_app_in_the_test_process_follows_the_order_yaml_Testing_launch_profile_Development()
    {
        var plain = ProjectFacts.Read(Api());
        Assert.That(plain.EnvironmentFor(null), Is.EqualTo("Development"), "ingen profil, ingen Testing-fil");

        Write("Api/Properties/launchSettings.json", """{ "profiles": { "http": { "applicationUrl": "http://localhost:5123", "environmentVariables": { "ASPNETCORE_ENVIRONMENT": "Local" } } } }""");
        var withProfile = ProjectFacts.Read(Api());
        Assert.That(withProfile.EnvironmentFor(null), Is.EqualTo("Local"));
        Assert.That(withProfile.RunEnvironment, Is.EqualTo("Local"));

        Write("Api/appsettings.Testing.json", "{}");
        Assert.That(ProjectFacts.Read(Api()).EnvironmentFor(null), Is.EqualTo("Testing"), "appsettings.Testing.json vinner över profilen");
        Assert.That(ProjectFacts.Read(Api()).EnvironmentFor("Staging"), Is.EqualTo("Staging"), "testgen.yaml vinner över allt");
        Assert.That(ProjectFacts.Read(Api()).EnvironmentFor(""), Is.EqualTo("Testing"), "tomt värde är inget värde");
    }

    [Test]
    public void The_environment_key_is_read_for_apis_and_apps_and_must_be_text()
    {
        var file = Write("testgen.yaml", "apis:\n  Api:\n    environment: Staging\napps:\n  Web:\n    environment: Local\n");
        var settings = TestGenSettings.LoadFile(file);
        Assert.That((settings.ApiEnvironment("api"), settings.AppEnvironment("Web"), settings.ApiEnvironment("Other")), Is.EqualTo(("Staging", "Local", (string?)null)));

        var bad = Write("bad.yaml", "apis:\n  Api:\n    environment: [a, b]\n");
        Assert.That(Assert.Throws<UsageException>(() => TestGenSettings.LoadFile(bad))!.Message, Does.Contain("apis.Api.environment ska vara text"));
    }

    [Test]
    public void An_app_can_have_environment_auth_and_settings_together()
    {
        var file = Write("testgen.yaml", "apps:\n  Web:\n    environment: Local\n    auth:\n      claims:\n        role: Admin\n    settings:\n      Api:Key: x\n");
        var settings = TestGenSettings.LoadFile(file);
        Assert.That((settings.AppEnvironment("Web"), settings.AppClaims("Web")["role"]), Is.EqualTo(("Local", "Admin")));
    }

    // --- Compose ---

    [Test]
    public void Images_are_read_from_the_compose_file_nearest_first_up_to_the_repository_root()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "repo", ".git"));
        Write("above.yml", "services:\n  x:\n    image: ovanfor-git\n");
        Write("compose.yaml", "services:\n  x:\n    image: ovanfor-git\n");
        Write("repo/compose.yaml", "services:\n  db:\n    image: postgres:17-alpine\n  web:\n    build: .\n  later:\n    image: ${LATER}\n  quoted:\n    image: \"redis:7\"\n");
        Write("repo/backend/docker-compose.yml", "services:\n  storage:\n    image: mcr.microsoft.com/azure-storage/azurite:3.37.0\n");
        Directory.CreateDirectory(Path.Combine(_dir, "repo", "backend", "src"));

        var images = ComposeImages.Find(Path.Combine(_dir, "repo", "backend", "src"));

        Assert.That(images, Is.EqualTo(new[] { "mcr.microsoft.com/azure-storage/azurite:3.37.0", "postgres:17-alpine", "redis:7" }));
    }

    [Test]
    public void A_broken_or_empty_compose_file_gives_no_images_and_no_error()
    {
        Directory.CreateDirectory(Path.Combine(_dir, ".git"));
        Write("compose.yml", "services: [inte, en, karta");
        Write("docker-compose.yaml", "");
        Write("docker-compose.yml", "name: bara-ett-namn\n");
        Assert.That(ComposeImages.Find(_dir), Is.Empty);
    }

    [TestCase("postgres:17-alpine", "postgres")]
    [TestCase("docker.io/library/postgres", "postgres")]
    [TestCase("library/redis@sha256:abc", "redis")]
    [TestCase("mcr.microsoft.com/mssql/server:2022-latest", "mcr.microsoft.com/mssql/server")]
    [TestCase("localhost:5000/team/postgres:1", "localhost:5000/team/postgres")]
    public void The_image_name_is_told_without_version_and_default_registry(string image, string expected) =>
        Assert.That(ComposeImages.Repository(image), Is.EqualTo(expected));

    [Test]
    public void Match_takes_the_first_image_with_the_wanted_name()
    {
        string[] images = ["nginx", "postgres:17", "postgres:16", "postgrest/postgrest"];
        Assert.That(ComposeImages.Match(images, "postgres"), Is.EqualTo("postgres:17"));
        Assert.That(ComposeImages.Match(images, "redis"), Is.Null);
    }

    // --- testgen.yaml och specen i testprojektet ---

    [Test]
    public void Testgen_yaml_in_the_test_project_is_found_and_relative_paths_start_there()
    {
        var facts = Repository();
        var file = Write("tests/S.Tests/testgen.yaml", "seed: 5\napis:\n  Api:\n    spec: api.openapi.json\n");

        var settings = facts.LoadSettings();

        Assert.That(settings.Path, Is.EqualTo(file));
        Assert.That(settings.Seed, Is.EqualTo(5));
        Assert.That(settings.SpecOf("Api"), Is.EqualTo(Path.Combine(_dir, "tests", "S.Tests", "api.openapi.json")));
    }

    [Test]
    public void Testgen_yaml_next_to_the_solution_is_still_found_and_without_a_file_the_defaults_are_used()
    {
        var facts = Repository();
        Assert.That(facts.LoadSettings().Path, Is.Null);
        var file = Write("testgen.yaml", "seed: 2\n");
        Assert.That(facts.LoadSettings().Path, Is.EqualTo(file));
    }

    [Test]
    public void Testgen_yaml_in_both_places_is_a_usage_error_that_names_both_files()
    {
        var facts = Repository();
        Write("testgen.yaml", "seed: 1\n");
        Write("tests/S.Tests/testgen.yaml", "seed: 2\n");

        var e = Assert.Throws<UsageException>(() => facts.LoadSettings())!;

        Assert.That(e.Message, Does.Contain("testgen.yaml finns på flera ställen").And.Contain("tests/S.Tests/testgen.yaml").And.Contain("testgen.yaml,"));
        Assert.That(e.Tip, Does.Contain("Behåll en"));
    }

    [Test]
    public void A_named_test_project_decides_where_to_look_so_another_test_projects_file_does_not_count()
    {
        var facts = Repository();
        Write("tests/S.Tests/testgen.yaml", "seed: 3\n");
        Assert.That(facts.LoadSettings("Annat.Tests").Path, Is.Null);
        Assert.That(facts.LoadSettings("S.Tests").Seed, Is.EqualTo(3));
    }

    [Test]
    public void The_spec_is_looked_for_in_the_test_project_before_the_project_and_the_build_output()
    {
        var first = Repository();
        Assert.That(first.SpecOf(first.Project("Api")!), Is.Null);

        var inProject = Write("Api/Api.openapi.json", "{}");
        var second = RepositoryFacts.Read(first.SolutionPath);
        Assert.That(second.SpecOf(second.Project("Api")!), Is.EqualTo(inProject));

        var inTests = Write("tests/S.Tests/Api.openapi.json", "{}");
        Assert.That(second.SpecOf(second.Project("Api")!), Is.EqualTo(inTests), "testprojektet går före projektet");

        File.Delete(inTests);
        Write("tests/S.Tests/Other.openapi.json", "{}");
        Assert.That(second.SpecOf(second.Project("Api")!), Is.EqualTo(inProject), "bara en fil som heter som projektet räknas");
    }

    [Test]
    public void The_advice_for_a_missing_spec_points_at_the_test_project_and_at_init()
    {
        var api = Repository().Project("Api")!;
        var advice = SpecLocator.MissingAdvice(api);

        Assert.That(advice, Does.StartWith("Lägg Api.openapi.json i testprojektet").And.Contain("testgen init"));
        Assert.That(advice, Does.Not.Contain("vid projektet"));
    }

    // --- Specen från appen ---

    [Test]
    public void A_served_spec_is_formatted_and_anything_else_is_refused()
    {
        var formatted = AppSpec.Format("""{"openapi":"3.1.1","info":{"title":"Åäö 'x'","version":"1"},"paths":{}}""");

        Assert.That(formatted, Does.StartWith("{\n  \"openapi\": \"3.1.1\"").And.Contain("Åäö 'x'").And.EndWith("}\n"));
        Assert.That(AppSpec.Format("""{"swagger":"2.0"}"""), Is.Not.Null);
        Assert.That(AppSpec.Format("<html>inte en spec</html>"), Is.Null);
        Assert.That(AppSpec.Format("""{"info":{}}"""), Is.Null);
        Assert.That(AppSpec.Format("[]"), Is.Null);
    }
}
