# 0007. Överraskningar och beslut under omskrivningen

## Status
Antagen 2026-11-08, när fas 10 slutfördes. Gäller de beslut som togs medan planen genomfördes och som inte stod i den.

## Beslut och skäl

**Razor Pages och Blazor-sidor läses ur Razors källgenerator (fas 2b).** Systemkartan läser inte `.razor` och `.cshtml` som text. MSBuildWorkspace kör Razors källgenerator, och klasserna den ger har route-attribut som läses som kod: `[Route]` på Blazor-sidan och `RazorCompiledItemMetadata` ("RouteTemplate", "Identifier") på Razor Page-klassen. Filen och raden är `@page`-raden. Då ser en sida likadan ut för kartan som en controller. Kostnaden är att projektet måste vara återställt (`dotnet restore`).

**Modellen för `views.json` ligger i Core (fas 5).** `TestGen.Views` hittar vyerna med Roslyn, men filen och dess modell (`ViewsFile`, `View`, `Form`, `Field`, `Rule`, `Locator`) ligger i `TestGen.Core`. Testprocessen läser bara filen och behöver därför aldrig Roslyn, och `TestGen.Browser` och `SagaPerception.Testing.NUnit` kan använda modellen utan att referera Views.

**Databasen upptäcks i appens DI, inte med Roslyn (fas 7b).** `ApiTests` läser appens `DbContextOptions` ur tjänsteleverantören med reflektion och väljer container (SQL Server eller PostgreSQL) efter providern. SQLite och minnesdatabaser startar ingenting. Roslyn får inte finnas i testprocessen, och bara appen behöver EF Core-paketet.

**`testgen mock` avslutas med 0 (fas 4).** En server avslutas med Ctrl+C, och det är hur den används, inte ett avbrott.

**Alla testgen-paket har samma version och ges ut på en tagg (fas 10).**
- `SagaPerception.TestGen.Core`, `.Excel`, `.Mock`, `.Browser` och `SagaPerception.CommandLine` är egna paket, inte inbakade. `dotnet pack` gör dem utan handpåläggning och `SagaPerception.Testing.NUnit` får dem som vanliga beroenden. Verktyget `SagaPerception.TestGen.Cli` är ett tool-paket och bär allt själv (ca 50 MB, mest Playwrights drivrutin). Playwrights `.ps1`-skript i verktyget ger NU5111, som stängs av för det projektet.
- `SagaPerception.Testing` har en egen version men packas med på `testgen/v*`, eftersom NUnit-paketet beror på det.
- Det långsamma init-testet använder fortfarande `TESTGEN_LIBRARY_PROJECT` (en `ProjectReference`) och inte en lokal feed. Paket med version 1.0.0 från en feed hamnar i användarens NuGet-cache och blir kvar, så nästa körning skulle testa gammal kod.

**testgen använder `Microsoft.OpenApi` 2.x, så apparna behöver 2.x.** Motorn läser specen i samma process som appen när `ApiTests` körs: testprojektet refererar API-projektet, och två huvudversioner av `Microsoft.OpenApi` kan inte laddas i samma process. `Microsoft.AspNetCore.OpenApi` i .NET 10 (`AddOpenApi()`) kräver 2.x, så testgen följer med. Appar med `AddOpenApi()` eller Swashbuckle 10 eller senare fungerar; Swashbuckle 9 och äldre (1.x) ger ett tydligt fel när testerna förbereds.
