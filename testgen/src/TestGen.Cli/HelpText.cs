using TestGen.Core.Run;

namespace TestGen.Cli;

/// <summary>Hjälptexter: en översikt och en per kommando, byggda med <see cref="HelpPage"/>.</summary>
public static class HelpText
{
    public static readonly string[] Commands = ["init", "run", "template", "web", "map", "mock", "views"];

    private static readonly Dictionary<int, string> CodeTexts = new()
    {
        [ExitCodes.Ok] = "Klart",
        [ExitCodes.Failed] = "Tester fallerade (orsakerna i --fail-on)",
        [ExitCodes.Usage] = "Fel i användning eller konfiguration",
        [ExitCodes.Unreachable] = "Målet gick inte att nå (API, specens adress eller --max-time)",
        [ExitCodes.Inconclusive] = "Osäkert resultat: inga säkra fel, men något gick inte att avgöra",
        [ExitCodes.Internal] = "Internt fel",
        [ExitCodes.Interrupted] = "Avbrutet med Ctrl+C",
        [ExitCodes.Terminated] = "Avbrutet med SIGTERM",
    };

    private static HelpItem[] Codes(params int[] codes) => codes.Select(c => new HelpItem(c.ToString(), CodeTexts[c])).ToArray();

    public static readonly string General = new HelpPage
    {
        Usage = ["testgen <kommando> [flaggor]"],
        Description = "Testar ett API utifrån dess OpenAPI-spec. Samma indata ger samma resultat.\ntestgen utan argument visar vad som finns i repot och nästa steg.\nHjälp för ett kommando: testgen help <kommando>",
        Commands =
        [
            new("init", "Skapa testprojektet så att dotnet test fungerar"),
            new("run", "Kör tester mot ett API"),
            new("template", "Skapa en Excel-mall för egna tester"),
            new("web", "Köra och läsa resultat i webbläsaren"),
            new("map", "Systemkarta: vägen genom källkoden ut till integrationerna"),
            new("mock", "Mock av ett API ur dess OpenAPI-spec, som kontrollerar anropen"),
            new("views", "Skriv webbappernas vyer och formulär till views.json"),
        ],
        Examples =
        [
            "testgen init",
            "testgen run",
            "testgen template --spec samples/shop.openapi.yaml --out tester.xlsx",
            "testgen web",
        ],
        ExitCodes = Codes(ExitCodes.Ok, ExitCodes.Failed, ExitCodes.Usage, ExitCodes.Unreachable, ExitCodes.Inconclusive, ExitCodes.Internal, ExitCodes.Interrupted, ExitCodes.Terminated),
    }.ToString();

    public static readonly string Run = new HelpPage
    {
        Usage = ["testgen run [--api <projekt|url>] [--spec <fil|url>] [--env <namn>] [flaggor]"],
        Description = """
            Kör genererade tester (giltiga och ogiltiga fall) och egna tester från Excel. Resultatet skrivs som OK, FEL eller HOPPAD per test.
            Utan flaggor testas det enda API:et i solutionen: specen läses ur projektet och adressen ur launchSettings.json (miljön local).
            Miljöer, värdpolicy och fältregler står i testgen.yaml (se samples/testgen.yaml). Värden får innehålla ${env:NAMN} (miljövariabeln NAMN), så att hemligheter inte står i filer eller skalhistorik. De visas som *** i utskrifter.
            """,
        Flags =
        [
            new("--api <projekt|url>", "API-projektets namn i solutionen, eller en adress. Utan flaggan: solutionens enda API."),
            new("--spec <fil|url>", "OpenAPI-spec. Utan flaggan: apis.<projekt>.spec i testgen.yaml, annars specen vid projektet."),
            new("--env <namn>", "Miljö i testgen.yaml (standard local). Flaggor på kommandoraden vinner."),
            new("--excel <fil>", "Egna tester (skapa en mall med testgen template)."),
            new("--solution <fil>", "Solutionen (standard: den som hittas från aktuell mapp)."),
            new("--token <värde>", "Inloggning: Authorization: Bearer, eller den header specen anger."),
            new("--header \"N: v\"", "Extra header på alla anrop. Kan anges flera gånger."),
            new("--seed <tal|random|ci>", "Slumpfrö för testdata (standard 12345). random väljer ett och skriver ut det. ci tar det från bygget."),
            new("--timeout <s>", $"Sekunder per anrop (standard {RunSettings.DefaultTimeoutSeconds})."),
            new("--max-time <s>", "Högsta tid för hela körningen. En partiell rapport skrivs och exitkoden blir 3."),
            new("--fail-on <lista>", "Orsaker som ger exitkod 1 (standard system,expectation): system, expectation, auth, environment, test-data, missing-data, test-user-rejected, rate-limited, tool, unsupported, dependency, read-only."),
            new("--baseline <fil>", "JSON-resultat från en tidigare körning att jämföra med."),
            new("--fail-on-new", "Med --baseline: exitkod 1 bara för nya fel."),
            new("--allow-empty", "Tillåt en körning utan tester."),
            new("--only <operationId>", "Kör bara ett anrop."),
            new("--test <T-018>", "Kör bara det testet, med samma id och testdata som i en full körning. Kan anges flera gånger."),
            new("--scenario <namn>", "Kör bara det scenariot i --excel. Kan anges flera gånger."),
            new("--excel-only", "Hoppa över de genererade testerna."),
            new("--config <fil>", "En annan testgen.yaml än den som hittas vid solutionen."),
            new("--explain", "Förklara varje test i klartext."),
            new("--report <fil>", "Skriv rapport: .html, .xlsx, .json eller .xml (JUnit; även .junit.xml). Kan anges flera gånger."),
            new("--force", "Skriv över en befintlig fil som inte är en testgen-rapport."),
            new("--verbose", "Visa anrop och svar för alla tester, och tekniska detaljer vid fel."),
        ],
        Environment =
        [
            new("TESTGEN_ENV", "Miljö när --env saknas (standard local)."),
            new("GITHUB_RUN_ID m.fl.", "--seed ci läser GITHUB_RUN_ID, BUILD_BUILDID, CI_PIPELINE_ID eller BUILD_NUMBER."),
            new("NO_COLOR", "Satt: ingen färg."),
        ],
        Examples =
        [
            "testgen run",
            "testgen run --api ShopApi --env test",
            "testgen run --spec <spec> --api <url> --token '${env:API_TOKEN}' --report resultat.html --report resultat.json",
            "testgen run --spec <spec> --api <url> --baseline forra.json --fail-on-new --seed ci",
        ],
        ExitCodes = Codes(ExitCodes.Ok, ExitCodes.Failed, ExitCodes.Usage, ExitCodes.Unreachable, ExitCodes.Inconclusive, ExitCodes.Internal, ExitCodes.Interrupted, ExitCodes.Terminated),
    }.ToString();

