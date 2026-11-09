using System.Globalization;
using System.Text.Json.Nodes;

namespace TestGen.Tests;

/// <summary>Ett litet JSON-schema-kontrollerande (bara det som våra scheman använder; inget beroende).</summary>
internal static class JsonSchemaCheck
{
    public static List<string> Errors(JsonNode? value, JsonObject root)
    {
        var errors = new List<string>();
        Check(value, root, root, "$", errors);
        return errors;
    }

    private static bool IsType(JsonNode? n, string type) => type switch
    {
        "null" => n is null,
        "object" => n is JsonObject,
        "array" => n is JsonArray,
        "string" => n is not null && n.GetValueKind() == System.Text.Json.JsonValueKind.String,
        "boolean" => n is not null && n.GetValueKind() is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False,
        "number" => n is not null && n.GetValueKind() == System.Text.Json.JsonValueKind.Number,
        "integer" => n is not null && n.GetValueKind() == System.Text.Json.JsonValueKind.Number && !n.ToJsonString().Any(ch => ch is '.' or 'e' or 'E'),
        _ => throw new NotSupportedException(type),
    };

    private static void Check(JsonNode? n, JsonObject schema, JsonObject root, string path, List<string> errors)
    {
        if (schema["$ref"] is JsonValue r)
        {
            JsonNode? target = root;
            foreach (var part in r.GetValue<string>().TrimStart('#', '/').Split('/')) target = target![part];
            Check(n, (JsonObject)target!, root, path, errors);
            return;
        }
        if (schema["oneOf"] is JsonArray alts)
        {
            if (!alts.OfType<JsonObject>().Any(a => { var e = new List<string>(); Check(n, a, root, path, e); return e.Count == 0; }))
                errors.Add($"{path}: matchar inget alternativ i oneOf");
            return;
        }
        if (schema["const"] is { } c && c.ToJsonString() != (n?.ToJsonString() ?? "null")) errors.Add($"{path}: väntade const {c.ToJsonString()}, fick {n?.ToJsonString() ?? "null"}");
        if (schema["enum"] is JsonArray en && !en.Any(e => (e?.ToJsonString() ?? "null") == (n?.ToJsonString() ?? "null")))
            errors.Add($"{path}: {n?.ToJsonString() ?? "null"} finns inte i enum {en.ToJsonString()}");
        if (schema["type"] is { } t)
        {
            var types = t is JsonArray ta ? ta.Select(x => x!.GetValue<string>()).ToList() : new List<string> { t.GetValue<string>() };
            if (!types.Any(x => IsType(n, x))) { errors.Add($"{path}: typen {string.Join("|", types)} väntades, fick {n?.ToJsonString() ?? "null"}"); return; }
        }
        if (n is JsonValue v)
        {
            if (schema["pattern"] is JsonValue p && v.TryGetValue<string>(out var s) && !System.Text.RegularExpressions.Regex.IsMatch(s, p.GetValue<string>())) errors.Add($"{path}: '{s}' följer inte mönstret");
            if (schema["minimum"] is JsonValue m && v.GetValueKind() == System.Text.Json.JsonValueKind.Number
                && double.Parse(v.ToJsonString(), CultureInfo.InvariantCulture) is var d && d < double.Parse(m.ToJsonString(), CultureInfo.InvariantCulture)) errors.Add($"{path}: {d} är under minimum");
            if (schema["format"]?.GetValue<string>() == "date-time" && v.TryGetValue<string>(out var dt)
                && !System.Text.RegularExpressions.Regex.IsMatch(dt, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?([+-]\d{2}:\d{2}|Z)$")) errors.Add($"{path}: '{dt}' är inte ISO 8601 med förskjutning");
        }
        if (n is JsonObject o)
        {
            var props = schema["properties"] as JsonObject;
            foreach (var req in (schema["required"] as JsonArray)?.Select(x => x!.GetValue<string>()) ?? Enumerable.Empty<string>())
                if (!o.ContainsKey(req)) errors.Add($"{path}: obligatoriskt fält {req} saknas");
            foreach (var (k, val) in o)
            {
                if (props is null) continue;
                if (props[k] is JsonObject sub) Check(val, sub, root, path + "." + k, errors);
                else errors.Add($"{path}.{k}: nyckeln finns i rapporten men inte i schemat");
            }
        }
        if (n is JsonArray a && schema["items"] is JsonObject item)
            for (var i = 0; i < a.Count; i++) Check(a[i], item, root, $"{path}[{i}]", errors);
    }
}
