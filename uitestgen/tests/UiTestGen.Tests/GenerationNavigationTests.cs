using System.IO.Compression;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using UiTestGen.Generation;
using UiTestGen.Model;

namespace UiTestGen.Tests;

/// <summary>Navigeringstester, visuella tester, föräldralösa vyer och PNG-jämförelsen i de genererade hjälparna.</summary>
public class GenerationNavigationTests
{
    private static View V(string id, string route, string? heading, IReadOnlyList<Link>? links = null, bool auth = false, string? constraint = null) =>
        new(id, route, ViewKind.Blazor, id + ".razor", null, heading,
            route.Contains("{id") ? [new("id", constraint, false)] : [], [], links ?? [], [], auth);

    /// <summary>Som ShopWeb: en lista med dynamiska länkar, en fast "Ny produkt" och en detaljvy med int-villkor.</summary>
    private static ViewCatalog Shop() => new(
    [
        V("About", "/about", "Kontakta oss", [new("/", "Till startsidan")]),
        V("CustomersCreate", "/customers/create", "Ny kund"),
        V("Home", "/", "Välkommen", [new("/customers/create", "Kunder"), new("/products", "Produkter")]),
        V("Products", "/products", "Produkter",
            [new("/products/{}", null), new("/products/create", "Ny produkt"), new("/products/create", null), new("https://example.com/", "Extern"),
             new("relativ", "Relativ"), new("/okand", "Okänd"), new("/products/{}/edit", "Ändra"), new("/admin", "Admin")]),
        V("ProductsCreate", "/products/create", "Ny produkt", [new("/products", "Avbryt")]),
        V("ProductsId", "/products/{id:int}", null, [new("/products", "Tillbaka")], constraint: "int"),
        V("Admin", "/admin", "Admin", auth: true),
    ], []);

    /// <summary>Katalogen med en enda föräldralös vy, /about.</summary>
    internal static ViewCatalog OrphanShop() => Shop();

    /// <summary>Katalogen med bara startsidan: ingen vy är föräldralös.</summary>
    internal static ViewCatalog HomeOnly() => new([V("Home", "/", "Välkommen")], []);

    private static string Tests(string id, ViewCatalog? catalog = null, Backend backend = Backend.Mock) =>
        new PlaywrightGenerator().Generate(catalog ?? Shop(), new("Shop.UiTests", backend)).Single(f => f.Path == $"Tests/{id}Tests.g.cs").Content;

    [Test]
    public void Links_get_one_test_each_with_the_right_locator()
    {
        var tests = Tests("Products");
        Assert.That(tests, Does.Contain("using System.Text.RegularExpressions;"));

        // Dynamisk adress utan text: prefix, och de andra kända vyernas fasta adresser utesluts.
        Assert.That(tests, Does.Contain("public async Task Link_to_ProductsId()"));
        Assert.That(tests, Does.Contain("await Page.Locator(\"a[href^='/products/']:not([href='/products/create'])\").First.ClickAsync();"));
        // Fast text: länkroll med exakt namn.
        Assert.That(tests, Does.Contain("public async Task Link_to_ProductsCreate()"));
        Assert.That(tests, Does.Contain("await Page.GetByRole(AriaRole.Link, new() { Name = \"Ny produkt\", Exact = true }).First.ClickAsync();"));
        // Samma mål en gång till (utan text): fast adress, och testnamnet får ett nummer.
        Assert.That(tests, Does.Contain("public async Task Link_to_ProductsCreate_2()"));
        Assert.That(tests, Does.Contain("await Page.Locator(\"a[href='/products/create']\").First.ClickAsync();"));
        // Adressen och rubriken kontrolleras mot målet.
        Assert.That(tests, Does.Contain("await Expect(Page).ToHaveURLAsync(new Regex(\"^https?://[^/]+/products/[^/]+/?(?:[?#].*)?$\"));"));
        Assert.That(tests, Does.Contain("await Expect(new ProductsCreatePage(Page).Heading).ToBeVisibleAsync();"));
        Assert.That(tests, Does.Contain("await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);"));
        Assert.That(Method(tests, "Link_to_ProductsId"), Does.Contain("await GotoAsync(ProductsPage.Path());").And.Contain("await AssertNoErrorsAsync();"));
        // ProductsId har ingen rubrik: ingen kontroll av den.
        Assert.That(Method(tests, "Link_to_ProductsId"), Does.Not.Contain("Heading"));
        Assert.That(tests.Split("[Category(\"Navigering\")]").Length - 1, Is.EqualTo(3));
    }

