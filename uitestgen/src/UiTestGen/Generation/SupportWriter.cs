namespace UiTestGen.Generation;

/// <summary>Skriver basklassen <c>UiTestBase</c> och projektfilerna i det genererade testprojektet.</summary>
internal static class SupportWriter
{
    public const string DefaultBaseUrl = "http://localhost:5090";

    public static string UiTestBase(string ns, Backend backend, bool mocks) => UiTestBaseSource
        .Replace("__NS__", ns)
        .Replace("__DEFAULT_BASE_URL__", CSharp.Literal(DefaultBaseUrl))
        .Replace("__DEFAULT_BACKEND__", CSharp.Literal(backend == Backend.Mock ? "mock" : "real"))
        .Replace("__BASE_URL_VARIABLE__", GenerateOptions.BaseUrlVariable)
        .Replace("__BACKEND_VARIABLE__", GenerateOptions.BackendVariable)
        .Replace("__REGEX_USING__", mocks ? "using System.Text.RegularExpressions;\n" : "")
        .Replace("    __MOCKS__", Indent(mocks ? MockRoutingSource : NoMocksSource))
        .Replace("    __FORM_HELPERS__", Indent(FormHelpersSource))
        .ReplaceLineEndings("\n");

    /// <summary>Flyttar in varje rad som inte är tom fyra mellanslag (till klassens nivå).</summary>
    private static string Indent(string text) =>
        string.Join('\n', text.TrimEnd('\n').Split('\n').Select(line => line.Length > 0 ? "    " + line : line));

    private static readonly string UiTestBaseSource = CSharp.Header + "\n\n" + """
        using System.Collections.Concurrent;
        __REGEX_USING__using Microsoft.Playwright;
        using Microsoft.Playwright.NUnit;
        using NUnit.Framework;

        namespace __NS__.Support;

        /// <summary>Gemensam bas för alla UI-tester: appens adress, backend-val och kontroll av fel i webbläsaren.</summary>
        public abstract class UiTestBase : PageTest
        {
            private const string DefaultBaseUrl = __DEFAULT_BASE_URL__;
            private const string DefaultBackend = __DEFAULT_BACKEND__;

            private readonly ConcurrentQueue<string> _errors = new();
            private readonly ConcurrentDictionary<IRequest, int> _statuses = new();

            /// <summary>Resurser som inte styr hur sidan fungerar: en saknad bild, ikon, typsnitt eller mediefil (404) syns men fäller inte testet.</summary>
            private static readonly HashSet<string> OptionalResourceTypes = ["image", "font", "media", "manifest"];

            /// <summary>Appens adress. Sätts med miljövariabeln __BASE_URL_VARIABLE__.</summary>
            protected static string BaseUrl => Environment.GetEnvironmentVariable("__BASE_URL_VARIABLE__") is { Length: > 0 } url ? url : DefaultBaseUrl;

            /// <summary>true om backend ska mockas. Sätts med miljövariabeln __BACKEND_VARIABLE__ (mock eller real).</summary>
            internal static bool IsMockBackend => string.Equals(Environment.GetEnvironmentVariable("__BACKEND_VARIABLE__") is { Length: > 0 } value ? value : DefaultBackend, "mock", StringComparison.OrdinalIgnoreCase);

            /// <inheritdoc cref="IsMockBackend"/>
            protected bool MockBackend => IsMockBackend;

            /// <summary>
            /// Fast fönsterstorlek, språk och tidszon, så att skärmdumpar, datum och talformat blir desamma på alla datorer.
            /// Åsidosätt för andra värden (referensbilderna måste då göras om).
            /// </summary>
            public override BrowserNewContextOptions ContextOptions() => new()
            {
                BaseURL = BaseUrl,
                ViewportSize = new ViewportSize { Width = 1280, Height = 800 },
                Locale = "sv-SE",
                TimezoneId = "Europe/Stockholm",
                ColorScheme = ColorScheme.Light,
            };

            [SetUp]
            public async Task UiTestSetUp()
            {
                _errors.Clear();
                _statuses.Clear();
                // Webbläsaren loggar också varje resurs som inte kunde laddas som console.error ("Failed to load resource"). Det räknas inte
                // här: samma fel kommer via Response (status 400 eller högre) eller RequestFailed (nätverksfel), och ska bara räknas en gång.
                Page.Console += (_, message) => { if (message.Type == "error" && !message.Text.StartsWith("Failed to load resource", StringComparison.Ordinal)) _errors.Enqueue($"console.error: {message.Text} ({message.Location})"); };
                Page.PageError += (_, error) => _errors.Enqueue($"Sidfel: {error}");
                Page.Response += (_, response) =>
                {
                    _statuses[response.Request] = response.Status;
                    if (response.Status >= 400 && !IsIgnored(response.Request)) _errors.Enqueue($"Svar {response.Status}: {response.Url}");
                };
                // net::ERR_ABORTED är att webbläsaren själv avbröt anropet (t.ex. vid navigering): inget fel i appen.
                Page.RequestFailed += (_, request) => { if (request.Failure != "net::ERR_ABORTED" && !IsIgnored(request)) _errors.Enqueue($"Anropet misslyckades: {request.Url} ({request.Failure})"); };
                if (MockBackend) await ConfigureMocksAsync(Page);
            }

            /// <summary>
            /// Anrop vars fel (svar med status 400 eller högre, eller nätverksfel) inte ska fälla testerna. Som standard: /favicon.ico, som
            /// webbläsaren hämtar av sig själv, och bilder, typsnitt, mediefiler och manifest som saknas (404): sidan fungerar utan dem.
            /// Dokument, fetch/xhr, skript och stilmallar räknas alltid, för utan dem fungerar inte sidan. Andra fel än 404 räknas också.
            /// Åsidosätt för att ignorera fler (t.ex. analysskript), eller färre.
            /// </summary>
            protected virtual bool IsIgnored(IRequest request) =>
                request.Url.Contains("/favicon.ico", StringComparison.OrdinalIgnoreCase)
                || (OptionalResourceTypes.Contains(request.ResourceType) && _statuses.TryGetValue(request, out var status) && status == 404);

            __MOCKS__

            /// <summary>Går till adressen och kräver att svaret är lyckat (status under 400).</summary>
            protected async Task<IResponse> GotoAsync(string path)
            {
                var response = await Page.GotoAsync(path);
                Assert.That(response, Is.Not.Null, $"Inget svar från {path}.");
                Assert.That(response!.Status, Is.LessThan(400), $"{path} svarade med status {response.Status}.");
                return response;
            }

            /// <summary>Kräver att inga fel samlats: console.error, sidfel, anrop som misslyckats och svar med status 400 eller högre.</summary>
            protected Task AssertNoErrorsAsync()
            {
                Assert.That(_errors, Is.Empty, "Webbläsaren rapporterade fel.");
                return Task.CompletedTask;
            }

            __FORM_HELPERS__
        }
        """ + "\n";

