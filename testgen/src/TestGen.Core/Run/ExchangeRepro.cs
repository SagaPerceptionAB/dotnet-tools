using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TestGen.Core;
using TestGen.Core.Run;

namespace TestGen.Core.Run;

/// <summary>
/// Felsökningstext för ett felande test: varje anrop i kedjan (tidpunkt, request, svar, korrelations-id, värden som
/// hämtades ur tidigare svar) och ett bash-skript som återskapar hela kedjan mot ett tomt system.
/// </summary>
public static partial class ExchangeText
{
    /// <summary>Förberedande anrops svar visas kortare än anropet som felade.</summary>
    private const int PrepLines = 15, PrepChars = 800;

    [GeneratedRegex(@"\$\{([^}]+)\}")]
    private static partial Regex PlaceholderRx();

    // Svarshuvuden som sällan hjälper vid felsökning och därför hoppas över i den korta listan.
    private static readonly string[] NoisyHeaders =
        { "Date", "Server", "Transfer-Encoding", "Connection", "Content-Length", "Keep-Alive", "Vary", "Cache-Control", "Accept-Ranges" };

    private sealed record Link(ValueUse Use, int Source);

    /// <summary>Anropen fram till och med anropet i fokus (anrop efter det hör inte till felet). Tom om inget anrop finns.</summary>
    private static List<Exchange> Chain(IReadOnlyList<Exchange> all)
    {
        var main = Main(all);
        if (main is null) return new();
        var index = all.ToList().FindIndex(e => ReferenceEquals(e, main));
        return all.Take(index + 1).ToList();
    }

    /// <summary>För varje anrop: vilket tidigare anrop varje använt värde kom från (-1 om det inte går att avgöra).</summary>
    private static List<List<Link>> Links(IReadOnlyList<Exchange> chain)
    {
        var result = new List<List<Link>>();
        for (var i = 0; i < chain.Count; i++)
        {
            var links = new List<Link>();
            foreach (var use in chain[i].Uses ?? Array.Empty<ValueUse>())
            {
                var src = -1;
                for (var j = i - 1; j >= 0 && src < 0; j--)
                    if (chain[j].SavedAs?.Contains(use.Alias, StringComparer.OrdinalIgnoreCase) == true) src = j;
                links.Add(new Link(use, src));
            }
            result.Add(links);
        }
        return result;
    }

    private static string[] Labels(IReadOnlyList<Exchange> chain)
    {
        var labels = new string[chain.Count];
        var prep = 0;
        for (var i = 0; i < chain.Count; i++)
        {
            var e = chain[i];
            labels[i] = e.EarlierStep ? $"steg {e.Step}{(e.TestId is null ? "" : $" ({e.TestId})")}"
                : e.Preparation is not null ? $"förberedande {++prep}"
                : "anrop";
        }
        return labels;
    }

    private static string ArrowStatus(Exchange e) => e.Status?.ToString() ?? "inget svar";

    private static List<string> DescribeFull(IReadOnlyList<Exchange> all, bool withTiming)
    {
        var chain = Chain(all);
        var lines = new List<string>();
        if (chain.Count == 0) return lines;
        var links = Links(chain);
        var labels = Labels(chain);
        for (var i = 0; i < chain.Count; i++)
        {
            var e = chain[i];
            var isMain = i == chain.Count - 1 && e.Preparation is null;
            var gave = links.SelectMany((ls, k) => k > i ? ls.Where(l => l.Source == i) : Enumerable.Empty<Link>())
                .Select(l => $"{l.Use.Field}={l.Use.Value} (används som {l.Use.Target})").Distinct().ToList();
            lines.Add(isMain
                ? $"anrop: {e.Method} {e.Url}"
                : $"{labels[i]}: {e.Method} {PathOf(e.Url)} → {ArrowStatus(e)}" + (gave.Count > 0 ? ", gav " + string.Join(", ", gave) : ""));
            if (e.StartedAtText is { } at) lines.Add("  tid: " + at);
            if (!isMain) lines.Add("  url: " + e.Url);
            if (e.Headers.Count > 0)
                lines.Add("  headers: " + string.Join(", ", e.Headers.Select(h => $"{h.Key}: {h.Value}")));
            var (ml, mc) = isMain ? (MaxLines, MaxChars) : (PrepLines, PrepChars);
            if (!string.IsNullOrEmpty(e.RequestBody)) AddBody(lines, e.RequestBody, ml, mc);
            foreach (var l in links[i])
                lines.Add($"  använder: {l.Use.Expression} = {l.Use.Value} " +
                          (l.Source >= 0 ? $"från {labels[l.Source]}, " : "") + $"används som {l.Use.Target}");
            var ind = isMain ? "" : "  ";   // anropet i fokus har "svar:" först på raden, som i den korta texten
            lines.Add(e.Status is { } s
                ? $"{ind}svar: {s}{(withTiming ? $" ({e.ElapsedMs} ms)" : "")}"
                : withTiming ? $"{ind}svar: inget svar (efter {e.ElapsedMs} ms, t.ex. timeout)" : $"{ind}svar: inget svar (t.ex. timeout)");
            if (e.Status is not null)
            {
                if (ShortHeaders(e.ResponseHeaders) is { Length: > 0 } rh) lines.Add("  svarshuvuden: " + rh);
                if (e.CorrelationId is { } id) lines.Add("  korrelations-id: " + id);
                if (string.IsNullOrWhiteSpace(e.ResponseBody)) lines.Add("  body: (tom)");
                else AddBody(lines, e.ResponseBody, ml, mc);
            }
        }
        return lines;
    }

