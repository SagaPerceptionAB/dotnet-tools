# 0002. Excel-testerna behålls

## Status
Antagen 2026-11-08.

## Beslut
Excel-testerna finns kvar och körs av samma motor i `testgen run` och `dotnet test`. Miljöerna flyttas från Excel till `testgen.yaml`.

## Varför
Testare som inte skriver kod arbetar i Excel. En gemensam motor ger samma resultat oavsett var testen körs.

## Följder
- Excel beskriver testfallen, `testgen.yaml` beskriver miljöerna.
- Det finns ingen separat Excel-motor att underhålla.
