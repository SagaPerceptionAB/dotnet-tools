namespace TestGen.Cli;

/// <summary>Hjälptexter: en översikt och en per kommando.</summary>
public static class HelpText
{
    public static readonly string[] Commands = { "run", "template", "ui", "map", "generate" };

    public const string ExitCodesText = """
        Exitkoder:
          0  alla tester OK
          1  minst ett FEL i systemet eller där förväntan i testet inte stämde (kan ändras med --fail-on),
             även om det samtidigt finns osäkra svar
          2  fel i användning eller konfiguration (flaggor, filer, spec, Excel, miljö, rapportens plats, tom hemlighet,
             inga tester att köra)
          3  API:et eller specens webbadress gick inte att nå eller svarade inte (även: inget svar på 3 anrop i rad inom
             --timeout, och när --max-time löpte ut)
          4  inget säkert svar: inga FEL i systemet, men FEL eller HOPPAD av osäker orsak (inloggning 401/403,
             testdata 409, miljön svarar inte / 502 / 503 / 504, följdfel)
        130  avbruten med Ctrl+C (SIGINT)
        143  avbruten med SIGTERM (t.ex. CI som avbryter jobbet)
          Vid 130, 143 och 3 med --max-time skrivs en partiell rapport med de tester som hann bli klara.
        """;

    public const string General = """
        testgen - testa ett API utifrån dess OpenAPI-spec (ingen AI vid körning, samma indata ger samma resultat)

        Användning:  testgen <kommando> [flaggor]

        Kommandon:
          run        Kör tester mot ett API (genererade giltiga + ogiltiga fall, plus egna fall från Excel)
          template   Skapa en Excel-mall för egna tester
          ui         Köra och läsa resultat i webbläsaren (utan terminal)
          map        Systemkarta: vägen genom källkoden ut till integrationerna (alias: karta)
          generate   Avancerat: skriv scenariofiler till disk för NUnit-spåret

        Hjälp för ett kommando:  testgen run --help   (samma för template, ui, map och generate)

        Är testgen inte installerat kör du det så här från projektets rot:
          dotnet run --project testgen/src/TestGen.Cli -- <kommando> [flaggor]
          t.ex.  dotnet run --project testgen/src/TestGen.Cli -- run --spec samples/shop.openapi.yaml --api http://localhost:5080

        Exempel:
          testgen run --spec samples/shop.openapi.yaml --api http://localhost:5080
          testgen template --spec samples/shop.openapi.yaml --out tester.xlsx
          testgen run --spec samples/shop.openapi.yaml --api http://localhost:5080 --excel tester.xlsx
          testgen ui
          testgen map --solution samples/Shop.slnx --spec samples/shop.openapi.yaml

        Resultatet skrivs som OK, FEL eller HOPPAD per test. Meddelandet "Kunde inte köra: ..." betyder att
        verktyget inte kunde köra alls (det är inte ett testresultat).

        """ + ExitCodesText;

