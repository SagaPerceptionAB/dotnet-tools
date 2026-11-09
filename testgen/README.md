# testgen

testgen testar ett API utifrån dess OpenAPI-spec och en webbapp utifrån dess vyer. Det genererar giltiga och ogiltiga anrop och formulärvärden, kör dem i `dotnet test` eller från kommandoraden och rapporterar OK, FEL eller HOPPAD per test. Egna tester skriver du i Excel. Ingen AI används vid körning: samma indata ger samma resultat.

## Krav

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (`dotnet --version` ska visa `10.x`).
- Ett API med OpenAPI-spec (en fil vid projektet, eller en adress), och/eller en webbapp.
- Docker bara om appen använder PostgreSQL, SQL Server, Azure Storage (Azurite) eller Redis lokalt. Chromium för UI-testerna installeras av `testgen init` (ca 200 MB).

## Installera

Som dotnet-verktyg från nuget.org (paketet heter `SagaPerception.TestGen.Cli`, kommandot `testgen`). Paketet ges ut som förhandsversion, så `--prerelease` behövs:

```
dotnet tool install -g SagaPerception.TestGen.Cli --prerelease
testgen --help
```

Som lokalt verktyg i ett projekt:

```
dotnet new tool-manifest
dotnet tool install SagaPerception.TestGen.Cli --prerelease
dotnet tool restore
```

Testerna i `dotnet test` kommer från paketet `SagaPerception.TestGen.NUnit` (som `testgen init` lägger i testprojektet). Alla testgen-paket ges ut tillsammans med samma version: `SagaPerception.TestGen.Cli`, `SagaPerception.TestGen.NUnit`, `SagaPerception.TestGen.Core`, `.Excel`, `.Mock`, `.Browser` och `SagaPerception.CommandLine`.

Använd `--tool-path verktyg` i stället för `--global` för att installera i en egen mapp.

