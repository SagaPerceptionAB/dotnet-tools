# SagaPerception.SystemMap.Cli

Ritar en karta över en .NET-solution: vägen från varje ingång (controller, minimal API, Razor Page, Blazor-sida, bakgrundstjänst, konsolprogram, meddelandehanterare, jobb, gRPC eller SignalR) genom projekten ut till integrationerna.
Kartan visar vägar som är möjliga enligt koden, inte vilken väg ett visst anrop tog, och dämpat det som ingen ingång når.

## Installera

Krav: .NET 10 SDK. Paketet är en förhandsversion och finns ännu inte på nuget.org (bygg det med `dotnet pack`, se repots README), så `--prerelease` behövs. Installera som lokalt dotnet-verktyg:

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
| `--level` | `projects`, `classes` (standard), `methods`, `all` | Detaljnivå: projekten, klasserna, metoderna i klasserna eller allt. På `methods` ligger läsningar, skrivningar och meddelanden på pilarna till integrationerna; på `all` är de egna rader. Gäller med `--mermaid`. Webbsidan har samma nivåer (Projekt, Klasser, Funktioner, Allt). |
| `--direction` | `LR` (standard), `TB` | Vänster till höger eller uppifrån och ned. Gäller med `--mermaid`. |
| `--entry` | `"<anrop>"` | Bara vägen för ett anrop, t.ex. `"POST /orders"`. Gäller med `--mermaid`. |
| `--json` | `<fil>` | Skriv kartan som JSON. |
| `--hide` | `<namn>` | Dölj ett projekt, en klass eller en integration; går att upprepa. Se nedan. |
| `--no-unreached` | | Ta inte med projekt, klasser och metoder som ingen ingång når. Se nedan. |
| `--no-browser` | | Öppna inte webbläsaren. Görs aldrig när miljövariabeln `CI` är satt. |
| `--no-restore` | | Kör inte `dotnet restore`. Annars körs den om paketen inte redan är hämtade. |
| `--verbose` | | Visa tekniska detaljer när något går fel. |

Utan `--html`, `--mermaid` och `--json` skrivs kartan som en webbsida i en tillfällig mapp och öppnas i webbläsaren.

### Dölja delar

`--hide <namn>` tar bort ett projekt, en klass eller en integration från kartan, t.ex. `--hide OrderRepository --hide ShopDatabase`. Namnet skrivs som i kartan, utan hänsyn till stora och små bokstäver; en klass går också att skriva med namnrymd (`Orders.OrderService`). Passar ett namn på flera delar (t.ex. ett projekt och en integration som heter likadant) får man veta vilka, och skriver `projekt:Namn`, `klass:Namn`, `integration:Namn` eller `Projekt/Klass`. Ett namn som inte finns ger fel (exitkod `2`) med de närmaste namnen, innan något skrivs.

- Ett dolt projekt eller en dold klass tas bort med sina metoder, men vägarna genom dem består: anropar A en dold klass som anropar B ritas A → B, med de anrop som går hela vägen.
- En dold integration tas bort med sina läsningar, skrivningar och meddelanden och pilarna dit.
- Anrop (ingångarna) går inte att dölja.
- Texten på stdout, `--mermaid` och `--json` blir utan de dolda delarna, och säger vilka som är dolda (en rad `Dolda delar:` i texten och Mermaid-filen, och listan `hidden` med id:n i JSON). JSON-filen har samma form som annars (`schemaVersion`, sorterade listor): `entryPoints`, `integrations`, `apps` och `overview` är filtrerade, men ett anrops egen `handler` står kvar, och `file` för en integration anges där den anropas.
- `--html` får hela kartan, med delarna avbockade i **Visa** i sidan, så att den som läser kan bocka i dem igen. Det en läsare bockar av i sidan sparas i webbläsaren och gäller före `--hide` nästa gång sidan öppnas i samma flik. Exportera som Mermaid i sidan följer inte det som är dolt; skapa filen med `--mermaid` och `--hide`.

Kartan skrivs som text på stdout. Förlopp, varningar och bekräftelser (`Skrev karta.html (webbsida)`) skrivs på stderr.

Exitkoder: `0` klart, `2` fel (t.ex. att solutionen eller specen inte finns eller inte går att läsa), `70` internt fel, `130` avbrutet med Ctrl+C.

