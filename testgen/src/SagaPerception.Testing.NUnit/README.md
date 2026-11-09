# SagaPerception.Testing.NUnit

API- och UI-tester i `dotnet test`, ur OpenAPI-specen och appens källkod. Skapa testprojektet med verktyget:

```
dotnet tool install -g SagaPerception.TestGen.Cli --prerelease
testgen init
dotnet test
```

En rad per API eller app:

```csharp
public sealed class ShopApiTests() : ApiTests("ShopApi");
public sealed class ShopWebViewTests() : ViewTests("ShopWeb");
```

Appen körs som lokalt: i miljön från `testgen.yaml`, `Testing` om `appsettings.Testing.json` finns, annars launch-profilens (oftast `Development`, som läser user secrets). Lokal PostgreSQL, SQL Server, Azure Storage och Redis byts mot containrar (Docker krävs); värden mot riktiga tjänster byts aldrig. Värdpolicyn blockerar fortfarande utgående anrop.

Se [testgen/README.md](https://github.com/SagaPerceptionAB/dotnet-tools/blob/main/testgen/README.md).