    private const string NoMocksSource = """
        /// <summary>Här sätts mockade svar upp innan varje test (bara när backend mockas). Ingen OpenAPI-spec angavs när koden genererades, så den är tom; åsidosätt för egna mockar.</summary>
        protected virtual Task ConfigureMocksAsync(IPage page) => Task.CompletedTask;
        """;

    /// <summary>
    /// Mockar anropen som webbläsaren gör (fetch och XHR) ur <c>ApiMocks</c>. Bara adresser som slutar på en sökväg i specen skickas
    /// till .NET (en <c>Regex</c> byggd ur <c>ApiMocks.All</c>); sidor, skript, CSS och bilder går direkt, utan omvägen.
    /// Serverrenderade appar anropar API:et från servern: där används stub-servern <c>ApiStub</c> i stället.
    /// </summary>
    private const string MockRoutingSource = """
        /// <summary>
        /// Adresser som kan vara ett mockat anrop: valfritt prefix, en sökväg ur <see cref="ApiMocks"/> (<c>{id}</c> är ett segment) och
        /// valfri fråga. Samma jämförelse som <c>ApiMocks.TryMatch</c> (sökvägen på slutet), men grövre; <c>TryMatch</c> avgör svaret.
        /// </summary>
        // Utan RegexOptions: Playwright skickar mönstret till webbläsarens drivrutin, som inte tar .NET-flaggor.
        private static readonly Regex ApiPaths = new(ApiMocks.UrlPattern);

        /// <summary>
        /// Sätter upp mockade svar innan varje test (bara när backend mockas): fetch och XHR mot en sökväg som finns i OpenAPI-specen
        /// besvaras ur <see cref="ApiMocks"/>, övriga anrop släpps igenom. Åsidosätt för egna mockar (anropa gärna base först).
        /// </summary>
        protected virtual async Task ConfigureMocksAsync(IPage page)
        {
            await page.RouteAsync(ApiPaths, async route =>
            {
                var request = route.Request;
                var isApiCall = request.ResourceType is "fetch" or "xhr";
                if (isApiCall && ApiMocks.TryMatch(request.Method, new Uri(request.Url).AbsolutePath, out var mock))
                {
                    await route.FulfillAsync(new()
                    {
                        Status = mock.Status,
                        ContentType = mock.Body.Length > 0 ? "application/json; charset=utf-8" : null,
                        Body = mock.Body,
                        Headers = new Dictionary<string, string> { ["Access-Control-Allow-Origin"] = "*" },
                    });
                    return;
                }
                await route.ContinueAsync();
            });
        }
        """;

