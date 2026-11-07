using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TestGen.Scenarios;

namespace TestGen.Core.Run;

/// <summary>Korrelations-id ur svarshuvuden (för att hitta anropet i serverloggar).</summary>
public static class ExchangeCorrelation
{
    private static readonly string[] Names = { "X-Request-Id", "X-Correlation-Id", "Request-Id", "traceparent", "X-Amzn-Trace-Id" };

    /// <summary>Första träffen i listans ordning. För traceparent (<c>00-traceid-spanid-flaggor</c>) returneras trace-id.</summary>
    public static string? Find(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null) return null;
        foreach (var name in Names)
        {
            var hit = headers.FirstOrDefault(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (hit.Key is null || string.IsNullOrWhiteSpace(hit.Value)) continue;
            var v = hit.Value.Trim();
            if (name == "traceparent")
            {
                var parts = v.Split('-');
                if (parts.Length >= 3) v = parts[1];
            }
            return v;
        }
        return null;
    }
}

/// <summary>Hittar ${alias.fält} i ett anrop och registrerar vilka värden som hämtades och var de användes.</summary>
public static partial class UseCollector
{
    [GeneratedRegex(@"\$\{([^}]+)\}")]
    private static partial Regex Placeholder();

    public static List<ValueUse> Collect(ResponseStore store, string path, JsonNode? rawBody,
        IDictionary<string, string> query, IDictionary<string, string> headers)
    {
        var uses = new List<ValueUse>();
        void Add(string text, string target)
        {
            foreach (Match m in Placeholder().Matches(text))
            {
                var expr = m.Groups[1].Value;
                var node = store.Substitute(JsonValue.Create("${" + expr + "}"));
                var isString = node is JsonValue v && v.TryGetValue<string>(out _);
                var use = new ValueUse(expr, ResponseStore.ToText(node), isString, target);
                if (!uses.Contains(use)) uses.Add(use);
            }
        }
        Add(path, "sökvägen");
        foreach (var (k, v) in query) Add(v, $"parametern {k}");
        foreach (var (k, v) in headers) Add(v, $"headern {k}");
        Walk(rawBody, null, Add);
        return uses;
    }

    private static void Walk(JsonNode? node, string? name, Action<string, string> add)
    {
        switch (node)
        {
            case JsonObject o: foreach (var kv in o) Walk(kv.Value, kv.Key, add); break;
            case JsonArray a: foreach (var x in a) Walk(x, name, add); break;
            case JsonValue v when v.TryGetValue<string>(out var s) && s.Contains("${"): add(s, name ?? "bodyn"); break;
        }
    }
}
