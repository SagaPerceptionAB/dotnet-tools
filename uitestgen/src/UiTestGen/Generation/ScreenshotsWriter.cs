namespace UiTestGen.Generation;

/// <summary>
/// Skriver hjälparna för visuella tester. Playwright för .NET har ingen <c>ToHaveScreenshot</c>, så jämförelsen skrivs ut som kod.
/// Bildjämförelsen (<c>ImageComparer</c>) använder bara klassbiblioteket och har en egen PNG-avkodare, så att den genererade
/// koden inte behöver något bildpaket (och ingen licens att kontrollera). Den prövas av enhetstester utan webbläsare.
/// </summary>
internal static class ScreenshotsWriter
{
    /// <summary>Miljövariabeln som slår på skapande och uppdatering av referensbilder: <c>1</c>.</summary>
    public const string UpdateVariable = "UITEST_UPDATE_SCREENSHOTS";
    /// <summary>Miljövariabeln med högsta andel avvikande pixlar, i procent.</summary>
    public const string ThresholdVariable = "UITEST_SCREENSHOT_THRESHOLD";
    /// <summary>Miljövariabeln med tolerans per färgkanal (0-255).</summary>
    public const string ToleranceVariable = "UITEST_SCREENSHOT_TOLERANCE";
    /// <summary>Miljövariabeln med mappen för referensbilderna (standard <c>Screenshots</c> i projektmappen).</summary>
    public const string DirectoryVariable = "UITEST_SCREENSHOT_DIR";
    public const string DefaultThresholdPercent = "0.1";
    public const int DefaultTolerance = 16;

    public static string Screenshots(string ns, string projectName) => ScreenshotsSource
        .Replace("__NS__", ns)
        .Replace("__PROJECT__", CSharp.Literal(projectName + ".csproj"))
        .Replace("__UPDATE__", UpdateVariable)
        .Replace("__THRESHOLD__", ThresholdVariable)
        .Replace("__TOLERANCE__", ToleranceVariable)
        .Replace("__DIRECTORY__", DirectoryVariable)
        .Replace("__DEFAULT_THRESHOLD__", DefaultThresholdPercent)
        .Replace("__DEFAULT_TOLERANCE__", DefaultTolerance.ToString(System.Globalization.CultureInfo.InvariantCulture))
        .ReplaceLineEndings("\n");

    public static string ImageComparer(string ns) => ImageComparerSource.Replace("__NS__", ns).ReplaceLineEndings("\n");