    [Test]
    public void Links_without_a_known_view_get_a_comment_and_no_test()
    {
        var tests = Tests("Products");
        Assert.That(tests, Does.Contain("// Inget navigeringstest för länken https://example.com/ (ingen känd vy)."));
        Assert.That(tests, Does.Contain("// Inget navigeringstest för länken relativ (ingen känd vy)."));
        Assert.That(tests, Does.Contain("// Inget navigeringstest för länken /okand (ingen känd vy)."));
        // /products/{}/edit finns inte som vy, och /admin kräver inloggning.
        Assert.That(tests, Does.Contain("// Inget navigeringstest för länken /products/{}/edit (ingen känd vy)."));
        Assert.That(tests, Does.Contain("// Inget navigeringstest för länken /admin (målet kräver inloggning, som inte stöds än)."));
        Assert.That(tests, Does.Not.Contain("Link_to_Admin"));
        // En vy utan länkar får inga navigeringstester.
        Assert.That(Tests("CustomersCreate"), Does.Not.Contain("Navigering"));
    }

    [Test]
    public void Navigation_tests_are_skipped_for_views_that_require_login()
    {
        var catalog = new ViewCatalog([V("Admin", "/admin", "Admin", [new("/", "Hem")], auth: true), V("Home", "/", "Hem")], []);
        var tests = Tests("Admin", catalog);
        Assert.That(tests, Does.Contain("// Inga navigeringstester: vyn kräver inloggning, som inte stöds än."));
        Assert.That(tests, Does.Not.Contain("Navigering"));
    }

    [TestCase("/products", "Products")]
    [TestCase("/products/", "Products")]
    [TestCase("/products/create", "ProductsCreate")]      // fast rutt vinner över parametern
    [TestCase("/products/{}", "ProductsId")]
    [TestCase("/products/7", "ProductsId")]
    [TestCase("/products/7?x=1#a", "ProductsId")]
    [TestCase("/PRODUCTS", "Products")]
    [TestCase("/", "Home")]
    [TestCase("/products/{}/edit", null)]
    [TestCase("/okand", null)]
    [TestCase("products", null)]                          // relativ
    [TestCase("https://example.com/products", null)]
    [TestCase("//example.com/products", null)]
    public void Links_resolve_to_the_most_specific_known_view(string href, string? expected) =>
        Assert.That(LinkTargets.Resolve(Shop(), href)?.Id, Is.EqualTo(expected));

    [Test]
    public void Constraints_and_optional_parameters_decide_which_view_a_link_reaches()
    {
        var catalog = new ViewCatalog([V("ById", "/items/{id:int}", null, constraint: "int"), V("ByName", "/items/{name}", null),
            V("Docs", "/docs/{slug?}", null), V("Files", "/files/{*path}", null)], []);
        Assert.That(LinkTargets.Resolve(catalog, "/items/5")!.Id, Is.EqualTo("ById"));
        Assert.That(LinkTargets.Resolve(catalog, "/items/abc")!.Id, Is.EqualTo("ByName"));
        Assert.That(LinkTargets.Resolve(catalog, "/items/{}")!.Id, Is.EqualTo("ById")); // lika många parametrar: lägst id
        Assert.That(LinkTargets.Resolve(catalog, "/docs")!.Id, Is.EqualTo("Docs"));
        Assert.That(LinkTargets.Resolve(catalog, "/docs/a")!.Id, Is.EqualTo("Docs"));
        Assert.That(LinkTargets.Resolve(catalog, "/docs/a/b"), Is.Null);
        Assert.That(LinkTargets.Resolve(catalog, "/files/a/b/c")!.Id, Is.EqualTo("Files"));
    }

    [TestCase("/", "^https?://[^/]+/?(?:[?#].*)?$")]
    [TestCase("/products/{id:int}", "^https?://[^/]+/products/[^/]+/?(?:[?#].*)?$")]
    [TestCase("/a.b/{x}/c/{y?}", @"^https?://[^/]+/a\.b/[^/]+/c(?:/[^/]+)?/?(?:[?#].*)?$")]
    [TestCase("/files/{*p}", "^https?://[^/]+/files(?:/.*)?/?(?:[?#].*)?$")]
    public void Routes_become_url_regexes(string route, string expected)
    {
        Assert.That(LinkTargets.UrlPattern(route), Is.EqualTo(expected));
        Assert.That(System.Text.RegularExpressions.Regex.IsMatch("http://localhost:5090" + System.Text.RegularExpressions.Regex.Replace(route, "{[^}]*}", "5"), expected), Is.True);
    }