    public static readonly string Run = """
        testgen run - kör tester mot ett API

        Användning:
          testgen run --spec <fil|url> --api <bas-url> [flaggor]

        Flaggor:
          --spec <fil|url>    OpenAPI-spec (fil eller webbadress). Krävs.
          --api <bas-url>     Adressen till API:et som testas, t.ex. http://localhost:5080. Behövs inte om --env anger den.
          --excel <fil>       Egna tester och miljöer i en Excel-fil (skapa en mall med: testgen template)
          --env <miljö>       Miljö i bladet Miljöer i --excel (api, header, token, timeout). Flaggor på kommandoraden vinner.
          --token <värde>     Inloggning. Skickas som Authorization: Bearer <värde>, eller i den header
                              specens securityScheme (apiKey) anger.
          --header "N: v"     Extra header på alla anrop; får anges flera gånger.
          --seed <heltal|slumpa|ci>  Slumpfrö för testdata (default 12345). Samma frö ger samma data. Byt det om
                              uppgifterna redan finns i systemet (svaret 409). Stora tal går bra, t.ex.
                              --seed $GITHUB_RUN_ID (de görs om till ett frö som ryms, samma tal ger samma frö).
                              "slumpa" (eller random) låter verktyget välja ett frö och skriver ut det så att
                              körningen kan upprepas. "ci" tar fröet från bygget i CI-systemet (GITHUB_RUN_ID +
                              GITHUB_RUN_ATTEMPT, BUILD_BUILDID, CI_PIPELINE_ID eller BUILD_NUMBER) och beter sig
                              som "slumpa" om ingen sådan variabel finns. Fröet som användes skrivs ut.
          --timeout <s>       Sekunder per anrop (default __TIMEOUT__). Efter 3 tidsgränser i rad avbryts körningen
                              (exitkod 3) i stället för att vänta på alla tester.
          --max-time <s>      Högsta tid i sekunder för hela körningen. Därefter avbryts pågående anrop, en partiell
                              rapport skrivs med en notis och exitkoden blir 3.
          --fail-on <lista>   Orsaker som ger exitkod 1, med komma emellan. Koder: system, forvantan, inloggning,
                              miljo, testdata, verktyg, foljd. Standard: system,forvantan. Övriga FEL/HOPPAD ger exitkod 4.
                              Exempel: --fail-on system,forvantan,inloggning (inloggning som misslyckas ska stoppa bygget).
          --jamfor <fil>      JSON-rapport från en tidigare körning (alias: --baseline). Tester matchas på anrop, regel och
                              fält (Excel: scenario och steg), inte på T-id. Skriver "Nya FEL sedan förra körningen",
                              "Lagade sedan sist" och "Kvar sedan tidigare" i terminalen och rapporterna. Filen får vara
                              samma som en --report (den läses före körningen). Fel format eller annan schemaVersion: exitkod 2.
          --fail-on-new       Med --jamfor: exitkod 1 bara om det finns NYA FEL (av orsak i --fail-on); fel som redan
                              fanns förra gången stoppar inte. Annars 0 (eller 4). Alias: --bara-nya-fel.
          --allow-empty       Tillåt en körning utan några tester (tom spec, filter som inte matchar): exitkod 0.
                              Utan flaggan är det exitkod 2.
          --only <operationId>  Kör bara tester för ett anrop. Testerna behåller sina ursprungliga id och data.
          --test <T-018>      Kör bara det testet, med samma id och testdata som i en full körning (förberedande
                              anrop körs som vanligt). Får anges flera gånger. Kör-om-kommandot i rapporten använder den.
          --scenario <namn>   Kör bara det scenariot i --excel (kan anges flera gånger). De genererade testerna hoppas då över.
          --bara-excel        Hoppa över de genererade testerna och kör bara Excel-filens scenarier (alias: --excel-only).
                              Ett urval (--only, --scenario, --bara-excel, --test) är en delkörning och märks så i utskriften.
          --config <fil>      (valfritt) testgen.yaml med fältregler m.m. Behövs inte.
          --verbose           Visa anrop, svar och curl-rad för alla tester, inte bara FEL. Visar också tekniska
                              felmeddelanden om verktyget inte kan köra.
          --klartext          Skriv rubrik och förklaring i klartext för varje test i stället för den tekniska raden.
          --report <fil>      Skriv en rapport efter körningen. Filändelsen avgör typ: .html, .xlsx, .json (maskinläsbart
                              resultat, schema: docs/ci/testgen-result.schema.json) eller .xml / .junit.xml (JUnit för
                              CI-systemens testvyer). Kan anges flera gånger: --report r.html --report r.xlsx
                              --report r.json --report r.junit.xml.
                              Rapporten skrivs även om API:et inte nås (då står det att körningen inte kunde genomföras).
                              Katalogen skapas vid behov och kontrolleras innan körningen startar. Rapporten får inte
                              ligga på samma fil som --excel, --spec eller --config (exitkod 2).
          --force             Skriv över en befintlig fil som --report pekar på även om den inte är en testgen-rapport.

        Excel-filen kontrolleras mot specen innan något test körs. Alla fel visas samtidigt med cell (t.ex. Tester!B3 (Anrop))
        och inget körs förrän de är rättade (exitkod 2). Varningar (t.ex. okänd rubrik, token i klartext) stoppar inte.

        Värden får innehålla ${env:NAMN} = miljövariabeln NAMN, så slipper hemligheter stå i Excel eller i
        skalhistoriken (använd enkla citattecken i skalet). Känsliga värden visas som *** i utskrifter.
        Varje test har ett id (T-001, T-002 ...) som är detsamma varje gång och även vid urval. Vid FEL visas anropet, svaret
        och en curl-rad. Sist står en rad som alltid ser likadan ut:
          Sammanfattning: 34 tester, 33 OK, 1 FEL, 0 HOPPAD, tid 2,1 s
        Kör-om-kommandot i rapporten börjar med ./testgen.sh (Windows: testgen.cmd).

        Exempel:
          testgen run --spec samples/shop.openapi.yaml --api http://localhost:5080
          testgen run --spec samples/shop.openapi.yaml --excel tester.xlsx --env lokal
          testgen run --spec <spec> --api https://test.example.se --token '${env:API_TOKEN}' --report resultat.html
          testgen run --spec <spec> --api http://localhost:5080 --test T-018 --seed 12345
          testgen run --spec <spec> --api https://test.example.se --token '${env:API_TOKEN}' --seed ci \
              --report resultat.json --report resultat.junit.xml --max-time 600 --jamfor forra.json --fail-on-new

        (Utan installerat testgen: dotnet run --project testgen/src/TestGen.Cli -- run ...)

        __EXIT__
        """.Replace("__TIMEOUT__", TestGen.Core.Run.RunSettings.DefaultTimeoutSeconds.ToString()).Replace("__EXIT__", ExitCodesText);