    private static readonly string ScreenshotsSource = CSharp.Header + "\n\n" + """
        using System.Globalization;
        using Microsoft.Playwright;
        using NUnit.Framework;

        namespace __NS__.Support;

        /// <summary>
        /// Visuella tester: jämför en skärmdump av hela sidan med referensbilden <c>Screenshots/{Vy}.png</c> i projektmappen.
        /// Playwright för .NET har ingen ToHaveScreenshot, så jämförelsen görs här (se <see cref="ImageComparer"/>).
        /// <list type="bullet">
        /// <item>Saknas referensen, eller är __UPDATE__=1: bilden skrivs som referens och testet blir Inconclusive (inte grönt), så att det syns att något nytt skapats som ska checkas in.</item>
        /// <item>Annars räknas pixlarna som skiljer mer än toleransen per färgkanal. Är andelen större än gränsen fäller testet, och bilderna .actual.png och .diff.png (skillnaden i rött) skrivs och bifogas.</item>
        /// <item>Element med attributet <c>data-uitest-mask</c> och element som anges per vy döljs bakom en färgad ruta.</item>
        /// </list>
        /// </summary>
        public static class Screenshots
        {
            /// <summary>Högsta andel pixlar som får avvika, i procent. Åsidosätts med miljövariabeln __THRESHOLD__.</summary>
            public const double DefaultThresholdPercent = __DEFAULT_THRESHOLD__;

            /// <summary>Högsta skillnad per färgkanal (0-255) som inte räknas som avvikelse. Åsidosätts med miljövariabeln __TOLERANCE__.</summary>
            public const int DefaultTolerance = __DEFAULT_TOLERANCE__;

            private const string ProjectFile = __PROJECT__;

            private static readonly CultureInfo Swedish = CultureInfo.GetCultureInfo("sv-SE");

            /// <summary>Mappen med referensbilderna: <c>Screenshots</c> i projektmappen (den som har projektfilen), alltså inte under bin.</summary>
            public static string ReferenceDirectory()
            {
                if (Environment.GetEnvironmentVariable("__DIRECTORY__") is { Length: > 0 } custom) return Path.GetFullPath(custom);
                for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir is not null; dir = dir.Parent)
                    if (File.Exists(Path.Combine(dir.FullName, ProjectFile))) return Path.Combine(dir.FullName, "Screenshots");
                return Path.Combine(Directory.GetCurrentDirectory(), "Screenshots");
            }

            /// <summary>Mappen dit .actual.png och .diff.png skrivs när en jämförelse fäller.</summary>
            public static string DiffDirectory() => Path.Combine(TestContext.CurrentContext.WorkDirectory, "ScreenshotDiffs");

            private static bool UpdateRequested => Environment.GetEnvironmentVariable("__UPDATE__") is "1" or "true" or "on";

            private static double ThresholdPercent => double.TryParse(Environment.GetEnvironmentVariable("__THRESHOLD__")?.Replace(',', '.'),
                NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : DefaultThresholdPercent;

            private static int Tolerance => int.TryParse(Environment.GetEnvironmentVariable("__TOLERANCE__"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? Math.Clamp(value, 0, 255) : DefaultTolerance;

            /// <summary>Tar en skärmdump av hela sidan och jämför den med referensbilden för vyn. Se klassen för vad som händer.</summary>
            /// <param name="viewId">Vyns id, som är referensbildens filnamn.</param>
            /// <param name="masks">Element som döljs, utöver <c>[data-uitest-mask]</c>.</param>
            public static async Task MatchReferenceAsync(IPage page, string viewId, IReadOnlyList<ILocator>? masks = null)
            {
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                var mask = new List<ILocator> { page.Locator("[data-uitest-mask]") };
                if (masks is not null) mask.AddRange(masks);
                var actualBytes = await page.ScreenshotAsync(new()
                {
                    Type = ScreenshotType.Png,
                    FullPage = true,
                    Animations = ScreenshotAnimations.Disabled,
                    Caret = ScreenshotCaret.Hide,
                    Scale = ScreenshotScale.Css,
                    Mask = mask,
                });
                MatchReference(actualBytes, viewId);
            }

            /// <summary>Jämför en PNG-bild med referensbilden för vyn. Skild från webbläsaren, så att den går att pröva utan den.</summary>
            public static void MatchReference(byte[] actualPng, string viewId)
            {
                var referencePath = Path.Combine(ReferenceDirectory(), viewId + ".png");
                var exists = File.Exists(referencePath);
                if (exists && !UpdateRequested)
                {
                    Compare(actualPng, File.ReadAllBytes(referencePath), referencePath, viewId);
                    return;
                }
                if (exists && ImageComparer.SamePng(actualPng, File.ReadAllBytes(referencePath))) return; // uppdatering utan ändring: lämna filen orörd
                Directory.CreateDirectory(Path.GetDirectoryName(referencePath)!);
                File.WriteAllBytes(referencePath, actualPng);
                Assert.Inconclusive(exists
                    ? $"Referensbilden {referencePath} uppdaterades (__UPDATE__=1). Granska den och checka in den."
                    : $"Referensbilden {referencePath} saknades och skapades nu. Granska den och checka in den; nästa körning jämför mot den.");
            }

            private static void Compare(byte[] actualPng, byte[] referencePng, string referencePath, string viewId)
            {
                if (actualPng.AsSpan().SequenceEqual(referencePng)) return; // samma byte: samma bild, ingen avkodning behövs
                RgbaImage expected;
                try { expected = ImageComparer.DecodePng(referencePng); }
                catch (Exception e) when (e is InvalidDataException or NotSupportedException)
                {
                    Assert.Fail($"Referensbilden {referencePath} går inte att läsa: {e.Message} Skapa den på nytt med __UPDATE__=1.");
                    return;
                }
                var actual = ImageComparer.DecodePng(actualPng);
                if (expected.Width != actual.Width || expected.Height != actual.Height)
                {
                    var sizePath = Save(viewId + ".actual.png", actualPng);
                    Assert.Fail($"Sidan har en annan storlek än referensbilden: {actual.Width}x{actual.Height} mot {expected.Width}x{expected.Height} pixlar.\n"
                        + $"Referens: {referencePath}\nFaktisk: {sizePath}\nÄr ändringen avsiktlig, kör om med __UPDATE__=1 och checka in den nya bilden.");
                    return;
                }

                var result = ImageComparer.Compare(expected, actual, Tolerance);
                var share = result.Share * 100;
                if (share <= ThresholdPercent) return;
                var actualPath = Save(viewId + ".actual.png", actualPng);
                var diffPath = Save(viewId + ".diff.png", ImageComparer.EncodePng(ImageComparer.RenderDiff(expected, actual, Tolerance)));
                Assert.Fail($"Sidan skiljer sig från referensbilden: {share.ToString("0.###", Swedish)} % av pixlarna ({result.DifferingPixels.ToString("N0", Swedish)} av {result.TotalPixels.ToString("N0", Swedish)}) "
                    + $"avviker mer än {Tolerance} per färgkanal; gränsen är {ThresholdPercent.ToString("0.###", Swedish)} %.\n"
                    + $"Referens: {referencePath}\nFaktisk: {actualPath}\nSkillnad (rött): {diffPath}\n"
                    + "Är ändringen avsiktlig, kör om med __UPDATE__=1 och checka in den nya bilden.");
            }

            private static string Save(string fileName, byte[] png)
            {
                var path = Path.Combine(DiffDirectory(), fileName);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, png);
                TestContext.AddTestAttachment(path, fileName.EndsWith(".diff.png", StringComparison.Ordinal) ? "Skillnad (rött)" : "Faktisk bild");
                return path;
            }
        }
        """ + "\n";

