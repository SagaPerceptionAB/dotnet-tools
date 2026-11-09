# Tredjepartslicenser

Verktygen i det här repot är under [MIT-licensen](LICENSE). Det här är det som följer med i paketen och verktygen från andra
upphovspersoner. Licenstexterna för JavaScript-biblioteken från systemkartan ligger i
[systemmap/src/SystemMap/Web/lib](systemmap/src/SystemMap/Web/lib) och packas under `licenses/`.

## Inbäddad JavaScript

| Komponent | Version | Licens | Användning |
| --- | --- | --- | --- |
| [axe-core](https://github.com/dequelabs/axe-core) | 4.10.3 | MPL-2.0 | Inbäddad oförändrad som `axe.min.js` i `SagaPerception.TestGen.NUnit` (modulen Browser, `testgen/src/TestGen.Browser/Resources`), för tillgänglighetskontrollen. Licenstexten står i filens huvud och på <https://mozilla.org/MPL/2.0/>; källkoden finns på GitHub. |
| [Cytoscape.js](https://js.cytoscape.org) | 3.34.4 | MIT | Ritar systemkartan (`SystemMap`, `testgen web`, `SagaPerception.SystemMap.AspNetCore`). |
| [cytoscape-fcose](https://github.com/iVis-at-Bilkent/cytoscape.js-fcose), cose-base, layout-base | – | MIT | Layouten för systemkartan. |
| [Playwright](https://playwright.dev) (drivrutin och Node.js) | 1.64.1 | Apache-2.0, MIT (Node.js) | Följer med `Microsoft.Playwright` i `testgen` och `SagaPerception.TestGen.NUnit`. Chromium laddas ner av `testgen init`, inte av paketet. |

## NuGet-paket

Beroendena omfattas av respektive pakets egen licens. De viktigaste (se [Directory.Packages.props](Directory.Packages.props)):

| Paket | Version | Licens | Finns i |
| --- | --- | --- | --- |
| Bogus | 35.6.5 | MIT | `SagaPerception.TestGen.NUnit` (Core) |
| Microsoft.OpenApi, Microsoft.OpenApi.YamlReader (med SharpYaml) | 2.13.0 | MIT | `SagaPerception.TestGen.NUnit` (Core), systemmap |
| YamlDotNet | 18.1.0 | MIT | `SagaPerception.TestGen.NUnit` (Core) |
| ClosedXML (med ClosedXML.Parser, DocumentFormat.OpenXml, ExcelNumberFormat, RBush.Signed, SharpZipLib, SixLabors.Fonts 1.0.0 [Apache-2.0]) | 0.105.1 | MIT | `SagaPerception.TestGen.NUnit` (Excel) |
| Microsoft.Playwright | 1.64.1 | MIT | `SagaPerception.TestGen.NUnit` (Browser), `testgen` |
| Microsoft.AspNetCore.Mvc.Testing | 10.0.14 | MIT | `SagaPerception.TestGen.NUnit` |
| NUnit | 5.0.0 | MIT | `SagaPerception.TestGen.NUnit` |
| Testcontainers, Testcontainers.Azurite, .MsSql, .PostgreSql, .Redis (med Docker.DotNet.Enhanced, SSH.NET, BouncyCastle.Cryptography) | 4.16.0 | MIT | `SagaPerception.TestGen.NUnit` |
| Microsoft.CodeAnalysis (Roslyn), Microsoft.Build.Framework, Microsoft.VisualStudio.SolutionPersistence | 5.9.0, 17.11.48, 1.0.52 | MIT | `testgen`, systemmap |

Testprojekten i repot använder också NUnit, NUnit3TestAdapter, NUnit.Analyzers, Microsoft.NET.Test.Sdk och coverlet.collector (alla MIT).

## Cytoscape.js

```
Copyright (c) 2016-2026, The Cytoscape Consortium.

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the “Software”), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies
of the Software, and to permit persons to whom the Software is furnished to do
so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED “AS IS”, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## cytoscape.js-fcose

```
Copyright (c) 2018 - present, iVis-at-Bilkent.

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the “Software”), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies
of the Software, and to permit persons to whom the Software is furnished to do
so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED “AS IS”, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## cose-base

```
MIT License

Copyright (c) 2019 - present, iVis@Bilkent.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## layout-base

```
MIT License

Copyright (c) 2019 iVis@Bilkent

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
