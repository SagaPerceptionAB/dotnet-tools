# SagaPerception.Testing

Testhjälp för SagaPerception-tjänster, oberoende av testramverk (NUnit, xUnit, MSTest).

```
dotnet add package SagaPerception.Testing --prerelease
```

## ApiFactory

`ApiFactory<TProgram>` är en `WebApplicationFactory<TProgram>` som startar tjänsten i miljön `Testing`, stänger av
telemetriexport (tom OTLP-adress, även om `OTEL_EXPORTER_OTLP_ENDPOINT` är satt på maskinen) och
fångar spår och loggar. Appen behöver `public partial class Program;`.

```csharp
using var factory = new ApiFactory<Program>()
    .WithSetting("ConnectionStrings:Db", "Data Source=:memory:")
    .WithServices(s => s.AddSingleton<IClock, FakeClock>());
using var client = factory.CreateClient();

var response = await client.GetAsync("/orders/1");

Assert.That(factory.Telemetry.Logs, Has.Some.Matches<CapturedLog>(l => l.Level == LogLevel.Warning));
Assert.That(factory.Telemetry.Activities.Select(a => a.OperationName), Does.Contain("Orders.Load"));
```

`WithEnvironment("Development")` byter miljö. `WithEnvironment`, `WithSetting` och `WithServices` måste anropas innan fabriken startats (första `CreateClient`, `Services` eller `Server`),
annars kastas `InvalidOperationException`. Värdena sätts med `UseSetting`, så appen ser dem också när den läser konfigurationen
innan den byggts (en adress i `Program.cs`).

`ApiFactory.Create("ShopApi")` skapar fabriken för en app som pekas ut med projektnamn (testprojektet har en `ProjectReference`
till den), och ger en `IApiFactory` med samma metoder.

## TelemetryCapture

- `Activities`: färdiga `Activity`-objekt, `Logs`: `CapturedLog(Category, Level, Message, Exception)`, `Clear()` tömmer båda.
- Kan också användas fristående: `new TelemetryCapture(name => name.StartsWith("Orders"))` fångar bara källor som matchar.

**Begränsning:** en `ActivityListener` är processglobal. Fångsten ser därför alla aktiviteter som avslutas i processen,
även från andra appar och tester som körs parallellt. Filtrera på operationsnamn eller taggar i testet och undvik att
räkna antal spår i tester som körs parallellt. Loggarna hör däremot bara till den app fångsten sitter i.