    /// <summary>Kort lista över svarshuvudena (utan brus som Date och Server), högst tio.</summary>
    private static string ShortHeaders(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null) return "";
        var keep = headers.Where(h => !NoisyHeaders.Contains(h.Key, StringComparer.OrdinalIgnoreCase)).ToList();
        var text = string.Join(", ", keep.Take(10).Select(h => $"{h.Key}: {h.Value}"));
        return keep.Count > 10 ? text + $" (+{keep.Count - 10} till)" : text;
    }

    // --- Återskapningsskript --------------------------------------------------------------

    /// <summary>
    /// Ett bash-skript som återskapar felet mot ett tomt system: kör de förberedande anropen (och tidigare Excel-steg) i ordning,
    /// plockar ut id:n ur svaren och använder dem i nästa anrop, och skriver status och body för anropet som felade.
    /// Använder jq om det finns, annars grep/sed. Hemliga värden finns aldrig i skriptet (token läses ur $TOKEN).
    /// Null om det inte finns något anrop att återskapa.
    /// </summary>
    /// <param name="all">Anropen för testet (<c>TestOutcome.Result.Exchanges</c>).</param>
    /// <param name="title">Valfri kort beskrivning av testet som skrivs överst i skriptet.</param>
    public static string? ReproScript(IReadOnlyList<Exchange> all, string? title = null)
    {
        var chain = Chain(all);
        if (chain.Count == 0) return null;
        var links = Links(chain);
        var last = chain.Count - 1;
        var main = chain[last];

        // Vilka värden som ska plockas ur varje svar.
        var needed = new Dictionary<int, List<ValueUse>>();
        foreach (var l in links.SelectMany(x => x).Where(l => l.Source >= 0))
        {
            if (!needed.TryGetValue(l.Source, out var list)) needed[l.Source] = list = new();
            if (!list.Any(u => u.Expression.Equals(l.Use.Expression, StringComparison.OrdinalIgnoreCase))) list.Add(l.Use);
        }
        // Tidigare Excel-steg som bara läser och vars svar inte används behövs inte för att återskapa läget.
        var keep = chain.Select((e, i) => i == last || !(e.EarlierStep && e.Method is "GET" or "HEAD" && !needed.ContainsKey(i))).ToList();
        var labels = Labels(chain);

        var vars = chain.Where((_, i) => keep[i]).SelectMany(CurlCommand.Variables).DistinctBy(v => v.Variable).ToList();
        var baseUrl = (main.BaseUrl.Length > 0 ? main.BaseUrl : Uri.TryCreate(main.Url, UriKind.Absolute, out var u0) ? u0.GetLeftPart(UriPartial.Authority) : "http://localhost").TrimEnd('/');

        var sb = new StringBuilder();
        void L(string line = "") => sb.Append(line).Append('\n');
        L("#!/usr/bin/env bash");
        L($"# Återskapar felet i {main.TestId ?? "testet"}{(string.IsNullOrWhiteSpace(title) ? "" : ": " + OneLine(title))}");
        L("# Anropen körs i samma ordning som i testet, mot ett tomt system.");
        L("# Kör: BASE=http://din-server bash skriptet.sh   (utan BASE används adressen från testkörningen)");
        L($"# I testrapporten gav {main.Method} {PathOf(main.Url)}: {(main.Status is { } st ? st.ToString() : "inget svar (timeout)")}");
        L("set -euo pipefail");
        L($"BASE=${{BASE:-{baseUrl}}}");
        foreach (var (variable, header) in vars)
        {
            var hint = variable == "TOKEN" ? "din token" : "värdet för " + header;
            // Inte ${VAR:?text}: bash tar bort enkelfnuttarna i texten, och exemplet ska visas som det ska skrivas.
            L($"[ -n \"${{{variable}:-}}\" ] || {{ echo \"Sätt {variable} först: export {variable}='{hint}'\" >&2; exit 1; }}");
        }

        var anyCapture = needed.Keys.Any(i => keep[i]);
        if (anyCapture)
        {
            L();
            L("# Plockar ut ett värde ur JSON på stdin (jq om det finns, annars grep/sed).");
            L("pick() {");
            L("  local v");
            L("  if [ -z \"$2\" ]; then v=$(cat)");
            L("  elif command -v jq >/dev/null 2>&1; then v=$(jq -r \"$1\" || true)");
            L("  else v=$(grep -oE \"\\\"$2\\\"[[:space:]]*:[[:space:]]*(\\\"[^\\\"]*\\\"|[^,}[:space:]]+)\" | sed -n '1{s/^[^:]*:[[:space:]]*//;s/^\"//;s/\"$//;p;}' || true); fi");
            L("  if [ -z \"$v\" ] || [ \"$v\" = null ]; then echo \"Hittade inget '$2' i svaret från ett förberedande anrop, kan inte fortsätta.\" >&2; return 1; fi");
            L("  printf '%s' \"$v\"");
            L("}");
        }

        var varOf = links.SelectMany(x => x).Select(l => l.Use.Expression).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x, VarName, StringComparer.OrdinalIgnoreCase);
        var number = 0;
        for (var i = 0; i < chain.Count; i++)
        {
            if (!keep[i]) continue;
            var e = chain[i];
            number++;
            var isMain = i == last;
            L();
            L(isMain
                ? $"# Anropet som felade{(e.TestId is null ? "" : $" ({e.TestId})")}: {e.Method} {PathOf(e.Url)}"
                : $"# {Cap(labels[i])}: {e.Method} {PathOf(e.Url)}");
            L(CurlLine(e, varOf));
            L("status=${out##*$'\\n'}; body=${out%$'\\n'*}");
            if (!isMain && e.Status is >= 200 and <= 299)
                L($"case $status in 2??) ;; *) echo \"{Cap(labels[i])}: {e.Method} {PathOf(e.Url).Replace("\"", "\\\"").Replace("$", "\\$").Replace("`", "\\`")} gav $status i stället för 2xx. Svar: $body\" >&2; exit 1;; esac");
            if (needed.TryGetValue(i, out var picks))
                foreach (var use in picks)
                {
                    var (jq, key) = Extract(use, e);
                    L($"{varOf[use.Expression]}=$(printf '%s' \"$body\" | pick {CurlCommand.Quote(jq)} {CurlCommand.Quote(key)})   # {use.Expression}");
                }
            if (isMain)
            {
                L();
                L("# Svaret på anropet som felade:");
                L("echo \"HTTP $status\"");
                L("printf '%s\\n' \"$body\"");
            }
        }
        return sb.ToString().TrimEnd('\n');
    }

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string OneLine(string s) => s.ReplaceLineEndings(" ").Trim();

    /// <summary>Skalvariabelns namn för ett uttryck: <c>kund.id</c> blir <c>kund_id</c> (gemener, så de inte krockar med TOKEN/BASE).</summary>
    private static string VarName(string expr)
    {
        var name = new string(expr.Select(c => char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_').ToArray());
        return char.IsAsciiDigit(name[0]) ? "v_" + name : name;
    }

    /// <summary>jq-filtret och sista fältnamnet (för grep-varianten) som plockar <paramref name="use"/> ur svaret i <paramref name="source"/>.</summary>
    private static (string Jq, string Key) Extract(ValueUse use, Exchange source)
    {
        var segs = use.Expression.Contains('.') ? use.Field.Split('.').ToList() : new List<string>();
        if (segs.Count == 0) return (".", "");
        JsonNode? root = null;
        try { if (!string.IsNullOrWhiteSpace(source.ResponseBody)) root = JsonNode.Parse(source.ResponseBody); } catch (System.Text.Json.JsonException) { }
        // Skiftläge som i svaret, och under "data" om fältet inte finns högst upp (som vid körningen).
        var direct = ActualPath(root, segs);
        var viaData = direct is null && root is JsonObject o && o.FirstOrDefault(kv => kv.Key.Equals("data", StringComparison.OrdinalIgnoreCase)) is { Key: not null } d
            ? ActualPath(d.Value, segs)?.Prepend(d.Key).ToList() : null;
        var path = direct ?? viaData ?? segs;
        var jq = new StringBuilder();
        foreach (var seg in path)
            jq.Append(int.TryParse(seg, out var n) && n >= 0 ? $"[{n}]"
                : Regex.IsMatch(seg, "^[A-Za-z_][A-Za-z0-9_]*$") ? "." + seg
                : ".[\"" + seg.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"]");
        var key = path.LastOrDefault(s => !int.TryParse(s, out _)) ?? "";
        return (jq.ToString(), key);
    }

    private static List<string>? ActualPath(JsonNode? node, List<string> segs)
    {
        var result = new List<string>();
        foreach (var seg in segs)
        {
            switch (node)
            {
                case JsonObject o:
                    var hit = o.FirstOrDefault(kv => kv.Key.Equals(seg, StringComparison.OrdinalIgnoreCase));
                    if (hit.Key is null) return null;
                    result.Add(hit.Key); node = hit.Value; break;
                case JsonArray a when int.TryParse(seg, out var i) && i >= 0 && i < a.Count:
                    result.Add(seg); node = a[i]; break;
                default: return null;
            }
        }
        return result;
    }

    /// <summary>Hela curl-raden för ett anrop i skriptet: <c>out=$(curl ... -w ...)</c> med ${variabler} där värden kom ur tidigare svar.</summary>
    private static string CurlLine(Exchange e, Dictionary<string, string> varOf)
    {
        var parts = new List<string> { "out=$(curl -sS" };
        if (e.TimeoutSeconds > 0) parts.AddRange(new[] { "--max-time", e.TimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        if (e.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)) parts.Add("-I");
        else parts.AddRange(new[] { "-X", e.Method.ToUpperInvariant() });
        parts.Add(UrlWord(e, varOf));
        foreach (var (name, value) in e.Headers)
            parts.AddRange(new[] { "-H", CurlCommand.SecretVariable(name, value) is { } v ? $"\"{name}: {v.Prefix}${{{v.Variable}}}\"" : CurlCommand.Quote($"{name}: {value}") });
        if (e.RequestBody is { Length: > 0 }) parts.AddRange(new[] { "--data-raw", BodyWord(e, varOf) });
        parts.AddRange(new[] { "-w", "$'\\n%{http_code}')" });
        return string.Join(" ", parts);
    }

    private static string DqEscape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("$", "\\$").Replace("`", "\\`");

    private static string UrlWord(Exchange e, Dictionary<string, string> varOf)
    {
        var url = e.UrlTemplate ?? e.Url;
        var b = e.BaseUrl.TrimEnd('/');
        var head = "";
        if (b.Length > 0 && (url.StartsWith(b + "/", StringComparison.Ordinal) || url == b)) { head = "${BASE}"; url = url[b.Length..]; }
        var sb = new StringBuilder("\"" + head);
        var pos = 0;
        foreach (Match m in PlaceholderRx().Matches(url))
        {
            sb.Append(DqEscape(url[pos..m.Index]));
            sb.Append(varOf.TryGetValue(m.Groups[1].Value, out var v) ? "${" + v + "}" : DqEscape(m.Value));
            pos = m.Index + m.Length;
        }
        sb.Append(DqEscape(url[pos..])).Append('"');
        return sb.ToString();
    }

    private static string BodyWord(Exchange e, Dictionary<string, string> varOf)
    {
        var text = e.BodyTemplate ?? e.RequestBody!;
        // Ett tal/bool som hela värdet ("customerId": "${kund.id}" gav 3) ska stå utan citattecken i bodyn.
        foreach (var use in e.Uses ?? Array.Empty<ValueUse>())
            if (!use.IsString) text = text.Replace("\"${" + use.Expression + "}\"", "${" + use.Expression + "}");
        var sb = new StringBuilder();
        var pos = 0;
        foreach (Match m in PlaceholderRx().Matches(text))
        {
            if (m.Index > pos) sb.Append(CurlCommand.Quote(text[pos..m.Index]));
            sb.Append(varOf.TryGetValue(m.Groups[1].Value, out var v) ? "\"${" + v + "}\"" : CurlCommand.Quote(m.Value));
            pos = m.Index + m.Length;
        }
        if (pos < text.Length) sb.Append(CurlCommand.Quote(text[pos..]));
        return sb.Length == 0 ? "''" : sb.ToString();
    }
}
