# SPA-fixtures

Testdata för testgens SPA-upptäckt (`SpaDiscoverer`), inte körbara appar. Varje mapp är ett litet källträd med
routerkonfiguration och några sidor: `react/` (React Router), `angular/` (Angular Router) och `vue/` (Vue Router).
Inget att installera eller bygga; testgen läser filerna som text.
Testerna i `testgen/tests/TestGen.Views.Tests/SpaDiscoveryTests.cs` kontrollerar vilka vyer som hittas, så ändra inte innehållet utan att uppdatera dem.