    public const string Template = """
        testgen template - skapa en Excel-mall för egna tester

        Användning:
          testgen template --spec <fil|url> --out tester.xlsx [--force]

        Flaggor:
          --spec <fil|url>    OpenAPI-spec (fil eller webbadress). Krävs.
          --out <fil>         Excel-filen som skapas. Krävs.
          --force             Skriv över filen om den redan finns.

        Mallen har bladen Tester (exempelrader att utgå från), Hjälp (steg-för-steg-guide och ordlista), Miljöer
        (api, header, token och timeout per miljö) och Fält (fälten för varje anrop). Fyll i Tester i Excel och kör:
          testgen run --spec <spec> --api <bas-url> --excel tester.xlsx
        Rättar du något fel i filen visas alla fel samtidigt med cell, t.ex. Tester!B3 (Anrop), innan något körs.

        (Utan installerat testgen: dotnet run --project testgen/src/TestGen.Cli -- template ...)

        Exitkoder: 0 klart, 2 fel i användning eller konfiguration (t.ex. filen finns redan, specen går inte att läsa), 3 specens webbadress svarar inte.
        """;

    public const string Ui = """
        testgen ui - köra och läsa resultat i webbläsaren

        Användning:
          testgen ui [--port 5100] [--spec <fil|url>] [--excel <fil>] [--solution <fil>]

        Startar en lokal webbsida (bara nåbar från den här datorn) där man fyller i spec, Excel-fil, miljö och
        ev. token, kör testerna och läser resultatet i klartext. Avsluta med Ctrl+C.

        Flaggor:
          --port <tal>        Port (default 5100). Välj en annan om den är upptagen.
          --spec <fil|url>    Fyller i fältet API-beskrivning i förväg.
          --excel <fil>       Fyller i fältet Excel i förväg.
          --solution <fil>    Solutionen som systemkartan ritas för. Utan flaggan letas den upp från mappen du står i.
                              Alias: --losning.

        (Utan installerat testgen: dotnet run --project testgen/src/TestGen.Cli -- ui)

        Exitkoder: 0 avslutad med Ctrl+C, 2 fel i användning (t.ex. porten är upptagen).
        """;

