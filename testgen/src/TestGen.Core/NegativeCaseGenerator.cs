using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.OpenApi;
using TestGen.Core;
using TestGen.Core.Run;

namespace TestGen.Core;

public sealed record NegativeCase(string Suffix, JsonNode Body, CaseMeta Meta);

/// <summary>
/// Negativa fall (förväntar att systemet säger nej, 400/422). Går genom hela bodyn: objektens fält och listornas element
/// (sökväg som fältnamn: "leverans.adress.postnummer", "rader[0].antal"). Varje fall är en kopia av den giltiga bodyn där
/// bara ett fält ändrats. Kapp: högst <see cref="MaxDepth"/> nivåer och <see cref="MaxCases"/> fall per operation.
/// </summary>
public static class NegativeCaseGenerator
{
    /// <summary>Namnet på bodyn själv när den är en lista på toppnivå.</summary>
    public const string RootName = "listan";

    private const int MaxStringProbe = 100_000;
    private const int MaxItemsProbe = 1000;

    /// <summary>Högsta antal nivåer (fältnamn och listpositioner) i en sökväg.</summary>
    public const int MaxDepth = 4;

    /// <summary>Högsta antal negativa fall per operation.</summary>
    public const int MaxCases = 60;

    private static readonly HashSet<string> CheckedFormats = new(StringComparer.OrdinalIgnoreCase) { "email", "date", "date-time", "uuid", "guid", "uri", "url" };

    public static List<NegativeCase> Generate(JsonNode valid, IOpenApiSchema bodySchema) => Generate(valid, bodySchema, out _);

    /// <summary>Som ovan; <paramref name="capped"/> blir true om kappen på antal fall gjorde att fall utelämnades.</summary>
    public static List<NegativeCase> Generate(JsonNode valid, IOpenApiSchema bodySchema, out bool capped)
    {
        var found = new List<(NegativeCase Case, int Depth)>();
        new Walker(valid, found).Visit(bodySchema, new List<object>(), valid, required: false);

        capped = found.Count > MaxCases;
        if (!capped) return found.Select(f => f.Case).ToList();
        // Över kappen: de djupaste fallen faller bort först (bland lika djupa de sista).
        var keep = found.Select((f, i) => (f.Depth, i)).OrderBy(x => x.Depth).ThenBy(x => x.i).Take(MaxCases).Select(x => x.i).ToHashSet();
        return found.Where((_, i) => keep.Contains(i)).Select(f => f.Case).ToList();
    }