    private static readonly string ImageComparerSource = CSharp.Header + "\n\n" + """
        using System.Buffers.Binary;
        using System.IO.Compression;
        using System.Text;

        namespace __NS__.Support;

        /// <summary>En bild som 8-bitars RGBA, rad för rad uppifrån.</summary>
        public sealed class RgbaImage(int width, int height, byte[] pixels)
        {
            public int Width { get; } = width;
            public int Height { get; } = height;
            /// <summary>Fyra byte per pixel: röd, grön, blå, alfa.</summary>
            public byte[] Pixels { get; } = pixels;
        }

        /// <param name="DifferingPixels">Antal pixlar där någon färgkanal skiljer mer än toleransen.</param>
        public sealed record ImageComparison(long DifferingPixels, long TotalPixels)
        {
            /// <summary>Andelen avvikande pixlar, 0-1.</summary>
            public double Share => TotalPixels == 0 ? 0 : (double)DifferingPixels / TotalPixels;
        }

        /// <summary>
        /// Läser och skriver PNG och jämför bilder, utan några paket. Avkodaren klarar det Chromium skriver: 8 bitar per kanal,
        /// gråskala, gråskala med alfa, RGB och RGBA, utan interlace.
        /// </summary>
        public static class ImageComparer
        {
            private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

            public static RgbaImage DecodePng(byte[] png)
            {
                if (png.Length < 8 || !png.AsSpan(0, 8).SequenceEqual(Signature)) throw new InvalidDataException("Filen är ingen PNG-bild.");
                int width = 0, height = 0, colorType = -1;
                using var compressed = new MemoryStream();
                var pos = 8;
                var ended = false;
                while (!ended && pos + 12 <= png.Length)
                {
                    var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos));
                    var type = Encoding.ASCII.GetString(png, pos + 4, 4);
                    var data = pos + 8;
                    if (length < 0 || data + (long)length + 4 > png.Length) throw new InvalidDataException("PNG-filen är avklippt.");
                    switch (type)
                    {
                        case "IHDR":
                            if (length < 13) throw new InvalidDataException("PNG-huvudet är för kort.");
                            width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(data));
                            height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(data + 4));
                            colorType = png[data + 9];
                            if (png[data + 8] != 8) throw new NotSupportedException($"PNG med {png[data + 8]} bitar per kanal stöds inte, bara 8.");
                            if (png[data + 12] != 0) throw new NotSupportedException("PNG med interlace stöds inte.");
                            break;
                        case "IDAT":
                            compressed.Write(png, data, length);
                            break;
                        case "IEND":
                            ended = true;
                            break;
                    }
                    pos = data + length + 4;
                }
                var channels = colorType switch { 0 => 1, 2 => 3, 4 => 2, 6 => 4, _ => throw new NotSupportedException($"PNG med färgtyp {colorType} stöds inte.") };
                if (width <= 0 || height <= 0 || (long)width * height * 4 > int.MaxValue) throw new InvalidDataException("PNG-bildens storlek är ogiltig.");

                var stride = width * channels;
                var raw = new byte[(stride + 1) * height];
                compressed.Position = 0;
                try
                {
                    using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
                    zlib.ReadExactly(raw);
                }
                catch (Exception e) when (e is EndOfStreamException or InvalidDataException) { throw new InvalidDataException("PNG-bilddatan är skadad.", e); }

                var scan = new byte[stride * height];
                for (var y = 0; y < height; y++)
                {
                    var filter = raw[y * (stride + 1)];
                    var src = y * (stride + 1) + 1;
                    var row = y * stride;
                    for (var x = 0; x < stride; x++)
                    {
                        int a = x >= channels ? scan[row + x - channels] : 0;
                        int b = y > 0 ? scan[row - stride + x] : 0;
                        int c = x >= channels && y > 0 ? scan[row - stride + x - channels] : 0;
                        var predictor = filter switch
                        {
                            0 => 0,
                            1 => a,
                            2 => b,
                            3 => (a + b) / 2,
                            4 => Paeth(a, b, c),
                            _ => throw new InvalidDataException($"Okänt PNG-filter {filter}."),
                        };
                        scan[row + x] = (byte)(raw[src + x] + predictor);
                    }
                }

                var rgba = new byte[width * height * 4];
                for (var i = 0; i < width * height; i++)
                {
                    var s = i * channels;
                    var d = i * 4;
                    switch (channels)
                    {
                        case 1: rgba[d] = rgba[d + 1] = rgba[d + 2] = scan[s]; rgba[d + 3] = 255; break;
                        case 2: rgba[d] = rgba[d + 1] = rgba[d + 2] = scan[s]; rgba[d + 3] = scan[s + 1]; break;
                        case 3: rgba[d] = scan[s]; rgba[d + 1] = scan[s + 1]; rgba[d + 2] = scan[s + 2]; rgba[d + 3] = 255; break;
                        default: Array.Copy(scan, s, rgba, d, 4); break;
                    }
                }
                return new RgbaImage(width, height, rgba);
            }

            private static int Paeth(int a, int b, int c)
            {
                var p = a + b - c;
                var pa = Math.Abs(p - a);
                var pb = Math.Abs(p - b);
                var pc = Math.Abs(p - c);
                return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
            }

            public static byte[] EncodePng(RgbaImage image)
            {
                using var output = new MemoryStream();
                output.Write(Signature);
                var header = new byte[13];
                BinaryPrimitives.WriteInt32BigEndian(header, image.Width);
                BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), image.Height);
                header[8] = 8; // bitar per kanal
                header[9] = 6; // RGBA
                WriteChunk(output, "IHDR", header);

                var stride = image.Width * 4;
                var raw = new byte[(stride + 1) * image.Height];
                for (var y = 0; y < image.Height; y++) // filter 0 (inget) på varje rad
                    Array.Copy(image.Pixels, y * stride, raw, y * (stride + 1) + 1, stride);
                using (var deflated = new MemoryStream())
                {
                    using (var zlib = new ZLibStream(deflated, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(raw);
                    WriteChunk(output, "IDAT", deflated.ToArray());
                }
                WriteChunk(output, "IEND", []);
                return output.ToArray();
            }

            private static void WriteChunk(Stream output, string type, byte[] data)
            {
                var length = new byte[4];
                BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
                output.Write(length);
                var typeBytes = Encoding.ASCII.GetBytes(type);
                output.Write(typeBytes);
                output.Write(data);
                var crc = Crc(Crc(0xFFFFFFFF, typeBytes), data) ^ 0xFFFFFFFF;
                var crcBytes = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
                output.Write(crcBytes);
            }

            private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
            {
                var c = (uint)n;
                for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                return c;
            }).ToArray();

            private static uint Crc(uint crc, byte[] data)
            {
                foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
                return crc;
            }

            /// <summary>
            /// Sant om bilderna är pixel för pixel lika. Är bytena desamma är bilderna det, och då avkodas ingenting; annars jämförs pixlarna
            /// utan tolerans. Ett fel i en PNG ger falskt.
            /// </summary>
            public static bool SamePng(byte[] a, byte[] b)
            {
                if (a.AsSpan().SequenceEqual(b)) return true;
                try
                {
                    var x = DecodePng(a);
                    var y = DecodePng(b);
                    return x.Width == y.Width && x.Height == y.Height && Compare(x, y, 0).DifferingPixels == 0;
                }
                catch (Exception e) when (e is InvalidDataException or NotSupportedException) { return false; }
            }

            /// <summary>
            /// Räknar pixlarna där någon färgkanal skiljer mer än <paramref name="tolerance"/>. Bilderna måste vara lika stora.
            /// Bygger ingen skillnadsbild (se <see cref="RenderDiff"/>), så en lyckad jämförelse allokerar inget.
            /// </summary>
            public static ImageComparison Compare(RgbaImage expected, RgbaImage actual, int tolerance)
            {
                RequireSameSize(expected, actual);
                var total = (long)expected.Width * expected.Height;
                long differing = 0;
                var rowLength = expected.Width * 4;
                for (var y = 0; y < expected.Height; y++)
                {
                    var row = y * rowLength;
                    if (expected.Pixels.AsSpan(row, rowLength).SequenceEqual(actual.Pixels.AsSpan(row, rowLength))) continue; // lika rader är det vanliga
                    for (var p = row; p < row + rowLength; p += 4)
                        if (IsDifferent(expected.Pixels, actual.Pixels, p, tolerance)) differing++;
                }
                return new ImageComparison(differing, total);
            }

            /// <summary>Skillnadsbilden: avvikande pixlar (mer än <paramref name="tolerance"/>) i rött, resten som en blek kopia av referensen.</summary>
            public static RgbaImage RenderDiff(RgbaImage expected, RgbaImage actual, int tolerance)
            {
                RequireSameSize(expected, actual);
                var diff = new byte[expected.Pixels.Length];
                for (var p = 0; p < diff.Length; p += 4)
                {
                    if (IsDifferent(expected.Pixels, actual.Pixels, p, tolerance))
                    {
                        diff[p] = 255;
                    }
                    else
                    {
                        var pale = (byte)(255 - (255 - (expected.Pixels[p] + expected.Pixels[p + 1] + expected.Pixels[p + 2]) / 3) / 4);
                        diff[p] = diff[p + 1] = diff[p + 2] = pale;
                    }
                    diff[p + 3] = 255;
                }
                return new RgbaImage(expected.Width, expected.Height, diff);
            }

            private static bool IsDifferent(byte[] expected, byte[] actual, int p, int tolerance)
            {
                for (var c = 0; c < 4; c++)
                    if (Math.Abs(expected[p + c] - actual[p + c]) > tolerance) return true;
                return false;
            }

            private static void RequireSameSize(RgbaImage expected, RgbaImage actual)
            {
                if (expected.Width != actual.Width || expected.Height != actual.Height) throw new ArgumentException("Bilderna är olika stora.");
            }
        }
        """ + "\n";
}