    public const string Map = """
        testgen map - systemkarta: vägen genom källkoden ut till integrationerna

        Användning:
          testgen map --solution <.sln|.slnx|.csproj> [--spec <fil|url>] [--out karta.json] [--mermaid karta.mmd] [--html karta.html]
                [--no-restore]

        Läser källkoden till systemet som testas och följer anropen från varje controller-action genom projekten ut
        till integrationerna: databas (EF Core, Dapper, SQL), andra API:er (HTTP, Refit), WCF/SOAP, köer (Service Bus,
        MassTransit, RabbitMQ, Kafka), lagring (Blob, filer) och e-post (SMTP). Skriver ett träd per ingång.
        Kartan visar vägar som är möjliga enligt koden, inte vilken väg ett visst anrop tog. Alias: testgen karta.

        Flaggor:
          --solution <fil>    Solutionen (.sln eller .slnx) eller ett projekt (.csproj). Krävs. Alias: --losning.
          --spec <fil|url>    (valfritt) OpenAPI-spec. Kopplar ingångarna till specens operationer och listar anrop
                              som bara finns i koden eller bara i specen.
          --out <fil>         Skriv kartan som JSON (samma data som webbgränssnittet visar).
          --mermaid <fil>     Skriv kartan som Mermaid-flödesschema, att lägga i dokumentation (GitHub ritar det i
                              ett ```mermaid-block). Varje databas och kö finns en gång.
          --level <nivå>      (med --mermaid) projects, classes (standard) eller all (även metoder, läsningar,
                              skrivningar och meddelanden).
          --direction <LR|TB> (med --mermaid) LR vänster till höger (standard), TB uppifrån och ned.
          --entry "<anrop>"   (med --mermaid) Bara vägen för ett anrop, t.ex. --entry "POST /orders".
          --html <fil>        Skriv kartan som en webbsida med samma nätverk som i testgen ui. Sidan hämtar inget
                              utifrån och kan öppnas utan nätverk, läggas i dokumentationen eller delas.
          --no-restore      Kör inte dotnet restore (annars körs den om paketen inte redan är hämtade). Saknas
                              paketen känns integrationerna igen på namnet.

        Projektfilerna läses med MSBuild, precis som vid dotnet build. Peka bara på kod du litar på.
        Ingångar: controllers med route-attribut ([Route], [HttpGet] ...). Minimal API känns inte igen än.

        Exempel:
          testgen map --solution samples/Shop.slnx --spec samples/shop.openapi.yaml
          testgen map --solution ../butik/Butik.sln --out karta.json
          testgen map --solution samples/Shop.slnx --mermaid docs/order.mmd --entry "POST /orders"
          testgen map --solution samples/Shop.slnx --html docs/systemkarta.html

        Systemkartan finns också som eget verktyg för andra solutions, se systemmap/README.md.

        (Utan installerat testgen: dotnet run --project testgen/src/TestGen.Cli -- map ...)

        Exitkoder: 0 klart, 2 fel i användning (t.ex. solutionen finns inte eller går inte att läsa), 3 specens webbadress svarar inte.
        """;

    public const string Generate = """
        testgen generate - avancerat: skriv scenariofiler till disk (NUnit-spåret)

        Användning:
          testgen generate --spec <fil|url> --out <katalog> [--config <fil>] [--schema <fil>]

        Flaggor:
          --spec <fil|url>    OpenAPI-spec (default: spec i testgen.yaml)
          --out <katalog>     Utkatalog för scenariofiler (default: output i testgen.yaml)
          --config <fil>      Konfiguration (default: testgen.yaml i aktuell katalog om den finns)
          --schema <fil>      Skriv JSON Schema för manuella YAML-scenarier (call-enum fylls från --spec om den anges).
                              Genererar bara scenarier om --out också anges.

        Kommandot skriver filer; det körs aldrig av sig självt. Utan flaggor används testgen.yaml.

        (Utan installerat testgen: dotnet run --project testgen/src/TestGen.Cli -- generate ...)

        Exitkoder: 0 klart, 2 fel i användning eller konfiguration, 3 specens webbadress svarar inte.
        """;

    public static string? For(string command) => command switch
    {
        "run" => Run,
        "template" => Template,
        "ui" => Ui,
        "map" or "karta" => Map,
        "generate" => Generate,
        _ => null,
    };
}
