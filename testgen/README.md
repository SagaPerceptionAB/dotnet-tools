# testgen

testgen testar ett API utifrån dess OpenAPI-spec. Det genererar giltiga och ogiltiga anrop, kör dem och rapporterar OK, FEL eller HOPPAD per test. Egna tester skriver du i Excel. Ingen AI används vid körning: samma indata ger samma resultat.

## Krav

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (`dotnet --version` ska visa `10.x`).
- Ett API med OpenAPI-spec (fil eller webbadress).
- Docker behövs bara för NUnit-spåret (`generate`).

## Installera

Som dotnet-verktyg från nuget.org (paketet heter `SagaPerception.TestGen.Cli`, kommandot `testgen`). Paketet ges ut som förhandsversion, så `--prerelease` behövs:

```
dotnet tool install --global SagaPerception.TestGen.Cli --prerelease
testgen --help
```

Som lokalt verktyg i ett projekt:

```
dotnet new tool-manifest
dotnet tool install SagaPerception.TestGen.Cli --prerelease
dotnet tool restore
```

Använd `--tool-path verktyg` i stället för `--global` för att installera i en egen mapp.

Från källkoden, utan att installera, från repots rot (klona [repot](https://github.com/SagaPerceptionAB/dotnet-tools) först):

```
./testgen.sh <kommando> [flaggor]      # macOS/Linux
testgen.cmd <kommando> [flaggor]       # Windows
```

Exemplen nedan skriver `testgen`. Utan installation ersätter du det med `./testgen.sh` eller `testgen.cmd`.

## Kom igång

Starta exemplet i ett eget fönster (port 5080):

```
dotnet run --project samples/ShopApi
```

Webbgränssnittet:

```
testgen ui --spec samples/shop.openapi.yaml
```

Öppna adressen som skrivs ut (standard port 5100), fyll i API-adressen `http://localhost:5080` och kör. Avsluta med Ctrl+C.

Kommandoraden:

```
testgen run --spec samples/shop.openapi.yaml --api http://localhost:5080
```

Varje test får ett id (T-001, T-002 ...) som är detsamma varje körning. Sist skrivs en rad som `Sammanfattning: 34 tester, 33 OK, 1 FEL, 0 HOPPAD, tid 2,1 s`. Vid FEL visas anropet, svaret och en curl-rad.

## Kommandon

`testgen <kommando> --help` visar alla flaggor och standardvärden.

| Kommando | Vad det gör | Exempel |
| --- | --- | --- |
| `run` | Kör tester mot ett API | `testgen run --spec s.yaml --api http://localhost:5080` |
| `template` | Skapar en Excel-mall för egna tester | `testgen template --spec s.yaml --out tester.xlsx` |
| `ui` | Kör och läs resultat i webbläsaren | `testgen ui --port 5100` |
| `map` (alias `karta`) | Systemkarta över källkoden | `testgen map --solution samples/Shop.slnx` |
| `generate` | Avancerat: skriver scenariofiler för NUnit-spåret | `testgen generate --config testgen/testgen.yaml` |

### Viktigaste flaggor för `run`

| Flagga | Betydelse |
| --- | --- |
| `--spec <fil\|url>` | OpenAPI-spec. Krävs. |
| `--api <bas-url>` | API:et som testas. Behövs inte om `--env` anger den. |
| `--excel <fil>` | Egna tester och miljöer i en Excel-fil. |
| `--env <miljö>` | Miljö ur bladet Miljöer i `--excel`. Flaggor på kommandoraden vinner. |
| `--token <värde>` | Skickas som `Authorization: Bearer <värde>`. |
| `--header "N: v"` | Extra header på alla anrop. Får anges flera gånger. |
| `--seed <tal\|slumpa\|ci>` | Slumpfrö för testdata (standard 12345). Byt det om uppgifterna redan finns (svar 409). |
| `--timeout <s>` | Sekunder per anrop. |
| `--max-time <s>` | Högsta tid för hela körningen. |
| `--only <operationId>` | Kör bara ett anrop. |
| `--test <T-018>` | Kör bara det testet, med samma data som i en full körning. Får anges flera gånger. |
| `--scenario <namn>` | Kör bara det Excel-scenariot. Får anges flera gånger. |
| `--bara-excel` | Hoppa över de genererade testerna. |
| `--klartext` | Förklaring i klartext för varje test. |
| `--verbose` | Visa anrop och svar för alla tester. |
| `--report <fil>` | Rapport efter körningen: `.html`, `.xlsx`, `.json` eller `.xml` / `.junit.xml`. Får anges flera gånger. |
| `--jamfor <fil>` | Jämför med en tidigare JSON-rapport (nya, lagade och kvarvarande FEL). |
| `--config <fil>` | `testgen.yaml` med fältregler m.m. Valfri. |

## Konfiguration

### testgen.yaml

Valfri fil som skickas med `--config`. Sökvägar i filen utgår från filens katalog. Exempel: [`testgen.yaml`](https://github.com/SagaPerceptionAB/dotnet-tools/blob/main/testgen/testgen.yaml).

| Nyckel | Betydelse |
| --- | --- |
| `spec` | Specens fil eller webbadress (för `generate`). |
| `output` | Katalog för scenariofiler (för `generate`). |
| `seed` | Bas-seed, standard `12345`. En stabil seed per operation härleds ur den. |
| `locale` | Språk för genererad data, standard `sv`. |
| `fieldRules` | Genereringsregel per fältnamn (skiftlägesokänsligt, regex tillåtet), t.ex. `email: internet.email`, `personnummer: se.personnummer`, `sku: pattern`. |
| `bindings` | Fält som fylls från svaret av en annan operation, t.ex. `createOrder: { customerId: ${createCustomer.id} }`. |
| `overrides` | Fasta värden per operation och fält, t.ex. `createOrder: { quantity: 2 }`. |
| `skipOperations` | Lista med operationer som inte ska generera scenarier. |

### Miljöer och inloggning

- `--token` skickas som `Authorization: Bearer ...`, eller i den header specens `apiKey`-schema anger.
- `--header` lägger till egna headers på alla anrop.
- Miljöbladet i Excel-filen (skapas av `testgen template`) har kolumnerna Miljö, Inställning och Värde. Inställningarna är `api`, `header` (kan finnas flera), `token` och `timeout`. Välj miljö med `--env`.
- Låt hemligheter ligga i miljövariabler: `${env:NAMN}` i ett värde ersätts med miljövariabeln `NAMN`. Använd enkla citattecken i skalet: `--token '${env:API_TOKEN}'`. Känsliga värden visas som `***` i utskrifter.

### Excel-tester

Skapa en mall, fyll i bladet Tester och kör med `--excel`. Filen kontrolleras mot specen innan något körs, och alla fel visas samtidigt med cell, t.ex. `Tester!B3 (Anrop)`.

```
testgen template --spec samples/shop.openapi.yaml --out tester.xlsx
testgen run --spec samples/shop.openapi.yaml --api http://localhost:5080 --excel tester.xlsx
```

Bladet Hjälp i mallen beskriver kolumnerna.

## Köra i CI

Kommandot (utskriften är ren text, exitkoden styr bygget):

```
testgen run --spec https://test.example.se/openapi.json --api https://test.example.se \
    --token '${env:API_TOKEN}' --seed ci --max-time 900 \
    --report resultat.json --report resultat.junit.xml --report resultat.html \
    --jamfor forra.json --fail-on-new
```

- Färdiga exempel: [GitHub Actions](https://github.com/SagaPerceptionAB/dotnet-tools/blob/main/testgen/docs/ci/github-actions.yml) och [Azure Pipelines](https://github.com/SagaPerceptionAB/dotnet-tools/blob/main/testgen/docs/ci/azure-pipelines.yml).
- `--seed ci` tar fröet från bygget, så en omkörning ger ny testdata.
- `--fail-on <orsaker>` väljer vilka orsaker som ger exitkod 1. Koder: `system`, `forvantan`, `inloggning`, `miljo`, `testdata`, `verktyg`, `foljd`. Standard är `system,forvantan`; övriga FEL och HOPPAD ger exitkod 4. Exempel: `--fail-on system,forvantan,inloggning`.
- `--jamfor forra.json --fail-on-new` ger exitkod 1 bara för nya FEL.
- `--allow-empty` tillåter en körning utan tester (annars exitkod 2).
- JSON-rapportens schema: [docs/ci/testgen-result.schema.json](https://github.com/SagaPerceptionAB/dotnet-tools/blob/main/testgen/docs/ci/testgen-result.schema.json).

| Exitkod | Betydelse |
| --- | --- |
| 0 | Alla tester OK. |
| 1 | Minst ett FEL i systemet eller där förväntan i testet inte stämde (ändra med `--fail-on`). |
| 2 | Fel i användning eller konfiguration (flaggor, filer, spec, Excel, miljö), eller inga tester att köra. |
| 3 | API:et eller specens webbadress gick inte att nå, eller `--max-time` löpte ut. En partiell rapport skrivs. |
| 4 | Inget säkert svar: inga FEL i systemet, men FEL eller HOPPAD av osäker orsak (inloggning 401/403, testdata 409, miljön svarar inte, följdfel). |
| 130 | Avbruten med Ctrl+C. Partiell rapport skrivs. |
| 143 | Avbruten med SIGTERM. Partiell rapport skrivs. |

Meddelandet `Kunde inte köra: ...` betyder att verktyget inte kunde köra alls. Det är inte ett testresultat.

## Systemkarta

`testgen map --solution <.sln|.slnx|.csproj>` visar vägen genom källkoden ut till databaser, API:er och köer. Kartan finns också som flik i `testgen ui`. Mer i [systemmap/README.md](https://github.com/SagaPerceptionAB/dotnet-tools/blob/main/systemmap/README.md).