    /// <summary>Hjälpare för formulärtesterna: skicka, och kontrollera felmeddelanden och markerade fält.</summary>
    private const string FormHelpersSource = """
        /// <summary>
        /// Klickar på knappen och väntar tills sidan är klar. Med <paramref name="browserValidation"/> false stängs webbläsarens egen kontroll
        /// (required, type=email, min/max) av först, annars stoppar den formuläret innan appen hunnit visa sina egna felmeddelanden.
        /// </summary>
        protected async Task SubmitAsync(ILocator submit, bool browserValidation = true)
        {
            if (!browserValidation) await submit.EvaluateAsync("b => { if (b.form) b.form.noValidate = true; }");
            await submit.ClickAsync();
            await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        }

        /// <summary>
        /// Tar bort maxlength från fältet. Razor Pages skriver ut det för [StringLength] och [MaxLength], och webbläsaren klipper då en för lång text
        /// när den fylls i. Utan det går det inte att pröva att appen själv avvisar för långa värden.
        /// </summary>
        protected static Task AllowTooLongAsync(ILocator field) => field.EvaluateAsync("e => e.removeAttribute('maxlength')");

        /// <summary>Kräver att felmeddelandet syns på sidan.</summary>
        protected Task ExpectMessageAsync(string message) => Expect(Page.GetByText(message, new() { Exact = true }).First).ToBeVisibleAsync();

        /// <summary>
        /// Elementen som räknas som ett fel som appen visar: <c>role="alert"</c>, utom bekräftelser (bootstrap-klasserna alert-success och
        /// alert-info). Åsidosätt om appen visar fel på något annat sätt.
        /// </summary>
        protected virtual ILocator ErrorAlerts => Page.Locator("[role=alert]:not(.alert-success):not(.alert-info)");

        /// <summary>
        /// Kräver att appen inte visar något fel (se <see cref="ErrorAlerts"/>). Efter ett giltigt formulär fångar det t.ex. att en serverrenderad app
        /// inte nådde sin backend: felet syns då på sidan med status 200, och ingen svarskod avslöjar det.
        /// </summary>
        protected Task ExpectNoAlertsAsync() => Expect(ErrorAlerts).ToHaveCountAsync(0);

        /// <summary>Kräver att inget av felmeddelandena syns.</summary>
        protected async Task ExpectNoMessagesAsync(params string[] messages)
        {
            foreach (var message in messages) await Expect(Page.GetByText(message, new() { Exact = true }).First).Not.ToBeVisibleAsync();
        }

        /// <summary>
        /// Kräver att fältet är markerat som ogiltigt: aria-invalid="true", klassen invalid (Blazor), klassen input-validation-error
        /// (Razor Pages), eller att webbläsaren anser att värdet inte är giltigt (validity.valid). Väntar några sekunder på att sidan svarar.
        /// </summary>
        protected async Task ExpectInvalidAsync(ILocator field)
        {
            const string script = "e => e.getAttribute('aria-invalid') === 'true' || e.classList.contains('invalid') || e.classList.contains('input-validation-error') || (!!e.validity && !e.validity.valid)";
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (true)
            {
                try
                {
                    if (await field.EvaluateAsync<bool>(script)) return;
                }
                catch (PlaywrightException) when (DateTime.UtcNow < deadline)
                {
                    // Sidan laddades om medan fältet lästes: försök igen.
                }
                if (DateTime.UtcNow >= deadline) Assert.Fail("Fältet är inte markerat som ogiltigt.");
                await Task.Delay(100);
            }
        }
        """;

