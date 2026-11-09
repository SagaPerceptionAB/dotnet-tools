# 0008. Appen i testprocessen körs som lokalt

## Status
Antagen 2026-11-09, efter provkörningen på en riktig app. Ersätter punkten om databasen i 0007.

## Sammanhang
Provkörningen visade att testgen inte fungerade på en vanlig .NET-app utan handpåläggning:
- specen hittades inte;
- testprojektet godtogs inte av `dotnet test`;
- appen startade inte i testprocessen;
- databasen hittades bara genom att appen startades;
- testanvändaren saknades;
- flera falska fel.

Målet är att `testgen init` och `dotnet test` fungerar direkt så långt det går. Appens kod och konfiguration ändras aldrig. testgen lägger bara till testprojektet, en rad i solutionen och paketversioner i `Directory.Packages.props`.

## Beslut och skäl

**Appen körs i sin egen lokala miljö.** Miljön väljs i den här ordningen:
1. `apis|apps.<p>.environment` i testgen.yaml;
2. `Testing` om appen har `appsettings.Testing.json`;
3. launch-profilens `ASPNETCORE_ENVIRONMENT`;
4. `Development`.

Det är den konfiguration utvecklarna redan kör appen med, till exempel signeringsnycklar, testinloggning och hastighetsgränser. Development läser user secrets lokalt. Värdpolicyn blockerar fortfarande utgående anrop.

`ThrowOnBadRequest` stängs av, så att appen svarar 400 på felaktiga anrop som i drift. Development slår på det som ett felsökningsstöd, och då blir 400 till 500.

**Appens lokala tjänster byts mot containrar.** Fyra slag stöds: PostgreSQL, SQL Server, Azure Storage (Azurite) och Redis. testgen letar efter konfigurationsvärden som pekar på en lokal instans och byter dem mot containerns.
- Avbildningen tas ur repots compose-fil när den har en tjänst av samma slag. Annars används en standardavbildning.
- Ett värde som pekar på en riktig värd byts aldrig ut, och testgen ansluter inte. Testerna fälls med ett meddelande om vad som ska anges.
- Saknas Docker blir testerna Inconclusive.

**Tjänsterna och databasen upptäcks utan appens startkod.** testgen bygger bara värden. Den stoppar ingångspunkten med `HostAbortedException` vid `HostBuilt`, som `dotnet ef` gör, och läser konfigurationen och `DbContextOptions` därifrån. Appar som migrerar eller kontrollerar sin konfiguration efter `Build()` kan därför upptäckas. Appen startas sedan en gång, med containrarnas värden.

**Startar appen inte, fälls testerna.** Ett fel i `OneTimeSetUp` fäller fixturens tester med ett meddelande i tre delar och utan stack. Annars blev en körning med VSTest grön utan att något testats. `apis|apps.<p>.settings` i testgen.yaml är sista utvägen för det som inte går att läsa ut.

**Testgen loggar in som en testanvändare.** Det är Microsofts mönster för integrationstester. När appen körs i testprocessen och anropet bär körningens slumpade token får det en testhanterare, oavsett schema: `IAuthenticationHandlerProvider` dekoreras, så inga scheman läggs till eller ändras. Claims är fiktiva, och kan ändras med `auth.claims`.
- För appar som själva slår upp användaren finns ett valbart inloggningssteg, `apis.<p>.login`. Det anropar appens egen inloggning och använder token den ger.
- Fall som appen inte godtar med testanvändaren blir HOPPAD (`test-user-rejected`) med vägledning, inte FEL.

**testgens egna filer ligger i testprojektet.** testgen.yaml och en hämtad spec hittas i testprojektet, och i solutionens katalog som förut. `testgen init` hämtar specen från appen när ingen finns (code-first) och skriver den i testprojektet.

**Specen som bygget skriver läses ur csproj.** Det gäller `OpenApiGenerateDocuments`, `OpenApiDocumentsDirectory` och `--file-name`. Standardvärdena, katalogen `obj` och filnamnet `<projekt>.json`, följer Microsofts dokumentation.

Ordningen för specen är:
1. testgen.yaml;
2. testprojektet;
3. vid projektet;
4. filen som byggs.

Jämförelsen med appens spec hoppas över när specen skrivs av bygget.

**Microsoft.Testing.Platform och aktuella testpaket.** `testgen init` läser `test.runner` i `global.json`. Med MTP får projektet `OutputType Exe` och `EnableNUnitRunner`, och kommandot blir `dotnet test --project`. NUnit-adaptern 5.0.0 kraschade `dotnet test` i SDK 10.0.4xx, så paketen är uppdaterade:
- NUnit 5.0.0;
- NUnit3TestAdapter 6.3.1;
- Microsoft.NET.Test.Sdk 18.11.0;
- NUnit.Analyzers 4.15.0.

**Inga falska fel för OpenAPI 3.1 och påhittade värden.**
- Alternativ som bara är `null` hoppas över i oneOf och anyOf.
- "Tom" testas bara när schemat förbjuder tom text.
- Microsoft.OpenApis null-markör i enum skickas aldrig.
- Ett dokumenterat 404 på ett påhittat värde blir HOPPAD (`missing-data`).
- Ett 429 respekterar `Retry-After` en gång och blir annars HOPPAD (`rate-limited`).

## Följder
- **En riktig app utan ändringar och utan testgen.yaml:** appen startar med `postgres:17-alpine` och `azurite:3.37.0` ur sin compose-fil. Det som faller är appens verkliga avvikelser från specen, plus ett anrop som kräver en riktig inloggningskod från en extern leverantör.
- **Webbappar** (`ViewTests`) får miljön och testanvändaren, men inga containrar än.
- **`testgen run` från kommandoraden** startar inga containrar; där är appen redan igång.