    [Test]
    public void Dynamic_links_with_a_suffix_use_both_ends()
    {
        var catalog = new ViewCatalog([V("Edit", "/products/{id:int}/edit", null, constraint: "int"), V("Home", "/", null, [new("/products/{}/edit", null)])], []);
        var link = catalog.Views.Single(v => v.Id == "Home").Links[0];
        Assert.That(LinkTargets.Locator(catalog, link, catalog.Views[0]), Is.EqualTo("Page.Locator(\"a[href^='/products/'][href$='/edit']\").First"));
    }

    [Test]
    public void Visual_tests_follow_the_backend_and_login()
    {
        var mock = Tests("ProductsId", backend: Backend.Mock);
        Assert.That(mock, Does.Contain("[Category(\"Visuellt\")]").And.Contain("public async Task Looks_like_the_reference()"));
        Assert.That(mock, Does.Not.Contain("Instabil"));
        Assert.That(mock, Does.Contain("await GotoAsync(ProductsIdPage.Path(1));"));
        Assert.That(mock, Does.Contain("MaskedElements(Page, masks);").And.Contain("partial void MaskedElements(IPage page, List<ILocator> masks);"));
        Assert.That(mock, Does.Contain("await Screenshots.MatchReferenceAsync(Page, \"ProductsId\", masks);"));
        Assert.That(Tests("Products", backend: Backend.Mock), Does.Contain("await Expect(new ProductsPage(Page).Heading).ToBeVisibleAsync();"));

        var real = Tests("Products", backend: Backend.Real);
        Assert.That(real, Does.Contain("[Category(\"Visuellt\")]\n    // Med riktig backend").And.Contain("[Category(\"Instabil\")]"));

        var auth = Tests("Admin");
        Assert.That(auth, Does.Contain("// Inget visuellt test: vyn kräver inloggning").And.Not.Contain("Looks_like_the_reference"));
    }

    [Test]
    public void Support_files_set_a_fixed_browser_and_write_references_to_the_project_folder()
    {
        var files = new PlaywrightGenerator().Generate(Shop(), new("Shop.UiTests", Backend.Mock)).ToDictionary(f => f.Path);
        var shots = files["Support/Screenshots.g.cs"].Content;
        Assert.That(shots, Does.Contain("FullPage = true").And.Contain("Animations = ScreenshotAnimations.Disabled").And.Contain("Caret = ScreenshotCaret.Hide")
            .And.Contain("[data-uitest-mask]").And.Contain("Mask = mask"));
        Assert.That(shots, Does.Contain("const string ProjectFile = \"Shop.UiTests.csproj\";").And.Contain("\"Screenshots\""));
        Assert.That(shots, Does.Contain("UITEST_UPDATE_SCREENSHOTS").And.Contain("Assert.Inconclusive(").And.Contain("TestContext.AddTestAttachment("));
        Assert.That(shots, Does.Contain("DefaultThresholdPercent = 0.1;").And.Contain("DefaultTolerance = 16;"));
        Assert.That(shots, Does.Not.Contain("__"));
        Assert.That(shots, Does.Contain("SequenceEqual(referencePng)").And.Contain("ImageComparer.SamePng(").And.Contain("ImageComparer.RenderDiff("));
        Assert.That(ScreenshotsWriter.Screenshots("T", "A\\B \"C\"").Contains("ProjectFile = \"A\\\\B \\\"C\\\".csproj\";", StringComparison.Ordinal), Is.True, "projektnamnet escapas som en C#-literal");
        Assert.That(files["Support/UiTestBase.g.cs"].Content, Does.Contain("Width = 1280, Height = 800").And.Contain("Locale = \"sv-SE\""));
        Assert.That(files["README.md"].Content, Does.Contain("Inconclusive").And.Contain("UITEST_UPDATE_SCREENSHOTS=1").And.Contain("data-uitest-mask").And.Contain("MaskedElements"));
        Assert.That(files["Shop.UiTests.csproj"].Content, Does.Not.Contain("ImageSharp"));
    }

    // --- Föräldralösa vyer ---