    public static string Csproj(string ns, bool stub)
    {
        var lines = new List<string>
        {
            "<Project Sdk=\"Microsoft.NET.Sdk\">",
            "",
            "  <!-- Playwright-tester (NUnit) skrivna av uitestgen. Den här filen skrivs om; egna ändringar görs i .cs-filerna. -->",
            "  <PropertyGroup>",
            "    <TargetFramework>net10.0</TargetFramework>",
            "    <Nullable>enable</Nullable>",
            "    <ImplicitUsings>enable</ImplicitUsings>",
            "    <IsPackable>false</IsPackable>",
            "    <IsTestProject>true</IsTestProject>",
            $"    <RootNamespace>{ns}</RootNamespace>",
            "  </PropertyGroup>",
            "",
            "  <ItemGroup>",
            $"    <PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"{PackageVersions.TestSdk}\" />",
            $"    <PackageReference Include=\"Microsoft.Playwright.NUnit\" Version=\"{PackageVersions.PlaywrightNUnit}\" />",
            $"    <PackageReference Include=\"NUnit\" Version=\"{PackageVersions.NUnit}\" />",
            $"    <PackageReference Include=\"NUnit3TestAdapter\" Version=\"{PackageVersions.NUnit3TestAdapter}\" />",
            "  </ItemGroup>",
            "",
        };
        if (stub)
        {
            lines.AddRange(
            [
                "  <!-- Stub-servern (Support/ApiStub.g.cs) är en minimal Kestrel-server och behöver ASP.NET Core. Inga fler paket. -->",
                "  <ItemGroup>",
                "    <FrameworkReference Include=\"Microsoft.AspNetCore.App\" />",
                "  </ItemGroup>",
                "",
            ]);
        }
        lines.Add("</Project>");
        lines.Add("");
        return string.Join('\n', lines);
    }

    public static readonly string DirectoryBuildProps = """
        <Project>

          <!-- Tom med flit: MSBuild slutar leta efter Directory.Build.props här, så testprojektet ärver inte inställningarna
               från ett repo det råkar ligga i. -->

        </Project>

        """.ReplaceLineEndings("\n");

    public static readonly string DirectoryBuildTargets = """
        <Project>

          <!-- Tom med flit, av samma skäl som Directory.Build.props. -->

        </Project>

        """.ReplaceLineEndings("\n");

    public static readonly string DirectoryPackagesProps = """
        <Project>

          <!-- Paketversionerna står i projektfilen. Det här stoppar central pakethantering från ett repo ovanför. -->
          <PropertyGroup>
            <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
          </PropertyGroup>

        </Project>

        """.ReplaceLineEndings("\n");

    public static string Readme(string projectName, bool mocks)
    {
        var lines = new List<string>
        {
            $"# {projectName}",
            "",
            "Playwright-tester (NUnit) för webbappen, skrivna av `uitestgen`.",
            "",
            "## Kör testerna",
            "",
            "1. Bygg och installera webbläsaren (en gång):",
            "",
            "   ```",
            "   dotnet build",
            "   pwsh bin/Debug/net10.0/playwright.ps1 install chromium",
            "   ```",
            "",
            $"2. Starta appen och sätt adressen i miljövariabeln `{GenerateOptions.BaseUrlVariable}` (standard `{SupportWriter.DefaultBaseUrl}`):",
            "",
            "   ```",
            $"   export {GenerateOptions.BaseUrlVariable}=http://localhost:5090",
            "   ```",
            "",
            $"3. Välj backend med `{GenerateOptions.BackendVariable}`: `mock` eller `real`.",
            "",
            "4. Kör:",
            "",
            "   ```",
            "   dotnet test",
            "   dotnet test --filter Category=Smoke",
            "   ```",
            "",
            "## Fel som fäller testerna",
            "",
            "`UiTestBase` samlar console.error, sidfel, anrop som misslyckats (nätverksfel) och svar med status 400 eller högre; `AssertNoErrorsAsync` kräver att inget samlats.",
            "Ett fel räknas en gång: webbläsarens egen console.error om en resurs som inte kunde laddas (\"Failed to load resource\") hoppas över, eftersom svaret",
            "eller nätverksfelet redan räknats. Åsidosätt `IsIgnored(IRequest)` för att ändra vilka anrop som inte räknas. Som standard ignoreras `/favicon.ico`,",
            "och bilder, typsnitt, mediefiler och manifest som saknas (404), eftersom sidan fungerar utan dem. Dokument, `fetch`/`xhr`, skript och stilmallar räknas alltid.",
            "",
        };
        if (mocks) lines.AddRange(MockSection());
        else
            lines.AddRange(
            [
                "## Mockad backend",
                "",
                "Testerna skrevs utan OpenAPI-spec, så det finns inga färdiga mockar. Med `mock` görs ingenting av sig självt:",
                "skriv egna mockar genom att åsidosätta `ConfigureMocksAsync` i `UiTestBase`, eller generera om med `--spec <fil>`.",
                "",
            ]);
        lines.AddRange(NavigationSection());
        lines.AddRange(VisualSection());
        lines.AddRange(
        [
            "## Vad som skrivs om",
            "",
            "- Filer som slutar på `.g.cs`, projektfilen och `Directory.*`-filerna skrivs om varje gång `uitestgen generate` körs. Ändra dem inte.",
            "- Filer som slutar på `.cs` (utan `.g`) är dina. De skapas en gång och skrivs aldrig över. Egna tester och tillägg till sidorna skrivs där.",
            "",
        ]);
        return string.Join('\n', lines);
    }

