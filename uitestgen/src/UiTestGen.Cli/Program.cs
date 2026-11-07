using TestGen.Core;
using TestGen.Core.Run;
using UiTestGen.Discovery;
using UiTestGen.Generation;
using UiTestGen.Model;
using UiTestGen.Probe;

namespace UiTestGen.Cli;

public static class Program
{
    internal const string See = "uitestgen";
    internal const string CatalogFile = "vykatalog.json";

    public static int Main(string[] args) =>
        Command.Run(args, () => Run(args), e => FriendlyError.From(e, See));

    private static int Run(string[] args)
    {
        // Första ordet utan streck är kommandot. Utan kommando görs båda stegen i ett.
        var command = args.Length > 0 && !args[0].StartsWith('-') ? args[0] : null;
        var rest = command is null ? args : args[1..];
        switch (command)
        {
            case null when rest.Length == 0:
                Console.WriteLine(Help);
                return StandardExitCodes.Ok;
            case null: return Both(rest);
            case "discover": return Discover(rest);
            case "probe": return Probe(rest);
            case "generate": return Generate(rest);
            default:
                throw new UsageException($"Okänt kommando: {command}.{Suggest.DidYouMean(command, ["discover", "probe", "generate"])}",
                    $"Kommandona är discover, probe och generate. Se: {See} --help");
        }
    }

    // --- discover ---

    private static int Discover(string[] args)
    {
        var a = Args.Parse($"{See} discover", "discover", args, DiscoverFlags);
        if (a.Help) { Console.WriteLine(DiscoverHelp); return StandardExitCodes.Ok; }
        SaveCatalog(DiscoverAndProbe(a), Path.GetFullPath(a.Get("out") ?? CatalogFile));
        return StandardExitCodes.Ok;
    }

    private static readonly string[] DiscoverFlags = ["solution", "spa", "out", "app", "max-pages", "browser-path"];

    /// <summary>Statisk upptäckt, och därefter sondering om --app anges.</summary>
    private static ViewCatalog DiscoverAndProbe(Args a)
    {
        var catalog = DiscoverViews(a.Get("solution"), a.GetAll("spa"));
        return a.Get("app") is { } app ? ProbeViews(catalog, app, a) : catalog;
    }

    private static ViewCatalog DiscoverViews(string? solution, IReadOnlyList<string> spaDirectories)
    {
        if (solution is null && spaDirectories.Count == 0)
            throw new UsageException("--solution saknas.", $"Ange solutionen, projektet eller mappen med --solution, eller en SPA-mapp med --spa. Se: {See} --help");
        var spas = spaDirectories.Select(d => Path.GetFullPath(d)).ToList();
        foreach (var dir in spas)
            if (!Directory.Exists(dir)) throw UsageException.MissingFile("SPA-mappen", dir);
        string root;
        IReadOnlyList<string> directories;
        if (solution is null) (root, directories) = (spas[0], spas);
        else
        {
            (root, directories) = ProjectDirectories.From(solution);
            directories = directories.Concat(spas).Distinct(StringComparer.Ordinal).ToList();
        }
        return ViewDiscovery.Run(new DiscoveryContext(root, directories, []), Discoverers.All());
    }

    private static ViewCatalog ProbeViews(ViewCatalog catalog, string app, Args a)
    {
        var defaults = new ProbeOptions();
        var maxPages = defaults.MaxPages;
        if (a.Get("max-pages") is { } s && (!int.TryParse(s, out maxPages) || maxPages < 1))
            throw new UsageException($"--max-pages ska vara ett heltal större än 0, inte '{s}'.", "Skriv t.ex. --max-pages 50.");
        var options = defaults with { MaxPages = maxPages, BrowserPath = a.Get("browser-path") };
        Console.WriteLine($"Sonderar vyerna i {app} (högst {maxPages} sidor).");
        return ViewProber.ProbeAsync(catalog, app, options).GetAwaiter().GetResult();
    }

    // --- probe ---

    private static int Probe(string[] args)
    {
        var a = Args.Parse($"{See} probe", "probe", args, ["catalog", "app", "out", "max-pages", "browser-path"]);
        if (a.Help) { Console.WriteLine(ProbeHelp); return StandardExitCodes.Ok; }
        var catalogFile = a.Require("catalog");
        var catalog = ProbeViews(ViewCatalogJson.Read(catalogFile), a.Require("app"), a);
        SaveCatalog(catalog, Path.GetFullPath(a.Get("out") ?? catalogFile));
        return StandardExitCodes.Ok;
    }

