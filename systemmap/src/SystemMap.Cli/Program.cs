using System.Diagnostics;

namespace SystemMap.Cli;

public static class Program
{
    private const string See = "dotnet systemmap";

    public static int Main(string[] args) =>
        Command.Run(args, () => RunAsync(args).GetAwaiter().GetResult(), e => FriendlyError.From(e, See));

    private static async Task<int> RunAsync(string[] args)
    {
        var a = Args.Parse(See, "systemmap", args, ["solution", "spec", .. MapFiles.Flags("json")], "no-restore", "no-browser");
        if (a.Help) { Console.WriteLine(Help); return StandardExitCodes.Ok; }
        var files = MapFiles.From(a, "json");
        var solution = a.Get("solution") ?? SolutionLoader.Find(Directory.GetCurrentDirectory(), See);
        var operations = a.Get("spec") is { } spec ? SpecReader.Load(spec) : null;

        // Utan filer att skriva blir kartan en webbsida i en tillfällig mapp, som öppnas i webbläsaren.
        var browse = files is { Json: null, Mermaid: null, Html: null };
        if (browse) files = files with { Html = Path.Combine(Path.GetTempPath(), "systemmap", Path.GetFileNameWithoutExtension(solution) + ".html") };
        await CodeMapRun.RunAsync(solution, operations, restore: !a.Flag("no-restore"), files, Console.Out, Console.Error);
        if (browse && !a.Flag("no-browser") && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"))) Open(files.Html!);
        return StandardExitCodes.Ok;
    }

    private static void Open(string page)
    {
        Console.WriteLine("Öppnar kartan i webbläsaren.");
        try { Process.Start(new ProcessStartInfo(Path.GetFullPath(page)) { UseShellExecute = true })?.Dispose(); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            Console.WriteLine($"Webbläsaren gick inte att öppna. Öppna filen själv: {Path.GetFullPath(page)}");
        }
    }

    internal const string Help = """
        systemmap - vägen genom källkoden ut till integrationerna

        Användning:
          dotnet systemmap [--solution <.sln|.slnx|.csproj>] [--spec <fil|url>] [--html karta.html]
                           [--mermaid karta.mmd [--level ...] [--direction ...] [--entry ...]] [--json karta.json]

        Läser källkoden och följer anropen från varje controller-action genom projekten ut till integrationerna:
        databas (EF Core, Dapper, SQL), andra API:er (HTTP, Refit), WCF/SOAP, köer (Service Bus, MassTransit,
        RabbitMQ, Kafka), lagring (Blob, filer) och e-post (SMTP). Kartan visar vägar som är möjliga enligt koden,
        inte vilken väg ett visst anrop tog.

        Utan filer att skriva ritas kartan som en webbsida som öppnas i webbläsaren: ett nätverk där du väljer ett
        anrop och ser vägen, fäller ut delar och laddar ned kartan som Mermaid. Kartan skrivs också som text.

        Flaggor:
          --solution <fil>    Solutionen (.sln eller .slnx) eller ett projekt (.csproj). Utan flaggan används den
                              solution som ligger i den här mappen eller närmast ovanför (till och med repots rot),
                              annars närmaste projekt.
          --spec <fil|url>    (valfritt) OpenAPI-spec. Kopplar anropen till specens operationer och listar anrop
                              som bara finns i koden eller bara i specen.
          --html <fil>        Skriv webbsidan hit, t.ex. till dokumentationen. Sidan hämtar inget utifrån.
          --mermaid <fil>     Skriv kartan som Mermaid-flödesschema (GitHub och Azure DevOps ritar det).
          --level <nivå>      (med --mermaid) projects, classes (standard) eller all.
          --direction <LR|TB> (med --mermaid) LR vänster till höger (standard), TB uppifrån och ned.
          --entry "<anrop>"   (med --mermaid) Bara vägen för ett anrop, t.ex. --entry "POST /orders".
          --json <fil>        Skriv kartan som JSON.
          --no-browser        Öppna inte webbläsaren (görs aldrig när miljövariabeln CI är satt).
          --no-restore        Kör inte dotnet restore (annars körs den om paketen inte redan är hämtade).
          --verbose           Visa tekniska detaljer när något går fel.

        Projektfilerna läses med MSBuild, precis som vid dotnet build. Peka bara på kod du litar på.

        Exempel:
          dotnet systemmap
          dotnet systemmap --html docs/systemkarta.html
          dotnet systemmap --mermaid docs/order.mmd --direction TB --entry "POST /orders"

        Exitkoder: 0 klart, 2 fel (t.ex. solutionen eller specen finns inte eller går inte att läsa).
        """;
}
