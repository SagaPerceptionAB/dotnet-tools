using System.Text.Json.Nodes;
using Microsoft.OpenApi;

namespace TestGen.Core;

public static class SchemaUtil
{
    /// <summary>Slår ihop allOf (och väljer första oneOf/anyOf) till properties + required.</summary>
    public static (SortedDictionary<string, IOpenApiSchema> Props, SortedSet<string> Required) Flatten(IOpenApiSchema schema)
    {
        var props = new SortedDictionary<string, IOpenApiSchema>(StringComparer.Ordinal);
        var required = new SortedSet<string>(StringComparer.Ordinal);
        Collect(schema, props, required, 0);
        return (props, required);
    }

    private static void Collect(IOpenApiSchema s, IDictionary<string, IOpenApiSchema> props, ISet<string> req, int depth)
    {
        if (depth > 10) return;
        foreach (var a in s.AllOfs()) Collect(a, props, req, depth + 1);
        if (s.AllOfs().Count == 0 && s.Props().Count == 0)
        {
            var alt = Alternatives(s).FirstOrDefault();
            if (alt is not null) Collect(alt, props, req, depth + 1);
        }
        foreach (var (k, v) in s.Props()) props[k] = v;
        foreach (var r in s.RequiredNames()) req.Add(r);
    }

    /// <summary>Schemats namn i components (referens-id), annars namnet på något allOf-led. Null för inline-scheman.</summary>
    public static string? SchemaName(IOpenApiSchema s) =>
        s.RefId() ?? s.AllOfs().Select(a => a.RefId()).FirstOrDefault(id => id is not null);

    public static bool IsObject(IOpenApiSchema s) => TypeOf(s) == "object";

    /// <summary>Alternativen (oneOf/anyOf) om schemat inte har något eget innehåll, annars en tom lista. Alternativ som bara är <c>null</c> räknas inte.</summary>
    public static IReadOnlyList<IOpenApiSchema> Alternatives(IOpenApiSchema s)
    {
        if (NameOf(s.Type) is not null || s.Props().Count > 0 || s.AllOfs().Count > 0) return Array.Empty<IOpenApiSchema>();
        var all = s.OneOfs().Count > 0 ? s.OneOfs() : s.AnyOfs();
        return all.Where(a => !IsNullOnly(a)).ToList();
    }

    /// <summary>Schemat är bara <c>{ type: null }</c>, utan något annat innehåll.</summary>
    public static bool IsNullOnly(IOpenApiSchema s) =>
        s.Type == JsonSchemaType.Null && s.Props().Count == 0 && s.AllOfs().Count == 0 && s.OneOfs().Count == 0 && s.AnyOfs().Count == 0 && s.RefId() is null;

    /// <summary>Schemat med oneOf/anyOf ersatt av första alternativet (rekursivt). Övriga scheman returneras som de är.</summary>
    public static IOpenApiSchema Effective(IOpenApiSchema s)
    {
        for (var i = 0; i < 10; i++)
        {
            var alts = Alternatives(s);
            if (alts.Count == 0) return s;
            s = alts[0];
        }
        return s;
    }

    public static string? TypeOf(IOpenApiSchema s)
    {
        s = Effective(s);
        if (NameOf(s.Type) is { } name) return name;
        if (s.AllOfs().Count > 0) return s.AllOfs().Select(TypeOf).FirstOrDefault(t => t is not null) ?? "object";
        return s.Props().Count > 0 ? "object" : null;
    }

    /// <summary>Exempel-, standard- och enum-värden är JSON-noder; null och JSON-null blir null, annars en egen kopia (en nod kan bara ha en förälder).</summary>
    public static JsonNode? ToNode(JsonNode? node) =>
        node is null || node.GetValueKind() == System.Text.Json.JsonValueKind.Null || JsonNullSentinel.IsJsonNullSentinel(node) ? null : node.DeepClone();

    /// <summary>Typens namn ("object", "array", "integer", "number", "boolean", "string"); null-flaggan räknas inte. Vid flera typer (3.1, t.ex. <c>[integer, string]</c> som AddOpenApi skriver för heltal) vinner den mest specifika. Null om typ saknas.</summary>
    public static string? NameOf(JsonSchemaType? type) => type switch
    {
        null => null,
        { } t when t.HasFlag(JsonSchemaType.Object) => "object",
        { } t when t.HasFlag(JsonSchemaType.Array) => "array",
        { } t when t.HasFlag(JsonSchemaType.Integer) => "integer",
        { } t when t.HasFlag(JsonSchemaType.Number) => "number",
        { } t when t.HasFlag(JsonSchemaType.Boolean) => "boolean",
        { } t when t.HasFlag(JsonSchemaType.String) => "string",
        _ => null,
    };

    public static bool IsNullable(IOpenApiSchema s) =>
        s.Type is { } t && t.HasFlag(JsonSchemaType.Null) || s.OneOfs().Concat(s.AnyOfs()).Any(IsNullOnly);
}
