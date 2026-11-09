using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TestGen.Core.Run;

/// <summary>
/// Sparade svar för ett testfall och ersättning av ${alias.fält}. JSON-typen bevaras när hela värdet
/// är en platshållare (tal förblir tal). Hittas fältet inte direkt prövas det under "data".
/// Delas av testgen run och ApiTests.
/// </summary>
public sealed partial class ResponseStore
{
    private readonly Dictionary<string, JsonNode?> _responses = new(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"\$\{([^}]+)\}")]
    private static partial Regex Placeholder();

    /// <summary>Namnen på de sparade svaren, i den ordning de sparades.</summary>
    public IReadOnlyCollection<string> Aliases => _responses.Keys;

    public bool Has(string alias) => _responses.ContainsKey(alias);
    public void Save(string alias, JsonNode? response) => _responses[alias] = response;

    /// <summary>Alias som refereras via ${alias.fält} i texten.</summary>
    public static IEnumerable<string> ReferencedAliases(string text) =>
        Placeholder().Matches(text).Select(m => m.Groups[1].Value.Split('.')[0]).Distinct();

    public JsonNode? Substitute(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var kv in o.ToList())
                {
                    var n = Substitute(kv.Value);
                    if (!ReferenceEquals(n, kv.Value)) o[kv.Key] = n;
                }
                return o;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                {
                    var old = a[i];
                    var n = Substitute(old);
                    if (!ReferenceEquals(n, old)) a[i] = n;
                }
                return a;
            case JsonValue v when v.TryGetValue<string>(out var s) && s.Contains("${"):
                var whole = Placeholder().Match(s);
                if (whole.Success && whole.Length == s.Length)
                    return Resolve(whole.Groups[1].Value)?.DeepClone();
                return JsonValue.Create(SubstituteString(s));
            default:
                return node;
        }
    }

    public string SubstituteString(string s) => !s.Contains("${") ? s : Placeholder().Replace(s, m =>
    {
        var v = Resolve(m.Groups[1].Value);
        return v is JsonValue jv && jv.TryGetValue<string>(out var str) ? str : v?.ToJsonString() ?? "";
    });

    private JsonNode? Resolve(string expr)
    {
        var parts = expr.Split('.');
        if (!_responses.TryGetValue(parts[0], out var root))
            throw new InvalidOperationException(UnknownAlias(expr, parts[0], _responses.Keys));
        if (root is null)
            throw new InvalidOperationException($"Platshållaren ${{{expr}}}: svaret som sparats som '{parts[0]}' var tomt, så det finns inget att hämta.");
        if (parts.Length == 1)
        {
            if (root is JsonObject or JsonArray) throw new InvalidOperationException(WholeResponse(expr, root));
            return root;
        }
        var cur = Walk(root, parts[1..]);
        if (cur is null && root is JsonObject && root["data"] is { } data) cur = Walk(data, parts[1..]);
        return cur ?? throw new InvalidOperationException(MissingField(expr, parts, root));
    }

    /// <summary>Fältnamnen i ett svar (objektets egna, plus de under "data" som också prövas).</summary>
    public static List<string> FieldNames(JsonNode? node)
    {
        var names = new List<string>();
        if (node is JsonObject o)
        {
            names.AddRange(o.Select(kv => kv.Key));
            if (o["data"] is JsonObject d) names.AddRange(d.Select(kv => kv.Key).Where(k => !names.Contains(k, StringComparer.OrdinalIgnoreCase)));
        }
        return names;
    }

    /// <summary>Texten för en referens ${namn.fält} till ett namn som inte sparats.</summary>
    public static string UnknownAlias(string expr, string alias, IEnumerable<string> saved)
    {
        var list = saved.ToList();
        return $"Platshållaren ${{{expr}}}: inget svar är sparat under namnet '{alias}'." + Suggest.DidYouMean(alias, list) +
               (list.Count == 0
                   ? " Inget svar har sparats ännu; ett tidigare steg i scenariot måste ha 'Spara som' ifyllt."
                   : $" Sparade namn hittills i scenariot: {Suggest.List(list)}.");
    }

    /// <summary>Texten för ${namn} utan fält när svaret är ett objekt eller en lista.</summary>
    public static string WholeResponse(string expr, JsonNode root, IReadOnlyCollection<string>? fields = null)
    {
        fields ??= FieldNames(root);
        if (root is JsonArray)
            return $"Platshållaren ${{{expr}}} är hela svaret, och det är en lista. Ange vilken post och vilket fält du vill ha, t.ex. ${{{expr}.0.id}}.";
        var example = fields.FirstOrDefault(f => f.Equals("id", StringComparison.OrdinalIgnoreCase)) ?? fields.FirstOrDefault() ?? "fält";
        return $"Platshållaren ${{{expr}}} är hela svaret (ett objekt), inte ett enskilt värde. Ange vilket fält du vill ha, t.ex. ${{{expr}.{example}}}." +
               (fields.Count > 0 ? $" Fält i svaret: {Suggest.List(fields)}." : "");
    }

    private static string MissingField(string expr, string[] parts, JsonNode root)
    {
        // Hitta första segmentet som saknas och vilket objekt det letades i.
        var node = root;
        for (var i = 1; i < parts.Length; i++)
        {
            var next = Walk(node, new[] { parts[i] });
            if (next is null && i == 1 && root is JsonObject && root["data"] is { } data) { node = data; next = Walk(node, new[] { parts[i] }); }
            if (next is null)
            {
                var where = i == 1 ? $"svaret för '{parts[0]}'" : $"'{string.Join(".", parts[..i])}' i svaret för '{parts[0]}'";
                if (node is JsonObject)
                {
                    var fields = FieldNames(node);
                    return $"Platshållaren ${{{expr}}}: {where} har inget fält '{parts[i]}'." + Suggest.DidYouMean(parts[i], fields) +
                           (fields.Count > 0 ? $" Fält där: {Suggest.List(fields)}." : "");
                }
                return $"Platshållaren ${{{expr}}}: {where} är inte ett objekt ({Shorten(node?.ToJsonString() ?? "tomt")}), så '{parts[i]}' kan inte slås upp.";
            }
            node = next;
        }
        return $"Platshållaren ${{{expr}}}: fältet finns inte i svaret {Shorten(root.ToJsonString())}.";
    }

    /// <summary>Slår upp en punktseparerad sökväg (skiftlägesokänsligt), t.ex. kund.id eller items.0.name.</summary>
    public static JsonNode? Walk(JsonNode? node, IEnumerable<string> path)
    {
        foreach (var p in path)
        {
            node = node switch
            {
                JsonObject o => o.FirstOrDefault(kv => string.Equals(kv.Key, p, StringComparison.OrdinalIgnoreCase)).Value,
                JsonArray a when int.TryParse(p, out var i) && i >= 0 && i < a.Count => a[i],
                _ => null,
            };
            if (node is null) return null;
        }
        return node;
    }

    public static string ToText(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n?.ToJsonString() ?? "";

    /// <summary>Tolkar ett svar som JSON; icke-JSON blir en sträng, tomt svar blir null.</summary>
    public static JsonNode? ParseBody(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try { return JsonNode.Parse(raw); } catch (System.Text.Json.JsonException) { return JsonValue.Create(raw); }
    }

    public static string Shorten(string s, int max = 200) => s.Length <= max ? s : s[..max] + "...";
}
