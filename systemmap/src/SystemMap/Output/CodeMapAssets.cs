namespace SystemMap;

/// <summary>
/// Filerna som ritar kartan i en webbläsare: Cytoscape.js och layouten fcose (MIT, licenserna följer med i paketen),
/// <c>systemmap.js</c> och <c>systemmap.css</c>. En webbserver kan servera dem som filer (<see cref="Get"/>); <see cref="CodeMapHtml"/>
/// lägger dem i sidan.
/// </summary>
public static class CodeMapAssets
{
    /// <summary>Skripten i den ordning de ska laddas: biblioteken först, kartan sist.</summary>
    public static IReadOnlyList<string> Scripts { get; } = ["cytoscape.min.js", "layout-base.js", "cose-base.js", "cytoscape-fcose.js", "systemmap.js"];

    public const string Style = "systemmap.css";

    private static readonly Lazy<Dictionary<string, byte[]>> Files = new(() =>
        Scripts.Append(Style).ToDictionary(n => n, Read, StringComparer.Ordinal));

    /// <summary>Mallen för den fristående sidan (se <see cref="CodeMapHtml"/>).</summary>
    internal static string Page => System.Text.Encoding.UTF8.GetString(Read("page.html"));

    /// <summary>En av <see cref="Scripts"/> eller <see cref="Style"/> med sin MIME-typ, annars null.</summary>
    public static (byte[] Bytes, string ContentType)? Get(string name) =>
        Files.Value.TryGetValue(name, out var bytes)
            ? (bytes, name.EndsWith(".css", StringComparison.Ordinal) ? "text/css; charset=utf-8" : "text/javascript; charset=utf-8")
            : null;

    internal static string Text(string name) => System.Text.Encoding.UTF8.GetString(Files.Value[name]);

    private static byte[] Read(string name)
    {
        using var s = typeof(CodeMapAssets).Assembly.GetManifestResourceStream("SystemMap.Web." + name)
            ?? throw new InvalidOperationException($"{name} saknas bland de inbäddade filerna.");
        using var bytes = new MemoryStream();
        s.CopyTo(bytes);
        return bytes.ToArray();
    }
}
