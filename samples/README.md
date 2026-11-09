# Exempel

Exempelappen Shop, bara för att prova och testa verktygen. Den ges inte ut som paket.

- `ShopApi`, `ShopApplication` och `ShopInfrastructure`: API:et.
- `ShopWeb`: webbappen mot API:et.
- `Shop.Tests`: API- och UI-testerna. `testgen.yaml`, `tester.xlsx` och OpenAPI-specarna ligger i den här mappen.

Två fel är avsiktliga, och CI kräver att exakt de testerna faller (se [expected-failures.txt](expected-failures.txt)):
- `ShopApi` ger 500 när `quantity` är 0.
- Kundformuläret i `ShopWeb` begränsar inte namnets längd, men specen har `maxLength` 100.