    [Test]
    public void Orphans_are_views_nobody_links_to_except_the_start_page()
    {
        var orphans = CatalogInsights.Orphans(Shop());
        // About: ingen länkar dit. Admin: bara en länk, men Products länkar till /admin. Home är undantagen.
        Assert.That(orphans.Select(v => v.Id), Is.EqualTo(new[] { "About" }));
        Assert.That(CatalogInsights.OrphanSummary(Shop()), Is.EqualTo(new[] { "1 vy som ingen annan vy länkar till:", "  /about  (About.razor)" }));
    }

    [Test]
    public void A_link_to_itself_does_not_count_and_dynamic_links_do()
    {
        var catalog = new ViewCatalog([V("A", "/a", null, [new("/a", null), new("/b/{}", null)]), V("B", "/b/{id}", null), V("C", "/c", null, [new("/a", null)])], []);
        Assert.That(CatalogInsights.Orphans(catalog).Select(v => v.Id), Is.EqualTo(new[] { "C" }));
        Assert.That(CatalogInsights.Orphans(new ViewCatalog([], [])), Is.Empty);
        Assert.That(CatalogInsights.OrphanSummary(new ViewCatalog([V("Home", "/", null)], [])), Is.Empty);
    }

    [Test]
    public void Catalog_report_lists_the_orphans()
    {
        var report = new PlaywrightGenerator().Generate(Shop(), new("Shop.UiTests", Backend.Mock)).Single(f => f.Path == "Tests/CatalogReport.g.cs");
        Assert.That(report.Overwrite, Is.True);
        Assert.That(report.Content, Does.Contain("namespace Shop.UiTests.Tests;").And.Contain("// /about  (About.razor)").And.Contain("OrphanRoutes ="));
        Assert.That(report.Content, Does.Contain("    \"/about\","));
        var none = CatalogInsights.CatalogReport(new ViewCatalog([V("Home", "/", null)], []), "X");
        Assert.That(none, Does.Contain("// Inga: alla vyer nås från någon annan vy.").And.Contain("OrphanRoutes = [];"));
    }

    // --- PNG-jämförelsen: den genererade koden kompileras i minnet ---

    private static readonly Lazy<Assembly> Comparer = new(() =>
    {
        var source = ScreenshotsWriter.ImageComparer("T");
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create("ImageComparerTest" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source), CSharpSyntaxTree.ParseText("global using System;\nglobal using System.Collections.Generic;\nglobal using System.Linq;\nglobal using System.IO;")],
            references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.That(result.Success, Is.True, string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return Assembly.Load(stream.ToArray());
    });

    private static Type ComparerType => Comparer.Value.GetType("T.Support.ImageComparer")!;

    private static object Decode(byte[] png)
    {
        try { return ComparerType.GetMethod("DecodePng")!.Invoke(null, [png])!; }
        catch (TargetInvocationException e) { throw e.InnerException!; }
    }

    private static byte[] Encode(object image) => (byte[])ComparerType.GetMethod("EncodePng")!.Invoke(null, [image])!;
    private static int Width(object image) => (int)image.GetType().GetProperty("Width")!.GetValue(image)!;
    private static int Height(object image) => (int)image.GetType().GetProperty("Height")!.GetValue(image)!;
    private static byte[] Pixels(object image) => (byte[])image.GetType().GetProperty("Pixels")!.GetValue(image)!;
    private static object Image(int w, int h, byte[] rgba) => Activator.CreateInstance(Comparer.Value.GetType("T.Support.RgbaImage")!, w, h, rgba)!;

    private static (long Differing, long Total, double Share) Compare(object expected, object actual, int tolerance)
    {
        var result = Invoke("Compare", expected, actual, tolerance);
        var t = result.GetType();
        return ((long)t.GetProperty("DifferingPixels")!.GetValue(result)!, (long)t.GetProperty("TotalPixels")!.GetValue(result)!,
            (double)t.GetProperty("Share")!.GetValue(result)!);
    }

    private static object RenderDiff(object expected, object actual, int tolerance) => Invoke("RenderDiff", expected, actual, tolerance);

    private static bool SamePng(byte[] a, byte[] b) => (bool)Invoke("SamePng", a, b);

    private static object Invoke(string method, params object[] args)
    {
        try { return ComparerType.GetMethod(method)!.Invoke(null, args)!; }
        catch (TargetInvocationException e) { throw e.InnerException!; }
    }