    private static IEnumerable<string> NavigationSection() =>
    [
        "## Navigeringstester",
        "",
        "Varje länk till en känd vy får ett eget test, `Link_to_<Vy>` (kategori `Navigering`): testet går till startvyn, klickar på länken,",
        "och kontrollerar att adressen är målvyns rutt, att målets rubrik syns och att inga fel samlats. Länkar till okända adresser får inget test;",
        "det står en kommentar om dem i testklassen. `Tests/CatalogReport.g.cs` listar vyer som ingen annan vy länkar till.",
        "",
    ];

    private static IEnumerable<string> VisualSection() =>
    [
        "## Visuella tester",
        "",
        "Varje vy utan inloggning har `Looks_like_the_reference` (kategori `Visuellt`): en skärmdump av hela sidan jämförs med referensbilden",
        "`Screenshots/<Vy>.png` i projektmappen. Fönstret är fast (1280x800), språket `sv-SE` och tidszonen `Europe/Stockholm`.",
        "Med riktig backend ändras innehållet av sig självt, så testet får också kategorin `Instabil`. Använd `mock` för stabila bilder.",
        "",
        "### Skapa, uppdatera och checka in referensbilder",
        "",
        "- Saknas referensbilden skapas den och testet blir *Inconclusive* (inte grönt), så att det syns att något nytt skapats.",
        "  Granska bilden och checka in `Screenshots/`. Nästa körning jämför mot den.",
        $"- Efter en avsiktlig ändring av sidan: kör med `{ScreenshotsWriter.UpdateVariable}=1`. Alla jämförda bilder skrivs om (oförändrade lämnas orörda), testerna blir *Inconclusive*;",
        "  granska med `git diff` och checka in de nya bilderna.",
        "- Vid skillnad fäller testet med andel och antal pixlar, och skriver `<Vy>.actual.png` och `<Vy>.diff.png` (skillnaden i rött) till",
        "  `ScreenshotDiffs/` bredvid testresultatet. Filerna bifogas testet. Olika storlek fäller direkt.",
        "",
        "| Miljövariabel | Betydelse |",
        "| --- | --- |",
        $"| `{ScreenshotsWriter.UpdateVariable}=1` | Skapa och uppdatera referensbilderna. |",
        $"| `{ScreenshotsWriter.ThresholdVariable}` | Högsta andel avvikande pixlar i procent (standard {ScreenshotsWriter.DefaultThresholdPercent}). |",
        $"| `{ScreenshotsWriter.ToleranceVariable}` | Tolerans per färgkanal, 0-255 (standard {ScreenshotsWriter.DefaultTolerance}). |",
        $"| `{ScreenshotsWriter.DirectoryVariable}` | Mapp för referensbilderna (standard `Screenshots` i projektmappen). |",
        "",
        "Referensbilderna läses direkt från projektmappen, inte från `bin/`, så de behöver inte kopieras och uppdateringar hamnar där de ska checkas in.",
        "Bilderna beror på webbläsare och typsnitt: skapa dem på samma slags dator som CI använder.",
        "",
        "Sparar appen något i minnet eller i en databas när formulärtesterna skickar giltiga värden, kan en lista se annorlunda ut beroende på",
        "vilka tester som hunnit före (testklasserna körs parallellt). Kör då de visuella testerna för sig mot en nystartad app:",
        "",
        "```",
        "dotnet test --filter Category=Visuellt",
        "dotnet test --filter Category!=Visuellt",
        "```",
        "",
        "### Maskering",
        "",
        "Allt som ändras mellan körningarna (klockslag, slumpvärden, animerade bilder) ska döljas:",
        "",
        "- Sätt attributet `data-uitest-mask` på elementet i appen. Det döljs bakom en rosa ruta i alla skärmdumpar.",
        "- Eller peka ut elementen per vy i den handskrivna testfilen (`Tests/<Vy>Tests.cs`), utan att röra den genererade:",
        "",
        "  ```csharp",
        "  partial void MaskedElements(IPage page, List<ILocator> masks) => masks.Add(page.Locator(\".clock\"));",
        "  ```",
        "",
    ];

