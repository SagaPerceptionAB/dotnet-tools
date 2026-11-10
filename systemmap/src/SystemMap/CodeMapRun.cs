namespace SystemMap;

/// <summary>Filerna kartan ska skrivas till. Null = skrivs inte.</summary>
/// <param name="Hide">Namnen på delar som inte ska med (<c>--hide</c>), se <see cref="CodeMapOverview.ResolveHidden"/>.</param>
public sealed record MapFiles(string? Json, string? Mermaid, string? Html, MermaidOptions MermaidOptions, IReadOnlyList<string>? Hide = null)
{
    /// <summary>Flaggorna för Mermaid-filen, som de skrivs efter <c>--mermaid</c>.</summary>
    private static readonly string[] MermaidFlags = ["level", "direction", "entry"];

    /// <summary>Flaggorna med värde som <see cref="From"/> läser. <paramref name="jsonFlag"/> är flaggan för JSON-filen, t.ex. <c>json</c>.</summary>
    public static string[] Flags(string jsonFlag) => [jsonFlag, "mermaid", "html", "hide", .. MermaidFlags];

    /// <summary>Filerna ur flaggorna (se <see cref="Flags"/>).</summary>
    public static MapFiles From(Args a, string jsonFlag)
    {
        var mermaid = MermaidOptions.Parse(a.Get("level"), a.Get("direction"), a.Get("entry"));
        if (a.Get("mermaid") is null && MermaidFlags.Any(f => a.Get(f) is not null))
            throw new UsageException("--level, --direction och --entry gäller Mermaid-filen.", "Lägg till --mermaid karta.mmd.");
        return new MapFiles(a.Get(jsonFlag), a.Get("mermaid"), a.Get("html"), mermaid, a.GetAll("hide"));
    }
}

/// <summary>
/// Kartan från kommandoraden: läser solutionen, följer anropen, skriver kartan som text (stdout) och skriver filerna. Förlopp och
/// bekräftelser går till stderr, så att stdout bara innehåller kartan.
/// </summary>
public static class CodeMapRun
{
    /// <param name="operations">Anropen i API-beskrivningen (se <see cref="SpecReader"/>), eller null utan spec.</param>
    public static async Task<CodeMapModel> RunAsync(string solution, IReadOnlyList<ApiOperation>? operations, bool restore, MapFiles files, Terminal terminal)
    {
        using var loaded = await SolutionLoader.LoadAsync(solution, restore, progress: terminal.Err.WriteLine);
        terminal.Err.WriteLine("Följer anropen ...");
        var map = await CodeMapper.AnalyzeAsync(loaded.Solution, solution, operations, loaded.Warnings);

        // Delarna som --hide pekar ut. Ett namn som inte finns ska stoppa innan något skrivs, liksom ett anrop som inte finns i kartan.
        var overview = CodeMapOverview.Build(map);
        var hidden = CodeMapOverview.ResolveHidden(overview, files.Hide ?? []);
        var hiddenNames = overview.Nodes.Where(n => hidden.Contains(n.Id)).Select(n => n.Name).ToList();
        var shown = CodeMapOverview.Hide(map, hidden);

        // Filerna byggs först: ett anrop som inte finns i kartan ska stoppa innan något skrivs.
        // Webbsidan får allt och bara avbockat, så att man kan bocka i delarna igen; de andra filerna blir utan dem.
        var writes = new List<(string Path, string Text, string What)>();
        if (files.Json is { } json) writes.Add((json, CodeMapJson.Serialize(shown, CodeMapOverview.Hide(overview, hidden), hidden), "karta som JSON"));
        if (files.Mermaid is { } mmd) writes.Add((mmd, CodeMapMermaid.Write(map, files.MermaidOptions with { Hidden = hidden }, overview), "karta som Mermaid"));
        if (files.Html is { } page) writes.Add((page, CodeMapHtml.Write(map, hidden), "webbsida"));

        foreach (var line in CodeMapText.Lines(shown, hiddenNames)) terminal.Out.WriteLine(line);
        foreach (var warning in map.Warnings) terminal.Warning(warning);
        foreach (var (path, text, what) in writes)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(path, text);
            terminal.Confirm(path, what);
        }
        return map;
    }
}
