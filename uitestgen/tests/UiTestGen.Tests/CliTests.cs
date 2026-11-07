using UiTestGen.Cli;
using UiTestGen.Model;

namespace UiTestGen.Tests;

public class CliTests
{
    /// <summary>Kör Main och fångar det som skrivs till stdout och stderr.</summary>
    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var oldOut = Console.Out;
        var oldErr = Console.Error;
        using var o = new StringWriter();
        using var e = new StringWriter();
        Console.SetOut(o);
        Console.SetError(e);
        try { return (Program.Main(args), o.ToString(), e.ToString()); }
        finally { Console.SetOut(oldOut); Console.SetError(oldErr); }
    }

    private static string TempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "uitestgen-cli-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [TestCase("--help")]
    [TestCase("discover", "--help")]
    [TestCase("generate", "--help")]
    [TestCase("probe", "--help")]
    public void Help_exits_with_zero(params string[] args)
    {
        var (code, output, _) = Run(args);
        Assert.That(code, Is.EqualTo(0));
        Assert.That(output, Does.Contain("uitestgen"));
        Assert.That(output, Does.Contain("Exitkoder"));
    }

    [Test]
    public void No_arguments_prints_help()
    {
        var (code, output, _) = Run();
        Assert.That(code, Is.EqualTo(0));
        Assert.That(output, Does.Contain("Användning"));
    }

    [TestCase("--okand", "x")]
    [TestCase("discover", "--okand", "x")]
    [TestCase("generate", "--okand", "x")]
    [TestCase("nonsens")]
    public void Unknown_flag_or_command_exits_with_two(params string[] args)
    {
        var (code, _, err) = Run(args);
        Assert.That(code, Is.EqualTo(2));
        Assert.That(err, Does.Contain("Gör så här"));
    }

    [Test]
    public void Missing_required_flag_exits_with_two()
    {
        Assert.That(Run("discover").Code, Is.EqualTo(2));
        Assert.That(Run("generate", "--out", "x").Code, Is.EqualTo(2));
        Assert.That(Run("generate", "--catalog", "finns-inte.json", "--out", "x").Code, Is.EqualTo(2));
    }

    [Test]
    public void Discover_on_an_empty_directory_gives_an_empty_catalog()
    {
        var dir = TempDirectory();
        try
        {
            var catalogPath = Path.Combine(dir, "vykatalog.json");
            var (code, output, _) = Run("discover", "--solution", dir, "--out", catalogPath);
            Assert.That(code, Is.EqualTo(0));
            Assert.That(output, Does.Contain("Hittade 0 vyer"));
            var catalog = ViewCatalogJson.Read(catalogPath);
            Assert.That(catalog.Views, Is.Empty);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public void Generate_writes_the_project_and_reports_the_count()
    {
        var dir = TempDirectory();
        try
        {
            var catalogPath = Path.Combine(dir, "vykatalog.json");
            ViewCatalogJson.Write(GenerationTests.Catalog(), catalogPath);
            var outDir = Path.Combine(dir, "Shop.UiTests");
            var (code, output, _) = Run("generate", "--catalog", catalogPath, "--out", outDir);
            Assert.That(code, Is.EqualTo(0));
            Assert.That(output, Does.Contain("Skrev 21 av 21 filer"));
            Assert.That(File.Exists(Path.Combine(outDir, "Shop.UiTests.csproj")), Is.True);
            Assert.That(File.ReadAllText(Path.Combine(outDir, "Support", "UiTestBase.g.cs")), Does.Contain("DefaultBackend = \"real\""));

            Assert.That(Run("generate", "--catalog", catalogPath, "--out", outDir, "--backend", "mock").Code, Is.EqualTo(0));
            Assert.That(File.ReadAllText(Path.Combine(outDir, "Support", "UiTestBase.g.cs")), Does.Contain("DefaultBackend = \"mock\""));
            Assert.That(Run("generate", "--catalog", catalogPath, "--out", outDir, "--backend", "x").Code, Is.EqualTo(2));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public void Generate_lists_orphan_views_and_stays_quiet_when_there_are_none()
    {
        var dir = TempDirectory();
        try
        {
            var outDir = Path.Combine(dir, "Shop.UiTests");
            var catalogPath = Path.Combine(dir, "vykatalog.json");
            ViewCatalogJson.Write(GenerationNavigationTests.OrphanShop(), catalogPath);
            var (code, output, _) = Run("generate", "--catalog", catalogPath, "--out", outDir);
            Assert.That(code, Is.EqualTo(0));
            Assert.That(output, Does.Contain("1 vy som ingen annan vy länkar till:").And.Contain("  /about  (About.razor)"));

            ViewCatalogJson.Write(GenerationNavigationTests.HomeOnly(), catalogPath);
            var quiet = Run("generate", "--catalog", catalogPath, "--out", Path.Combine(dir, "Quiet.UiTests"));
            Assert.That(quiet.Out, Does.Not.Contain("som ingen annan vy länkar till"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public void Both_steps_in_one_list_orphans_and_the_layout_menu_removes_them_from_the_list()
    {
        var dir = TempDirectory();
        try
        {
            var source = Path.Combine(dir, "app");
            Directory.CreateDirectory(Path.Combine(source, "Pages"));
            File.WriteAllText(Path.Combine(source, "Pages", "Index.cshtml"), "@page\n<h1>Hem</h1>");
            File.WriteAllText(Path.Combine(source, "Pages", "Hidden.cshtml"), "@page\n<h1>Gömd</h1>");
            var (code, output, _) = Run("--solution", source, "--out", Path.Combine(dir, "Out.UiTests"));
            Assert.That(code, Is.EqualTo(0));
            Assert.That(output, Does.Contain("1 vy som ingen annan vy länkar till:").And.Contain("  /Hidden  (Pages/Hidden.cshtml)"));

            File.WriteAllText(Path.Combine(source, "Pages", "_ViewStart.cshtml"), "@{ Layout = \"_Layout\"; }");
            Directory.CreateDirectory(Path.Combine(source, "Pages", "Shared"));
            File.WriteAllText(Path.Combine(source, "Pages", "Shared", "_Layout.cshtml"), "<a href=\"/Hidden\">Gömd</a>@RenderBody()");
            Assert.That(Run("--solution", source, "--out", Path.Combine(dir, "Out.UiTests")).Out, Does.Not.Contain("som ingen annan vy länkar till"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public void Help_texts_name_every_flag_and_environment_variable()
    {
        var all = Run("--help").Out;
        foreach (var flag in new[] { "--solution", "--spa", "--app", "--max-pages", "--browser-path", "--catalog", "--out", "--name", "--backend", "--spec", "--seed", "--verbose" })
            Assert.That(all, Does.Contain(flag), flag);
        foreach (var variable in new[] { "UITEST_BROWSER_PATH", "UITEST_BASE_URL", "UITEST_BACKEND", "UITEST_STUB", "UITEST_STUB_PORT", "UITEST_UPDATE_SCREENSHOTS" })
            Assert.That(all, Does.Contain(variable), variable);
        Assert.That(Run("discover", "--help").Out, Does.Contain("--max-pages").And.Contain("--browser-path").And.Contain("UITEST_BROWSER_PATH"));
        Assert.That(Run("probe", "--help").Out, Does.Contain("--catalog").And.Contain("--app").And.Contain("--max-pages").And.Contain("UITEST_BROWSER_PATH"));
        Assert.That(Run("generate", "--help").Out, Does.Contain("--backend").And.Contain("--spec").And.Contain("--seed").And.Contain("--name").And.Contain("UITEST_BACKEND"));
    }

    [Test]
    public void Both_steps_in_one_write_the_catalog_in_the_output_directory()
    {
        var dir = TempDirectory();
        try
        {
            var source = Path.Combine(dir, "app");
            Directory.CreateDirectory(source);
            var outDir = Path.Combine(dir, "Out.UiTests");
            var (code, _, _) = Run("--solution", source, "--out", outDir);
            Assert.That(code, Is.EqualTo(0));
            Assert.That(File.Exists(Path.Combine(outDir, "vykatalog.json")), Is.True);
            Assert.That(File.Exists(Path.Combine(outDir, "Out.UiTests.csproj")), Is.True);
        }
        finally { Directory.Delete(dir, true); }
    }
}
