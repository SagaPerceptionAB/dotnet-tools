using TestGen.Cli;
using TestGen.Core.Projects;

namespace TestGen.Tests;

/// <summary>Statusvyn (<c>testgen</c> utan argument).</summary>
public class StatusViewTests
{
    private string _dir = "";
    private string _previous = "";

    [SetUp]
    public void Up()
    {
        _previous = Directory.GetCurrentDirectory();
        _dir = Directory.CreateTempSubdirectory("testgen-status-").FullName;
        Directory.CreateDirectory(Path.Combine(_dir, ".git"));
    }

    [TearDown]
    public void Down()
    {
        Directory.SetCurrentDirectory(_previous);
        Directory.Delete(_dir, true);
    }

    private static (string Out, string Err) Render(RepositoryFacts facts, bool chromium = true)
    {
        var (so, se) = (new StringWriter { NewLine = "\n" }, new StringWriter { NewLine = "\n" });
        StatusView.Render(facts, new Terminal(so, se, false), chromium);
        return (so.ToString(), se.ToString());
    }

    private static (int Exit, string Out, string Err) Cli(params string[] args)
    {
        var (o, e) = (Console.Out, Console.Error);
        var (so, se) = (new StringWriter { NewLine = "\n" }, new StringWriter { NewLine = "\n" });
        Console.SetOut(so); Console.SetError(se);
        try { return (Program.Main(args), so.ToString(), se.ToString()); }
        finally { Console.SetOut(o); Console.SetError(e); }
    }

    [Test]
    public void Shop_shows_api_with_spec_app_tests_and_environments_and_the_next_step()
    {
        var (o, e) = Render(RepositoryFacts.Read(ShopSolution.Path));
        Assert.That(e, Is.Empty);
        Assert.That(o, Does.StartWith("testgen – ").And.Contain("Shop.slnx\n\n"));
        Assert.That(o, Does.Contain("  API:er   ShopApi   spec: ").And.Contain("shop.openapi.yaml"));
        Assert.That(o, Does.Contain("\n  Appar    ShopWeb   6 vyer (blazor, razor-page)   anropar ShopApi via ShopApi:BaseUrl\n"));
        Assert.That(o, Does.Contain("\n  Tester   Shop.Tests (API: ShopApi, UI: ShopWeb)\n").And.Contain("\n  Miljöer  local (ingen konfiguration behövs)\n"));
        Assert.That(o, Does.EndWith("\nNästa steg: testgen init\n"));
    }

