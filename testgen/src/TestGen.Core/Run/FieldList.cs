using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TestGen.Core.Run;

/// <summary>Fel i en cell med fält=värde. <see cref="Technical"/> är den råa systemtexten (visas bara med --verbose).</summary>
public sealed class FieldListException(string message, string? technical = null) : ArgumentException(message)
{
    public string? Technical { get; } = technical;
}

/// <summary>
/// Tolkar cellformatet <c>fält=värde; fält=värde</c>. Värden: tal, true/false och null tolkas;
/// "citerat" värde är alltid text (och får innehålla ;). Allt annat är text. ${alias.fält} behålls som text
/// och ersätts vid körning (typen bevaras då). Ett värde som börjar med { eller [ ska vara komplett JSON.
/// </summary>
public static class FieldList
{
    public static List<KeyValuePair<string, JsonNode?>> Parse(string? text, string where = "")
    {
        var result = new List<KeyValuePair<string, JsonNode?>>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        foreach (var part in Split(text))
        {
            if (string.IsNullOrWhiteSpace(part)) continue;
            var eq = part.IndexOf('=');
            if (eq <= 0)
                throw new FieldListException($"{where}kan inte tolka \"{part.Trim()}\". Skriv fält=värde, flera åtskilda med ; (t.ex. quantity=5; status=active).");
            var name = part[..eq].Trim();
            result.Add(KeyValuePair.Create(name, ParseValue(part[(eq + 1)..], name, where)));
        }
        return result;
    }

    /// <summary>
    /// Som <see cref="Parse"/> men för "Förväntat svar": ett par kan vara <c>fält=värde</c> eller <c>fält~mönster</c>.
    /// Det första av <c>=</c> och <c>~</c> i paret avgör operatorn. Mönstret (regex) kan stå inom "citattecken" (krävs om det
    /// innehåller ;). Ett trasigt mönster ger <see cref="FieldListException"/> på svenska (den råa systemtexten i Technical).
    /// </summary>
    public static (List<KeyValuePair<string, JsonNode?>> Values, List<KeyValuePair<string, string>> Patterns) ParseExpected(string? text, string where = "")
    {
        var equals = new List<KeyValuePair<string, JsonNode?>>();
        var patterns = new List<KeyValuePair<string, string>>();
        if (string.IsNullOrWhiteSpace(text)) return (equals, patterns);
        foreach (var part in Split(text))
        {
            if (string.IsNullOrWhiteSpace(part)) continue;
            var op = part.IndexOfAny(new[] { '=', '~' });
            if (op <= 0 || part[..op].Trim().Length == 0)
                throw new FieldListException($"{where}kan inte tolka \"{part.Trim()}\". Skriv fält=värde eller fält~mönster, flera åtskilda med ; (t.ex. status=active; ordernr~^ORD-\\d{{6}}$).");
            var name = part[..op].Trim();
            if (part[op] == '=') { equals.Add(KeyValuePair.Create(name, ParseValue(part[(op + 1)..], name, where))); continue; }

            var regex = part[(op + 1)..].Trim();
            if (regex.Length >= 2 && regex[0] == '"' && regex[^1] == '"') regex = regex[1..^1].Replace("\"\"", "\"");
            if (regex.Length == 0)
                throw new FieldListException($"{where}mönstret efter ~ för {name} är tomt. Skriv ett mönster, t.ex. {name}~^ORD-\\d+$, eller {name}=värde för exakt jämförelse.");
            try { _ = new Regex(regex, RegexOptions.None, TimeSpan.FromSeconds(1)); }
            catch (ArgumentException e)
            {
                throw new FieldListException($"{where}Mönstret efter ~ för {name} går inte att läsa: {RegexReason(e)}. Vill du jämföra exakt, skriv = i stället för ~.",
                    $"{regex} ({e.Message})");
            }
            patterns.Add(KeyValuePair.Create(name, regex));
        }
        return (equals, patterns);
    }

    /// <summary>Det vanligaste som kan vara fel i ett regex, på enkel svenska.</summary>
    public static string RegexReason(ArgumentException e)
    {
        if (e is not RegexParseException p) return "mönstret är inte ett giltigt regex";
        return p.Error switch
        {
            RegexParseError.UnterminatedBracket => "en [ är inte stängd med ]",
            RegexParseError.InsufficientClosingParentheses => "en ( är inte stängd med )",
            RegexParseError.InsufficientOpeningParentheses => "en ) saknar en ( framför sig",
            RegexParseError.UnescapedEndingBackslash => "en \\ står sist; skriv \\\\ om du menar ett vanligt bakstreck",
            RegexParseError.QuantifierAfterNothing or RegexParseError.NestedQuantifiersNotParenthesized or RegexParseError.QuantifierOrCaptureGroupOutOfRange =>
                "ogiltig kvantifierare: *, + , ? eller {} måste stå efter något som kan upprepas",
            RegexParseError.ReversedQuantifierRange => "intervallet i { } är bakvänt (det lägsta talet ska stå först)",
            RegexParseError.ReversedCharacterRange => "intervallet i [ ] är bakvänt (skriv t.ex. a-z, inte z-a)",
            _ => $"mönstret har ett fel vid tecken nummer {p.Offset + 1}",
        };
    }

    /// <param name="field">Fältets namn (för felmeddelanden). Null = tolka trasig JSON-lik text som vanlig text.</param>
    public static JsonNode? ParseValue(string raw, string? field = null, string where = "")
    {
        var s = raw.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') return JsonValue.Create(s[1..^1].Replace("\"\"", "\""));
        if (s.Equals("true", StringComparison.OrdinalIgnoreCase)) return JsonValue.Create(true);
        if (s.Equals("false", StringComparison.OrdinalIgnoreCase)) return JsonValue.Create(false);
        if (s.Equals("null", StringComparison.OrdinalIgnoreCase)) return null;
        if (s.Length > 0 && s[0] is '{' or '[')
        {
            try { return JsonNode.Parse(s); }
            catch (JsonException e) when (field is not null)
            {
                var incomplete = s[0] == '{' ? s[^1] != '}' : s[^1] != ']';
                var why = incomplete ? "(saknar } eller ])" : "(kontrollera citattecken och kommatecken)";
                throw new FieldListException(
                    $"{where}värdet för {field} ser ut som JSON men är inte komplett {why}. Vill du ha det som vanlig text, sätt \"citattecken\" runt värdet.", e.Message);
            }
            catch (JsonException) { /* vanlig text */ }
        }
        var plain = s.TrimStart('-', '+');
        var leadingZero = plain.Length > 1 && plain[0] == '0' && char.IsDigit(plain[1]);   // "007" är text
        if (!leadingZero && plain.Length > 0)
        {
            var inv = CultureInfo.InvariantCulture;
            if (long.TryParse(s, NumberStyles.AllowLeadingSign, inv, out var l)) return JsonValue.Create(l);
            var dec = Regex.IsMatch(s, @"^[-+]?\d+,\d+$") ? s.Replace(',', '.') : s;
            if (Regex.IsMatch(dec, @"^[-+]?\d+\.\d+$") && double.TryParse(dec, NumberStyles.Float, inv, out var d))
                return JsonValue.Create(d);
        }
        return JsonValue.Create(s);
    }

    // Delar på ; utanför citattecken.
    private static IEnumerable<string> Split(string text)
    {
        var sb = new StringBuilder();
        var inQuotes = false;
        foreach (var c in text)
        {
            if (c == '"') inQuotes = !inQuotes;
            if (c == ';' && !inQuotes) { yield return sb.ToString(); sb.Clear(); }
            else sb.Append(c);
        }
        yield return sb.ToString();
    }
}