    /// <summary>Sökvägen som fältnamn: "leverans.adress.postnummer", "rader[0].antal".</summary>
    public static string DisplayPath(IReadOnlyList<object> path)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var seg in path)
        {
            if (seg is int i) sb.Append('[').Append(i).Append(']');
            else { if (sb.Length > 0) sb.Append('.'); sb.Append((string)seg); }
        }
        return sb.ToString();
    }

    /// <summary>Sökvägen som del av ett testnamn: "rader_0.antal".</summary>
    public static string NamePart(IReadOnlyList<object> path) => DisplayPath(path).Replace("[", "_").Replace("]", "");

    /// <summary>Kopia av <paramref name="root"/> där värdet på sökvägen bytts ut (eller tagits bort om <paramref name="value"/> är null).</summary>
    public static JsonNode Mutate(JsonNode root, IReadOnlyList<object> path, JsonNode? value)
    {
        if (path.Count == 0) return value!.DeepClone();
        var clone = root.DeepClone();
        JsonNode cur = clone;
        for (var i = 0; i < path.Count - 1; i++) cur = Child(cur, path[i])!;
        var last = path[^1];
        switch (cur)
        {
            case JsonObject o when last is string k:
                if (value is null) o.Remove(k); else o[k] = value;
                break;
            case JsonArray a when last is int idx:
                a[idx] = value;
                break;
        }
        return clone;
    }

    private static JsonNode? Child(JsonNode node, object seg) => node switch
    {
        JsonObject o when seg is string k => o[k],
        JsonArray a when seg is int i && i < a.Count => a[i],
        _ => null,
    };

    private sealed class Walker(JsonNode root, List<(NegativeCase Case, int Depth)> found)
    {
        private static string N(decimal d) => d.ToString(System.Globalization.CultureInfo.InvariantCulture);

        private void Add(List<object> path, string suffix, JsonNode? value, TestRule rule, string? limit = null, bool exclusive = false)
        {
            var prefix = path.Count == 0 || path[0] is int ? RootName : "";   // lista på toppnivå: "listan[0].n"
            var field = prefix + DisplayPath(path);
            var name = prefix + NamePart(path);
            var full = suffix.StartsWith('_') ? name + suffix : suffix + name;   // "missing_" + sökväg, eller sökväg + "_too_long"
            found.Add((new NegativeCase(full, Mutate(root, path, value), new CaseMeta(rule, field, limit, CaseMeta.Json(value), exclusive)), path.Count));
        }

        public void Visit(IOpenApiSchema schema, List<object> path, JsonNode? valid, bool required)
        {
            if (path.Count > MaxDepth) return;
            schema = SchemaUtil.Effective(schema);
            var type = SchemaUtil.TypeOf(schema);
            var isRoot = path.Count == 0;
            var inList = !isRoot && path[^1] is int;

            if (!isRoot && valid is not null)
            {
                if (required && !inList) Add(path, "missing_", null, TestRule.Missing);
                Rules(schema, type, path, valid, required && !inList);
            }

            if (valid is JsonObject obj && type == "object")
            {
                if (path.Count >= MaxDepth) return;
                var (props, req) = SchemaUtil.Flatten(schema);
                foreach (var (name, child) in props)
                {
                    if (!obj.ContainsKey(name)) continue;
                    path.Add(name);
                    Visit(child, path, obj[name], req.Contains(name));
                    path.RemoveAt(path.Count - 1);
                }
            }
            else if (valid is JsonArray arr && type == "array")
            {
                if (isRoot) ArrayRules(schema, path, arr);   // listan själv (övriga regler gäller fält)
                if (path.Count >= MaxDepth || arr.Count == 0 || schema.Items is null) return;
                path.Add(0);
                Visit(schema.Items, path, arr[0], required: false);
                path.RemoveAt(path.Count - 1);
            }
        }

        private void ArrayRules(IOpenApiSchema schema, List<object> path, JsonArray arr)
        {
            if (schema.MinItems is { } minItems && minItems > 0 && arr.Count > 0)
                Add(path, "_too_few_items", Take(arr, minItems - 1), TestRule.TooFewItems, minItems.ToString());
            if (schema.MaxItems is { } maxItems && maxItems < MaxItemsProbe && arr.Count > 0)
                Add(path, "_too_many_items", Take(arr, maxItems + 1), TestRule.TooManyItems, maxItems.ToString());
        }

        private void Rules(IOpenApiSchema schema, string? type, List<object> path, JsonNode valid, bool required)
        {
            var good = (valid as JsonValue)?.TryGetValue<string>(out var gs) == true ? gs : null;

            if (type is null or "string")
            {
                if (schema.MaxLength is { } max && max < MaxStringProbe)
                    Add(path, "_too_long", JsonValue.Create(new string('a', max + 1)), TestRule.TooLong, max.ToString());
                if (schema.MinLength is { } min && min > 0)
                    Add(path, "_too_short", JsonValue.Create(new string('a', min - 1)), TestRule.TooShort, min.ToString());
                // Mönstret kommer ur specen: systemet ska säga nej till ett värde som bryter mot det.
                if (!string.IsNullOrEmpty(schema.Pattern) && PatternMismatch.Find(schema.Pattern, good, schema.MinLength, schema.MaxLength) is { } bad)
                    Add(path, "_pattern_mismatch", JsonValue.Create(bad), TestRule.PatternMismatch, schema.Pattern);
                if (schema.Format is { } fmt && CheckedFormats.Contains(fmt) && BadFormatValue(fmt) is { } badFormat
                    && string.IsNullOrEmpty(schema.Pattern) && schema.EnumValues().Count == 0)
                    Add(path, "_bad_format", JsonValue.Create(badFormat), TestRule.BadFormat, fmt.ToLowerInvariant());
                if (required && ForbidsEmpty(schema) && schema.MinLength != 1)   // minLength 1 ger redan "" som för kort
                    Add(path, "_empty", JsonValue.Create(""), TestRule.Empty);
            }
            else if (type is "integer" or "number")
            {
                if (schema.Lower() is var (mn, minExclusive))
                    Add(path, "_below_min", Num(type, minExclusive ? mn : mn - 1), TestRule.BelowMinimum, N(mn), minExclusive);
                if (schema.Upper() is var (mx, maxExclusive))
                    Add(path, "_above_max", Num(type, maxExclusive ? mx : mx + 1), TestRule.AboveMaximum, N(mx), maxExclusive);
            }
            else if (type == "array" && valid is JsonArray arr) ArrayRules(schema, path, arr);

            var enums = schema.EnumValues().Select(SchemaUtil.ToNode).Where(n => n is not null).ToList();
            if (enums.Count > 0)
            {
                JsonNode outside = type is "integer" or "number"
                    ? JsonValue.Create(enums.Max(e => (long)Math.Ceiling(Convert.ToDouble(ResponseStore.ToText(e), System.Globalization.CultureInfo.InvariantCulture))) + 1000)
                    : JsonValue.Create("__not_in_enum__");
                Add(path, "_not_in_enum", outside, TestRule.NotInEnum, string.Join(", ", enums.Select(CaseMeta.Json)));
            }

            Add(path, "_wrong_type", WrongType(type, schema), TestRule.WrongType, type ?? "string");
        }

        /// <summary>De första <paramref name="count"/> elementen; är listan kortare upprepas första elementet.</summary>
        private static JsonArray Take(JsonArray source, int count)
        {
            var result = new JsonArray();
            for (var i = 0; i < count; i++) result.Add(source[i < source.Count ? i : 0]!.DeepClone());
            return result;
        }
    }

    /// <summary>Schemat förbjuder tom sträng: minLength 1 eller mer, ett mönster som inte matchar "", en enum utan "" eller ett format som kräver innehåll.</summary>
    private static bool ForbidsEmpty(IOpenApiSchema s)
    {
        if ((s.MinLength ?? 0) >= 1) return true;
        if (!string.IsNullOrEmpty(s.Pattern) && !MatchesEmpty(s.Pattern)) return true;
        var enums = s.EnumValues().Select(SchemaUtil.ToNode).Where(n => n is not null).ToList();
        if (enums.Count > 0) return !enums.Any(e => ResponseStore.ToText(e) == "");
        return s.Format is { } f && CheckedFormats.Contains(f);
    }

    private static bool MatchesEmpty(string? pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return false;
        try { return Regex.IsMatch("", pattern, RegexOptions.None, PatternMismatch.Timeout); }
        catch (ArgumentException) { return true; }
        catch (RegexMatchTimeoutException) { return true; }
    }

    private static string? BadFormatValue(string format) => format.ToLowerInvariant() switch
    {
        "email" => "abc",
        "date" => "2023-13-45",
        "date-time" => "2023-13-45T25:61:61Z",
        "uuid" or "guid" => "inte-ett-uuid",
        "uri" or "url" => "http://",   // "abc" är en giltig relativ adress
        _ => null,
    };

    private static JsonNode Num(string type, decimal v) =>
        type == "integer" ? JsonValue.Create((long)Math.Round(v)) : JsonValue.Create((double)v);

    /// <summary>Ett värde av en typ som schemat inte tillåter. Tillåter schemat text vid sidan av typen (3.1: <c>[integer, string]</c>) och saknar mönster blir det ett objekt.</summary>
    private static JsonNode WrongType(string? type, IOpenApiSchema schema) =>
        type != "string" && schema.Type is { } t && t.HasFlag(JsonSchemaType.String) && string.IsNullOrEmpty(schema.Pattern) ? new JsonObject() : WrongType(type);

    private static JsonNode WrongType(string? type) => type switch
    {
        "integer" or "number" => JsonValue.Create("not-a-number"),
        "boolean" => JsonValue.Create("not-a-boolean"),
        "array" => JsonValue.Create("not-an-array"),
        "object" => JsonValue.Create("not-an-object"),
        _ => new JsonObject(),   // ett objekt i stället för text: löst typade stackar gör annars om ett tal till text och svarar 2xx
    };
}
