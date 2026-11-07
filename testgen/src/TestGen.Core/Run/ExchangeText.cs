using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TestGen.Core.Run;

/// <summary>
/// Gör inspelade anrop till text för terminalen och rapporterna (samma text överallt):
/// förberedande anrop kort, sedan det anrop som är i fokus med headers, body och svar.
/// </summary>
public static partial class ExchangeText
{
    public const int MaxLines = 40;
    public const int MaxChars = 2000;
    public const string SetupMarker = "# Sätt först: ";
    public const string SecretTip = "Tips: *** ersätter känsliga värden (t.ex. din token). I curl-raden används en variabel i stället ($TOKEN): byt ut 'din token' i raden export mot din riktiga token och kör den innan du kör curl.";

    /// <summary>
    /// Anropet i fokus: det sista icke-förberedande. Saknas det (förberedande anrop föll) tas det sista anropet
    /// om det inte lyckades. Annars null.
    /// </summary>
    public static Exchange? Main(IReadOnlyList<Exchange> all)
    {
        var main = all.LastOrDefault(e => e.Preparation is null);
        if (main is not null) return main;
        var last = all.LastOrDefault();
        return last is not null && last.Status is not (>= 200 and <= 299) ? last : null;
    }

    /// <summary>Rader utan indrag (underrader är indragna med två mellanslag). Utan curl-raden, se <see cref="Curl"/>.</summary>
    public static List<string> Describe(IReadOnlyList<Exchange> all, bool withTiming = true, bool full = false)
    {
        if (full) return DescribeFull(all, withTiming);
        string Ms(long ms) => withTiming ? $" ({ms} ms)" : "";
        var lines = new List<string>();
        var main = Main(all);
        foreach (var p in all.Where(e => e.Preparation is not null && !e.EarlierStep && !ReferenceEquals(e, main)))
            lines.Add($"förberedande: {p.Method} {PathOf(p.Url)} -> {StatusText(p)}{Ms(p.ElapsedMs)}");
        if (main is null) return lines;

        lines.Add($"{(main.Preparation is null ? "anrop" : "förberedande anrop")}: {main.Method} {main.Url}");
        if (main.Headers.Count > 0)
            lines.Add("  headers: " + string.Join(", ", main.Headers.Select(h => $"{h.Key}: {h.Value}")));
        if (!string.IsNullOrEmpty(main.RequestBody)) AddBody(lines, main.RequestBody);
        lines.Add(main.Status is { } s ? $"svar: {s}{Ms(main.ElapsedMs)}" : withTiming ? $"svar: inget svar (efter {main.ElapsedMs} ms, t.ex. timeout)" : "svar: inget svar (t.ex. timeout)");
        if (main.Status is not null)
        {
            if (string.IsNullOrWhiteSpace(main.ResponseBody)) lines.Add("  body: (tom)");
            else AddBody(lines, main.ResponseBody);
        }
        return lines;
    }

    /// <summary>
    /// Curl för anropet i fokus, utan "curl:"-prefix (null om inget anrop). Är en header maskad kommer först en kommentar
    /// ("# Sätt först: ...") och en riktig rad <c>export TOKEN='din token'</c>, och sedan curl-raden, som läser variabeln.
    /// </summary>
    public static string? Curl(IReadOnlyList<Exchange> all)
    {
        if (Main(all) is not { } m) return null;
        var curl = CurlCommand.Build(m);
        var vars = CurlCommand.Variables(m);
        if (vars.Count == 0) return curl;
        var exports = string.Join("\n", vars.Select(v => $"export {v.Variable}={CurlCommand.Quote(v.Variable == "TOKEN" ? "din token" : "värdet för " + v.Header)}"));
        var what = vars.Count == 1 && vars[0].Variable == "TOKEN" ? "din token" : "dina värden";
        return SetupMarker + $"byt ut texten i citattecken mot {what} och kör raderna nedan.\n" + exports + "\n" + curl;
    }

    private static string StatusText(Exchange e) => e.Status?.ToString() ?? "inget svar";

    private static string PathOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.PathAndQuery : url;

    private static void AddBody(List<string> lines, string body, int maxLines = MaxLines, int maxChars = MaxChars)
    {
        lines.Add("  body:");
        foreach (var l in Shorten(Pretty(body), maxLines, maxChars)) lines.Add("    " + l);
    }

    /// <summary>Formaterad JSON; allt annat oförändrat.</summary>
    public static string Pretty(string body)
    {
        try { return JsonNode.Parse(body)!.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }); }
        catch (Exception e) when (e is JsonException or NullReferenceException) { return body; }
    }

    /// <summary>Högst <see cref="MaxLines"/> rader och <see cref="MaxChars"/> tecken; avkortning markeras.</summary>
    public static List<string> Shorten(string text) => Shorten(text, MaxLines, MaxChars);

    /// <summary>Som <see cref="Shorten(string)"/> med egna gränser.</summary>
    public static List<string> Shorten(string text, int maxLines, int maxChars)
    {
        var all = text.ReplaceLineEndings("\n").Split('\n');
        var result = new List<string>();
        var chars = 0;
        foreach (var l in all)
        {
            if (result.Count >= maxLines || chars + l.Length > maxChars)
            {
                var room = maxChars - chars;
                if (result.Count < maxLines && room > 0) { result.Add(l[..room]); }
                result.Add($"... (avkortat, {text.Length} tecken totalt)");
                return result;
            }
            result.Add(l);
            chars += l.Length;
        }
        return result;
    }
}