    /// <summary>Ett eget PNG-skrivande för testet: valfri färgtyp och ett filter per rad, så att avkodarens alla filter prövas.</summary>
    private static byte[] Png(int width, int height, int colorType, byte[] samples, Func<int, int> filterFor)
    {
        var channels = colorType switch { 0 => 1, 2 => 3, 4 => 2, _ => 4 };
        var stride = width * channels;
        var raw = new List<byte>();
        for (var y = 0; y < height; y++)
        {
            var filter = filterFor(y);
            raw.Add((byte)filter);
            for (var x = 0; x < stride; x++)
            {
                int a = x >= channels ? samples[y * stride + x - channels] : 0;
                int b = y > 0 ? samples[(y - 1) * stride + x] : 0;
                int c = x >= channels && y > 0 ? samples[(y - 1) * stride + x - channels] : 0;
                int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                var predictor = filter switch { 0 => 0, 1 => a, 2 => b, 3 => (a + b) / 2, _ => pa <= pb && pa <= pc ? a : pb <= pc ? b : c };
                raw.Add((byte)(samples[y * stride + x] - predictor));
            }
        }
        using var output = new MemoryStream();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = (byte)colorType;
        Chunk(output, "IHDR", header);
        using var deflated = new MemoryStream();
        using (var z = new ZLibStream(deflated, CompressionLevel.SmallestSize, true)) z.Write(raw.ToArray());
        // Delad i två IDAT-bitar, som en riktig kodare kan göra.
        var all = deflated.ToArray();
        Chunk(output, "IDAT", all[..(all.Length / 2)]);
        Chunk(output, "IDAT", all[(all.Length / 2)..]);
        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void Chunk(Stream output, string type, byte[] data)
    {
        // Avkodaren kontrollerar inte CRC, så testet skriver 0.
        var length = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);
        output.Write(System.Text.Encoding.ASCII.GetBytes(type));
        output.Write(data);
        output.Write(new byte[4]);
    }

    private static byte[] Noise(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    [TestCase(6, 0), TestCase(6, 1), TestCase(6, 2), TestCase(6, 3), TestCase(6, 4)]
    [TestCase(2, 4), TestCase(2, 1)]
    public void Decoder_reads_rgb_and_rgba_with_every_filter(int colorType, int filter)
    {
        const int w = 13, h = 7;
        var channels = colorType == 6 ? 4 : 3;
        var samples = Noise(w * h * channels, filter + colorType);
        var image = Decode(Png(w, h, colorType, samples, _ => filter));
        Assert.That((Width(image), Height(image)), Is.EqualTo((w, h)));
        var expected = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            Array.Copy(samples, i * channels, expected, i * 4, channels);
            if (channels == 3) expected[i * 4 + 3] = 255;
        }
        Assert.That(Pixels(image), Is.EqualTo(expected));
    }

    [Test]
    public void Decoder_reads_mixed_filters_and_gray_images()
    {
        var rgb = Noise(20 * 10 * 3, 5);
        Assert.That(Pixels(Decode(Png(20, 10, 2, rgb, y => y % 5)))[..8], Is.EqualTo(new byte[] { rgb[0], rgb[1], rgb[2], 255, rgb[3], rgb[4], rgb[5], 255 }));
        var gray = Decode(Png(4, 2, 0, [10, 20, 30, 40, 50, 60, 70, 80], y => y + 1));
        Assert.That(Pixels(gray)[..8], Is.EqualTo(new byte[] { 10, 10, 10, 255, 20, 20, 20, 255 }));
        var grayAlpha = Decode(Png(2, 1, 4, [10, 200, 30, 100], _ => 0));
        Assert.That(Pixels(grayAlpha), Is.EqualTo(new byte[] { 10, 10, 10, 200, 30, 30, 30, 100 }));
    }

    [Test]
    public void Decoder_rejects_what_it_cannot_read()
    {
        Assert.Throws<InvalidDataException>(() => Decode([1, 2, 3]));
        var png = Png(3, 3, 6, Noise(36, 1), _ => 0);
        Assert.Throws<InvalidDataException>(() => Decode(png[..(png.Length - 30)]));
        var sixteen = (byte[])png.Clone();
        sixteen[8 + 8 + 8] = 16; // bitdjup i IHDR
        Assert.Throws<NotSupportedException>(() => Decode(sixteen));
        var interlaced = (byte[])png.Clone();
        interlaced[8 + 8 + 12] = 1;
        Assert.Throws<NotSupportedException>(() => Decode(interlaced));
    }

