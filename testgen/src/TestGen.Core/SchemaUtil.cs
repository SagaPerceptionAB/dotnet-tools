using System.Text.Json.Nodes;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;

namespace TestGen.Core;

public static class SchemaUtil
{
    /// <summary>Slår ihop allOf (och väljer första oneOf/anyOf) till properties + required.</summary>
    public static (SortedDictionary<string, OpenApiSchema> Props, SortedSet<string> Required) Flatten(OpenApiSchema schema)
    {
        var props = new SortedDictionary<string, OpenApiSchema>(StringComparer.Ordinal);
        var required = new SortedSet<string>(StringComparer.Ordinal);
        Collect(schema, props, required, 0);
        return (props, required);
    }

    private static void Collect(OpenApiSchema s, IDictionary<string, OpenApiSchema> props, ISet<string> req, int depth)
    {
        if (depth > 10) return;
        foreach (var a in s.AllOf) Collect(a, props, req, depth + 1);
        if (s.AllOf.Count == 0 && s.Properties.Count == 0)
        {
            var alt = s.OneOf.FirstOrDefault() ?? s.AnyOf.FirstOrDefault();
            if (alt is not null) Collect(alt, props, req, depth + 1);
        }
        foreach (var (k, v) in s.Properties) props[k] = v;
        foreach (var r in s.Required) req.Add(r);
    }

    public static bool IsObject(OpenApiSchema s) => TypeOf(s) == "object";

    /// <summary>Alternativen (oneOf/anyOf) om schemat inte har något eget innehåll, annars en tom lista.</summary>
    public static IReadOnlyList<OpenApiSchema> Alternatives(OpenApiSchema s)
    {
        if (!string.IsNullOrEmpty(s.Type) || s.Properties.Count > 0 || s.AllOf.Count > 0) return Array.Empty<OpenApiSchema>();
        return s.OneOf.Count > 0 ? s.OneOf.ToList() : s.AnyOf.ToList();
    }

    /// <summary>Schemat med oneOf/anyOf ersatt av första alternativet (rekursivt). Övriga scheman returneras som de är.</summary>
    public static OpenApiSchema Effective(OpenApiSchema s)
    {
        for (var i = 0; i < 10; i++)
        {
            var alts = Alternatives(s);
            if (alts.Count == 0) return s;
            s = alts[0];
        }
        return s;
    }

    public static string? TypeOf(OpenApiSchema s)
    {
        s = Effective(s);
        if (!string.IsNullOrEmpty(s.Type)) return s.Type;
        if (s.AllOf.Count > 0) return s.AllOf.Select(TypeOf).FirstOrDefault(t => t is not null) ?? "object";
        return s.Properties.Count > 0 ? "object" : null;
    }

    public static JsonNode? ToNode(IOpenApiAny? any) => any switch
    {
        null => null,
        OpenApiNull => null,
        OpenApiString v => JsonValue.Create(v.Value),
        OpenApiInteger v => JsonValue.Create(v.Value),
        OpenApiLong v => JsonValue.Create(v.Value),
        OpenApiFloat v => JsonValue.Create(v.Value),
        OpenApiDouble v => JsonValue.Create(v.Value),
        OpenApiBoolean v => JsonValue.Create(v.Value),
        OpenApiDate v => JsonValue.Create(v.Value.ToString("yyyy-MM-dd")),
        OpenApiDateTime v => JsonValue.Create(v.Value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")),
        OpenApiArray a => new JsonArray(a.Select(ToNode).ToArray()),
        OpenApiObject o => new JsonObject(o.Select(kv => KeyValuePair.Create(kv.Key, ToNode(kv.Value)))),
        _ => JsonValue.Create(any.ToString()),
    };
}
