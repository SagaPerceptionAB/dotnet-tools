using System.Diagnostics;
using System.Text.RegularExpressions;
using TestGen.Cli;

namespace TestGen.Tests;

/// <summary>testgen init: testprojektet, solutionen, paketversionerna och att andra körningen inte ändrar något. API-bara repon, utan Chromium.</summary>
public class InitCommandTests
{
    private string _root = "";

    [SetUp] public void Up() => _root = Directory.CreateTempSubdirectory("testgen-init-").FullName;
    [TearDown] public void Down() => Directory.Delete(_root, true);

    private static (StringWriter Out, StringWriter Err, Terminal Terminal) NewTerminal()
    {
        var (o, e) = (new StringWriter { NewLine = "\n" }, new StringWriter { NewLine = "\n" });
        return (o, e, new Terminal(o, e, interactive: false));
    }

    private static readonly InitOptions Options = new(Restore: false, InstallChromium: false, LibraryProject: LibraryProjectPath());

    private static string LibraryProjectPath()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var f = Path.Combine(d.FullName, "testgen", "src", "SagaPerception.TestGen.NUnit", "SagaPerception.TestGen.NUnit.csproj");
            if (File.Exists(f)) return f;
        }
        throw new FileNotFoundException("testgen/src/SagaPerception.TestGen.NUnit");
    }

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private string Rel(string path) => Terminal.Relative(Path.Combine(_root, path));

    private const string Spec = "openapi: 3.0.3\ninfo: { title: T, version: '1' }\npaths: {}\n";

    private string Api(string name)
    {
        Write($"{name}/{name}.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        Directory.CreateDirectory(Path.Combine(_root, name, "Controllers"));
        Write($"{name.ToLowerInvariant()}.openapi.yaml", Spec);
        return $"{name}/{name}.csproj";
    }

    private string Solution(params string[] projects) =>
        Write("My.slnx", "<Solution>" + string.Concat(projects.Select(p => $"<Project Path=\"{p}\" />")) + "</Solution>");

    private const string Central = "<Project>\n  <PropertyGroup>\n    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>\n  </PropertyGroup>\n"
        + "  <ItemGroup>\n    <PackageVersion Include=\"Alpha\" Version=\"1.0.0\" />\n    <PackageVersion Include=\"Zeta\" Version=\"1.0.0\" />\n  </ItemGroup>\n</Project>\n";

    private async Task<(int Exit, string Out, string Err)> InitAsync(string solution, InitOptions? options = null)
    {
        var (o, e, terminal) = NewTerminal();
        var exit = await InitCommand.RunAsync(solution, options ?? Options, terminal);
        return (exit, o.ToString(), e.ToString());
    }

    private string Snapshot() =>
        string.Join("\n", Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Order(StringComparer.Ordinal).Select(f => f + "\n" + File.ReadAllText(f)));

    [Test]
    public async Task Creates_the_test_project_with_one_line_per_api_adds_it_to_the_solution_and_shows_the_ci_step()
    {
        var solution = Solution(Api("ShopApi"), Api("Billing.Api"));

        var (exit, o, e) = await InitAsync(solution);

        Assert.That(exit, Is.EqualTo(0), e);
        var tests = Path.Combine(_root, "tests", "My.Tests");
        var csproj = File.ReadAllText(Path.Combine(tests, "My.Tests.csproj"));
        Assert.That(csproj, Does.Contain("<TargetFramework>net10.0</TargetFramework>").And.Contain("<IsTestProject>true</IsTestProject>")
            .And.Contain("Include=\"..\\..\\ShopApi\\ShopApi.csproj\"").And.Contain("Include=\"..\\..\\Billing.Api\\Billing.Api.csproj\"")
            .And.Contain("<PackageReference Include=\"NUnit\" Version=\"").And.Contain("<PackageReference Include=\"NUnit3TestAdapter\" Version=\"")
            .And.Contain("<PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\""), "utan central pakethantering står versionerna i projektet");
        Assert.That(File.ReadAllText(Path.Combine(tests, "ShopApiTests.cs")),
            Is.EqualTo("using SagaPerception.TestGen.NUnit;\n\nnamespace My.Tests;\n\npublic sealed class ShopApiTests() : ApiTests(\"ShopApi\");\n"));
        Assert.That(File.ReadAllText(Path.Combine(tests, "BillingApiTests.cs")), Does.Contain("ApiTests(\"Billing.Api\")"), "klassnamnet är en giltig identifierare");
        Assert.That(File.Exists(Path.Combine(tests, "views.json")), Is.False, "utan webbapp ingen views.json");
        Assert.That(File.ReadAllText(solution), Does.Contain("tests/My.Tests/My.Tests.csproj").And.Not.Contain("SagaPerception.TestGen.NUnit"), "bara testprojektet läggs till, inte projekten det refererar");

        Assert.That(e, Does.Contain($"Skrev {Rel("tests/My.Tests/My.Tests.csproj")} (testprojekt)").And.Contain($"Skrev {Rel("tests/My.Tests/ShopApiTests.cs")} (API-tester för ShopApi)")
            .And.Not.Contain("Allt finns redan"));
        Assert.That(o, Does.Contain("- run: dotnet test tests/My.Tests").And.EndWith("\nNästa steg: dotnet test\n").And.Not.Contain("chromium"));
    }

    [Test]
    public async Task With_Microsoft_Testing_Platform_in_global_json_the_project_is_an_exe_and_the_commands_use_project()
    {
        Write("global.json", "{ \"test\": { \"runner\": \"Microsoft.Testing.Platform\" } }");
        var solution = Solution(Api("ShopApi"));

        var (_, o, _) = await InitAsync(solution);

        var csproj = File.ReadAllText(Path.Combine(_root, "tests", "My.Tests", "My.Tests.csproj"));
        Assert.That(csproj, Does.Contain("<OutputType>Exe</OutputType>").And.Contain("<EnableNUnitRunner>true</EnableNUnitRunner>"));
        Assert.That(o, Does.Contain("- run: dotnet test --project tests/My.Tests").And.EndWith("\nNästa steg: dotnet test --project tests/My.Tests\n"));
    }

    [Test]
    public async Task A_global_json_further_up_is_found_and_other_runners_change_nothing()
    {
        Write("global.json", "{ \"sdk\": { \"version\": \"10.0.100\" } }");
        var (_, o, _) = await InitAsync(Solution(Api("ShopApi")));
        Assert.That(File.ReadAllText(Path.Combine(_root, "tests", "My.Tests", "My.Tests.csproj")), Does.Not.Contain("EnableNUnitRunner"));
        Assert.That(o, Does.EndWith("\nNästa steg: dotnet test\n"));
        Assert.That(InitCommand.UsesTestingPlatform(Path.Combine(_root, "tests")), Is.False);
        Write("global.json", "{ \"test\": { \"runner\": \"Microsoft.Testing.Platform\" } }");
        Assert.That(InitCommand.UsesTestingPlatform(Path.Combine(_root, "tests", "x")), Is.True, "letar uppåt");
    }

    [Test]
    public async Task With_central_package_management_versions_go_in_Directory_Packages_props_in_alphabetical_order()
    {
        var props = Write("Directory.Packages.props", Central);
        var solution = Solution(Api("ShopApi"));

        var (_, _, e) = await InitAsync(solution, Options with { LibraryProject = null });

        var csproj = File.ReadAllText(Path.Combine(_root, "tests", "My.Tests", "My.Tests.csproj"));
        Assert.That(csproj, Does.Contain("<PackageReference Include=\"NUnit\" />").And.Contain("<PackageReference Include=\"SagaPerception.TestGen.NUnit\" />").And.Not.Contain("Version="));
        var lines = File.ReadAllLines(props).Where(l => l.Contains("<PackageVersion")).Select(l => Regex.Match(l, "Include=\"([^\"]+)\" Version=\"([^\"]+)\"").Groups).ToList();
        Assert.That(lines.Select(g => g[1].Value), Is.EqualTo(new[]
            { "Alpha", "Microsoft.NET.Test.Sdk", "NUnit", "NUnit3TestAdapter", "SagaPerception.TestGen.NUnit", "Zeta" }));
        Assert.That(lines.Select(g => g[2].Value), Has.All.Matches<string>(v => v is not null && Regex.IsMatch(v, @"^\d+\.\d+\.\d+")), "riktiga versioner");
        Assert.That(e, Does.Contain("(paketversioner: Microsoft.NET.Test.Sdk, NUnit, NUnit3TestAdapter, SagaPerception.TestGen.NUnit)"));
    }

    [Test]
    public async Task A_second_run_changes_nothing_and_says_so()
    {
        Write("Directory.Packages.props", Central);
        var solution = Solution(Api("ShopApi"));
        await InitAsync(solution);
        var before = Snapshot();

        var (exit, o, e) = await InitAsync(solution);

        Assert.That(exit, Is.EqualTo(0));
        Assert.That(Snapshot(), Is.EqualTo(before));
        Assert.That(e, Does.Contain($"Allt finns redan i {Rel("tests/My.Tests")}. Inget ändrades.").And.Not.Contain("Skrev "));
        Assert.That(o, Does.EndWith("Nästa steg: dotnet test\n"));
    }

    [Test]
    public async Task A_new_api_adds_only_what_is_missing_and_never_overwrites_the_users_files()
    {
        var solution = Solution(Api("ShopApi"));
        await InitAsync(solution);
        var tests = Path.Combine(_root, "tests", "My.Tests");
        var mine = Path.Combine(tests, "ShopApiTests.cs");
        File.WriteAllText(mine, File.ReadAllText(mine) + "// mina egna ändringar\n");
        var own = Write("tests/My.Tests/Own.cs", "// inte testgens\n");
        Solution(Api("ShopApi"), Api("StockApi"), "tests/My.Tests/My.Tests.csproj");
        var ownText = File.ReadAllText(own);

        var (_, _, e) = await InitAsync(solution);

        Assert.That(File.ReadAllText(mine), Does.EndWith("// mina egna ändringar\n"));
        Assert.That(File.ReadAllText(own), Is.EqualTo(ownText));
        Assert.That(File.ReadAllText(Path.Combine(tests, "StockApiTests.cs")), Does.Contain("ApiTests(\"StockApi\")"));
        Assert.That(File.ReadAllText(Path.Combine(tests, "My.Tests.csproj")), Does.Contain("StockApi.csproj"));
        Assert.That(e, Does.Contain($"Skrev {Rel("tests/My.Tests/StockApiTests.cs")}").And.Not.Contain("ShopApiTests.cs"));
    }

    [Test]
    public async Task An_existing_file_with_the_same_name_is_left_alone_with_a_warning()
    {
        var solution = Solution(Api("ShopApi"));
        var mine = Write("tests/My.Tests/ShopApiTests.cs", "// min egen\n");

        var (_, _, e) = await InitAsync(solution);

        Assert.That(File.ReadAllText(mine), Is.EqualTo("// min egen\n"));
        Assert.That(e, Does.Contain($"Varning: {Rel("tests/My.Tests/ShopApiTests.cs")} finns redan och lämnades orörd").And.Contain("ApiTests(\"ShopApi\")"));
    }

    [Test]
    public async Task A_test_project_folder_that_does_not_use_testgen_is_a_usage_error_with_what_to_do()
    {
        var solution = Solution(Api("ShopApi"));
        Write("tests/My.Tests/My.Tests.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        var e = await Assert.ThrowsAsync<UsageException>(() => InitAsync(solution));

        Assert.That(e!.Message, Does.Contain("finns redan men använder inte testgen"));
        Assert.That(e.Tip, Does.Contain("SagaPerception.TestGen.NUnit"));
    }

    [Test]
    public async Task An_api_without_a_spec_is_skipped_with_a_warning()
    {
        Write("Lone/Lone.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        Directory.CreateDirectory(Path.Combine(_root, "Lone", "Controllers"));
        var solution = Solution("Lone/Lone.csproj", Api("ShopApi"));
        File.Delete(Path.Combine(_root, "shopapi.openapi.yaml"));
        Write("a.openapi.yaml", Spec);
        Write("b.openapi.yaml", Spec);

        var (_, _, e) = await InitAsync(solution);

        Assert.That(File.Exists(Path.Combine(_root, "tests", "My.Tests", "LoneTests.cs")), Is.False);
        Assert.That(e, Does.Contain("Varning: Lone saknar spec").And.Contain("Lone.openapi.json").And.Contain("Kör sedan testgen init igen"));
    }

    [Test]
    public async Task A_code_first_api_without_a_spec_is_started_and_its_spec_is_saved_in_the_test_project()
    {
        var app = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "OpenApiApp", "OpenApiApp.csproj"));
        var solution = Write("My.slnx", $"<Solution><Project Path=\"{app}\" /></Solution>");

        var (exit, _, e) = await InitAsync(solution);

        var spec = Path.Combine(_root, "tests", "My.Tests", "OpenApiApp.openapi.json");
        Assert.That(exit, Is.EqualTo(0), e);
        Assert.That(e, Does.Contain($"Skrev {Rel("tests/My.Tests/OpenApiApp.openapi.json")} (spec för OpenApiApp, hämtad från appen)"));
        Assert.That(File.ReadAllText(spec), Does.StartWith("{\n  \"openapi\"").And.Contain("\"/greetings/{id}\""));
        Assert.That(File.ReadAllText(Path.Combine(_root, "tests", "My.Tests", "OpenApiAppTests.cs")), Does.Contain("ApiTests(\"OpenApiApp\")"));

        var before = Snapshot();
        var (_, _, again) = await InitAsync(solution);
        Assert.That(Snapshot(), Is.EqualTo(before), "andra körningen startar inte appen och ändrar inget");
        Assert.That(again, Does.Not.Contain("Startar OpenApiApp").And.Not.Contain("Skrev "));
    }

    [Test]
    public async Task A_code_first_api_that_does_not_start_is_an_error_but_init_goes_on_with_the_rest()
    {
        Write("Broken/Broken.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>"
            + "<ItemGroup><PackageReference Include=\"Microsoft.AspNetCore.OpenApi\" Version=\"10.0.14\" /></ItemGroup></Project>");
        Write("Broken/Program.cs", "throw new System.InvalidOperationException(\"databasen saknas\");\n");
        var solution = Solution("Broken/Broken.csproj", Api("ShopApi"));

        var (exit, _, e) = await InitAsync(solution);

        Assert.That(exit, Is.EqualTo(2), "ett fel, men resten gjordes");
        Assert.That(e, Does.Contain("Fel: Broken startade inte.").And.Contain("databasen saknas").And.Contain("Lägg Broken.openapi.json i testprojektet").And.Contain("Kör sedan testgen init igen"));
        var tests = Path.Combine(_root, "tests", "My.Tests");
        Assert.That(File.Exists(Path.Combine(tests, "ShopApiTests.cs")), Is.True);
        Assert.That(File.Exists(Path.Combine(tests, "BrokenTests.cs")), Is.False);
        Assert.That(File.Exists(Path.Combine(tests, "Broken.openapi.json")), Is.False);
    }

    [Test]
    public async Task Nothing_to_test_is_a_usage_error()
    {
        Write("L/L.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var solution = Solution("L/L.csproj");
        var e = await Assert.ThrowsAsync<UsageException>(() => InitAsync(solution));
        Assert.That(e!.Message, Does.Contain("varken API eller webbapp"));
    }

    [Test]
    public async Task On_the_checked_in_samples_everything_exists_and_nothing_changes()
    {
        var views = Path.Combine(Path.GetDirectoryName(ShopSolution.Path)!, "Shop.Tests", "views.json");
        var before = File.ReadAllText(views);

        var (exit, _, e) = await InitAsync(ShopSolution.Path);

        Assert.That(exit, Is.EqualTo(0), e);
        Assert.That(e, Does.Contain("views.json är oförändrad (vyer: 6, appar: 1)").And.Contain("Allt finns redan i ").And.Contain("Inget ändrades.").And.Not.Contain("Skrev "));
        Assert.That(File.ReadAllText(views), Is.EqualTo(before));
    }

    [Test]
    public void The_ci_step_installs_and_caches_chromium_only_when_there_is_a_web_app()
    {
        var withApp = InitCommand.CiStep("tests/My.Tests", "net10.0", chromium: true);
        Assert.That(withApp, Does.Contain("actions/cache@v4").And.Contain("~/.cache/ms-playwright")
            .And.Contain("pwsh tests/My.Tests/bin/Debug/net10.0/playwright.ps1 install --with-deps chromium").And.Contain("dotnet test tests/My.Tests --no-build"));
        var without = InitCommand.CiStep("tests/My.Tests", "net10.0", chromium: false);
        Assert.That(without, Does.Not.Contain("chromium").And.Contain("dotnet test tests/My.Tests"));
    }

    [Test]
    public void The_command_line_requires_no_flags_and_the_help_describes_it()
    {
        Assert.That(HelpText.For("init"), Does.StartWith("Användning:").And.Contain("--solution").And.Contain("Exitkoder:"));
        Assert.That(HelpText.Commands, Has.Member("init"));
    }
}

/// <summary>
/// Hela vägen på en kopia av Shop utan testprojekt: testgen init, sondering och dotnet test på resultatet. Långsamt (bygger och kör UI-testerna i Chromium),
/// så kategorin Slow går att filtrera bort lokalt. Testprojektet pekar på testgens källkod i stället för paketet (InitOptions.LibraryProject).
/// </summary>
[Category("Slow")]
[TestFixture]
[NonParallelizable]
public class InitOnShopCopyTests
{
    private string _root = "";
    private string _solution = "";
    private string _tests = "";

    [OneTimeSetUp]
    public async Task InitAsync()
    {
        var samples = Path.GetDirectoryName(ShopSolution.Path)!;
        var repo = Path.GetDirectoryName(samples)!;
        _root = Directory.CreateTempSubdirectory("testgen-init-shop-").FullName;
        foreach (var file in new[] { "Directory.Build.props", "Directory.Packages.props", "global.json", "eng/Versions.props" })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(_root, file))!);
            File.Copy(Path.Combine(repo, file), Path.Combine(_root, file));
        }
        foreach (var project in new[] { "ShopApi", "ShopApplication", "ShopInfrastructure", "ShopWeb" }) Copy(Path.Combine(samples, project), Path.Combine(_root, "samples", project));
        foreach (var file in Directory.GetFiles(samples, "*.yaml")) File.Copy(file, Path.Combine(_root, "samples", Path.GetFileName(file)));
        _solution = Path.Combine(_root, "samples", "Shop.slnx");
        File.WriteAllText(_solution, "<Solution>\n  <Project Path=\"ShopApi/ShopApi.csproj\" />\n  <Project Path=\"ShopApplication/ShopApplication.csproj\" />\n"
            + "  <Project Path=\"ShopInfrastructure/ShopInfrastructure.csproj\" />\n  <Project Path=\"ShopWeb/ShopWeb.csproj\" />\n</Solution>\n");
        _tests = Path.Combine(_root, "samples", "tests", "Shop.Tests");

        var (o, e) = (new StringWriter(), new StringWriter());
        var library = Path.Combine(repo, "testgen", "src", "SagaPerception.TestGen.NUnit", "SagaPerception.TestGen.NUnit.csproj");
        await InitCommand.RunAsync(_solution, new InitOptions(InstallChromium: false, LibraryProject: library), new Terminal(o, e, interactive: false));
        Assert.That(e.ToString(), Does.Contain($"Skrev {Terminal.Relative(Path.Combine(_tests, "views.json"))} (vyer: 6, appar: 1)"));
    }

    [OneTimeTearDown]
    public void Cleanup()
    {
        try { Directory.Delete(_root, true); }
        catch (IOException) { /* kvarglömda processer kan hålla en fil; tempkatalogen städas av systemet */ }
    }

    private static void Copy(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(from, file);
            if (relative.StartsWith("bin" + Path.DirectorySeparatorChar) || relative.StartsWith("obj" + Path.DirectorySeparatorChar)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(to, relative))!);
            File.Copy(file, Path.Combine(to, relative));
        }
    }

    [Test]
    public void The_generated_project_looks_like_samples_Shop_Tests()
    {
        var samples = Path.GetDirectoryName(ShopSolution.Path)!;
        foreach (var file in new[] { "ShopApiTests.cs", "ShopWebViewTests.cs" })
            Assert.That(File.ReadAllText(Path.Combine(_tests, file)), Is.EqualTo(File.ReadAllText(Path.Combine(samples, "Shop.Tests", file))));
        Assert.That(File.ReadAllText(Path.Combine(_tests, "Shop.Tests.csproj")), Does.Contain("views.json").And.Contain("ShopApi.csproj").And.Contain("ShopWeb.csproj"));
        Assert.That(File.ReadAllText(_solution), Does.Contain("Shop.Tests.csproj"));
    }

    [Test, DependsOnTest(nameof(The_generated_project_looks_like_samples_Shop_Tests), AllowFailure = true), CancelAfter(600_000)]
    public void Probe_adds_what_only_shows_when_the_app_runs()
    {
        var (exit, o, err) = Cli("views", "--probe", "--solution", _solution);
        Assert.That(exit, Is.EqualTo(0), err);
        Assert.That(err, Does.Contain("Sonderar ShopWeb ..."));
        Assert.That(o, Does.Contain("ShopWeb   anropar ShopApi via ShopApi:BaseUrl"));
        var json = File.ReadAllText(Path.Combine(_tests, "views.json"));
        Assert.That(json, Does.Contain("\"text\": \"Kaffebryggare\""), "länktexten syns bara i den körande appen");
    }

    [Test, DependsOnTest(nameof(Probe_adds_what_only_shows_when_the_app_runs), AllowFailure = true), CancelAfter(900_000)]
    public async Task Dotnet_test_runs_green_except_the_expected_failures()
    {
        var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = _tests };
        foreach (var arg in new[] { "test", "--nologo" }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var text = await output + await error;

        var summary = Regex.Match(text, @"Failed:\s+(\d+), Passed:\s+(\d+), Skipped:\s+(\d+), Total:\s+(\d+)");
        Assert.That(summary.Success, Is.True, text);
        Assert.That(summary.Groups[1].Value, Is.EqualTo("2"), "ShopApi och ShopWeb har avsiktliga fel (samples/expected-failures.txt)" + Environment.NewLine + text);
        Assert.That(int.Parse(summary.Groups[2].Value), Is.GreaterThan(50), text);
    }

    private static (int Exit, string Out, string Err) Cli(params string[] args)
    {
        var (o, e) = (Console.Out, Console.Error);
        var (so, se) = (new StringWriter { NewLine = "\n" }, new StringWriter { NewLine = "\n" });
        Console.SetOut(so); Console.SetError(se);
        try { return (Program.Main(args), so.ToString(), se.ToString()); }
        finally { Console.SetOut(o); Console.SetError(e); }
    }
}