    private static IEnumerable<string> MockSection() =>
    [
        "## Mockad backend",
        "",
        "Svaren kommer ur OpenAPI-specen och står i `Support/ApiMocks.g.cs`: en rad per operation med metod, sökvägsmönster, status",
        "(första 2xx i specen, annars 200) och en JSON-kropp som räknades ut när koden genererades (samma spec och `--seed` ger samma svar).",
        "En sökväg jämförs på slutet, så `/api/orders/5` träffar också `/orders/{id}`.",
        "",
        $"Mockning sker bara när `{GenerateOptions.BackendVariable}=mock`. Det finns två varianter, beroende på vem som anropar API:et.",
        "",
        "### 1. Appen anropar API:et från webbläsaren (SPA, Blazor WebAssembly, `fetch` i JavaScript)",
        "",
        "Inget behöver göras. `UiTestBase.ConfigureMocksAsync` registrerar `page.RouteAsync`: fetch och XHR mot en sökväg som finns i specen",
        "besvaras ur tabellen. Bara adresser som slutar på en sökväg i specen går via .NET; sidor, skript, CSS och bilder går direkt.",
        "Åsidosätt metoden för egna mockar.",
        "",
        "### 2. Appen anropar API:et från servern (Blazor SSR, Razor Pages, MVC)",
        "",
        "Webbläsaren ser aldrig anropet, så `RouteAsync` hjälper inte. I stället startar testprojektet en liten stub-server",
        "(`Support/ApiStub.g.cs`, Kestrel) som svarar ur samma tabell. Okända sökvägar ger 404 med en tydlig text.",
        "",
        "Stubben startar en gång för alla tester, och bara när backend mockas:",
        "",
        "| Miljövariabel | Betydelse |",
        "| --- | --- |",
        $"| `{GenerateOptions.StubPortVariable}=<port>` | Stubben startar på den porten. |",
        $"| `{GenerateOptions.StubVariable}=on` | Stubben startar på standardporten {GenerateOptions.DefaultStubPort} (om ingen port är angiven). |",
        $"| `{GenerateOptions.StubVariable}=off` | Stubben startar inte, även om en port är angiven. |",
        "| ingen av dem | Stubben startar inte. |",
        "",
        "Starta appen med stubbens adress som API-adress. Hur adressen konfigureras beror på appen; för en app som läser `ShopApi:BaseUrl`:",
        "",
        "```",
        $"export {GenerateOptions.BackendVariable}=mock {GenerateOptions.StubVariable}=on",
        $"ShopApi__BaseUrl=http://localhost:{GenerateOptions.DefaultStubPort} dotnet run     # appen, i ett eget fönster",
        $"{GenerateOptions.BaseUrlVariable}=http://localhost:5090 dotnet test             # testerna",
        "```",
        "",
        "Appen anropar stubben först när ett formulär skickas, så den kan startas före testerna. Anropar den API:et redan vid uppstart behövs en stub som redan kör.",
        "Ett formulär som skickas till stubben ska lyckas; visar appen ett fel (`role=\"alert\"`) underkänns testet av formuläret med giltiga värden.",
        "",
    ];
}
