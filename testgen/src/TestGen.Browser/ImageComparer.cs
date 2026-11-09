using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace TestGen.Browser;

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
