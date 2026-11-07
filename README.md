# dotnet-tools

SagaPerception: tre fristående .NET-verktyg med öppen källkod (MIT). Allt byggs och paketeras var för sig:

| | Vad det gör | Paket |
|---|---|---|
| [**testgen**](testgen/README.md) | Testar ett API utifrån dess OpenAPI-spec: hittar på testdata, kör testerna och visar resultatet i terminalen, i en webbläsare och som rapport. Egna tester skrivs i Excel. | `SagaPerception.TestGen.Cli` (kommandot `testgen`) |
| [**systemmap**](systemmap/README.md) | Ritar en karta över en .NET-solution: vägen från varje anrop genom projekten ut till databaser, andra system, köer, lagring och e-post. Som nätverk i webbläsaren, Mermaid, JSON och text. | `SagaPerception.SystemMap.Cli` (kommandot `systemmap`) |
| [**uitestgen**](uitestgen/README.md) | Hittar vyerna i en webbapp (Blazor, Razor Pages, MVC, React, Angular, Vue) och skriver Playwright-tester i C# (NUnit): smoke, formulär, navigering och skärmdumpar, med mockad backend ur OpenAPI-specen. | `SagaPerception.UiTestGen.Cli` (kommandot `uitestgen`) |

testgen visar också systemkartan (`testgen map`, kartan i webbgränssnittet och vägen genom koden i varje test). Det gör den
genom att använda systemkartans bibliotek; systemkartan vet inget om testgen.

Systemkartan kan också visas i en ASP.NET Core-app, som Swagger UI: `dotnet add package SagaPerception.SystemMap.AspNetCore --prerelease` och
`app.MapSystemMap();` (se [systemmap/README.md](systemmap/README.md#i-appen-som-swagger)).

Säkerhet i korthet (vad verktygen skyddar och vad man bör tänka på) står i [SECURITY.md](SECURITY.md).

## Installera

Paketen ligger på nuget.org och ges än så länge ut som förhandsversioner (`0.1.0-preview.1`). Därför behövs `--prerelease`. Du behöver .NET 10 SDK.

```
dotnet tool install -g SagaPerception.TestGen.Cli --prerelease
dotnet tool install -g SagaPerception.SystemMap.Cli --prerelease
dotnet tool install -g SagaPerception.UiTestGen.Cli --prerelease
```

Eller som lokala verktyg i ett projekt, med ett verktygsmanifest:

```
dotnet new tool-manifest
dotnet tool install SagaPerception.TestGen.Cli --prerelease
dotnet tool install SagaPerception.SystemMap.Cli --prerelease
dotnet tool install SagaPerception.UiTestGen.Cli --prerelease
dotnet tool restore
```

Kommandona är `testgen`, `systemmap` och `uitestgen` (lokalt: `dotnet testgen`, `dotnet systemmap`, `dotnet uitestgen`).

## Licens

[MIT](LICENSE), Copyright (c) 2026 SagaPerception AB. Systemkartans webbsida innehåller några JavaScript-bibliotek under egna
MIT-licenser, se [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Mapparna

```
SagaPerception.Tools.slnx  alla projekt i en solution; komponenterna har också egna solutions
eng/              Versions.props: paketens versioner (se "Släppa en ny version")
Directory.Build.props, Directory.Build.targets, Directory.Packages.props, global.json   gemensamma inställningar, paketversioner och SDK
testgen/          testgen: src, tests, docs, testgen.slnx och README
systemmap/        systemkartan: src, tests, SystemMap.slnx och README
uitestgen/        uitestgen: src (UiTestGen, UiTestGen.Cli), tests, UiTestGen.slnx och README
shared/           CommandLine: flaggorna, felen på enkel svenska och exitkoderna som verktygen använder
samples/          exempel-API:et Shop (tre projekt och en OpenAPI-spec) som testgen och systemmap provas mot,
                  ShopWeb (Blazor-app med en Razor Page och formulär) och spa-fixtures (källträd för React, Angular och Vue) för uitestgen
testgen.sh        startar testgen från källkoden (Windows: testgen.cmd)
```

## Kom igång

Du behöver .NET 10 SDK. Alla kommandon körs från den här mappen.

```
dotnet run --project samples/ShopApi                       # exempel-API:et på port 5080, i ett eget fönster
./testgen.sh ui --spec samples/shop.openapi.yaml --excel samples/tester.xlsx --solution samples/Shop.slnx
dotnet run --project systemmap/src/SystemMap.Cli -- --solution samples/Shop.slnx
dotnet run --project uitestgen/src/UiTestGen.Cli -- --solution samples/ShopWeb/ShopWeb.csproj --spec samples/shop.openapi.yaml --out tests/Shop.UiTests
```

Mer om varje verktyg står i [testgen/README.md](testgen/README.md), [systemmap/README.md](systemmap/README.md) och [uitestgen/README.md](uitestgen/README.md). Uitestgens rad ovan skriver testerna för ShopWeb; hur de körs står under "Kom igång" i dess README.

## Bygga, testa och paketera

```
dotnet build SagaPerception.Tools.slnx                 # allt, utan varningar (varningar är fel)
dotnet test testgen/testgen.slnx
dotnet test systemmap/SystemMap.slnx
dotnet test uitestgen/UiTestGen.slnx

dotnet pack testgen/src/TestGen.Cli -c Release -o nupkg
dotnet pack systemmap/src/SystemMap.Cli -c Release -o nupkg
dotnet pack systemmap/src/SystemMap.AspNetCore -c Release -o nupkg
dotnet pack uitestgen/src/UiTestGen.Cli -c Release -o nupkg
```

Paketens versioner kommer från [eng/Versions.props](eng/Versions.props); `-p:Version` behövs inte.

## Släppa en ny version

Versionerna står på ett ställe: [eng/Versions.props](eng/Versions.props). Varje paketprojekt läser sin version därifrån.

1. Höj versionen i `eng/Versions.props` i en PR och merga den.
2. Tagga: `<komponent>/v<version>` (semver). [release.yml](.github/workflows/release.yml) avbryter om taggen inte är samma version som i
   `eng/Versions.props`, och annars testar komponenten, paketerar och publicerar till nuget.org.

| Tagg | Paket |
|---|---|
| `testgen/v0.1.0-preview.1` | `SagaPerception.TestGen.Cli` |
| `systemmap/v0.1.0-preview.1` | `SagaPerception.SystemMap.Cli` och `SagaPerception.SystemMap.AspNetCore` (samma version) |
| `uitestgen/v0.1.0-preview.1` | `SagaPerception.UiTestGen.Cli` |

```
git tag testgen/v0.1.0-preview.1
git push origin testgen/v0.1.0-preview.1
```

Publiceringen använder trusted publishing mot nuget.org (GitHub OIDC), så det finns ingen API-nyckel i repot.

En publicerad version kan inte tas bort från nuget.org, bara avlistas. Kontrollera därför versionen innan du taggar.