    public static readonly string Template = new HelpPage
    {
        Usage = ["testgen template --spec <fil|url> --out tester.xlsx [--force]"],
        Description = "Skapar en Excel-mall med bladen Tester, Hjälp och Fält. Fyll i Tester och kör den med testgen run --excel.",
        Flags =
        [
            new("--spec <fil|url>", "OpenAPI-spec. Krävs."),
            new("--out <fil>", "Excel-filen som skapas. Krävs."),
            new("--force", "Skriv över filen om den finns."),
        ],
        Examples = ["testgen template --spec samples/shop.openapi.yaml --out tester.xlsx"],
        ExitCodes = Codes(ExitCodes.Ok, ExitCodes.Usage, ExitCodes.Unreachable, ExitCodes.Internal, ExitCodes.Interrupted),
    }.ToString();

    public static readonly string Web = new HelpPage
    {
        Usage = ["testgen web [--port 5100] [--spec <fil|url>] [--excel <fil>] [--solution <fil>] [--config <fil>]"],
        Description = "Startar en lokal webbsida (bara nåbar från den här datorn) där du kör testerna och läser resultatet i klartext, från både testgen run och dotnet test, och jämför körningar. Avsluta med Ctrl+C.",
        Flags =
        [
            new("--port <tal>", "Port (standard 5100)."),
            new("--spec <fil|url>", "Fyller i API-beskrivningen i förväg."),
            new("--excel <fil>", "Fyller i Excel-filen i förväg."),
            new("--solution <fil>", "Solutionen som kartan, API-projekten och resultaten från dotnet test hämtas ur. Utan flaggan letas den upp från aktuell mapp."),
            new("--config <fil>", "En annan testgen.yaml än den som hittas vid solutionen."),
        ],
        Examples = ["testgen web --spec samples/shop.openapi.yaml"],
        ExitCodes = Codes(ExitCodes.Ok, ExitCodes.Usage, ExitCodes.Internal, ExitCodes.Interrupted),
    }.ToString();

