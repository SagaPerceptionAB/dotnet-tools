# Säkerhet i korthet

En översikt över vad verktygen och paketen gör ur säkerhetssynpunkt och vad man bör tänka på.

## Rapportera en säkerhetsbrist

Skapa inget öppet issue. Använd i stället GitHubs privata säkerhetsrapport: fliken *Security* i repot, sedan *Report a vulnerability*.

## Bra som det är

| Område | Vad som skyddar |
|---|---|
| **testgen ui** | Lyssnar bara på `127.0.0.1`. Kräver en sessionsnyckel (`X-Session-Key`) på alla `/api`-anrop. Avvisar andra `Host` än localhost, som skydd mot DNS rebinding. Token hålls bara i minnet och loggas aldrig. |
| **testgens rapporter** | `Authorization`, `Cookie`, `Set-Cookie` och headers som ser hemliga ut (key, token, secret med flera) maskeras. Kända hemliga värden döljs också i svarstexter (`HeaderMask`). Token kan läsas från miljövariabler (`${env:...}`) i stället för att skrivas i filer. |
| **Systemkartan i appen** | Visas bara i Development, eller om adressen kräver inloggning (`.RequireAuthorization()`). Sidan hämtar inget utifrån, eftersom Cytoscape följer med i paketet. |
| **uitestgen** | Upptäckten läser filerna som text och kör aldrig koden, inte heller MSBuild eller `dotnet restore`. Den genererade stub-servern lyssnar bara på localhost. |
| **Telemetri** | Inga av beroendena skickar telemetri. .NET SDK:ns telemetri stängs av i verktygen, och allt de startar ärver det. Den stängs också av i CI. |
| **Workflows** | Minsta behörighet: `contents: read`. Release publicerar till nuget.org med trusted publishing (GitHub OIDC), så ingen API-nyckel ligger i repot. |

## Tänk på

- **systemmap kör kod från solutionen den läser.** `dotnet restore` och Roslyns MSBuild-läsning utvärderar projektfilerna, och därmed deras MSBuild-mål och paket. Kör den bara på kod du litar på, ungefär som `dotnet build`.
- **testgen skickar riktiga anrop**, även negativa fall och påhittad data som kan skapa poster. Kör bara mot test- och utvecklingsmiljöer, aldrig mot produktion.
- **uitestgens sondering och tester använder appen på riktigt.** `--app` öppnar varje sida i en webbläsare och följer länkar inom samma adress (inte utloggning). De genererade formulärtesterna skickar riktiga formulär. Med riktig backend skapar testerna i kategorin `Muterar` data. Kör bara mot test- och utvecklingsmiljöer.
- **Release:** den som får pusha taggar kan ge ut paket. Skydda taggmönstren (`*/v*`) med tag protection rules eller rulesets.
- **Actions** pekar på versionstaggar (`@v4`), inte commit-SHA. Lås till SHA om ni vill skydda er mot att en action byts ut uppströms.
