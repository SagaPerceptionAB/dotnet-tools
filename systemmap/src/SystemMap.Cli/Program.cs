using System.Diagnostics;

namespace SystemMap.Cli;

public static class Program
{
    private const string See = "dotnet systemmap";

    public static int Main(string[] args) => Run(args, Terminal.Default);

    public static int Run(string[] args, Terminal terminal) =>
        Command.Run(args, () => RunAsync(args, terminal).GetAwaiter().GetResult(), e => FriendlyError.From(e, See), terminal);

    private static async Task<int> RunAsync(string[] args, Terminal terminal)
    {
        var a = Args.Parse(See, "systemmap", args, ["solution", "spec", .. MapFiles.Flags("json")], "no-restore", "no-browser");
        if (a.Help) { terminal.Out.Write(Help.ToString()); return ExitCodes.Ok; }
        var files = MapFiles.From(a, "json");
        var solution = a.Get("solution") ?? SolutionFinder.Find(Directory.GetCurrentDirectory(), See);
        var operations = a.Get("spec") is { } spec ? SpecReader.Load(spec) : null;

        // Utan filer att skriva blir kartan en webbsida i en tillfällig mapp, som öppnas i webbläsaren.
        var browse = files is { Json: null, Mermaid: null, Html: null };
        if (browse) files = files with { Html = Path.Combine(Path.GetTempPath(), "systemmap", Path.GetFileNameWithoutExtension(solution) + ".html") };
        await CodeMapRun.RunAsync(solution, operations, restore: !a.Flag("no-restore"), files, terminal);
        if (browse && !a.Flag("no-browser") && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"))) Open(files.Html!, terminal);
        return ExitCodes.Ok;
    }

    private static void Open(string page, Terminal terminal)
    {
        terminal.Err.WriteLine("Öppnar kartan i webbläsaren.");
        try { Process.Start(new ProcessStartInfo(Path.GetFullPath(page)) { UseShellExecute = true })?.Dispose(); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            terminal.Warning($"Webbläsaren gick inte att öppna. Öppna filen själv: {Path.GetFullPath(page)}");
        }
    }

    internal static readonly HelpPage Help = new()
    {
        Usage = ["dotnet systemmap [--solution <fil>] [--spec <fil|url>] [--html karta.html] [--mermaid karta.mmd] [--json karta.json] [--hide <namn>]"],
        Description = """
            Följer anropen från varje ingång (controller-action, minimal API, Razor Page eller Blazor-sida) genom projekten ut till integrationerna (databas, HTTP, köer, lagring, e-post).
            Kartan visar vägar som är möjliga enligt koden, inte vilken väg ett visst anrop tog. Den skrivs som text på stdout.
            Utan filer att skriva öppnas den som webbsida. Projektfilerna läses med MSBuild, precis som vid dotnet build: peka bara på kod du litar på.
            """,
        Flags =
        [
            new("--solution <fil>", "Solution (.sln, .slnx) eller projekt (.csproj). Utan flaggan används närmaste solution i mappen eller ovanför."),
            new("--spec <fil|url>", "OpenAPI-spec. Kopplar anropen till specens operationer och visar skillnader."),
            new("--html <fil>", "Skriv kartan som webbsida. Sidan hämtar inget utifrån."),
            new("--mermaid <fil>", "Skriv kartan som Mermaid-flödesschema."),
            new("--level <nivå>", "Med --mermaid: projects, classes (standard), methods eller all."),
            new("--direction <LR|TB>", "Med --mermaid: LR (standard) eller TB."),
            new("--entry \"<anrop>\"", "Med --mermaid: bara vägen för ett anrop, t.ex. \"POST /orders\"."),
            new("--json <fil>", "Skriv kartan som JSON."),
            new("--hide <namn>", "Dölj ett projekt, en klass eller en integration (går att upprepa). Vägarna genom dolda projekt och klasser består. Gäller text, --mermaid och --json; i --html är delarna avbockade i sidan."),
            new("--no-browser", "Öppna inte webbläsaren (görs aldrig när CI är satt)."),
            new("--no-restore", "Kör inte dotnet restore."),
            new("--verbose", "Visa tekniska detaljer när något går fel."),
        ],
        Environment = [new("CI", "Satt: webbläsaren öppnas inte."), new("NO_COLOR", "Satt: ingen färg.")],
        Examples = ["dotnet systemmap", "dotnet systemmap --html docs/systemkarta.html", "dotnet systemmap --mermaid docs/order.mmd --direction TB --entry \"POST /orders\"", "dotnet systemmap --mermaid docs/karta.mmd --level methods --hide OrderRepository"],
        ExitCodes = [new("0", "Klart"), new("2", "Fel i användning (solutionen eller specen saknas eller går inte att läsa)"), new("70", "Internt fel"), new("130", "Avbrutet med Ctrl+C")],
    };
}
