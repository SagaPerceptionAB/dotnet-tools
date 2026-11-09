# 0006. API och webbapp körs i testprocessen

## Status
Antagen 2026-11-08 efter en spik på Windows. Spikens kod sparades inte. Linux verifieras i CI när `ApiTests` och `ViewTests` byggs (fas 7–8).

## Beslut
`dotnet test` startar allt själv, utan manuella steg:
- API:et körs i `TestServer` (`WebApplicationFactory`).
- Webbappen körs på Kestrel, port 0, så att Playwright når den.
- Appens API-anrop går till en mock-server (egen `WebApplication`, port 0) i testprocessen.

```csharp
// Appen pekas ut med projektnamn; båda har Program i globala namnrymden, så typen kan inte skrivas ut.
var entry = Assembly.Load("ShopWeb").EntryPoint!.DeclaringType!;
var f = (IAppFactory)Activator.CreateInstance(typeof(AppFactory<>).MakeGenericType(entry))!;
f.Settings["ShopApi:BaseUrl"] = mock.Url;   // egen fabrik: ConfigureWebHost -> builder.UseSetting(...)
var url = f.StartOnKestrel();               // UseKestrel(0); StartServer(); adressen från IServerAddressesFeature
```

## Varför
Det fungerar: ShopApi ger 201 i TestServer, och Chromium fyller i `/products/create` mot ShopWeb på Kestrel.
Mocken får `POST /products` med `{"name":"Testprodukt","sku":"ABC-1234","price":49}` och sidan visar inget fel.

Fallgropar:
- `Program` är publik i .NET 10, så `public partial class Program` behövs inte. Projektnamn och `MakeGenericType` fungerar trots dubbletterna. Content root blir respektive projektmapp, inga krockar med `appsettings.json`.
- ShopWeb läser `ShopApi:BaseUrl` före `Build()`. Bara `UseSetting` (i `ConfigureWebHost`) och miljövariabeln `ShopApi__BaseUrl` får genomslag. `ConfigureAppConfiguration` och `ConfigureServices` kommer för sent, och anropet går tyst till `localhost:5080`. Miljövariabeln är processglobal och duger inte med parallella tester.
- `WithWebHostBuilder` tillsammans med Kestrel fungerar inte: `UseKestrel(0)` på den härledda fabriken ger bindning mot port 5000, och på föräldern ger den adressen `127.0.0.1:0` och `CreateClient` kastar. En egen fabrik med `UseSetting` i `ConfigureWebHost` undviker det.
- Med Kestrel kastar `factory.Server`. Port 0 vinner över `launchSettings` (5090) och appens egna adresser. Läs adressen från `IServerAddressesFeature`.
- `UseUrls("http://localhost:5080")` i ShopApi stör inte TestServer.
- ShopApi har ingen `GET /products` (bara POST), så rökprovet är POST.
- Utan omdirigering ser användaren alerten "tjänsten går inte att nå", men först efter 4-6 s.

Tider (Windows): ShopApi-uppstart 0,6-1,1 s (första körningen efter bygge ca 12 s); ShopWeb på Kestrel 20-30 ms;
mock + ShopWeb 0,2-0,3 s; webbläsarflödet ca 3 s; hela testet ca 3,6 s; alla 9 tester ca 25 s.

## Följder
- `ApiTests`: en `WebApplicationFactory` per app, pekad med projektnamn. TestServer, ingen port. Utgående anrop mockas med `ConfigureTestServices`.
- `ViewTests`: ShopWeb på Kestrel port 0 plus en mock per test. Alla adresser sätts med `UseSetting`, inte miljövariabler, så att testerna kan gå parallellt.
- Adresser som appen läser före `Build()` måste gå att sätta med en konfigurationsnyckel. Hårdkodade adresser kan inte pekas om utan ändring i appen.
- Playwright-webbläsare måste finnas i CI (`playwright install chromium`).
- Linux: WSL har bara `docker-desktop`, ingen .NET-distro. Verifieras i CI.
