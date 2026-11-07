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
        var (so, se) = (new StringWriter(), new StringWriter());
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
        Assert.That(output, Does.Contain("ShopDatabase").And.Contain($"Kartan som webbsida skriven: {html}"));
        Assert.That(output, Does.Not.Contain("Öppnar kartan"), "med --html öppnas ingen webbläsare");
        Assert.That(File.ReadAllText(html), Does.Contain("<title>Systemkarta – Shop.slnx</title>"));
        Assert.That(File.ReadAllText(mmd), Does.StartWith("flowchart LR\n").And.Contain("%% Bara vägen för POST /Orders."), "utan spec: sökvägen som i koden");
    }

    [Test]
    public void Several_solutions_in_the_folder_give_an_error()
    {
        File.WriteAllText(Path.Combine(_dir, "Butik.sln"), "");
        File.WriteAllText(Path.Combine(_dir, "Butik.slnx"), "");
        var e = Assert.Throws<UsageException>(() => SolutionLoader.Find(_dir, "dotnet systemmap"))!;
        Assert.That(e.Message, Does.Contain("Butik.sln, Butik.slnx"));
        Assert.That(e.Tip, Does.Contain("dotnet systemmap --solution Butik.sln"));
    }

    [Test]
    public void Solution_is_found_in_a_parent_folder()
    {
        File.WriteAllText(Path.Combine(_dir, "Butik.slnx"), "");
        Directory.CreateDirectory(Path.Combine(_dir, ".git"));
        var src = Directory.CreateDirectory(Path.Combine(_dir, "src", "Api")).FullName;
        File.WriteAllText(Path.Combine(src, "Api.csproj"), "");
        Assert.That(SolutionLoader.Find(src, "dotnet systemmap"), Is.EqualTo(Path.Combine("..", "..", "Butik.slnx")), "solutionen visar hela systemet");
        File.Delete(Path.Combine(_dir, "Butik.slnx"));
        Assert.That(SolutionLoader.Find(src, "dotnet systemmap"), Is.EqualTo("Api.csproj"), "utan solution: närmaste projekt");

        // Repots rot är gränsen: ovanför den letas inte.
        var empty = Directory.CreateDirectory(Path.Combine(_dir, "empty")).FullName;
        Directory.CreateDirectory(Path.Combine(empty, ".git"));
        var e = Assert.Throws<UsageException>(() => SolutionLoader.Find(empty, "dotnet systemmap"))!;
        Assert.That(e.Message, Does.StartWith("Hittade ingen solution"));
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
        Assert.That(help.Out, Does.StartWith("systemmap - vägen genom källkoden"));
    }
}
