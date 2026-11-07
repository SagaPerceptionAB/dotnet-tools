# SPA-fixtures

Det här är testdata för uitestgens SPA-upptäckt (`SpaDiscoverer`), inte körbara appar. Varje mapp är ett litet källträd med
routerkonfiguration och några sidor: `react/` (React Router), `angular/` (Angular Router) och `vue/` (Vue Router).
Det finns inget `package.json`-beroende att installera och inget att bygga; uitestgen läser bara filerna som text.
Testerna i `uitestgen/tests/UiTestGen.Tests/SpaDiscoveryTests.cs` kontrollerar vilka vyer som hittas, så ändra inte innehållet utan att uppdatera dem.