JSON-filen börjar med `schemaVersion` (nu 3). null utelämnas, listorna är sorterade och filen innehåller inga sökvägar eller tider. Sedan version 2 (fälten på engelska)
har version 3 att ingångar som inte är HTTP saknar `method` och `path` (och har `name`), att delar i `overview` som ingen ingång når har `reached: false`, och att ett HTTP-anrop vars adress varken står i koden eller i konfigurationen (den bestäms vid körning) har `address: "runtime"` i steget och i översiktens rad, medan `operation` och `name` bara är verbet.

## I en app

Kartan kan också visas i en ASP.NET Core-app, som Swagger UI, med `app.MapSystemMap();` (den ligger i namnrymden
`Microsoft.AspNetCore.Builder`, så ingen extra `using` behövs). Installation och
konfiguration står i [SagaPerception.SystemMap.AspNetCore](https://github.com/SagaPerceptionAB/dotnet-tools/blob/main/systemmap/src/SystemMap.AspNetCore/README.md).

### Det som ingen ingång når

Alla projekt (utom testprojekt) och alla klasser med metoder som har kod finns på kartan, också de som ingen ingång når. De ritas dämpade och streckade
(`reached: false` i JSON), med förklaringen "Nås inte från någon ingång" i sidan, och i texten som en lista sist ("Nås inte från någon ingång:", per
projekt). På nivåerna Funktioner och Allt syns också metoderna i en klass som nås men som ingen anropar. I sidan har **Visa** valet
"Visa delar som inte nås" (från början ibockat, sparas i webbläsaren som det man bockar av); `--no-unreached` tar bort dem ur text, Mermaid och JSON och
avbockar valet i `--html`. `--hide` fungerar på dem som på andra projekt och klasser.

Vad som räknas som nått: det som en ingång anropar, direkt eller genom hjälpmetoder som kartan inte ritar, och det som körs utan att en ingång anropar det:
konstruktorer, fält- och egenskapsvärden och egenskaper hos klasser som används, metoder som skickas vidare som värden (`list.Select(Load)`), och det som
anropas av ramverket eller uppstarten. Det senare är metoder som skriver över eller implementerar något utanför solutionen (`Dispose`,
`IRequestHandler.Handle`, `IHealthCheck.CheckHealthAsync` ...), middleware (`Invoke`, `InvokeAsync`), metoder som tar eller ger ramverkets typer för att
sätta upp appen (`IServiceCollection`, `IApplicationBuilder`, `WebApplication` ...), `Main` och Blazor-komponenter. Anropen från dem följs för att se vad
de når, men de syns inte som ingångar och påverkar inte integrationerna på kartan.

Vad som inte räknas alls: klasser som bara är data (enum, interface, record och klasser med bara egenskaper och fält), klasser med bara en konstruktor,
genererad kod (`obj`, `.g.cs`, `[GeneratedCode]`), `Program` och `Startup` i en webbapp samt själva uppstartsmetoderna. Ett projekt med bara sådant syns
men sägs inte vara onått. Gränser: anrop som bara går genom reflektion, händelser, dynamisk dispatch eller längre än åtta anrop från ingången syns inte, och
en klass som nås bara via hjälpmetoder som kartan inte ritar ritas inte alls (inte heller dess metoder som inte nås).

## Vad som känns igen

Ingångar: controllers, minimal API (`MapGet`, `MapPost` ..., med `MapGroup`), Razor Pages och Blazor-sidor (`@page`), och det som inte är HTTP:

| Sort i JSON | Vad | Känns igen på | Namn på kartan |
|---|---|---|---|
| `controller`, `minimal-api`, `razor-page`, `blazor-page`, `mvc-view` | HTTP-anrop | som ovan | `POST /orders` |
| `background` | bakgrundstjänst, i en worker eller i en webbapp | klass som ärver `BackgroundService` (`ExecuteAsync`) eller implementerar `IHostedService` (`StartAsync`, och `StopAsync` och `IHostedLifecycleService` om klassen skriver egna) | `Bakgrund: OrderCleanupWorker` |
| `console` | konsolprogram | `Main` eller satserna på översta nivån i ett projekt som inte är en webbapp, inte sätter upp en värd (`Host.CreateApplicationBuilder` ...) och inte har några andra ingångar | `Konsol: Tool` |
| `consumer` | meddelandehanterare | MassTransit `IConsumer<T>` (`Consume`), NServiceBus `IHandleMessages<T>` (`Handle`), Azure Service Bus `ServiceBusProcessor.ProcessMessageAsync += hanterare` (och sessionsvarianten) | `Meddelande: OrderPlaced → OrderPlacedConsumer`, `Meddelande: order-lagd → Receiver.HandleAsync` |
| `job` | jobb | Hangfire `RecurringJob.AddOrUpdate(...)`, `BackgroundJob.Enqueue(...)` och `Schedule` (metoden i uttrycket; genom ett gränssnitt klasserna som implementerar det), Quartz `IJob` (`Execute`) | `Jobb: NightlyReportJob`, `Jobb: nattrapport → IReportService.RunAsync` |
| `grpc` | gRPC-tjänst | klass som ärver en genererad bas med `[BindServiceMethod]`, eller mappas med `MapGrpcService<T>`; en ingång per metod som skrivs över | `gRPC: Greeter/SayHello` |
| `signalr` | SignalR-hubb | klass som ärver `Hub` eller `Hub<T>`; en ingång per publik metod | `SignalR: ChatHub.Send` |

Typerna känns igen på namnrymd och namn, så paketen behöver inte finnas för kartan. MediatR (`INotificationHandler`, `IRequestHandler`) är inte med: det anropas inom processen och nås från den som anropar det. Azure Functions känns inte igen.
En ingång som inte är HTTP har `name` i JSON i stället för `method`, `path` och `codePath`, ingår aldrig i jämförelsen med `--spec` och
väljs i sidan under sin sort (Bakgrundstjänster, Konsolprogram, Meddelanden, Jobb, gRPC, SignalR) efter HTTP-anropen. `--entry` tar namnet, t.ex. `--entry "Bakgrund: OrderCleanupWorker"`.

Fakta per app (JSON `apps`): sort (`api`, `web`, `worker`, `console`), hälsovägar, databaser (leverantör, DbContext, nyckel för anslutningen) och
utgående HTTP-klienter med projektet i solutionen de pekar på (namnet ur nyckeln, annars porten i `appsettings*.json`). Ett program som körs
(exe) är en app när det har bakgrundsarbete eller är ett konsolprogram; en webbapp eller en värd får `worker` också av tjänster i projekten den använder.

Integrationer: databas (EF Core, Dapper, SQL), andra API:er (HTTP, Refit), WCF/SOAP, köer (Service Bus, MassTransit, RabbitMQ, Kafka,
Azure Storage Queue), lagring (Blob, filer) och e-post (SMTP).

Lagring: Azure Blob (`BlobClient`, `BlobContainerClient`, `BlobServiceClient`, med och utan `Async`) och filsystemet (`File`, `Directory`, `FileInfo`,
`DirectoryInfo`, `new FileStream`, och egenskaperna `Exists` och `Length` på `FileInfo` och `DirectoryInfo`). Operationen väljs efter metodens namn: *finns*
(`Exists`), *läsa*/*hämta*, *skriva*/*ladda upp*, *ta bort* (`Delete`, `DeleteIfExists`), *lista* (`GetBlobs`, `EnumerateFiles`, `GetFiles`), *skapa*
(`CreateIfNotExists`, `CreateDirectory`), *läsa egenskaper* (`GetProperties`), *ändra egenskaper* (`SetMetadata`), *kopiera* (blob) och *flytta eller
kopiera* (fil). Metoder som inte rör lagringen (`Path.Combine`, `GenerateSasUri`, `Directory.GetCurrentDirectory`) är inga integrationer.

OIDC-discovery är HTTP ut ur systemet: `ConfigurationManager<OpenIdConnectConfiguration>.GetConfigurationAsync` och `RequestRefresh` (också genom
`IConfigurationManager<T>`) hämtar discovery-dokumentet och sedan nycklarna (JWKS), och `OpenIdConnectConfigurationRetriever.GetAsync` hämtar
dokumentet. Integrationen heter som värden i adressen (`appleid.apple.com`) och tekniken är "HTTP (OIDC-konfiguration och nycklar)"; operationen är
`GET` med dokumentets adress. Kommer adressen ur konfigurationen heter integrationen som nyckeln, som för andra HTTP-anrop. Adressen läses i
`new ConfigurationManager<T>(adress, ...)`, och kartan följer hanteraren dit: lokala variabler, fält och egenskaper, parametrar (till argumenten hos
anroparen), konstruktorparametrar (till `new` av klassen) och metoder i solutionen som returnerar hanteraren. Går den inte att följa, eller ger två
vägar olika adresser, är adressen okänd och anropet får `address: "runtime"` som andra HTTP-anrop. `HttpDocumentRetriever.GetDocumentAsync` är en vanlig HTTP-hämtning.

## Säkerhet

Projektfilerna läses med MSBuild och `dotnet restore` kan köras. Kör bara på kod du litar på.

## Licenser

Webbsidan innehåller Cytoscape.js och fcose (MIT). Licensfilerna ligger i paketets `licenses/`.
