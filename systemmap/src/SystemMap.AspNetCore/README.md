# SagaPerception.SystemMap.AspNetCore

Systemkartan som en sida i din ASP.NET Core-app, som Swagger UI. Kartan ritas från källkoden av bygget; appen
innehåller bara en liten middleware.

## Installera

Paketet är en förhandsversion och finns ännu inte på nuget.org (bygg det med `dotnet pack`, se repots README), så `--prerelease` behövs.

```
dotnet add package SagaPerception.SystemMap.AspNetCore --prerelease
```

I `Program.cs` (`MapSystemMap` ligger i namnrymden `Microsoft.AspNetCore.Builder`, så ingen extra `using` behövs):

```csharp
app.MapSystemMap();                                          // /systemmap, i Development
app.MapSystemMap().RequireAuthorization("Utvecklare");       // även i andra miljöer, bakom inloggning
app.MapSystemMap("/karta");                                  // egen sökväg
app.MapSystemMap(configure: o => o.AllowWithoutAuthorization = true);  // utan inloggningskrav
```

## Hur kartan ritas

- **Release-bygge:** kartan ritas när appen byggs och följer med i utdatamappen (`systemmap/`) och vid publish.
- **Andra bygge (t.ex. Debug):** kartan ritas första gången sidan öppnas i Development, och ritas om när koden ändrats.

## Adresser

| Adress | Innehåll |
|---|---|
| `/systemmap` | Kartan |
| `/systemmap/map.json` | Kartan som JSON |

Adresserna följer sökvägen du anger i `MapSystemMap`.

## Inställningar

MSBuild-egenskaper, sätts i projektfilen:

| Egenskap | Standard | Betydelse |
|---|---|---|
| `GenerateSystemMap` | `true` i Release, annars inte satt | Rita kartan vid bygget. |
| `SystemMapSolution` | solutionen bygget körs för | Solution (eller projekt) som kartan ritas för. |
| `SystemMapSpec` | ingen | OpenAPI-spec (fil eller url) som kartan kopplas till. |
| `SystemMapFailOnError` | `false` | Bygget misslyckas om kartan inte kan ritas (annars varning SYSMAP001). |
| `SystemMapToolPath` | verktyget i paketet | Sökväg till `SystemMap.Cli.dll`. |
| `SystemMapDotnet` | den dotnet som kör bygget | Sökväg till `dotnet`. |

Options till `MapSystemMap`:

| Option | Standard | Betydelse |
|---|---|---|
| `AllowWithoutAuthorization` | `false` | Visa kartan utanför Development utan krav på inloggning. |
| `Directory` | `systemmap` i appens mapp | Mappen där bygget lagt kartan. |

## Säkerhet

Utanför Development ger alla adresser 404, om inte `MapSystemMap` har `.RequireAuthorization(...)` eller
`AllowWithoutAuthorization` är satt. Kartan visar hur systemet hänger ihop, så skydda den.
