# uitestgen

uitestgen hittar vyerna i en webbapp och skriver Playwright-tester i C# (NUnit) för dem: smoke, formulär, navigering och skärmdumpar. Backend kan mockas ur appens OpenAPI-spec. Det går i tre steg:

1. **Hitta.** Läser källkoden (Blazor, Razor Pages, MVC, React, Angular och Vue) och skriver en *vykatalog*: en JSON-fil med en post per sida, med rutt, titel, rubrik, formulär med fält och regler, och länkar (också länkarna i delade layouter, t.ex. menyn).
2. **Sondera** (valfritt). Öppnar vyerna i den körande appen och fyller i det som källkoden inte visar: etiketter, fält i komponenter, länkar och sidor som bara går att nå genom länkar.
3. **Generera.** Skriver ett fristående testprojekt ur katalogen: en Page Object per vy och tester för smoke, formulär, navigering och skärmdumpar.

Katalogen är kontraktet mellan stegen. Den är sorterad och utan tider och absoluta sökvägar: samma källkod ger exakt samma JSON, och samma katalog ger exakt samma testkod.

## Krav

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (`dotnet --version` ska visa `10.x`).
- Playwright-webbläsare (Chromium) för att köra de genererade testerna, och för sondering. Att hitta vyerna och generera testerna kräver ingen webbläsare.
  - Testprojektet installerar sin webbläsare med `pwsh bin/Debug/net10.0/playwright.ps1 install chromium` (se "Kom igång").
  - Sonderingen använder verktygets egen Playwright. Har du ingen Chromium: kör `playwright.ps1 install chromium` som ligger bredvid `UiTestGen.Cli.dll` i verktygets mapp (för ett verktyg installerat med `--tool-path verktyg`: `verktyg/.store/sagaperception.uitestgen.cli/<version>/sagaperception.uitestgen.cli/<version>/tools/net10.0/any/`), eller ange en befintlig webbläsare med `--browser-path` eller `UITEST_BROWSER_PATH`.

## Installera

Som dotnet-verktyg från nuget.org (paketet heter `SagaPerception.UiTestGen.Cli`, kommandot `uitestgen`). Paketet ges ut som förhandsversion, så `--prerelease` behövs:

```
dotnet tool install --global SagaPerception.UiTestGen.Cli --prerelease
uitestgen --help
```

Som lokalt verktyg i ett projekt:

```
dotnet new tool-manifest
dotnet tool install SagaPerception.UiTestGen.Cli --prerelease
dotnet tool restore
```

Använd `--tool-path verktyg` i stället för `--global` för att installera i en egen mapp.

