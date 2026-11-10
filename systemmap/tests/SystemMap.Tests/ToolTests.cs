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
        Assert.That(description, Does.Contain("bakgrundstjänst").And.Contain("konsolprogram").And.Contain("meddelandehanterare").And.Contain("jobb").And.Contain("gRPC").And.Contain("SignalR"));
        Assert.That(description, Does.Contain("--no-unreached"));
    }

    /// <summary>En solution på disk med ett bibliotek, en worker (värd + bakgrundstjänst) och ett konsolprogram, och kod som ingen når.</summary>
    private string ProgramsSolution()
    {
        void Write(string path, string text)
        {
            var full = Path.Combine(_dir, "Program", path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }
        const string Props = "<TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>";
        Write("Lib/Lib.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>{Props}</PropertyGroup></Project>");
        Write("Lib/Store.cs", "namespace Lib; public class Store(HttpClient http) { public Task<string> GetAsync() => http.GetStringAsync(\"https://example.com/data\"); public void Unused() { } }\npublic class Orphan { public void Gone() { } }");
        Write("Worker/Worker.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType>{Props}</PropertyGroup><ItemGroup><FrameworkReference Include=\"Microsoft.AspNetCore.App\" /><ProjectReference Include=\"..\\Lib\\Lib.csproj\" /></ItemGroup></Project>");
        Write("Worker/Program.cs", "var builder = Host.CreateApplicationBuilder(args);\nbuilder.Services.AddHostedService<Cleanup>();\nbuilder.Build().Run();\npublic class Cleanup(Lib.Store store) : BackgroundService { protected override async Task ExecuteAsync(CancellationToken ct) { await store.GetAsync(); } }");
        Write("Tool/Tool.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType>{Props}</PropertyGroup><ItemGroup><ProjectReference Include=\"..\\Lib\\Lib.csproj\" /></ItemGroup></Project>");
        Write("Tool/Program.cs", "Console.WriteLine(await new Lib.Store(new HttpClient()).GetAsync());");
        Write("Programs.slnx", "<Solution><Project Path=\"Lib/Lib.csproj\" /><Project Path=\"Worker/Worker.csproj\" /><Project Path=\"Tool/Tool.csproj\" /></Solution>");
        return Path.Combine(_dir, "Program", "Programs.slnx");
    }

    [Test]
    public void A_worker_and_a_console_program_are_on_the_map_and_code_nobody_reaches_is_listed_unless_no_unreached()
    {
        var solution = ProgramsSolution();
        var json = Path.Combine(_dir, "karta.json");
        var (exit, output, err) = Run("--solution", solution, "--no-browser", "--json", json);

        Assert.That(exit, Is.EqualTo(0), err);
        Assert.That(output, Does.Contain("Bakgrund: Cleanup  Cleanup.ExecuteAsync [Worker]").And.Contain("Konsol: Tool  Program.Main [Tool]"));
        Assert.That(output, Does.Contain("  Worker: worker").And.Contain("  Tool: console"));
        Assert.That(output, Does.Contain("→ Store (HTTP): GET https://example.com/data"), "Store nås från båda");
        Assert.That(output, Does.Contain("Nås inte från någon ingång:\n  Lib: Orphan; metoder i klasser som nås: Store.Unused\n"));
        Assert.That(err, Does.Not.Contain("Hittade inga ingångar"));
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(json));
        Assert.That(doc.RootElement.GetProperty("overview").GetProperty("nodes").EnumerateArray().Where(n => n.TryGetProperty("reached", out _)).Select(n => n.GetProperty("id").GetString()),
            Is.EqualTo(new[] { "class:Lib/Orphan", "method:Lib/Orphan.Gone", "method:Lib/Store.Unused" }));

        var (_, without, _) = Run("--solution", solution, "--no-browser", "--no-unreached", "--json", json);
        Assert.That(without, Does.Contain("Bakgrund: Cleanup").And.Not.Contain("Nås inte").And.Not.Contain("Orphan"));
        Assert.That(File.ReadAllText(json), Does.Not.Contain("Orphan").And.Not.Contain("\"reached\""));
        var page = Path.Combine(_dir, "karta.html");
        Run("--solution", solution, "--no-browser", "--no-unreached", "--html", page);
        Assert.That(File.ReadAllText(page), Does.Contain("unreached: false,"), "sidan startar med Visa delar som inte nås avbockad");
        Run("--solution", solution, "--no-browser", "--html", page);
        Assert.That(File.ReadAllText(page), Does.Contain("unreached: true,"));
    }
    [Test]
    public void Hide_leaves_the_named_parts_out_of_the_text_Mermaid_and_JSON_but_the_page_gets_them_unchecked()
    {
        var (mmd, json, html) = (Path.Combine(_dir, "karta.mmd"), Path.Combine(_dir, "karta.json"), Path.Combine(_dir, "karta.html"));
        var (exit, output, err) = Run("--solution", TestPaths.Solution, "--no-browser", "--level", "methods", "--hide", "orderservice", "--hide", "shopinfrastructure/StockService",
            "--hide", "LagerService", "--mermaid", mmd, "--json", json, "--html", html);

        Assert.That(exit, Is.EqualTo(0), err);
        Assert.That(output, Does.Contain("Dolda delar: OrderService, StockService, LagerService. Vägarna genom dem består."));
        Assert.That(output, Does.Not.Contain("OrderService.").And.Not.Contain("StockService.").And.Not.Contain("Reservera"), "text: varken klasserna eller integrationen");
        Assert.That(output, Does.Contain("OrdersController.Create [ShopApi]").And.Contain("PaymentGateway.ChargeAsync [ShopInfrastructure]"), "anropets egen rad och klassen efter står kvar");
        var written = File.ReadAllText(mmd);
        Assert.That(written, Does.StartWith("flowchart LR\n").And.Contain("%% Dolda delar: OrderService, StockService, LagerService."));
        var diagram = string.Join("\n", written.Split('\n').Where(l => !l.StartsWith("  %% Dolda delar")));
        Assert.That(diagram, Does.Not.Contain("OrderService").And.Not.Contain("StockService").And.Not.Contain("LagerService"));
        Assert.That(diagram, Does.Contain("method_OrdersController_Create --> method_CustomerRepository_GetAsync"), "pilen går förbi den dolda klassen");
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(json));
        Assert.That(doc.RootElement.GetProperty("hidden").EnumerateArray().Select(e => e.GetString()),
            Is.EqualTo(new[] { "class:ShopApplication/OrderService", "class:ShopInfrastructure/StockService", "int:wcf:ILagerService" }));
        Assert.That(File.ReadAllText(json), Does.Not.Contain("Reservera").And.Not.Contain("OrderService."));
        var page = File.ReadAllText(html);
        Assert.That(page, Does.Contain("id=\"hidden-data\">[\"class:ShopApplication/OrderService\",\"class:ShopInfrastructure/StockService\",\"int:wcf:ILagerService\"]</script>"), "sidan startar med delarna avbockade");
        Assert.That(page, Does.Contain("Reservera").And.Contain("OrderService"), "men kartan i sidan är komplett, så att man kan bocka i dem igen");
    }

    [Test]
    public void Hide_with_a_name_that_is_unknown_or_fits_several_parts_stops_before_anything_is_written()
    {
        var mmd = Path.Combine(_dir, "karta.mmd");
        var unknown = Run("--solution", TestPaths.Solution, "--mermaid", mmd, "--hide", "OrderServise");
        Assert.That(unknown.Exit, Is.EqualTo(2));
        Assert.That(unknown.Err, Does.Contain("--hide 'OrderServise' finns inte i kartan.").And.Contain("Gör så här: Menade du ShopApplication/OrderService?"));
        Assert.That(unknown.Out, Is.Empty, "inget skrivs på stdout");
        Assert.That(File.Exists(mmd), Is.False, "ingen fil skrivs");

        var ambiguous = Run("--solution", TestPaths.Solution, "--hide", "ShopApi");
        Assert.That(ambiguous.Exit, Is.EqualTo(2));
        Assert.That(ambiguous.Err, Does.Contain("--hide 'ShopApi' kan vara flera delar: projekt:ShopApi, integration:ShopApi.").And.Contain("Gör så här: Skriv vilken du menar, t.ex. projekt:ShopApi."));
        Assert.That(Run("--solution", TestPaths.Solution, "--no-browser", "--hide", "integration:shopapi").Exit, Is.EqualTo(0), "med integration: är det entydigt");

        Assert.That(Run("--solution", TestPaths.Solution, "--hide").Err, Does.Contain("Flaggan --hide kräver ett värde."));
    }

    [Test]
    public void Help_describes_hide_and_all_four_levels()
    {
        var help = Run("--help").Out;
        Assert.That(help, Does.Contain("--hide <namn>").And.Contain("projects, classes (standard), methods eller all"));
        Assert.That(Run("--level", "funktioner", "--mermaid", "x.mmd").Err, Does.Contain("--level måste vara projects, classes, methods eller all, inte 'funktioner'."));
    }
}
