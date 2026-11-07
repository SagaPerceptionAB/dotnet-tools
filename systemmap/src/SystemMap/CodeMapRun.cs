namespace SystemMap;

/// <summary>Filerna kartan ska skrivas till. Null = skrivs inte.</summary>
public sealed record MapFiles(string? Json, string? Mermaid, string? Html, MermaidOptions MermaidOptions)
{
    /// <summary>Flaggorna för Mermaid-filen, som de skrivs efter <c>--mermaid</c>.</summary>
    private static readonly string[] MermaidFlags = ["level", "direction", "entry"];

    /// <summary>Flaggorna med värde som <see cref="From"/> läser. <paramref name="jsonFlag"/> är flaggan för JSON-filen, t.ex. <c>json</c>.</summary>
    public static string[] Flags(string jsonFlag) => [jsonFlag, "mermaid", "html", .. MermaidFlags];

    /// <summary>Filerna ur flaggorna (se <see cref="Flags"/>).</summary>
    public static MapFiles From(Args a, string jsonFlag)
    {
        var mermaid = MermaidOptions.Parse(a.Get("level"), a.Get("direction"), a.Get("entry"));
        if (a.Get("mermaid") is null && MermaidFlags.Any(f => a.Get(f) is not null))
            throw new UsageException("--level, --direction och --entry gäller Mermaid-filen.", "Lägg till --mermaid karta.mmd.");
        return new MapFiles(a.Get(jsonFlag), a.Get("mermaid"), a.Get("html"), mermaid);
    }
}

/// <summary>
/// Kartan från kommandoraden: läser solutionen, följer anropen, skriver kartan som text och skriver filerna. Förloppet går
/// till <c>progress</c> så att utskriften bara innehåller kartan.
/// </summary>
public static class CodeMapRun
{
    /// <param name="operations">Anropen i API-beskrivningen (se <see cref="SpecReader"/>), eller null utan spec.</param>
    public static async Task<CodeMapModel> RunAsync(string solution, IReadOnlyList<ApiOperation>? operations, bool restore, MapFiles files, TextWriter output, TextWriter progress)
    {
        using var loaded = await SolutionLoader.LoadAsync(solution, restore, progress: progress.WriteLine);
        progress.WriteLine("Följer anropen ...");
        var map = await CodeMapper.AnalyzeAsync(loaded.Solution, solution, operations, loaded.Warnings);

        // Filerna byggs först: ett anrop som inte finns i kartan ska stoppa innan något skrivs.
        var writes = new List<(string Path, string Text, string Done)>();
        if (files.Json is { } json) writes.Add((json, CodeMapJson.Serialize(map), "Kartan skriven"));
        if (files.Mermaid is { } mmd) writes.Add((mmd, CodeMapMermaid.Write(map, files.MermaidOptions), "Kartan som Mermaid skriven"));
        if (files.Html is { } page) writes.Add((page, CodeMapHtml.Write(map), "Kartan som webbsida skriven"));

        foreach (var line in CodeMapText.Lines(map)) output.WriteLine(line);
        foreach (var (path, text, done) in writes)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(path, text);
            output.WriteLine();
            output.WriteLine($"{done}: {path}");
        }
        return map;
    }
}
