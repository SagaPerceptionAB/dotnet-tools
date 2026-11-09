# Säkerhet i korthet

En översikt över vad verktygen och paketen gör ur säkerhetssynpunkt och vad man bör tänka på.

## Rapportera en säkerhetsbrist

Skapa inget öppet issue. Använd i stället GitHubs privata säkerhetsrapport: fliken *Security* i repot, sedan *Report a vulnerability*.

## Bra som det är

| Område | Vad som skyddar |
|---|---|
| **testgen web** | Lyssnar bara på `127.0.0.1`. Kräver en sessionsnyckel (`X-Session-Key`) på alla `/api`-anrop. Avvisar andra `Host` än localhost, som skydd mot DNS rebinding. Token hålls bara i minnet och loggas aldrig. Resultatfiler läses bara ur `TestResults/testgen` i solutionens testprojekt (högst 20 MB, inga länkar); sidan får ogenomskinliga id, aldrig en sökväg. |
| **testgens rapporter** | `Authorization`, `Cookie`, `Set-Cookie` och headers som ser hemliga ut (key, token, secret med flera) maskeras. Kända hemliga värden döljs också i svarstexter (`HeaderMask`). Token kan läsas från miljövariabler (`${env:...}`) i stället för att skrivas i filer. |
| **Systemkartan i appen** | Visas bara i Development, eller om adressen kräver inloggning (`.RequireAuthorization()`). Sidan hämtar inget utifrån, eftersom Cytoscape följer med i paketet. |
| **API- och UI-tester i `dotnet test`** | `ApiTests` och `ViewTests` startar appen och mocken i testprocessen, bara på localhost (port 0). Värdpolicyn blockerar appens utgående anrop som standard (`hosts` i `testgen.yaml`). Playwrights spårfiler är avstängda som standard, eftersom de kan innehålla känsliga uppgifter. Testdatan är fiktiv (Skatteverkets testpersonnummer, `example.com`, PTS fiktiva nummer). Resultatfilerna läses bara ur testprojekten. |
| **Telemetri** | Inga av beroendena skickar telemetri. .NET SDK:ns telemetri stängs av i verktygen, och allt de startar ärver det. Den stängs också av i CI. |
| **Workflows** | Minsta behörighet: `contents: read`. Release (`packages: write`) publicerar till organisationens privata GitHub Packages med jobbets `GITHUB_TOKEN`, så ingen API-nyckel ligger i repot. Actions är låsta till commit-SHA. CodeQL och NuGetAudit körs i CI. |

## Tänk på

- **systemmap, och testgen när den visar kartan, kör kod från solutionen de läser.** `dotnet restore` och Roslyns MSBuild-läsning utvärderar projektfilerna, och därmed deras MSBuild-mål och paket. Verktygen litar alltså på repot de körs i, precis som `dotnet build`. Kör dem bara på kod du litar på.
- **`testgen run` och miljöer med adress skickar riktiga anrop**, även negativa fall och påhittad data som kan skapa poster. Kör bara mot test- och utvecklingsmiljöer, aldrig mot produktion.
- **`testgen views --probe` kör appen på riktigt.** Sonderingen startar appen (API:erna mockade) och öppnar varje sida i en webbläsare, och följer länkar inom samma adress (inte utloggning). UI-testerna skickar formulär, men mot en mock av API:et. Med `apps.<projekt>.url` i `testgen.yaml` testas en riktig adress i stället: använd det bara i test- och utvecklingsmiljöer. En miljö med `readOnly: true` skickar inga formulär.
- **Release:** den som får pusha taggar kan ge ut paket. Skydda taggmönstret (`*/v*`) med tag protection rules eller rulesets.