Från källkoden, utan att installera, från repots rot (klona [repot](https://github.com/SagaPerceptionAB/dotnet-tools) först):

```
dotnet run --project uitestgen/src/UiTestGen.Cli -- <kommando> [flaggor]
```

Exemplen nedan skriver `uitestgen`. Utan installation ersätter du det med raden ovan.

## Kom igång

Exemplet [samples/ShopWeb](https://github.com/SagaPerceptionAB/dotnet-tools/tree/main/samples/ShopWeb) är en liten Blazor-app (statisk SSR) med en Razor Page, en meny i en delad layout och två formulär. Kommandona körs från repots rot.

1. **Starta ShopWeb** i ett eget fönster. Appen skickar sina formulär till ett API; vi låter testerna mocka det (se "Mockad backend") och pekar därför appen på stubbens adress, port 5099:

   ```
   ShopApi__BaseUrl=http://localhost:5099 dotnet run --project samples/ShopWeb --urls http://localhost:5090
   ```

2. **Generera testerna** ur källkoden och OpenAPI-specen:

   ```
   uitestgen --solution samples/ShopWeb/ShopWeb.csproj --spec samples/shop.openapi.yaml --out tests/Shop.UiTests
   ```

   Verktyget visar en rad per vy (`/products  Blazor  formulär: 0  länkar: 6`), skriver testprojektet och vykatalogen (`vykatalog.json`) i `tests/Shop.UiTests` och listar vyer som ingen annan vy länkar till. Vill du bara se vilka vyer som hittas: `uitestgen discover --solution samples/ShopWeb/ShopWeb.csproj`.

3. **Installera webbläsaren** (en gång) och bygg testprojektet:

   ```
   cd tests/Shop.UiTests
   dotnet build
   pwsh bin/Debug/net10.0/playwright.ps1 install chromium
   ```

4. **Kör testerna** med mockad backend och stubben på:

   ```
   UITEST_STUB=on dotnet test
   ```

   Första körningen skapar referensbilderna för de visuella testerna (`Screenshots/*.png`), och dessa tester blir *Inconclusive* i stället för gröna. Granska bilderna. Kör sedan igen: nu ska allt vara grönt.

   Formulärtesterna skickar giltiga värden, så ShopWeb sparar en produkt i minnet medan de körs, och listan `/products` ser då olika ut beroende på vilka tester som hunnit före. Kör de visuella testerna för sig mot en nystartad app:

   ```
   UITEST_STUB=on dotnet test --filter Category=Visuellt
   UITEST_STUB=on dotnet test --filter Category!=Visuellt
   ```

Utan `--spec` blir backend `real`: testerna går mot appen och dess riktiga API (`ShopApi__BaseUrl` utelämnas; starta `samples/ShopApi` på port 5080).

## Kommandon

`uitestgen <kommando> --help` visar alla flaggor och standardvärden.

| Kommando | Vad det gör |
|---|---|
| `uitestgen discover` | Hittar vyerna i källkoden och skriver vykatalogen. Med `--app` sonderas vyerna också. |
| `uitestgen probe` | Sonderar en befintlig vykatalog mot den körande appen. |
| `uitestgen generate` | Skriver testprojektet ur en vykatalog. |
| `uitestgen` (utan kommando) | Hitta (och sondera) och generera i ett steg. Skriver också `vykatalog.json` i utmappen. |

### Flaggor

| Flagga | Värde | Gäller | Betydelse |
|---|---|---|---|
| `--solution` | `<fil\|mapp>` | discover, utan kommando | Solution (`.slnx`, `.sln`), projekt (`.csproj`) eller mapp att leta i. Obligatorisk om `--spa` saknas. |
| `--spa` | `<mapp>` | discover, utan kommando | Mapp med en SPA (React, Angular, Vue). Kan anges flera gånger. Utan `--solution` blir den första mappen roten för sökvägarna i katalogen. |
| `--app` | `<url>` | discover, probe, utan kommando | Den körande appens adress, t.ex. `http://localhost:5000`. Slår på sonderingen. Obligatorisk för `probe`. |
| `--max-pages` | `<N>` | discover, probe, utan kommando | Sonderingen öppnar högst N sidor, de kända vyerna medräknade. Standard 50. |
| `--browser-path` | `<fil>` | discover, probe, utan kommando | Chromium som sonderingen använder. Annars `UITEST_BROWSER_PATH`, annars Playwrights egen. |
| `--catalog` | `<fil>` | probe, generate | Vykatalogen att läsa. Obligatorisk. |
| `--out` | `<sökväg>` | alla | discover: filen att skriva (standard `vykatalog.json`). probe: filen att skriva (standard: katalogen själv skrivs över). generate och utan kommando: mappen för testprojektet (obligatorisk). |
| `--name` | `<namn>` | generate, utan kommando | Testprojektets namn och rotnamnrymd, t.ex. `Shop.UiTests`. Standard: utmappens namn. |
| `--backend` | `mock`, `real` | generate, utan kommando | Backend som standard när testerna körs. Standard: `mock` om `--spec` anges, annars `real`. Kan bytas vid körning med `UITEST_BACKEND`. |
| `--spec` | `<fil\|url>` | generate, utan kommando | OpenAPI-spec för mockade svar. Kopieras inte; en fil sparas som sökväg relativt utmappen. |
| `--seed` | `<N>` | generate, utan kommando | Slumpfrö för testdata. Standard 12345. |
| `--verbose` | | alla | Visa tekniska detaljer när något går fel. |

Varningar från sökningen (t.ex. en rutt som inte gick att läsa) skrivs till stderr.

## Vad som känns igen

| Ramverk | Vyer | Formulär och regler | Länkar |
|---|---|---|---|
| **Blazor** | `@page` i `.razor` (flera `@page` ger flera vyer), `[Authorize]` | `EditForm` med `InputText`, `InputNumber`, `InputDate`, `InputTextArea`, `InputSelect`, `InputCheckbox`. Regler ur modellklassens attribut (`[Required]`, `[StringLength]`, `[Range]`, `[RegularExpression]`, `[EmailAddress]` med flera). | `<a>` och `<NavLink>`. Länkar i layouten (`@inherits LayoutComponentBase`) och komponenterna den använder, ett steg djupt, läggs till på varje sida som använder layouten: sidans `@layout`, annars närmaste `_Imports.razor`, annars `DefaultLayout` i `<RouteView>`. Inte för `@layout null`. |
| **Razor Pages** | `@page` i `.cshtml` under `Pages/` (rutt ur sökvägen, områden och `@page "{id:int}"`) | `<form>` med `asp-for` (och `PageModel`-klassens attribut) | `<a href>`. Länkar i layouten (`Layout` i `_ViewStart.cshtml` eller i sidan) och partials den använder med `<partial name="..." />`, ett steg djupt, läggs till på alla sidor i projektet. Inte för `Layout = null`. |
| **MVC** | GET-actions som returnerar `View(...)`. Rutt ur `[Route]`/`[HttpGet]`, annars `{controller=Home}/{action=Index}/{id?}`. | Som Razor Pages, ur vyfilen och `@model` | `<a href>` och `<a asp-controller asp-action asp-route-*>`. Layouten på samma sätt som för Razor Pages. |
| **React, Angular, Vue** | React Router (JSX och objektform), Angular `Routes` (med `loadChildren`) och Vue Router, ur routerkonfigurationen | Formulär i JSX, Angular-mallar och Vue-templates, med HTML-regler och valideringsattribut | `<a href>`, `<Link>`/`<NavLink>` (React Router), `routerLink` (Angular) och `<router-link>` (Vue). Anrop till backend (`axios`, `fetch`, `HttpClient`) sparas för mockning. |

Rutter med parametrar (`/products/{id:int}`) får exempelvärden efter villkoret. Vyer som kräver inloggning (`[Authorize]`, route guard) hittas men får inga formulär- eller skärmdumpstester.

Sonderingen lägger till vad källkoden inte ger: titel, rubrik, etiketter, HTML-regler (`required`, `minlength`, `maxlength`, `min`, `max`, `pattern`), länkar och formulär som bara syns i den körande appen. Länkar inom appen till okända sidor följs (högst 3 steg och N sidor) och blir nya vyer av sorten `Probed`. Regler med felmeddelande från koden ändras aldrig.

## Vilka tester som genereras

För varje vy skrivs `Tests/<Vy>Tests.g.cs` med dessa tester, och en Page Object `Pages/<Vy>Page.g.cs`:

| Test | Kategori | Vad det gör |
|---|---|---|
| `Opens_without_errors` | `Smoke` | Öppnar vyn och kontrollerar titel, rubrik och att inga fel samlats (`console.error`, sidfel, misslyckade anrop och svar med status 400 eller högre; saknade bilder, typsnitt och ikoner räknas inte, se `IsIgnored` i `UiTestBase`). |
| `<Formulär>_sends_valid_values` | `Formulär` (med riktig backend också `Muterar`) | Fyller i alla fält giltigt, skickar och kontrollerar att inget felmeddelande visas. |
| `<Formulär>_<Fält>_<Regel>_shows_error` | `Formulär` | Ett test per regel som går att bryta (obligatoriskt, längd, gränser, mönster, e-post): skickar ett ogiltigt värde och väntar på felet. |
| `Link_to_<Vy>` | `Navigering` | Går till vyn, klickar på länken och kontrollerar att adressen och målets rubrik stämmer. Länkar till okända adresser får inget test. |
| `Looks_like_the_reference` | `Visuellt` (med riktig backend också `Instabil`) | Jämför en skärmdump av hela sidan med referensbilden. |

Vyer som kräver inloggning får ett smoke-test i kategorin `Inloggning` som hoppas över (`Assert.Ignore`).

Filtrera med NUnit-kategorier:

```
dotnet test --filter Category=Smoke
dotnet test --filter "Category!=Muterar&Category!=Instabil"     # inget som ändrar data, inget instabilt
dotnet test --filter Category=Navigering
```

`Tests/CatalogReport.g.cs` listar vyer som ingen annan vy länkar till (utom startsidan), eftersom inga navigeringstester leder dit. Samma lista skrivs i terminalen efter `generate`.

## Mockad backend

Med `--spec <fil|url>` skrivs svar ur OpenAPI-specen i `Support/ApiMocks.g.cs`: en rad per operation med metod, sökvägsmönster, status (första 2xx, annars 200) och en JSON-kropp. Svaren räknas ut när koden genereras, så samma spec och `--seed` ger samma svar. Mockning sker bara när `UITEST_BACKEND=mock` (standard om `--spec` anges). Det finns två varianter, beroende på vem som anropar API:et:

| | Appen anropar API:et från webbläsaren | Appen anropar API:et från servern |
|---|---|---|
| Passar | SPA, Blazor WebAssembly, `fetch` i JavaScript | Blazor SSR, Razor Pages, MVC |
| Hur | `UiTestBase.ConfigureMocksAsync` registrerar `page.RouteAsync` med en regex som bara täcker sökvägarna i specen: fetch och XHR dit besvaras ur tabellen. Sidor, skript, CSS och bilder går direkt, utan omvägen via .NET. | Webbläsaren ser aldrig anropet, så testprojektet startar en liten stub-server (`Support/ApiStub.g.cs`, Kestrel) som svarar ur samma tabell. Okända sökvägar ger 404. |
| Att göra | Inget. | Starta stubben (`UITEST_STUB=on`) och peka appens API-adress på den, t.ex. `ShopApi__BaseUrl=http://localhost:5099`. |

Miljövariabler när testerna körs:

| Miljövariabel | Betydelse |
|---|---|
| `UITEST_BASE_URL` | Appens adress. Standard `http://localhost:5090`. |
| `UITEST_BACKEND` | `mock` eller `real`. Standard enligt `--backend`. |
| `UITEST_STUB=on` | Stub-servern startar på port 5099 (om ingen port är angiven). |
| `UITEST_STUB=off` | Stubben startar inte, även om en port är angiven. |
| `UITEST_STUB_PORT=<port>` | Stubben startar på den porten. |

Stubben startar en gång för alla tester och bara när backend mockas. Utan någon av variablerna startar den inte. Appen anropar stubben först när ett formulär skickas, så den kan startas före testerna; anropar den API:et redan vid uppstart behövs en stub som redan kör. Ett formulär som skickas till stubben ska lyckas: visar appen ett fel (`role="alert"`) underkänns testet av formuläret med giltiga värden.

Utan `--spec` finns inga mockar. Med `--backend mock` skrivs då en varning, och egna mockar skrivs genom att åsidosätta `ConfigureMocksAsync`.

## Skärmdumpar

Varje vy utan inloggning har `Looks_like_the_reference`: en skärmdump av hela sidan jämförs med `Screenshots/<Vy>.png` i testprojektet. Fönstret är fast (1280x800), språket `sv-SE` och tidszonen `Europe/Stockholm`. Playwright för .NET har ingen `ToHaveScreenshot`, så jämförelsen skrivs som kod (med en egen PNG-avkodare; inget bildpaket behövs).

- **Referenser.** Saknas referensbilden skapas den och testet blir *Inconclusive*, så att det syns att något nytt skapats. Granska bilden och checka in `Screenshots/`.
- **Uppdatering.** Efter en avsiktlig ändring: kör med `UITEST_UPDATE_SCREENSHOTS=1`. Alla jämförda bilder skrivs om och granskas med `git diff`.
- **Skillnad.** Testet fäller med andel och antal avvikande pixlar, och skriver `<Vy>.actual.png` och `<Vy>.diff.png` (skillnaden i rött) till `ScreenshotDiffs/` bredvid testresultatet.
- **Maskering.** Sätt attributet `data-uitest-mask` på det som ändras mellan körningarna (klockslag, slumpvärden); det döljs bakom en ruta. Eller peka ut elementen i den handskrivna `Tests/<Vy>Tests.cs`:

  ```csharp
  partial void MaskedElements(IPage page, List<ILocator> masks) => masks.Add(page.Locator(".clock"));
  ```

| Miljövariabel | Betydelse |
|---|---|
| `UITEST_UPDATE_SCREENSHOTS=1` | Skapa och uppdatera referensbilderna. |
| `UITEST_SCREENSHOT_THRESHOLD` | Högsta andel avvikande pixlar i procent. Standard 0.1. |
| `UITEST_SCREENSHOT_TOLERANCE` | Tolerans per färgkanal, 0-255. Standard 16. |
| `UITEST_SCREENSHOT_DIR` | Mapp för referensbilderna. Standard `Screenshots` i projektmappen. |

Bilderna beror på webbläsare och typsnitt: skapa dem på samma slags dator som CI använder.

## Genererade filer

- Filer som slutar på `.g.cs`, projektfilen och `Directory.*`-filerna skrivs om varje gång du kör `uitestgen generate`. Ändra dem inte.
- Filer som slutar på `.cs` (utan `.g`), t.ex. `Tests/<Vy>Tests.cs` och `Pages/<Vy>Page.cs`, är dina. De skapas en gång och skrivs aldrig över. Egna tester och tillägg till sidorna skrivs där. Det gäller också `README.md` i testprojektet.

Tester för vyer som tagits bort ur appen tas inte bort av en ny körning; ta bort filerna själv.

## Begränsningar

- Inloggning stöds inte: vyer som kräver inloggning hoppas över i formulär- och skärmdumpstesterna.
- Rutter, adresser och titlar som inte är fasta strängar (`@page @Konstant`, uttryck) går inte att läsa; vyn hoppas över med en varning eller får `null`/`{}` i katalogen. Konventionella MVC-rutter antas följa standardmönstret.
- Layoutlänkar läses för Blazor, Razor Pages och MVC, inte för SPA. I layouter läses `<a href>`, `<NavLink>` och i MVC tag helpers (`asp-controller`/`asp-action`); `asp-page` och länkar som skapas av kod läses inte. Bara ett steg djupt i komponenter och partials.
- Formulärtesterna skickar riktiga värden. Med riktig backend kan de skapa poster (kategorin `Muterar`); mockad backend undviker det. Sparar appen något i minnet kan visuella tester påverkas av de andra testerna (se "Kom igång").
- Regler som bara finns i JavaScript (egen validering i en komponent) läses inte; HTML-regler och attribut i modellen läses.
- Sonderingen följer inte utloggning, filer eller länkar med query, och öppnar högst `--max-pages` sidor.
- Källkod läses som text av en egen läsare, inte av kompilatorn. Ovanliga mönster kan missas; då hjälper sondering eller en handskriven `.cs`-fil.

## Exitkoder och säkerhet

Exitkoder: `0` klart, `2` fel (t.ex. en flagga eller fil som inte stämmer, eller en webbläsare som saknas för sonderingen).

uitestgen läser filerna som text och kör aldrig appens kod; projektfilerna läses inte med MSBuild. Sonderingen (`--app`) öppnar däremot appen i en webbläsare och följer dess länkar: peka bara på en app du litar på, helst en test- eller utvecklingsmiljö. De genererade testerna skickar riktiga formulär till den app du anger; kör dem inte mot produktion.
