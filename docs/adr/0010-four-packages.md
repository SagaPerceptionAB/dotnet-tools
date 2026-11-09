# 0010. Fyra paket

## Status
Antagen 2026-11-09. Ersätter paketavsnittet i 0007.

## Beslut
Repot ger ut fyra paket:
- `SagaPerception.TestGen.Cli` (verktyget `testgen`)
- `SagaPerception.TestGen.NUnit`
- `SagaPerception.SystemMap.Cli` (verktyget `systemmap`)
- `SagaPerception.SystemMap.AspNetCore`

I `SagaPerception.TestGen.NUnit` är modulerna `TestGen.Core`, `Excel`, `Mock`, `Browser` och `SagaPerception.CommandLine` inbakade som DLL:er. De är egna projekt men inga egna paket, eftersom `dotnet pack` annars gör varje projektreferens till ett eget paket. Modulgränserna finns kvar som projekt och i arkitekturtesterna. Användarens kod behöver bara `ApiTests` och `ViewTests`.

`SagaPerception.Testing` är borttaget. Det fanns bara för att NUnit-paketet återanvände dess `ApiFactory`. En nedbantad intern kopia finns nu i `testgen/src/SagaPerception.TestGen.NUnit/AppHost/`.

`CommandLine` heter `SagaPerception.CommandLine`, för att inte krocka med paketet CommandLineParser i testprocessen.

Verktygen känner inte till något särskilt bibliotek eller någon särskild mall. `MapSagaPerceptionHealthChecks` och inställningen `Observability:OtlpEndpoint` är borttagna. Standardvariabeln `OTEL_EXPORTER_OTLP_ENDPOINT` gäller fortfarande.

## Publicering
Paketen ges ut till GitHub Packages tills vidare och är privata. Att göra ett paket publikt går inte att ångra. nuget.org kommer senare, och då blir paket-ID:n permanenta.

## NUnit är ett uttalat val
- NUnit kom med i det första skelettet och valdes aldrig aktivt.
- Bindningen är liten. Motorn använder inte NUnit, och ungefär 40 rader i NUnit-paketet gör det.
- `Inconclusive` skiljer "osäkert" från "fel".
- `testgen init` skapar ett eget testprojekt, så användaren skriver ingen NUnit-kod. Projektet kan ligga bredvid xUnit- och MSTest-projekt.
- Namnet `TestGen.NUnit` lämnar plats för ett `TestGen.Xunit` om någon ber om det.