    /// <summary>Skriver varningarna från sökningen (stderr) och vykatalogen till filen, och sammanfattar vyerna.</summary>
    private static void SaveCatalog(ViewCatalog catalog, string catalogPath)
    {
        foreach (var warning in catalog.Warnings) Console.Error.WriteLine("Varning: " + warning);
        ViewCatalogJson.Write(catalog, catalogPath);
        Console.WriteLine($"Hittade {catalog.Views.Count} vyer. Vykatalogen skrevs till {catalogPath}.");
        foreach (var v in catalog.Views)
            Console.WriteLine($"  {v.Route}  {v.Kind}  formulär: {v.Forms.Count}  länkar: {v.Links.Count}");
    }

    // --- generate ---

    private static int Generate(string[] args)
    {
        var a = Args.Parse($"{See} generate", "generate", args, ["catalog", "out", .. GenerateFlags]);
        if (a.Help) { Console.WriteLine(GenerateHelp); return StandardExitCodes.Ok; }
        var catalog = ViewCatalogJson.Read(a.Require("catalog"));
        GenerateProject(catalog, a, Path.GetFullPath(a.Require("out")));
        return StandardExitCodes.Ok;
    }

    private static readonly string[] GenerateFlags = ["name", "backend", "spec", "seed"];

    private static void GenerateProject(ViewCatalog catalog, Args a, string outDir)
    {
        var name = a.Get("name") ?? Path.GetFileName(outDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (name.Length == 0) throw new UsageException("Det går inte att avgöra projektnamnet.", "Ange det med --name, t.ex. --name Shop.UiTests.");
        var spec = a.Get("spec");
        var backend = a.Get("backend") switch
        {
            null => spec is null ? Backend.Real : Backend.Mock,
            "mock" => Backend.Mock,
            "real" => Backend.Real,
            var other => throw new UsageException($"Okänd backend: {other}.", "Använd --backend mock eller --backend real."),
        };
        var seed = 12345;
        if (a.Get("seed") is { } s && !int.TryParse(s, out seed))
            throw new UsageException($"--seed ska vara ett heltal, inte '{s}'.", "Skriv t.ex. --seed 42.");
        if (spec is not null && !Sources.IsUrl(spec) && !File.Exists(spec)) throw UsageException.MissingFile("specen", Path.GetFullPath(spec));

        // Specens sökväg skrivs relativt utdatamappen, eftersom det är därifrån testerna körs.
        var specPath = spec is null || Sources.IsUrl(spec) ? spec : Path.GetRelativePath(outDir, Path.GetFullPath(spec)).Replace('\\', '/');
        var apiSpec = spec is null ? null : LoadSpec(spec);
        if (spec is null && backend == Backend.Mock)
            Console.Error.WriteLine("Varning: --backend mock utan --spec. Det finns inga mockade svar, så ConfigureMocksAsync blir tom. Ange --spec <fil> för att generera mockar.");
        var files = new PlaywrightGenerator().Generate(catalog, new GenerateOptions(name, backend, specPath, seed) { Spec = apiSpec });
        var written = GeneratedFiles.Write(files, outDir);
        Console.WriteLine($"Skrev {written} av {files.Count} filer till {outDir}.");
        foreach (var line in CatalogInsights.OrphanSummary(catalog)) Console.WriteLine(line);
    }

    private static ApiSpec LoadSpec(string spec)
    {
        try { return OpenApiLoader.LoadSpec(spec); }
        catch (SourceException e) { throw new UsageException(e.Message, e.Tip, e.Technical); }
        catch (MissingSourceException e) { throw new UsageException(e.Message, UsageException.MissingFileTip); }
    }

    // --- båda stegen ---

    private static int Both(string[] args)
    {
        var a = Args.Parse(See, "uitestgen", args, [.. DiscoverFlags, .. GenerateFlags]);
        if (a.Help) { Console.WriteLine(Help); return StandardExitCodes.Ok; }
        var outDir = Path.GetFullPath(a.Require("out"));
        var catalog = DiscoverAndProbe(a);
        SaveCatalog(catalog, Path.Combine(outDir, CatalogFile));
        GenerateProject(catalog, a, outDir);
        return StandardExitCodes.Ok;
    }

    // --- hjälptexter ---

    internal const string Help = """
        uitestgen - Playwright-tester för vyerna i en webbapp

        Användning:
          uitestgen discover --solution <.slnx|.sln|.csproj|mapp> [--spa <mapp>]... [--app <url>] [--out vykatalog.json]
          uitestgen probe    --catalog vykatalog.json --app <url> [--out vykatalog.json]
          uitestgen generate --catalog vykatalog.json --out <mapp> [--name Shop.UiTests] [--backend mock|real]
                             [--spec <fil|url>] [--seed N]
          uitestgen --solution <.slnx|.sln|.csproj|mapp> [--spa <mapp>]... [--app <url>] --out <mapp> [flaggor för generate]

        Läser källkoden och hittar vyerna (Blazor, Razor Pages, MVC, React, Angular och Vue) med rutter, formulär, fält och
        länkar, också länkarna i delade layouter (menyer). Med --app öppnas varje vy dessutom i den körande appen, och den
        riktiga DOM:en fyller i det som källkoden inte visar (etiketter, fält i komponenter, länkar). Skriver sedan ett
        fristående NUnit-testprojekt med Playwright: en Page Object per vy och tester för smoke, formulär, navigering och
        skärmdumpar. Med --spec mockas backend ur OpenAPI-specen. Vyer som ingen annan vy länkar till listas efter generate.

        Kommandon:
          discover   Hittar vyerna och skriver vykatalogen (JSON). Visar rutt, sort, formulär och länkar per vy.
          probe      Sonderar en befintlig vykatalog mot den körande appen.
          generate   Skriver testprojektet ur en vykatalog.
          (inget)    Gör båda stegen i ett och skriver även vykatalog.json i utmappen.

        Flaggor:
          --solution <fil>    Solutionen (.slnx eller .sln), ett projekt (.csproj) eller en mapp att leta i.
          --spa <mapp>        Mapp med en SPA (React, Angular, Vue). Kan anges flera gånger. Utan --solution blir den första roten.
          --app <url>         Adressen till den körande appen, t.ex. http://localhost:5000. Slår på sonderingen.
          --max-pages <N>     Sonderingen öppnar högst N sidor (standard 50).
          --browser-path <fil>
                              Webbläsaren (Chromium) som sonderingen använder. Annars miljövariabeln UITEST_BROWSER_PATH.
          --catalog <fil>     Vykatalogen från discover.
          --out <sökväg>      discover och probe: filen att skriva (standard vykatalog.json, för probe katalogen själv).
                              generate och utan kommando: mappen för testprojektet.
          --name <namn>       Testprojektets namn (standard: utmappens namn), t.ex. Shop.UiTests.
          --backend <mock|real>
                              Backend som standard när testerna körs. Standard: mock om --spec anges, annars real.
          --spec <fil|url>    OpenAPI-spec för mockade svar (kopieras inte; en fil sparas som sökväg relativt utmappen). Ger
                              Support/ApiMocks.g.cs (svaren), mockning av webbläsarens anrop (RouteAsync) och en stub-server
                              för serverrenderade appar. Utan --spec och med --backend mock skrivs en varning och inga mockar.
          --seed <N>          Slumpfrö för testdata (standard 12345).
          --verbose           Visa tekniska detaljer när något går fel.

        Miljövariabler:
          UITEST_BROWSER_PATH Webbläsaren för sonderingen (samma som --browser-path).
          För de genererade testerna (när de körs, inte när uitestgen körs):
          UITEST_BASE_URL     Appens adress (standard http://localhost:5090).
          UITEST_BACKEND      mock eller real (standard från --backend).
          UITEST_STUB         on startar stub-servern på port 5099; off stänger av den.
          UITEST_STUB_PORT    Stub-serverns port.
          UITEST_UPDATE_SCREENSHOTS=1, UITEST_SCREENSHOT_THRESHOLD, UITEST_SCREENSHOT_TOLERANCE, UITEST_SCREENSHOT_DIR
                              Referensbilderna för de visuella testerna. Se README.md i testprojektet.

        Uitestgen läser filerna som text och kör aldrig appens kod. Sonderingen (--app) öppnar appen i en webbläsare:
        peka bara på en app du litar på. Genererade filer som slutar på .g.cs skrivs om varje gång; filer utan .g är dina och
        skrivs aldrig över.

        Exempel:
          uitestgen discover --solution Shop.slnx
          uitestgen generate --catalog vykatalog.json --out tests/Shop.UiTests
          uitestgen --solution Shop.slnx --out tests/Shop.UiTests --name Shop.UiTests
          uitestgen --solution Shop.slnx --app http://localhost:5000 --out tests/Shop.UiTests
          uitestgen --solution Shop.slnx --spec shop.openapi.yaml --out tests/Shop.UiTests
          uitestgen --spa frontend --app http://localhost:4200 --out tests/Web.UiTests

        Exitkoder: 0 klart, 2 fel (t.ex. en flagga eller fil som inte stämmer). Varningar från sökningen skrivs till stderr.
        """;

    internal const string DiscoverHelp = """
        uitestgen discover - hitta vyerna i en webbapp

        Användning:
          uitestgen discover --solution <.slnx|.sln|.csproj|mapp> [--spa <mapp>]... [--app <url>]
                             [--max-pages N] [--browser-path <fil>] [--out vykatalog.json]

        Läser källkoden (Blazor, Razor Pages, MVC, React, Angular och Vue), skriver vykatalogen som JSON och visar en rad per
        vy: rutt, sort, antal formulär och länkar. Länkarna i delade layouter (menyer) räknas med på varje vy som använder layouten.
        Med --app sonderas därefter vyerna i den körande appen (se uitestgen probe --help).
        Varningar (t.ex. en rutt som inte gick att läsa) skrivs till stderr.

        Flaggor:
          --solution <fil>   Solutionen, projektet eller mappen att leta i.
          --spa <mapp>       Mapp med en SPA. Kan anges flera gånger. Utan --solution blir den första mappen roten för
                             sökvägarna i katalogen; med --solution är roten solutionens mapp (och SPA-källor utanför får ../).
          --app <url>        Den körande appens adress. Slår på sonderingen.
          --max-pages <N>    Högst N sidor sonderas (standard 50).
          --browser-path <fil>
                             Webbläsaren (Chromium) för sonderingen. Annars miljövariabeln UITEST_BROWSER_PATH.
          --out <fil>        Filen att skriva (standard vykatalog.json).
          --verbose          Visa tekniska detaljer när något går fel.

        Källkoden läses som text och körs aldrig. Sonderingen öppnar appen i en webbläsare: peka bara på en app du litar på.

        Exitkoder: 0 klart, 2 fel.
        """;

    internal const string ProbeHelp = """
        uitestgen probe - sondera vyerna i den körande appen

        Användning:
          uitestgen probe --catalog vykatalog.json --app <url> [--out vykatalog.json] [--max-pages N] [--browser-path <fil>]

        Öppnar varje vy i Chromium (Playwright) och läser den riktiga DOM:en. Fyller i titel, rubrik, etiketter, HTML-regler
        (required, minlength, maxlength, min, max, pattern) och länkar, byter CSS-locators mot etikett eller data-testid när
        det går, och lägger till formulär som bara syns i appen. Regler med felmeddelande från koden ändras aldrig.
        Länkar inom appen till okända sidor följs (högst 3 steg och N sidor) och blir nya vyer. Utloggning, filer och länkar
        med query följs inte. En sida som svarar med fel, tar för lång tid eller omdirigeras ger en varning, och vyn behålls.

        Flaggor:
          --catalog <fil>    Vykatalogen att sondera.
          --app <url>        Den körande appens adress, t.ex. http://localhost:5000.
          --out <fil>        Filen att skriva (standard: katalogen själv skrivs över).
          --max-pages <N>    Högst N sidor öppnas (standard 50).
          --browser-path <fil>
                             Webbläsaren (Chromium) att använda. Annars miljövariabeln UITEST_BROWSER_PATH. Finns ingen webbläsare:
                             installera med pwsh playwright.ps1 install chromium (filen ligger i verktygets mapp, bredvid UiTestGen.Cli.dll).
          --verbose          Visa tekniska detaljer när något går fel.

        Sonderingen öppnar appen i en webbläsare: peka bara på en app du litar på.

        Exitkoder: 0 klart, 2 fel.
        """;

    internal const string GenerateHelp = """
        uitestgen generate - skriv testprojektet ur en vykatalog

        Användning:
          uitestgen generate --catalog vykatalog.json --out <mapp> [--name Shop.UiTests] [--backend mock|real]
                             [--spec <fil|url>] [--seed N]

        Skriver ett fristående NUnit-testprojekt med Playwright: en Page Object per vy och tester för smoke, formulär,
        navigering och skärmdumpar. Filer som slutar på .g.cs och projektfilen skrivs om; handskrivna filer (utan .g) och
        README.md skrivs bara om de inte redan finns. Därefter visas vyer som ingen annan vy länkar till.

        Flaggor:
          --catalog <fil>    Vykatalogen från discover.
          --out <mapp>       Mappen för testprojektet.
          --name <namn>      Projektets namn (standard: utmappens namn).
          --backend <mock|real>
                             Standard för UITEST_BACKEND när testerna körs. Standard: mock om --spec anges, annars real.
          --spec <fil|url>   OpenAPI-spec för mockade svar (kopieras inte; en fil sparas som sökväg relativt utmappen).
                             Utan den (och med --backend mock) skrivs en varning och inga mockar.
          --seed <N>         Slumpfrö för testdata (standard 12345).
          --verbose          Visa tekniska detaljer när något går fel.

        Miljövariabler när testerna körs: UITEST_BASE_URL, UITEST_BACKEND, UITEST_STUB, UITEST_STUB_PORT och
        UITEST_UPDATE_SCREENSHOTS (med flera). De står i testprojektets README.md.

        Exitkoder: 0 klart, 2 fel.
        """;
}
