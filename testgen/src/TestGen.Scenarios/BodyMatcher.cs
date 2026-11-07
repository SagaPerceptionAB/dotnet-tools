using System.Globalization;
using System.Text.Json.Nodes;

namespace TestGen.Scenarios;

/// <summary>Jämför förväntade fält mot ett svar (delmängd). Fältnamn får vara punktsökvägar (kund.namn).</summary>
public static class BodyMatcher
{
    public static List<string> Mismatches(IEnumerable<KeyValuePair<string, JsonNode?>> expected, JsonNode? actual)
    {
        var errors = new List<string>();
        foreach (var (path, want) in expected)
        {
            var got = ResponseStore.Walk(actual, path.Split('.'));
            if (got is null && actual is JsonObject && actual["data"] is { } data) got = ResponseStore.Walk(data, path.Split('.'));
            if (!Equal(want, got))
                errors.Add($"svar.{path}: väntade {Show(want)}, fick {(got is null && !Exists(actual, path) ? "(saknas)" : Show(got))}");
        }
        return errors;
    }

    /// <summary>
    /// <c>fält~regex</c>: fältets värde (som text) ska matcha regexet (osankrat, sökt var som helst).
    /// Regexen antas redan vara validerade; 1 s timeout per matchning.
    /// </summary>
    public static List<string> RegexMismatches(IEnumerable<KeyValuePair<string, string>> expected, JsonNode? actual)
    {
        var errors = new List<string>();
        foreach (var (path, pattern) in expected)
        {
            var got = ResponseStore.Walk(actual, path.Split('.'));
            if (got is null && actual is JsonObject && actual["data"] is { } data) got = ResponseStore.Walk(data, path.Split('.'));
            if (got is null) { errors.Add($"svar.{path}: väntade ~{pattern}, fick (saknas)"); continue; }
            var text = ResponseStore.ToText(got);
            try
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(text, pattern, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1)))
                    errors.Add($"svar.{path}: {ShowText(got, text)} matchar inte ~{pattern}");
            }
            catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
            {
                errors.Add($"svar.{path}: regexet ~{pattern} tog för lång tid (över 1 s) att pröva mot {ShowText(got, text)}");
            }
        }
        return errors;
    }

    private static string ShowText(JsonNode got, string text) =>
        got is JsonValue v && v.TryGetValue<string>(out _) ? $"\"{ResponseStore.Shorten(text, 80)}\"" : ResponseStore.Shorten(text, 80);

    private static bool Exists(JsonNode? actual, string path) => ResponseStore.Walk(actual, path.Split('.')) is not null;

    private static string Show(JsonNode? n) => n is null ? "null" : ResponseStore.Shorten(n.ToJsonString(), 80);

    private static bool Equal(JsonNode? want, JsonNode? got)
    {
        if (want is null || got is null) return want is null && got is null;
        if (want is JsonValue && got is JsonValue)
        {
            var w = ResponseStore.ToText(want); var g = ResponseStore.ToText(got);
            if (w == g) return true;
            return double.TryParse(w, NumberStyles.Float, CultureInfo.InvariantCulture, out var wd) &&
                   double.TryParse(g, NumberStyles.Float, CultureInfo.InvariantCulture, out var gd) && wd == gd;
        }
        if (want is JsonObject wo && got is JsonObject go)
            return wo.All(kv => Equal(kv.Value, go.FirstOrDefault(x => string.Equals(x.Key, kv.Key, StringComparison.OrdinalIgnoreCase)).Value));
        return want.ToJsonString() == got.ToJsonString();
    }
}