    public static readonly string Map = new HelpPage
    {
        Usage = ["testgen map --solution <.sln|.slnx|.csproj> [--spec <fil|url>] [--out karta.json] [--mermaid karta.mmd] [--html karta.html]"],
        Description = """
            Följer anropen från varje controller-action genom projekten ut till integrationerna. Kartan skrivs som text på stdout.
            Den visar vägar som är möjliga enligt koden, inte vilken väg ett visst anrop tog.
            Projektfilerna läses med MSBuild, precis som vid dotnet build: peka bara på kod du litar på.
            """,
        Flags =
        [
            new("--solution <fil>", "Solution eller projekt. Krävs."),
            new("--spec <fil|url>", "OpenAPI-spec. Kopplar anropen till specens operationer och visar skillnader."),
            new("--out <fil>", "Skriv kartan som JSON."),
            new("--mermaid <fil>", "Skriv kartan som Mermaid-flödesschema."),
            new("--level <nivå>", "Med --mermaid: projects, classes (standard) eller all."),
            new("--direction <LR|TB>", "Med --mermaid: LR (standard) eller TB."),
            new("--entry \"<anrop>\"", "Med --mermaid: bara vägen för ett anrop, t.ex. \"POST /orders\"."),
            new("--html <fil>", "Skriv kartan som webbsida. Sidan hämtar inget utifrån."),
            new("--no-restore", "Kör inte dotnet restore."),
        ],
        Examples =
        [
            "testgen map --solution samples/Shop.slnx --spec samples/shop.openapi.yaml",
            "testgen map --solution samples/Shop.slnx --mermaid docs/order.mmd --entry \"POST /orders\"",
        ],
        ExitCodes = Codes(ExitCodes.Ok, ExitCodes.Usage, ExitCodes.Unreachable, ExitCodes.Internal, ExitCodes.Interrupted),
    }.ToString();

    public static readonly string Mock = new HelpPage
    {
        Usage = ["testgen mock [--spec <fil|url>]... [--port 5080] [--seed 12345]"],
        Description = """
            Startar en mock per spec (bara nåbar från den här datorn). Utan --spec startas en per API i solutionen, med projektets namn. Mocken svarar enligt specen och kontrollerar varje anrop mot den:
            ett anrop som inte stämmer får 400 med en förklaring, en okänd väg 404. Varje anrop skrivs på stdout. Avsluta med Ctrl+C.
            """,
        Flags =
        [
            new("--spec <fil|url>", "OpenAPI-spec. Kan anges flera gånger."),
            new("--port <tal>", "Fast port (standard: systemet väljer). Bara med en spec."),
            new("--seed <tal>", "Slumpfrö för svaren (standard 12345)."),
        ],
        Examples = ["testgen mock --spec samples/shop.openapi.yaml --port 5080"],
        ExitCodes = Codes(ExitCodes.Ok, ExitCodes.Usage, ExitCodes.Unreachable, ExitCodes.Internal),
    }.ToString();

    public static readonly string Views = new HelpPage
    {
        Usage = ["testgen views [--solution <fil>] [--out <fil>] [--probe]"],
        Description = """
            Läser webbappernas vyer, formulär och formulärregler ur källkoden och skriver dem, tillsammans med vilket API appen anropar
            och via vilken nyckel, till views.json. Filen checkas in; UI-testerna läser bara den.
            Med --probe startas varje app (med API:erna mockade ur sina specar) och sidorna öppnas i Chromium, så att det som bara syns
            när appen körs läggs till.
            Projektfilerna läses med MSBuild, precis som vid dotnet build, och --probe kör appen: peka bara på kod du litar på.
            """,
        Flags =
        [
            new("--solution <fil>", "Solution eller projekt (standard: den närmaste uppåt från aktuell katalog)."),
            new("--out <fil>", "Var filen skrivs (standard: views.json i testprojektet för testgen, annars i aktuell katalog)."),
            new("--probe", "Starta apparna och sondera sidorna i Chromium (kräver Chromium, se testgen init)."),
            new("--max-pages <tal>", "Med --probe: högsta antal sidor per app (standard 50)."),
            new("--no-restore", "Kör inte dotnet restore."),
        ],
        Examples = ["testgen views --solution samples/Shop.slnx", "testgen views --probe"],
        ExitCodes = Codes(ExitCodes.Ok, ExitCodes.Usage, ExitCodes.Internal, ExitCodes.Interrupted),
    }.ToString();

    public static readonly string Init = new HelpPage
    {
        Usage = ["testgen init [--solution <fil>]"],
        Description = """
            Gör det som behövs för att dotnet test ska fungera: skapar tests/<Solution>.Tests med en rad per API och webbapp, lägger
            projektet i solutionen, skriver views.json och installerar Chromium om det saknas. Det som redan finns lämnas orört, så
            kommandot går att köra igen när en ny app tillkommit. Sist visas ett utdrag för CI.
            Projektfilerna läses med MSBuild, precis som vid dotnet build: peka bara på kod du litar på.
            """,
        Flags =
        [
            new("--solution <fil>", "Solution eller projekt (standard: den närmaste uppåt från aktuell katalog)."),
            new("--no-restore", "Kör inte dotnet restore när vyerna läses."),
        ],
        Examples = ["testgen init"],
        ExitCodes = Codes(ExitCodes.Ok, ExitCodes.Usage, ExitCodes.Internal, ExitCodes.Interrupted),
    }.ToString();

    public static string? For(string command) => command switch
    {
        "run" => Run,
        "template" => Template,
        "web" => Web,
        "map" => Map,
        "mock" => Mock,
        "views" => Views,
        "init" => Init,
        _ => null,
    };
}