    [Test]
    public void Encoder_and_decoder_round_trip_and_the_encoder_makes_valid_png()
    {
        var pixels = Noise(30 * 20 * 4, 9);
        var png = Encode(Image(30, 20, pixels));
        Assert.That(png[..8], Is.EqualTo(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
        Assert.That(Pixels(Decode(png)), Is.EqualTo(pixels));
        // CRC för IHDR enligt PNG-specen: samma värde som System.IO.Hashing inte behövs för; kontrollera mot egen uträkning.
        var crc = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(29));
        Assert.That(crc, Is.EqualTo(Crc32(png.AsSpan(12, 17).ToArray())));
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }

    [Test]
    public void Compare_counts_pixels_over_the_tolerance_and_marks_them_red()
    {
        const int w = 10, h = 10;
        var a = new byte[w * h * 4];
        Array.Fill(a, (byte)100);
        var b = (byte[])a.Clone();
        b[0] += 16;                // exakt toleransen: räknas inte
        b[4 + 1] += 17;            // en över: räknas
        b[8 + 3] = 0;              // alfa skiljer 100: räknas
        b[(w * h - 1) * 4 + 2] -= 50;
        var result = Compare(Image(w, h, a), Image(w, h, b), 16);
        Assert.That((result.Differing, result.Total, result.Share), Is.EqualTo((3L, 100L, 0.03)));
        var diff = Pixels(RenderDiff(Image(w, h, a), Image(w, h, b), 16));
        Assert.That(diff[4..8], Is.EqualTo(new byte[] { 255, 0, 0, 255 }));
        Assert.That(diff[0..4], Is.Not.EqualTo(new byte[] { 255, 0, 0, 255 }));
        Assert.That(Compare(Image(w, h, a), Image(w, h, b), 0).Differing, Is.EqualTo(4L));
        Assert.That(Compare(Image(w, h, a), Image(w, h, a), 0).Differing, Is.Zero);
    }

    [Test]
    public void Compare_rejects_different_sizes()
    {
        Assert.Throws<ArgumentException>(() => Compare(Image(2, 2, new byte[16]), Image(3, 2, new byte[24]), 16));
        Assert.Throws<ArgumentException>(() => RenderDiff(Image(2, 2, new byte[16]), Image(3, 2, new byte[24]), 16));
    }

    [Test]
    public void Compare_counts_the_same_when_whole_rows_are_equal()
    {
        // Lika rader hoppas över utan pixeljämförelse: räkningen får ändå inte ändras.
        const int w = 4, h = 3;
        var a = new byte[w * h * 4];
        Array.Fill(a, (byte)7);
        var b = (byte[])a.Clone();
        b[(1 * w + 2) * 4] = 200; // en pixel på mittersta raden
        Assert.That(Compare(Image(w, h, a), Image(w, h, b), 16).Differing, Is.EqualTo(1L));
        Assert.That(Compare(Image(w, h, a), Image(w, h, a), 0).Differing, Is.Zero);
    }

    [Test]
    public void Identical_bytes_are_the_same_image_without_decoding()
    {
        // Snabbvägen: samma byte räcker. Bytena här är ingen PNG, så ett försök att avkoda skulle ge falskt (InvalidDataException).
        byte[] notAPng = [1, 2, 3, 4, 5];
        Assert.That(SamePng(notAPng, (byte[])notAPng.Clone()), Is.True);
        Assert.That(SamePng(notAPng, [1, 2, 3, 4, 6]), Is.False);

        // Andra bytes men samma pixlar (olika filter) är också samma bild; en annan pixel är det inte.
        var samples = new byte[3 * 3 * 4];
        Array.Fill(samples, (byte)9);
        var plain = Png(3, 3, 6, samples, _ => 0);
        var filtered = Png(3, 3, 6, samples, _ => 1);
        Assert.That(plain, Is.Not.EqualTo(filtered));
        Assert.That(SamePng(plain, filtered), Is.True);
        var other = (byte[])samples.Clone();
        other[0] = 10;
        Assert.That(SamePng(plain, Png(3, 3, 6, other, _ => 0)), Is.False);
        Assert.That(SamePng(plain, Png(4, 3, 6, new byte[4 * 3 * 4], _ => 0)), Is.False, "olika storlek");
    }

    private static string Method(string source, string name)
    {
        var start = source.IndexOf($"public async Task {name}()", StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0), name);
        var end = source.IndexOf("\n    }\n", start, StringComparison.Ordinal);
        return source[start..end];
    }
}
