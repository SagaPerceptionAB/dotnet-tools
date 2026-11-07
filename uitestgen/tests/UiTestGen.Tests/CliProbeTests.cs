using UiTestGen.Cli;
using UiTestGen.Model;

namespace UiTestGen.Tests;

/// <summary>Kommandoraden för sonderingen och --spa. Inga av dem öppnar en webbläsare.</summary>
public class CliProbeTests
{
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

    private static string Samples()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "samples", "spa-fixtures"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("Hittar inte samples."), "samples");
    }

    private static string TempFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "uitestgen-probe-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "vykatalog.json");
    }

    [Test]
    public void Probe_help_and_main_help_describe_the_new_flags()
    {
        var (code, output, _) = Run("probe", "--help");
        Assert.That(code, Is.EqualTo(0));
        Assert.That(output, Does.Contain("--app").And.Contain("--browser-path").And.Contain("UITEST_BROWSER_PATH").And.Contain("Exitkoder"));
        Assert.That(Run("--help").Out, Does.Contain("uitestgen probe").And.Contain("--spa").And.Contain("--max-pages"));
        Assert.That(Run("discover", "--help").Out, Does.Contain("--spa").And.Contain("--app"));
    }

    [Test]
    public void Probe_requires_catalog_and_app()
    {
        Assert.That(Run("probe", "--app", "http://localhost:1").Code, Is.EqualTo(2));
        var (code, _, err) = Run("probe", "--catalog", "finns-inte.json", "--app", "http://localhost:1");
        Assert.That(code, Is.EqualTo(2));
        Assert.That(err, Does.Contain("Hittar inte vykatalogen"));
    }

    [Test]
    public void An_unusable_app_url_or_max_pages_is_a_usage_error()
    {
        var spa = Path.Combine(Samples(), "spa-fixtures", "react");
        var (code, _, err) = Run("discover", "--spa", spa, "--app", "localhost:5000", "--out", TempFile());
        Assert.That((code, err), Is.EqualTo((2, err)));
        Assert.That(err, Does.Contain("--app"));
        Assert.That(Run("discover", "--spa", spa, "--app", "http://localhost:5000", "--max-pages", "0", "--out", TempFile()).Err, Does.Contain("--max-pages"));
    }

    [Test]
    public void Missing_browser_gives_install_instructions_and_exit_code_2()
    {
        var spa = Path.Combine(Samples(), "spa-fixtures", "react");
        var (code, _, err) = Run("discover", "--spa", spa, "--app", "http://localhost:5000", "--browser-path", "/finns/inte/chrome", "--out", TempFile());
        Assert.That(code, Is.EqualTo(2));
        Assert.That(err, Does.Contain("Hittar inte webbläsaren").And.Contain("playwright.ps1 install chromium"));
    }

    [Test]
    public void Spa_alone_makes_the_first_folder_the_root()
    {
        var react = Path.Combine(Samples(), "spa-fixtures", "react");
        var catalogFile = TempFile();
        var (code, _, _) = Run("discover", "--spa", react, "--out", catalogFile);
        Assert.That(code, Is.EqualTo(0));
        var views = ViewCatalogJson.Read(catalogFile).Views;
        Assert.That(views, Is.Not.Empty);
        Assert.That(views.Select(v => v.Kind).Distinct(), Is.EqualTo(new[] { ViewKind.Spa }));
        Assert.That(views.Select(v => v.Source), Has.All.StartWith("src/"));
    }

    [Test]
    public void Spa_together_with_solution_uses_the_solution_folder_as_root_and_dotdot_paths_outside_it()
    {
        var shop = Path.Combine(Samples(), "ShopWeb", "ShopWeb.csproj");
        var catalogFile = TempFile();
        var (code, _, _) = Run("discover", "--solution", shop, "--spa", Path.Combine(Samples(), "spa-fixtures", "react"),
            "--spa", Path.Combine(Samples(), "spa-fixtures", "vue"), "--out", catalogFile);
        Assert.That(code, Is.EqualTo(0));
        var views = ViewCatalogJson.Read(catalogFile).Views;
        Assert.That(views.Any(v => v.Kind == ViewKind.Blazor && v.Source.StartsWith("Components/")), Is.True);
        Assert.That(views.Where(v => v.Kind == ViewKind.Spa).Select(v => v.Source), Has.All.StartWith("../spa-fixtures/"));
        Assert.That(views.Any(v => v.Source.StartsWith("../spa-fixtures/vue/")), Is.True);
    }

    [Test]
    public void Discover_without_solution_or_spa_is_a_usage_error()
    {
        var (code, _, err) = Run("discover", "--out", TempFile());
        Assert.That(code, Is.EqualTo(2));
        Assert.That(err, Does.Contain("--solution").And.Contain("--spa"));
    }
}
