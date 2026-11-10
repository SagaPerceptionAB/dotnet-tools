namespace SystemMap.Tests;

/// <summary>Verktyget systemmap (src/SystemMap.Cli).</summary>
[NonParallelizable]
public class ToolTests
{
    private string _dir = "";

    [SetUp] public void Up() => _dir = Directory.CreateTempSubdirectory("systemmap-").FullName;
    [TearDown] public void Down() => Directory.Delete(_dir, true);

    /// <summary>Kör verktygets Main med omdirigerad stdout/stderr.</summary>
    private static (int Exit, string Out, string Err) Run(params string[] args)
    {
        var (o, e) = (Console.Out, Console.Error);
        var (so, se) = (new StringWriter { NewLine = "\n" }, new StringWriter { NewLine = "\n" });
        Console.SetOut(so); Console.SetError(se);
        try { return (Cli.Program.Main(args), so.ToString(), se.ToString()); }
        finally { Console.SetOut(o); Console.SetError(e); }
    }

    [Test]
    public void Sample_as_web_page_and_mermaid()
    {
        var (html, mmd) = (Path.Combine(_dir, "docs", "karta.html"), Path.Combine(_dir, "order.mmd"));
        var (exit, output, err) = Run("--solution", TestPaths.Solution, "--html", html, "--mermaid", mmd,
            "--entry", "POST /orders", "--no-browser");

        Assert.That(exit, Is.EqualTo(0), err);
        Assert.That(output, Does.Contain("ShopDatabase"), "kartan som text är data på stdout");
        Assert.That(output, Does.Not.Contain("Skrev ").And.Not.Contain("Följer anropen"), "bekräftelser och förlopp hör inte hemma på stdout");
        Assert.That(err, Does.Contain($"Skrev {Terminal.Relative(html)} (webbsida)").And.Contain($"Skrev {Terminal.Relative(mmd)} (karta som Mermaid)").And.Contain("Följer anropen"));
        Assert.That(err, Does.Not.Contain("Öppnar kartan"), "med --html öppnas ingen webbläsare");
        Assert.That(File.ReadAllText(html), Does.Contain("<title>Systemkarta – Shop.slnx</title>"));
        Assert.That(File.ReadAllText(mmd), Does.StartWith("flowchart LR\n").And.Contain("%% Bara vägen för POST /Orders."), "utan spec: sökvägen som i koden");
    }

    [Test]
    public void Unknown_flag_points_to_the_tools_help()
    {
        var (exit, _, err) = Run("--levl", "all");
        Assert.That(exit, Is.EqualTo(2));
        Assert.That(err, Does.Contain("Menade du --level?").And.Contain("Se: dotnet systemmap --help"));
        Assert.That(Run("--level", "all", "--solution", "x.slnx").Err, Does.Contain("--mermaid karta.mmd"));
        Assert.That(Run("--solution", "x.slnx", "--verbose").Err, Does.Contain("Hittar inte solutionen"), "--verbose gäller alla kommandon");
        var help = Run("--help");
        Assert.That(help.Exit, Is.EqualTo(0));
        Assert.That(help.Err, Is.Empty, "hjälpen är data på stdout");
        var positions = HelpPage.Headings.Select(h => help.Out.IndexOf(h + ":", StringComparison.Ordinal)).ToList();
        Assert.That(positions.Where(p => p >= 0), Is.Ordered, "sektionerna i fast ordning");
        Assert.That(help.Out, Does.Contain("Flaggor:").And.Contain("Miljövariabler:").And.Contain("Exempel:").And.Contain("Exitkoder:"));
        Assert.That(help.Out, Does.StartWith("Användning:"));
    }

    [Test]
    public void Help_names_every_kind_of_entry_the_map_follows()
    {
        var description = Run("--help").Out;
        Assert.That(description, Does.Contain("controller-action").And.Contain("minimal API").And.Contain("Razor Page").And.Contain("Blazor-sida"));
    }
}
