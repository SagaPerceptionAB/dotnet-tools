# SagaPerception.SystemMap.Cli

Ritar en karta över en .NET-solution: vägen från varje anrop (controller-action) genom projekten ut till integrationerna.
Kartan visar vägar som är möjliga enligt koden, inte vilken väg ett visst anrop tog.

## Installera

Krav: .NET 10 SDK. Paketet ges ut som förhandsversion på nuget.org, så `--prerelease` behövs. Installera som lokalt dotnet-verktyg:

```
dotnet new tool-manifest
dotnet tool install SagaPerception.SystemMap.Cli --prerelease
dotnet tool restore
```

Eller globalt:

```
dotnet tool install --global SagaPerception.SystemMap.Cli --prerelease
```

## Kom igång

```
dotnet systemmap
```

Verktyget hittar solutionen i den här mappen eller närmast ovanför, läser koden och öppnar kartan i webbläsaren.

```
dotnet systemmap --html docs/systemkarta.html
dotnet systemmap --mermaid docs/order.mmd --direction TB --entry "POST /orders"
dotnet systemmap --spec https://example.com/openapi.json
```

## Flaggor

| Flagga | Värde | Betydelse |
|---|---|---|
| `--solution` | `<fil>` | Solution (`.sln`, `.slnx`) eller projekt (`.csproj`). Utan flaggan: solutionen i mappen eller närmast ovanför (till och med repots rot), annars närmaste projekt. |
| `--spec` | `<fil\|url>` | OpenAPI-spec. Kopplar anropen till specens operationer och listar anrop som bara finns i koden eller bara i specen. |
| `--html` | `<fil>` | Skriv kartan som webbsida. Sidan hämtar inget utifrån. |
| `--mermaid` | `<fil>` | Skriv kartan som Mermaid-flödesschema. |
| `--level` | `projects`, `classes` (standard), `all` | Detaljnivå. Gäller med `--mermaid`. |
| `--direction` | `LR` (standard), `TB` | Vänster till höger eller uppifrån och ned. Gäller med `--mermaid`. |
| `--entry` | `"<anrop>"` | Bara vägen för ett anrop, t.ex. `"POST /orders"`. Gäller med `--mermaid`. |
| `--json` | `<fil>` | Skriv kartan som JSON. |
| `--no-browser` | | Öppna inte webbläsaren. Görs aldrig när miljövariabeln `CI` är satt. |
| `--no-restore` | | Kör inte `dotnet restore`. Annars körs den om paketen inte redan är hämtade. |
| `--verbose` | | Visa tekniska detaljer när något går fel. |

Utan `--html`, `--mermaid` och `--json` skrivs kartan som en webbsida i en tillfällig mapp och öppnas i webbläsaren.

Exitkoder: `0` klart, `2` fel (t.ex. att solutionen eller specen inte finns eller inte går att läsa).

## I en app

Kartan kan också visas i en ASP.NET Core-app, som Swagger UI, med `app.MapSystemMap();`. Installation och
konfiguration står i [SagaPerception.SystemMap.AspNetCore](https://github.com/SagaPerceptionAB/dotnet-tools/blob/main/systemmap/src/SystemMap.AspNetCore/README.md).

## Vad som känns igen

Databas (EF Core, Dapper, SQL), andra API:er (HTTP, Refit), WCF/SOAP, köer (Service Bus, MassTransit, RabbitMQ, Kafka,
Azure Storage Queue), lagring (Blob, filer) och e-post (SMTP).

## Säkerhet

Projektfilerna läses med MSBuild och `dotnet restore` kan köras. Kör bara på kod du litar på.

## Licenser

Webbsidan innehåller Cytoscape.js och fcose (MIT). Licensfilerna ligger i paketets `licenses/`.