    [Test]
    public void Shop_status_is_fast()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Render(RepositoryFacts.Read(ShopSolution.Path));
        Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)), "målet är under en sekund, gränsen är vid för att tåla långsamma maskiner");
    }

    [Test]
    public void The_app_row_shows_views_and_calls_only_when_views_json_exists()
    {
        File.WriteAllText(Path.Combine(_dir, "r.slnx"), "<Solution><Project Path=\"W/W.csproj\" /></Solution>");
        Directory.CreateDirectory(Path.Combine(_dir, "W"));
        File.WriteAllText(Path.Combine(_dir, "W", "W.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />");
        Assert.That(Render(RepositoryFacts.Read(Path.Combine(_dir, "r.slnx"))).Out, Does.Contain("  Appar    W\n"));

        File.WriteAllText(Path.Combine(_dir, "views.json"),
            """
            { "schemaVersion": 1, "apps": [ { "project": "W", "apis": [ { "project": "ShopApi", "configKey": "ShopApi:BaseUrl" } ], "views": [
              { "id": "ui:/a", "route": "/a", "kind": "blazor", "source": "a", "parameters": [], "forms": [], "links": [], "apiCalls": [], "requiresAuth": false },
              { "id": "ui:/b", "route": "/b", "kind": "blazor", "source": "b", "parameters": [], "forms": [], "links": [], "apiCalls": [], "requiresAuth": false },
              { "id": "ui:/c", "route": "/c", "kind": "blazor", "source": "c", "parameters": [], "forms": [], "links": [], "apiCalls": [], "requiresAuth": false } ] } ] }
            """);
        Assert.That(Render(RepositoryFacts.Read(Path.Combine(_dir, "r.slnx"))).Out,
            Does.Contain("  Appar    W   3 vyer (blazor)   anropar ShopApi via ShopApi:BaseUrl\n"));
    }

    [Test]
    public void An_api_without_a_spec_warns_on_stderr_with_what_to_do()
    {
        File.WriteAllText(Path.Combine(_dir, "r.slnx"), "<Solution><Project Path=\"ShopApi/ShopApi.csproj\" /></Solution>");
        Directory.CreateDirectory(Path.Combine(_dir, "ShopApi", "Controllers"));
        File.WriteAllText(Path.Combine(_dir, "ShopApi", "ShopApi.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />");
        var (o, e) = Render(RepositoryFacts.Read(Path.Combine(_dir, "r.slnx")));
        Assert.That(o, Does.Contain("ShopApi   spec: saknas"));
        Assert.That(e, Does.StartWith("Varning: ShopApi saknar spec.").And.Contain("ShopApi.openapi.json"));
    }

    [Test]
    public void Missing_chromium_warns_only_when_a_project_needs_it()
    {
        File.WriteAllText(Path.Combine(_dir, "r.slnx"), "<Solution><Project Path=\"T/T.csproj\" /></Solution>");
        Directory.CreateDirectory(Path.Combine(_dir, "T"));
        File.WriteAllText(Path.Combine(_dir, "T", "T.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"NUnit\" /></ItemGroup></Project>");
        Assert.That(Render(RepositoryFacts.Read(Path.Combine(_dir, "r.slnx")), chromium: false).Err, Is.Empty);

        File.WriteAllText(Path.Combine(_dir, "T", "T.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"NUnit\" /><PackageReference Include=\"SagaPerception.TestGen.NUnit\" /></ItemGroup></Project>");
        Assert.That(Render(RepositoryFacts.Read(Path.Combine(_dir, "r.slnx")), chromium: false).Err, Is.Empty, "bara API-tester: ingen webbläsare behövs");

        File.WriteAllText(Path.Combine(_dir, "T", "WebTests.cs"), "public sealed class WebTests() : ViewTests(\"ShopWeb\");");
        var (o, e) = Render(RepositoryFacts.Read(Path.Combine(_dir, "r.slnx")), chromium: false);
        Assert.That(e, Does.StartWith("Varning: Chromium saknas"));
        Assert.That(o, Does.Contain("  Tester   T (UI: ShopWeb)\n"));
        Assert.That(Render(RepositoryFacts.Read(Path.Combine(_dir, "r.slnx")), chromium: true).Err, Is.Empty);
    }

    [Test]
    public void Without_a_solution_it_says_so_with_exit_0()
    {
        Directory.SetCurrentDirectory(_dir);
        var (exit, o, e) = Cli();
        Assert.That((exit, e), Is.EqualTo((0, "")));
        Assert.That(o, Does.Contain("ingen solution").And.Contain("testgen --help"));
    }

    [Test]
    public void No_arguments_in_a_folder_with_a_solution_shows_the_status()
    {
        File.WriteAllText(Path.Combine(_dir, "Butik.slnx"), "<Solution />");
        Directory.SetCurrentDirectory(_dir);
        var (exit, o, _) = Cli();
        Assert.That(exit, Is.EqualTo(0));
        Assert.That(o, Does.StartWith("testgen – Butik.slnx").And.Contain("Nästa steg: testgen init"));
    }

    [Test]
    public void Several_solutions_are_a_usage_error_and_the_tip_works()
    {
        File.WriteAllText(Path.Combine(_dir, "A.slnx"), "<Solution />");
        File.WriteAllText(Path.Combine(_dir, "B.slnx"), "<Solution />");
        Directory.SetCurrentDirectory(_dir);
        var (exit, _, e) = Cli();
        Assert.That(exit, Is.EqualTo(2));
        Assert.That(e, Does.Contain("testgen --solution A.slnx"));
        var (exit2, o2, _) = Cli("--solution", "A.slnx");
        Assert.That(exit2, Is.EqualTo(0));
        Assert.That(o2, Does.StartWith("testgen – A.slnx"));
    }
}
