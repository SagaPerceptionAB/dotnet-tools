# dotnet-tools

SagaPerception: .NET-verktyg med öppen källkod (MIT). Allt byggs och paketeras var för sig:

| | Vad det gör | Paket |
|---|---|---|
| [**testgen**](testgen/README.md) | API- och UI-tester i `dotnet test`, ur OpenAPI-specen och appens källkod. Mockar backend ur specen, kör tester mot ett API från kommandoraden, visar resultatet i en webbläsare och som rapport. Egna tester skrivs i Excel. | `SagaPerception.TestGen.Cli` (kommandot `testgen`), `SagaPerception.Testing.NUnit` |
| [**systemmap**](systemmap/README.md) | Ritar en karta över en .NET-solution: vägen från varje anrop genom projekten ut till databaser, andra system, köer, lagring och e-post. Som nätverk i webbläsaren, Mermaid, JSON och text. | `SagaPerception.SystemMap.Cli` (kommandot `systemmap`) |
| [**Testing**](testing/src/SagaPerception.Testing/README.md) | Testhjälp för ASP.NET Core-tjänster, oberoende av testramverk: `ApiFactory` och fångst av spår och loggar. testgens NUnit-paket bygger på den. | `SagaPerception.Testing` |

testgen visar också systemkartan (`testgen map`, kartan i webbgränssnittet och vägen genom koden i varje test). Det gör den
genom att använda systemkartans bibliotek; systemkartan vet inget om testgen.

Systemkartan kan också visas i en ASP.NET Core-app, som Swagger UI: `dotnet add package SagaPerception.SystemMap.AspNetCore --prerelease` och
`app.MapSystemMap();` (se [systemmap/README.md](systemmap/README.md#i-appen-som-swagger)).

Säkerhet i korthet (vad verktygen skyddar och vad man bör tänka på) står i [SECURITY.md](SECURITY.md).

## Installera

Paketen ges ut till nuget.org som förhandsversioner (`0.1.0-preview.1`). Därför behövs `--prerelease`. Du behöver .NET 10 SDK.

```
dotnet tool install -g SagaPerception.TestGen.Cli --prerelease
dotnet tool install -g SagaPerception.SystemMap.Cli --prerelease
```

Eller som lokala verktyg i ett projekt, med ett verktygsmanifest:

```
dotnet new tool-manifest
dotnet tool install SagaPerception.TestGen.Cli --prerelease
dotnet tool install SagaPerception.SystemMap.Cli --prerelease
dotnet tool restore
```

Kommandona är `testgen` och `systemmap` (lokalt: `dotnet testgen`, `dotnet systemmap`).

## Testa en solution

I en solution med ett API eller en webbapp:

```
testgen            # vad som finns i repot och nästa steg
testgen init       # skapar tests/<Solution>.Tests, views.json och installerar Chromium
dotnet test
```

Mer i [testgen/README.md](testgen/README.md).

## Licens

[MIT](LICENSE), Copyright (c) 2026 SagaPerception AB. Verktygen innehåller JavaScript-bibliotek och NuGet-beroenden under egna
licenser, se [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Mapparna

```
SagaPerception.Tools.slnx  alla projekt i en solution; komponenterna har också egna solutions
eng/              Versions.props (paketens versioner, se "Släppa en ny version") och check-expected-failures.sh
Directory.Build.props, Directory.Build.targets, Directory.Packages.props, global.json   gemensamma inställningar, paketversioner och SDK
testgen/          testgen: src, tests, docs, testgen.slnx och README
systemmap/        systemkartan: src, tests, SystemMap.slnx och README
testing/          SagaPerception.Testing (src, tests) och Testing.slnx
shared/           CommandLine: flaggorna, felen på enkel svenska och exitkoderna som verktygen använder
samples/          exemplet Shop (ShopApi, ShopWeb, Shop.Tests och OpenAPI-specar) som testgen och systemmap provas mot
tests/            Repository.Tests: arkitekturreglerna för hela repot
docs/adr/         besluten bakom verktygen
.github/workflows ci.yml (bygge, test, kodanalys och paketprov) och release.yml (ger ut paket vid tagg)
testgen.sh        startar testgen från källkoden (Windows: testgen.cmd)
```

## Prova från källkoden

Klona repot. Alla kommandon körs från den här mappen.

```
dotnet test samples/Shop.Tests                              # API- och UI-testerna mot ShopApi och ShopWeb (två fel är avsiktliga)
./testgen.sh web --spec samples/shop.openapi.yaml           # webbgränssnittet; exempel-API:et: dotnet run --project samples/ShopApi
dotnet run --project systemmap/src/SystemMap.Cli -- --solution samples/Shop.slnx
```

## Bygga, testa och paketera

```
dotnet build SagaPerception.Tools.slnx                 # allt, utan varningar (varningar är fel)
dotnet test shared/Shared.slnx
dotnet test testgen/testgen.slnx
dotnet test systemmap/SystemMap.slnx
dotnet test testing/Testing.slnx
dotnet test tests/Repository.Tests

dotnet pack testgen/src/TestGen.Cli -c Release -o nupkg
dotnet pack systemmap/src/SystemMap.Cli -c Release -o nupkg
dotnet pack systemmap/src/SystemMap.AspNetCore -c Release -o nupkg
dotnet pack testing/src/SagaPerception.Testing -c Release -o nupkg
```

UI-testerna kör Chromium. Installera den en gång med `playwright.ps1 install chromium` i ett testprojekts `bin`-mapp.

`SagaPerception.Testing.NUnit` och dess beroenden (`SagaPerception.TestGen.Core`, `.Excel`, `.Mock`, `.Browser`, `SagaPerception.CommandLine`) packas på samma sätt, se [release.yml](.github/workflows/release.yml).
Paketens versioner kommer från [eng/Versions.props](eng/Versions.props); `-p:Version` behövs inte.

## Släppa en ny version

Versionerna står på ett ställe: [eng/Versions.props](eng/Versions.props). Varje paketprojekt läser sin version därifrån.

1. Höj versionen i `eng/Versions.props` i en PR och merga den.
2. Tagga: `<komponent>/v<version>` (semver). [release.yml](.github/workflows/release.yml) avbryter om taggen inte är samma version som i
   `eng/Versions.props`, och annars testar komponenten, paketerar och publicerar till nuget.org.

| Tagg | Paket |
|---|---|
| `testing/v0.1.0-preview.1` | `SagaPerception.Testing` |
| `testgen/v0.1.0-preview.1` | `SagaPerception.TestGen.Cli`, `SagaPerception.Testing.NUnit`, `SagaPerception.TestGen.Core`, `.Excel`, `.Mock`, `.Browser` och `SagaPerception.CommandLine` (samma version). `SagaPerception.Testing` följer med men har sin egen version. |
| `systemmap/v0.1.0-preview.1` | `SagaPerception.SystemMap.Cli` och `SagaPerception.SystemMap.AspNetCore` (samma version) |

Ge ut `testing` först, eftersom `SagaPerception.Testing.NUnit` beror på den versionen.

```
git tag testing/v0.1.0-preview.1
git push origin testing/v0.1.0-preview.1
```

Publiceringen använder trusted publishing mot nuget.org (GitHub OIDC), så det finns ingen API-nyckel i repot.

En publicerad version kan inte tas bort från nuget.org, bara avlistas. Kontrollera därför versionen innan du taggar.
