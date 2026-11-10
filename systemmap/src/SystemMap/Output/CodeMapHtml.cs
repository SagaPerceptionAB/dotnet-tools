using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SystemMap;

/// <summary>
/// Kartan som en fristående webbsida: nätverket i systemmap.js, med Cytoscape.js, fcose, kartan och Mermaid-texten för varje
/// nivå och anrop i en enda fil. Sidan hämtar inget utifrån och går att öppna utan nätverk, lägga i dokumentationen eller
/// spara i ett bygge. Samma karta ger samma fil.
/// </summary>
public static partial class CodeMapHtml
{
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <param name="hidden">Id:n på delar som är avbockade i sidan när den öppnas (se <see cref="CodeMapOverview.Hide(Overview, IReadOnlySet{string})"/>).
    /// Kartan innehåller allt, så att man kan bocka i dem igen.</param>
    /// <param name="showUnreached">Om "Visa delar som inte nås" är ibockad när sidan öppnas (det man själv väljer i sidan sparas och gäller före).</param>
    public static string Write(CodeMapModel map, IReadOnlySet<string>? hidden = null, bool showUnreached = true)
    {
        // Bara solutionens filnamn: sidan kan delas, och sökvägen på den här datorn hör inte hemma där.
        var shown = map with { Solution = Path.GetFileName(map.Solution) };
        var overview = CodeMapOverview.Build(shown);

        // Mermaid-texten för varje nivå och anrop, vänster till höger. Uppifrån och ned skiljer sig bara på första raden,
        // som sidan byter själv.
        var mermaid = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in map.EntryPoints.Select(CodeMapOverview.EntryKey).Prepend(""))
            foreach (var level in MermaidOptions.Levels)
                mermaid[$"{level}|{entry}"] = CodeMapMermaid.Write(shown, MermaidOptions.Parse(level, null, entry), overview);

        var scripts = new StringBuilder();
        foreach (var name in CodeMapAssets.Scripts)
            scripts.Append("<script>\n").Append(InlineScript(CodeMapAssets.Text(name))).Append("\n</script>\n");
        var parts = new Dictionary<string, string>
        {
            ["TITLE"] = WebUtility.HtmlEncode(shown.Solution),
            ["STYLE"] = CodeMapAssets.Text(CodeMapAssets.Style),
            ["MAP"] = InlineJson(CodeMapJson.ToDto(shown, overview)),
            ["MERMAID"] = InlineJson(mermaid),
            ["HIDDEN"] = InlineJson((hidden ?? new HashSet<string>()).Order(StringComparer.Ordinal).ToList()),
            ["UNREACHED"] = showUnreached ? "true" : "false",
            ["SCRIPTS"] = scripts.ToString().TrimEnd('\n'),
        };
        // En genomgång: det som sätts in letas inte igenom igen.
        return Placeholder().Replace(CodeMapAssets.Page, m => parts[m.Groups[1].Value]);
    }

    /// <summary>Ett skript i en skripttagg får inte innehålla <c>&lt;/script</c>; i JavaScript betyder <c>&lt;\/script</c> samma sak.</summary>
    private static string InlineScript(string script) => script.Replace("</script", "<\\/script", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// JSON i en skripttagg, utan onödig kodning av citattecken och å, ä, ö. <c>&lt;/</c> och <c>&lt;!--</c> kan bara stå i
    /// strängar, och där betyder <c>&lt;\/</c> och <c><!--</c> samma sak.
    /// </summary>
    private static string InlineJson(object value) =>
        JsonSerializer.Serialize(value, Json).Replace("</", "<\\/").Replace("<!--", "\\u003C!--");

    [GeneratedRegex("__(TITLE|STYLE|MAP|MERMAID|HIDDEN|UNREACHED|SCRIPTS)__")]
    private static partial Regex Placeholder();
}