Från källkoden, utan att installera, från repots rot (klona [repot](https://github.com/SagaPerceptionAB/dotnet-tools) först):

```
./testgen.sh <kommando> [flaggor]      # macOS/Linux
testgen.cmd <kommando> [flaggor]       # Windows
```

Exemplen nedan skriver `testgen`. Utan installation ersätter du det med `./testgen.sh` eller `testgen.cmd`.

## Kom igång

I en solution med ett API eller en webbapp:

```
testgen          # vad som finns i repot och nästa steg
testgen init
dotnet test
```

Från installationen till klara tester tog det 2 minuter (122 s) på exemplet Shop utan testprojekt, med tom NuGet-cache och Chromium redan installerat; Chromium tar ungefär 40 s till första gången. `testgen init` ensam tog 39 s och `dotnet test` (bygge och 70 tester) 80 s. Exemplet har två avsiktliga fel, så två tester fallerar.

`testgen init` skapar `tests/<Solution>.Tests` (en rad per API och webbapp), lägger projektet i solutionen, skriver `views.json`, installerar Chromium om det saknas och visar ett utdrag för CI. Det som redan finns lämnas orört, och en andra körning ändrar inget. Resten av avsnittet visar webbgränssnittet och kommandoraden.


Starta exemplet i ett eget fönster (port 5080):

```
dotnet run --project samples/ShopApi
```

Webbgränssnittet:

```
testgen web --spec samples/shop.openapi.yaml
```

Öppna adressen som skrivs ut (standard port 5100), fyll i API-adressen `http://localhost:5080` och kör. Avsluta med Ctrl+C.

Fliken Resultat listar körningar gjorda i sidan och resultatfilerna som `dotnet test` skriver (`TestResults/testgen/<projekt>.json` i testprojekten för testgen i solutionen), för både API-tester (T-…) och UI-tester (U-…). Välj en rad för att läsa den, eller jämför två körningar i samma lager: nya FEL, lagade och kvarstående, matchade på `key`.

Kommandoraden:

```
testgen run --spec samples/shop.openapi.yaml --api http://localhost:5080
```

Varje test får ett id (T-001, T-002 ...) som är detsamma varje körning. Sist skrivs en rad som `Sammanfattning: 34 tester, 33 OK, 1 FEL, 0 HOPPAD, tid 2,1 s`. Vid FEL visas anropet, svaret och en curl-rad.

## Testerna i `dotnet test`

Samma testfall som `testgen run`, mot appen i testprocessen (TestServer). Inget startas för hand och Docker behövs bara om appen använder PostgreSQL, SQL Server, Azure Storage eller Redis lokalt.

```csharp
public sealed class ShopApiTests() : ApiTests("ShopApi");
```

Testprojektet behöver en `ProjectReference` till API-projektet och paketet `SagaPerception.TestGen.NUnit` (`testgen init` ordnar det; exempel: [`samples/Shop.Tests`](https://github.com/SagaPerceptionAB/dotnet-tools/tree/main/samples/Shop.Tests)). Solutionen, specen, `testgen.yaml` (i testprojektet eller vid solutionen) och miljön hittas av sig själva; miljön väljs med `TESTGEN_ENV` (standard `local`). I en miljö med adress testas den adressen i stället.

- **Testnamn:** `T-001 POST /customers giltig indata`. Kategorier: `api`, `mutating` (POST, PUT, PATCH, DELETE) och orsakskoden vid fel, så `--filter TestCategory=mutating` fungerar.
- **Utfall:** fel i systemet och förväntan som inte stämmer fäller testet, med samma förklaring, anrop, svar och curl som i terminalen. Osäkra orsaker (`auth`, `test-data`, `missing-data`, `test-user-rejected`, `rate-limited`, `environment`, `tool`, `dependency`) blir Inconclusive. Skrivskyddad miljö blir Ignore.
- **Appens utgående anrop** styrs av `hosts` i `testgen.yaml`. `mock` svarar ur `<första delen av värdnamnet>.openapi.yaml` vid `testgen.yaml` (exempel: [`betaltjanst.openapi.yaml`](https://github.com/SagaPerceptionAB/dotnet-tools/blob/main/samples/betaltjanst.openapi.yaml)). Blockerade och mockade anrop och appens varningar följer med när ett test fallerar.
- **Appen körs som lokalt:** miljön är `apis.<projekt>.environment` i `testgen.yaml`, annars `Testing` om `appsettings.Testing.json` finns, annars `ASPNETCORE_ENVIRONMENT` i launch-profilen (den som ger adressen), annars `Development`. Development läser också user secrets på datorn; värdpolicyn (`hosts`) blockerar ändå utgående anrop. Appens kod och konfiguration ändras aldrig.
- **Konfiguration:** det som inte går att läsa ur appen anges under `apis.<projekt>.settings` i `testgen.yaml`. Startar appen ändå inte fäller testerna med det appen sa. Appen ska ge `args` till `WebApplication.CreateBuilder`.
- **Lokala tjänster:** värden som pekar på en lokal PostgreSQL, SQL Server, Azure Storage (`UseDevelopmentStorage=true`, Azurite) eller Redis (`localhost:<port>`) byts mot en container, i samma form, och appen startas en gång. Avbildningen tas ur repots compose-fil (`docker-compose.yml`, `compose.yaml` m.fl. vid solutionen eller ovanför) om den har en tjänst av samma slag, annars en standardavbildning. EF Cores `DbContextOptions` läses också; en tom anslutningssträng byts via den enda nyckeln under `ConnectionStrings`, annars anges nyckeln under `settings`. Ett värde som pekar på en riktig värd byts aldrig: appen startas inte och felet säger att en lokal ska anges under `settings`. Utan Docker blir testerna Inconclusive.
- **Inloggning:** appen får en fiktiv testanvändare (`sub`, `name`, `email`). Anropen bär en slumpad token per körning som appens autentisering godtar, oavsett schema (standard, med namn eller policy); inga andra anrop påverkas. Roller och andra claims anges under `apis.<projekt>.auth.claims` (`role: Admin`), för webbappar under `apps.<projekt>.auth.claims`. Slår appen upp användaren själv och nekar den (401/403, även för negativa fall) hoppas testet över med orsaken `test-user-rejected`. Då anges claims eller ett inloggningssteg. En `token` i `testgen.yaml` eller `--token` används i stället för testanvändaren.
- **Inloggningssteg:** `apis.<projekt>.login` anropar en operation en gång per fixture och använder token ur svaret i stället för testanvändaren: `operation` (operationId), `body` (värden; resten genereras ur specen) och `token` (sökväg i svaret, t.ex. `accessToken` eller `data.token`). Misslyckas det fäller det testerna med vad som hände och vad som ändras. Får ett anrop 401 (t.ex. efter en utloggning) loggar testgen in igen en gång för alla parallella tester och skickar anropet om; nekas det ändå hoppas testet över (`test-user-rejected`). Inloggningsoperationen själv rörs inte.
- **Hastighetsgränser:** vid 429 väntar testgen enligt `Retry-After` (högst 10 s) och försöker en gång till. Hjälper det inte hoppas testet över med orsaken `rate-limited`.
- **503 från appen:** körs appen i testprocessen betyder 502/503/504 oftast att den saknar en tjänst. Texten pekar på `apis.<projekt>.settings` och, när värdpolicyn blockerade ett anrop, på `hosts`.
- **Microsoft.OpenApi 2:** testgen läser specen i samma process som appen, så appen måste använda Microsoft.OpenApi 2.x: `AddOpenApi()` i .NET 10, eller Swashbuckle 10 eller senare. Med Swashbuckle 9 eller äldre fäller testerna med ett förklarande fel.
- **Code-first:** hittas ingen spec startar `testgen init` appen (launch-profilens miljö, port 0), hämtar `/openapi/v1.json` eller `/swagger/v1/swagger.json` och sparar den som `<projekt>.openapi.json` i testprojektet; startar appen inte får du felet och rådet, och init fortsätter med resten. Serverar appen sin spec jämförs den med den incheckade i testet "Specen i repot stämmer med appen". Skrivs specen av bygget hoppas jämförelsen över.
- **Microsoft.Testing.Platform:** anger `global.json` den som `test.runner` får testprojektet `OutputType` Exe och `EnableNUnitRunner`, och testerna körs med `dotnet test --project <katalog>`.
- **Excel:** `apis.<projekt>.excel` i `testgen.yaml` ger Excel-raderna som testfall. Ett scenario körs i ett svep.

### UI-tester

```csharp
public sealed class ShopWebViewTests() : ViewTests("ShopWeb");
```

Webbappen körs på Kestrel (port 0) i testprocessen och dess API-anrop går till en mock ur API:ets spec, så ett formulär som tillåter mer än API:et hittas. `testgen init` ordnar det som behövs: en `ProjectReference` till webbappen, en incheckad `views.json` och Chromium. Testnamn `U-001 /products/create formulär`, kategorier `ui`, `form`, `boundary`, `accessibility`, `screenshot`. Testfallen: sidan (laddas utan fel), länkarna, formuläret med giltiga värden (mocken får exakt ett anrop som klarar specen), gränsvärden mot specen, ogiltiga värden per formulärregel och tjänstefel (mocken svarar 500). I en miljö med `apps.<projekt>.url` testas den adressen utan mock, och i en skrivskyddad miljö skickas inga formulär. När ett test fallerar bifogas skärmdump, konsollogg, appens logg och mockens anropslogg (`TestResults/testgen/attachments`).
- **Resultatfil:** `TestResults/testgen/<projekt>.json` under testprojektet, samma format som `testgen run --report` (`layer: api`).

## Kommandon

`testgen` utan argument visar vad som finns i repot (API:er, appar, specar, tester) och nästa steg. `testgen <kommando> --help` visar alla flaggor och standardvärden.

| Kommando | Vad det gör | Exempel |
| --- | --- | --- |
| `init` | Skapar testprojektet så att `dotnet test` fungerar | `testgen init` |
| `run` | Kör tester mot ett API | `testgen run` (i en solution med ett API) |
| `template` | Skapar en Excel-mall för egna tester | `testgen template --spec s.yaml --out tester.xlsx` |
| `web` | Kör och läs resultat i webbläsaren | `testgen web --port 5100` |
| `map` | Systemkarta över källkoden | `testgen map --solution samples/Shop.slnx` |
| `mock` | Mock av ett API ur specen | `testgen mock --spec s.yaml --port 5080` |
| `views` | Vyer och formulär till `views.json` | `testgen views --solution samples/Shop.slnx` |

### Viktigaste flaggor för `run`

| Flagga | Betydelse |
| --- | --- |
| `--api <projekt|url>` | API-projektets namn i solutionen, eller en adress. Utan flaggan: solutionens enda API. Adressen kommer annars ur miljön (lokalt ur `launchSettings.json`). |
| `--spec <fil|url>` | OpenAPI-spec. Utan flaggan: `apis.<projekt>.spec` i testgen.yaml, annars `<projekt>.openapi.(yaml|yml|json)` i testprojektet, annars `*.openapi.(yaml|yml|json)` vid projektet, annars filen som bygget skriver (`OpenApiGenerateDocuments`, `OpenApiDocumentsDirectory` och `--file-name` ur csproj; standard `obj/<projekt>.json`). |
| `--excel <fil>` | Egna tester i en Excel-fil. |
| `--env <miljö>` | Miljö i testgen.yaml, annars `TESTGEN_ENV`, annars `local`. Flaggor på kommandoraden vinner. |
| `--token <värde>` | Skickas som `Authorization: Bearer <värde>`. |
| `--header "N: v"` | Extra header på alla anrop. Får anges flera gånger. |
| `--seed <tal\|random\|ci>` | Slumpfrö för testdata (standard 12345). Byt det om uppgifterna redan finns (svar 409). |
| `--timeout <s>` | Sekunder per anrop. |
| `--max-time <s>` | Högsta tid för hela körningen. |
| `--only <operationId>` | Kör bara ett anrop. |
| `--test <T-018>` | Kör bara det testet, med samma data som i en full körning. Får anges flera gånger. |
| `--scenario <namn>` | Kör bara det Excel-scenariot. Får anges flera gånger. |
| `--excel-only` | Hoppa över de genererade testerna. |
| `--explain` | Förklaring i klartext för varje test. |
| `--verbose` | Visa anrop och svar för alla tester. |
| `--report <fil>` | Rapport efter körningen: `.html`, `.xlsx`, `.json` eller `.xml` / `.junit.xml`. Får anges flera gånger. |
| `--baseline <fil>` | Jämför med en tidigare JSON-rapport (nya, lagade och kvarvarande FEL). |
| `--fail-on <orsaker>` | Orsaker som ger exitkod 1 (se Köra i CI). |
| `--config <fil>` | En annan testgen.yaml än den som hittas vid solutionen. |

## Konfiguration

### testgen.yaml

Valfri fil som hittas i testprojektet eller i solutionens katalog eller ovanför (eller pekas ut med `--config`). Finns den på båda ställena är det ett fel. Sökvägar utgår från filens katalog. Okända nycklar ger fel med rad och förslag. Exempel: [`samples/testgen.yaml`](https://github.com/SagaPerceptionAB/dotnet-tools/blob/main/samples/testgen.yaml).

| Nyckel | Betydelse |
| --- | --- |
| `apis.<projekt>` | Per API (projektnamnet): `spec`, `excel`, `environment`, `skipOperations`, `overrides`, `bindings`, `settings`, `auth`, `login`. |
| `apps.<projekt>` | Per webbapp: `environment`, `settings`, `auth`. |
| `auth.claims` (under `apis`, `apps`) | Claims för testanvändaren, typ till värde: `role: Admin` (flera roller kommaseparerade), `sub`, `name`, `email`. |
| `login` (under `apis`) | Inloggningssteget: `operation`, `body`, `token`. Se Inloggningssteg ovan. |
| `settings` (under `apis`, `apps`) | Konfiguration för appen när den körs i testprocessen, nyckel för nyckel, t.ex. `Auth:SigningKey: ${env:SIGNING_KEY}`. Gäller inte en miljö med `url`. |
| `ui` | UI-testerna: `checks` (`accessibility` = axe-core WCAG 2.1 AA, `screenshots` = jämförelse mot `screenshots/` i testprojektet; första körningen skapar bilderna och blir Inconclusive) och `trace: true` (Playwright-spår, även `TESTGEN_TRACE=1`; kan innehålla känsliga uppgifter). |
| `hosts` | Värdpolicy för anrop som appen under test gör till andra system: `default` (standard `block`) och värdnamn med `block`, `allow` eller `mock`. |
| `environments.<namn>` | Miljö med `readOnly` och per projekt `apis.<projekt>` / `apps.<projekt>`: `url`, `token`, `headers`, `timeout`. `local` finns alltid. |
| `seed` | Bas-seed, standard `12345`. En stabil seed per operation härleds ur den. |
| `locale` | Språk för genererad data, standard `sv`. |
| `bindings` (under `apis`) | Fält som fylls från svaret av en annan operation, t.ex. `createOrder: { customerId: ${createCustomer.id} }`. |
| `overrides` (under `apis`) | Fasta värden per operation och fält, t.ex. `createOrder: { quantity: 2 }`. |
| `skipOperations` (under `apis`) | Lista med operationer som inte ska generera scenarier. |

### Testdata

Datan kommer ur specen, i den här ordningen: `enum`, `overrides`, `example`, `format`, `pattern`, gränser (`minLength`, `minimum`, `multipleOf`, `minItems` m.fl.), fältnamn, typ. Ett `example` används bara om det följer schemat.

Datan är fiktiv. Personnummer tas ur Skatteverkets testpersonnummer, telefon ur PTS fiktiva nummerserier och e-post och webbadresser använder `example.com`. Ett `example` med personnummer, e-post eller telefon utanför de serierna byts ut. Egna format: `personnummer`, `phone` (eller `tel`), `organisationsnummer`, `postnummer`.

### Miljöer och inloggning

- `--token` skickas som `Authorization: Bearer ...`, eller i den header specens `apiKey`-schema anger.
- `--header` lägger till egna headers på alla anrop.
- Miljöerna står i testgen.yaml (inte i Excel). I en miljö med `readOnly: true` hoppas tester som skickar POST, PUT, PATCH eller DELETE över (orsak `read-only`, räknas inte som fel).
- Hemligheter (`token`, headers som ser hemliga ut) måste skrivas `${env:NAMN}` i filen; klartext är ett konfigurationsfel (exitkod 2).
- Låt hemligheter ligga i miljövariabler: `${env:NAMN}` i ett värde ersätts med miljövariabeln `NAMN`. Använd enkla citattecken i skalet: `--token '${env:API_TOKEN}'`. Känsliga värden visas som `***` i utskrifter.

### Excel-tester

Skapa en mall, fyll i bladet Tester och kör med `--excel`. Filen kontrolleras mot specen innan något körs, och alla fel visas samtidigt med cell, t.ex. `Tester!B3 (Anrop)`.

```
testgen template --spec samples/shop.openapi.yaml --out tester.xlsx
testgen run --spec samples/shop.openapi.yaml --api http://localhost:5080 --excel tester.xlsx
```

Bladet Hjälp i mallen beskriver kolumnerna.

## Köra i CI

Resultatet skrivs till stdout och förlopp, varningar, fel och bekräftelser (`Skrev r.html (HTML-rapport)`) till stderr. Exitkoden styr bygget:

```
testgen run --spec https://test.example.se/openapi.json --api https://test.example.se \
    --token '${env:API_TOKEN}' --seed ci --max-time 900 \
    --report resultat.json --report resultat.junit.xml --report resultat.html \
    --baseline forra.json --fail-on-new
```

- Färdiga exempel: [GitHub Actions](https://github.com/SagaPerceptionAB/dotnet-tools/blob/main/testgen/docs/ci/github-actions.yml) och [Azure Pipelines](https://github.com/SagaPerceptionAB/dotnet-tools/blob/main/testgen/docs/ci/azure-pipelines.yml).
- `--seed ci` tar fröet från bygget, så en omkörning ger ny testdata.
- `--fail-on <orsaker>` väljer vilka orsaker som ger exitkod 1. Koder: `system`, `expectation`, `auth`, `environment`, `test-data`, `missing-data`, `test-user-rejected`, `rate-limited`, `tool`, `dependency`, `read-only`. Standard är `system,expectation`; övriga FEL och HOPPAD ger exitkod 4. Exempel: `--fail-on system,expectation,auth`.
- `--baseline forra.json --fail-on-new` ger exitkod 1 bara för nya FEL.
- `--allow-empty` tillåter en körning utan tester (annars exitkod 2).
- JSON-rapportens schema: [docs/ci/testgen-result.schema.json](https://github.com/SagaPerceptionAB/dotnet-tools/blob/main/testgen/docs/ci/testgen-result.schema.json). Orsakskoder, regler och resultat (`passed`, `failed`, `skipped`) är engelska ord; null utelämnas.

| Exitkod | Betydelse |
| --- | --- |
| 0 | Alla tester OK. |
| 1 | Minst ett FEL i systemet eller där förväntan i testet inte stämde (ändra med `--fail-on`). |
| 2 | Fel i användning eller konfiguration (flaggor, filer, spec, Excel, miljö), eller inga tester att köra. |
| 3 | API:et eller specens webbadress gick inte att nå, eller `--max-time` löpte ut. En partiell rapport skrivs. |
| 4 | Inget säkert svar: inga FEL i systemet, men FEL eller HOPPAD av osäker orsak (inloggning 401/403, testdata 409, miljön svarar inte, följdfel). |
| 70 | Internt fel. Version, system, runtime och kommandoraden (med hemligheter maskade) skrivs till stderr. |
| 130 | Avbruten med Ctrl+C. Partiell rapport skrivs. |
| 143 | Avbruten med SIGTERM. Partiell rapport skrivs. |

Meddelandet `Kunde inte köra: ...` betyder att verktyget inte kunde köra alls. Det är inte ett testresultat.

## Systemkarta

`testgen map --solution <.sln|.slnx|.csproj>` visar vägen genom källkoden ut till databaser, API:er och köer. Kartan finns också som flik i `testgen web`. Kartan skrivs som text på stdout. Mer i [systemmap/README.md](https://github.com/SagaPerceptionAB/dotnet-tools/blob/main/systemmap/README.md).

## Mock

`testgen mock --spec <fil|url>` startar en mock på 127.0.0.1. Den svarar enligt specen, kontrollerar varje anrop mot den och skriver anropet på stdout. Ett anrop som inte stämmer får `400` med en förklaring, en okänd väg `404`. Peka appen på mockens adress. Flera `--spec` ger en mock per spec. Avsluta med Ctrl+C.

Samma mock finns som bibliotek, `TestGen.Mock`: `await using var mock = await MockServer.StartAsync("s.yaml")`, sedan `mock.Url`, `mock.Calls`, `mock.Respond(operationId, status)` och `mock.Reset()`.

## Vyer

`testgen views` läser webbappernas vyer, formulär och formulärregler ur källkoden (Razor Pages, Blazor, MVC och SPA) och skriver dem till `views.json`, tillsammans med vilket API appen anropar och via vilken konfigurationsnyckel (`ShopApi:BaseUrl`). Filen hamnar i testprojektet för testgen, annars i aktuell katalog; ändra med `--out`. Checka in den: UI-testerna läser bara filen. Projektfilerna läses med MSBuild, så peka bara på kod du litar på.

Med `--probe` startas varje app (API:erna mockade ur sina specar) och sidorna öppnas i Chromium, så att det som bara syns när appen körs läggs till: etiketter, HTML-regler, länktexter och sidor källkoden inte nämner. `--max-pages` begränsar hur många sidor som öppnas (standard 50).

Ett formulär kopplas till specens operation (`operationId`) via anropet som sidan gör när det skickas, och formulärets fält till specens fält på namn. Matchar inget utelämnas kopplingen, och ett anrop som specen saknar ger en varning. Formatet står i [docs/views.schema.json](https://github.com/SagaPerceptionAB/dotnet-tools/blob/main/testgen/docs/views.schema.json).
