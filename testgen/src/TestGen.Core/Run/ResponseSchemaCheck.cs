using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.OpenApi.Models;
using TestGen.Scenarios;

namespace TestGen.Core.Run;

/// <summary>Resultatet av <see cref="ResponseSchemaCheck"/>: hårda fel (testet får FEL) och varningar (påverkar inte utfallet).</summary>
public sealed record SchemaCheckResult(List<string> Errors, List<string> Warnings)
{
    public static SchemaCheckResult Empty => new(new(), new());
    public bool HasErrors => Errors.Count > 0;
}

/// <summary>
/// Kontrollerar ett JSON-svar mot specens svarsschema. Reglerna kommer enbart från specen.
/// Hårda fel: obligatoriskt fält saknas (writeOnly räknas inte), fel JSON-typ (heltal får inte vara decimaltal), värde utanför enum,
/// format som inte går att tolka (date, date-time enligt RFC 3339, uuid, email, uri), null där fältet inte är nullable,
/// writeOnly-fält i svaret, minItems/maxItems/minLength/maxLength/minimum/maximum och pattern.
/// Varning: extra fält som inte finns i schemat (hårt fel om schemat har <c>additionalProperties: false</c>).
/// allOf slås ihop, oneOf/anyOf är godkänt om något alternativ passar och $ref är redan upplöst av specläsaren.
/// Nästlade objekt och listor gås igenom. Högst <see cref="MaxMessages"/> meddelanden per sort, sedan "och N till".
/// Ett svar som inte är JSON (body null) kontrolleras inte.
/// </summary>
public static class ResponseSchemaCheck
{
    public const int MaxMessages = 10;
    private const int MaxDepth = 40;

    public static SchemaCheckResult Check(JsonNode? body, OpenApiSchema? schema)
    {
        if (body is null || schema is null) return SchemaCheckResult.Empty;
        var r = Validate(body, schema, "svar", 0, false);
        return new SchemaCheckResult(Cap(Distinct(r.Errors, collapseIndexes: false)), Cap(Distinct(r.Warnings, collapseIndexes: true)));
    }

    // --- Intern resultatbärare ------------------------------------------------------------

    private sealed class Issues
    {
        public List<string> Errors { get; } = new();
        public List<string> Warnings { get; } = new();
        public void Add(Issues o) { Errors.AddRange(o.Errors); Warnings.AddRange(o.Warnings); }
    }

    private static List<string> Distinct(List<string> list, bool collapseIndexes)
    {
        var seen = new HashSet<string>();
        return list.Where(l => seen.Add(collapseIndexes ? Regex.Replace(l, @"\[\d+\]", "[]") : l)).ToList();
    }

    private static List<string> Cap(List<string> list) =>
        list.Count <= MaxMessages ? list : list.Take(MaxMessages).Append($"och {list.Count - MaxMessages} till").ToList();

    // --- Sammanslaget schema (allOf) ------------------------------------------------------

    private sealed class Merged
    {
        public List<OpenApiSchema> Parts { get; } = new();
        public Dictionary<string, OpenApiSchema> Props { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Required { get; } = new(StringComparer.Ordinal);
        public bool Strict { get; set; }
        public OpenApiSchema? ExtraSchema { get; set; }
        public bool Nullable { get; set; }
        public string? Type { get; set; }
        public OpenApiSchema? Items { get; set; }
        /// <summary>Varje grupp är en oneOf/anyOf: något alternativ i gruppen ska passa.</summary>
        public List<List<OpenApiSchema>> Alternatives { get; } = new();
    }

    private static Merged Merge(OpenApiSchema schema)
    {
        var m = new Merged();
        Collect(schema, m, 0);
        return m;
    }

    private static void Collect(OpenApiSchema s, Merged m, int depth)
    {
        if (depth > 12 || m.Parts.Contains(s)) return;
        m.Parts.Add(s);
        foreach (var a in s.AllOf) Collect(a, m, depth + 1);
        foreach (var (k, v) in s.Properties) m.Props.TryAdd(k, v);
        foreach (var r in s.Required) m.Required.Add(r);
        if (!s.AdditionalPropertiesAllowed) m.Strict = true;
        if (s.AdditionalProperties is { } ap) m.ExtraSchema ??= ap;
        if (s.Nullable) m.Nullable = true;
        if (m.Type is null && !string.IsNullOrEmpty(s.Type)) m.Type = s.Type;
        if (s.Items is not null) m.Items ??= s.Items;
        if (s.OneOf.Count > 0) m.Alternatives.Add(s.OneOf.ToList());
        if (s.AnyOf.Count > 0) m.Alternatives.Add(s.AnyOf.ToList());
    }

    /// <summary>Alla egenskapsnamn som något alternativ (oneOf/anyOf) känner till: de räknas inte som extra fält.</summary>
    private static void AltProps(Merged m, HashSet<string> names, int depth)
    {
        if (depth > 6) return;
        foreach (var group in m.Alternatives)
            foreach (var alt in group)
            {
                var am = Merge(alt);
                foreach (var k in am.Props.Keys) names.Add(k);
                AltProps(am, names, depth + 1);
            }
    }

    private static bool IsNullable(Merged m, int depth = 0) =>
        m.Nullable || (depth < 6 && m.Alternatives.Any(g => g.Any(a => a.Type == "null" || IsNullable(Merge(a), depth + 1))));

    // --- Validering -----------------------------------------------------------------------

    private static Issues Validate(JsonNode? node, OpenApiSchema schema, string path, int depth, bool inAlternative)
    {
        var issues = new Issues();
        if (depth > MaxDepth) return issues;
        var m = Merge(schema);

        if (node is null)
        {
            var hasRules = m.Type is not null || m.Props.Count > 0 || m.Parts.Any(p => p.Enum.Count > 0) || m.Alternatives.Count > 0;
            if (hasRules && !IsNullable(m))
                issues.Errors.Add($"{path} är null men fältet får inte vara null (enligt specen)");
            return issues;
        }

        var kind = KindOf(node);
        if (m.Type is { } type && !TypeFits(type, kind, node))
        {
            issues.Errors.Add($"{path} är {Describe(kind, node)} men ska vara {TypeName(type)}");
            return issues;
        }

        foreach (var part in m.Parts) CheckScalar(node, part, kind, path, issues);

        switch (node)
        {
            case JsonObject obj: CheckObject(obj, m, path, depth, inAlternative, issues); break;
            case JsonArray arr: CheckArray(arr, m, path, depth, issues); break;
        }

        foreach (var group in m.Alternatives) CheckAlternatives(node, group, path, depth, issues);
        return issues;
    }

    private static void CheckAlternatives(JsonNode node, List<OpenApiSchema> group, string path, int depth, Issues issues)
    {
        Issues? best = null;
        foreach (var alt in group)
        {
            var r = Validate(node, alt, path, depth + 1, true);
            if (r.Errors.Count == 0) { issues.Warnings.AddRange(r.Warnings); return; }
            if (best is null || r.Errors.Count < best.Errors.Count) best = r;
        }
        if (best is not null) issues.Errors.AddRange(best.Errors);
    }

    private static void CheckObject(JsonObject obj, Merged m, string path, int depth, bool inAlternative, Issues issues)
    {
        foreach (var name in m.Required.OrderBy(n => n, StringComparer.Ordinal))
        {
            if (m.Props.TryGetValue(name, out var ps) && ps.WriteOnly) continue;   // writeOnly skickas aldrig tillbaka
            if (!obj.ContainsKey(name)) issues.Errors.Add($"{Child(path, name)} saknas (obligatoriskt enligt specen)");
        }

        var extra = new List<string>();
        foreach (var (name, child) in obj)
        {
            if (m.Props.TryGetValue(name, out var ps))
            {
                if (ps.WriteOnly)
                {
                    issues.Errors.Add($"{Child(path, name)} skickas tillbaka – fältet är writeOnly och ska aldrig finnas i svaret");
                    continue;
                }
                issues.Add(Validate(child, ps, Child(path, name), depth + 1, false));
            }
            else if (m.ExtraSchema is { } es) issues.Add(Validate(child, es, Child(path, name), depth + 1, false));
            else extra.Add(name);
        }

        if (extra.Count == 0 || inAlternative) return;
        var known = new HashSet<string>(m.Props.Keys, StringComparer.Ordinal);
        AltProps(m, known, 0);
        extra = extra.Where(n => !known.Contains(n)).ToList();
        if (extra.Count == 0) return;
        if (m.Strict)
            issues.Errors.Add($"{path} har extra fält som inte får finnas (additionalProperties är false i specen): {string.Join(", ", extra)}");
        else if (m.Props.Count > 0 || m.Alternatives.Count > 0)   // ett fritt objekt utan egenskaper får ha vilka fält som helst
            issues.Warnings.Add($"{path} har extra fält som inte finns i specen: {string.Join(", ", extra)} (varning)");
    }

    private static void CheckArray(JsonArray arr, Merged m, string path, int depth, Issues issues)
    {
        foreach (var part in m.Parts)
        {
            if (part.MinItems is { } mn && arr.Count < mn) issues.Errors.Add($"{path} har {arr.Count} {Poster(arr.Count)} men ska ha minst {mn}");
            if (part.MaxItems is { } mx && arr.Count > mx) issues.Errors.Add($"{path} har {arr.Count} {Poster(arr.Count)} men ska ha högst {mx}");
        }
        if (m.Items is null) return;
        for (var i = 0; i < arr.Count; i++) issues.Add(Validate(arr[i], m.Items, $"{path}[{i}]", depth + 1, false));
    }

    private static string Poster(int n) => n == 1 ? "post" : "poster";

    private static string Child(string path, string name) => $"{path}.{name}";

    // --- Skalära regler -------------------------------------------------------------------

    private static void CheckScalar(JsonNode node, OpenApiSchema s, JsonValueKind kind, string path, Issues issues)
    {
        if (s.Enum.Count > 0 && node is JsonValue)
        {
            var allowed = s.Enum.Select(SchemaUtil.ToNode).Where(n => n is not null).Select(n => n!).ToList();
            if (allowed.Count > 0 && !allowed.Any(a => SameValue(a, node)))
                issues.Errors.Add($"{path} {Show(node)} finns inte bland tillåtna värden ({string.Join(", ", allowed.Select(PlainValue))})");
        }

        if (kind == JsonValueKind.String)
        {
            var text = node.GetValue<string>();
            var length = text.EnumerateRunes().Count();
            if (s.MinLength is { } mn && length < mn) issues.Errors.Add($"{path} {Show(node)} är för kort (minst {mn} tecken, fick {length})");
            if (s.MaxLength is { } mx && length > mx) issues.Errors.Add($"{path} {Show(node)} är för långt (högst {mx} tecken, fick {length})");
            if (!string.IsNullOrEmpty(s.Pattern) && PatternFails(s.Pattern, text))
                issues.Errors.Add($"{path}: \"{ResponseStore.Shorten(text, 80)}\" följer inte mönstret {s.Pattern} (enligt specen)");
            if (!string.IsNullOrEmpty(s.Format) && FormatProblem(s.Format, text) is { } what)
                issues.Errors.Add($"{path} {Show(node)} är inte {what}");
        }
        else if (kind == JsonValueKind.Number && decimal.TryParse(node.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
        {
            if (s.Minimum is { } min && (s.ExclusiveMinimum == true ? n <= min : n < min))
                issues.Errors.Add($"{path} är {Num(n)} men ska vara {(s.ExclusiveMinimum == true ? "större än" : "minst")} {Num(min)}");
            if (s.Maximum is { } max && (s.ExclusiveMaximum == true ? n >= max : n > max))
                issues.Errors.Add($"{path} är {Num(n)} men ska vara {(s.ExclusiveMaximum == true ? "mindre än" : "högst")} {Num(max)}");
        }
    }

    private static string Num(decimal d) => d.ToString("0.############", CultureInfo.InvariantCulture);

    private static bool PatternFails(string pattern, string text)
    {
        Regex re;
        try { re = new Regex(pattern, RegexOptions.None, PatternMismatch.Timeout); }
        catch (ArgumentException) { return false; }   // mönstret går inte att tolka i .NET: kan inte kontrolleras
        try { return !re.IsMatch(text); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    private static readonly Regex DateTimeRe = new(
        @"^(\d{4})-(\d{2})-(\d{2})[Tt](\d{2}):(\d{2}):(\d{2})(\.\d+)?([Zz]|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant);

    /// <summary>Beskrivning ("ett giltigt datum") om texten inte följer formatet, annars null. Okända format kontrolleras inte.</summary>
    private static string? FormatProblem(string format, string text)
    {
        switch (format.ToLowerInvariant())
        {
            case "date":
                return DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ? null : "ett giltigt datum";
            case "date-time":
                return IsRfc3339(text) ? null : "ett giltigt datum med tid";
            case "uuid":
                return Guid.TryParseExact(text, "D", out _) ? null : "ett giltigt UUID";
            case "email":
                return Regex.IsMatch(text, @"^[^@\s]+@[^@\s.][^@\s]*$") && !text.EndsWith('.') ? null : "en giltig e-postadress";
            case "uri":
                return !text.Any(char.IsWhiteSpace) && Uri.TryCreate(text, UriKind.Absolute, out _) ? null : "en giltig adress (URI)";
            default:
                return null;
        }
    }

    private static bool IsRfc3339(string text)
    {
        var m = DateTimeRe.Match(text);
        if (!m.Success) return false;
        int G(int i) => int.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture);
        try { _ = new DateTime(G(1), G(2), G(3)); } catch (ArgumentOutOfRangeException) { return false; }
        if (G(4) > 23 || G(5) > 59 || G(6) > 60) return false;
        var offset = m.Groups[8].Value;
        return offset.Length != 6
            || (int.Parse(offset[1..3], CultureInfo.InvariantCulture) <= 23 && int.Parse(offset[4..], CultureInfo.InvariantCulture) <= 59);
    }

    // --- Typer och värden -----------------------------------------------------------------

    /// <summary>JSON-sort, där false och true båda blir <see cref="JsonValueKind.True"/> (boolean).</summary>
    private static JsonValueKind KindOf(JsonNode node)
    {
        var k = node.GetValueKind();
        return k == JsonValueKind.False ? JsonValueKind.True : k;
    }

    /// <summary>Heltal i JSON-text: utan decimalpunkt och exponent (5.0 räknas som decimaltal).</summary>
    private static bool IsInteger(JsonNode node)
    {
        var t = node.ToJsonString();
        return !t.Contains('.') && !t.Contains('e') && !t.Contains('E');
    }

    private static bool TypeFits(string type, JsonValueKind kind, JsonNode node) => type switch
    {
        "string" => kind == JsonValueKind.String,
        "integer" => kind == JsonValueKind.Number && IsInteger(node),
        "number" => kind == JsonValueKind.Number,
        "boolean" => kind == JsonValueKind.True,
        "object" => kind == JsonValueKind.Object,
        "array" => kind == JsonValueKind.Array,
        _ => true,
    };

    private static string TypeName(string type) => type switch
    {
        "string" => "text",
        "integer" => "heltal",
        "number" => "tal",
        "boolean" => "sant/falskt",
        "object" => "ett objekt",
        "array" => "en lista",
        _ => type,
    };

    private static string Describe(JsonValueKind kind, JsonNode node) => kind switch
    {
        JsonValueKind.String => "text",
        JsonValueKind.Number => IsInteger(node) ? "heltal" : "decimaltal",
        JsonValueKind.True => "sant/falskt",
        JsonValueKind.Object => "ett objekt",
        JsonValueKind.Array => "en lista",
        _ => "något annat",
    };

    private static bool SameValue(JsonNode a, JsonNode b)
    {
        var (ka, kb) = (KindOf(a), KindOf(b));
        if (ka != kb) return false;
        if (ka == JsonValueKind.Number)
            return decimal.TryParse(a.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                && decimal.TryParse(b.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var y) && x == y;
        return a.ToJsonString() == b.ToJsonString();
    }

    private static string PlainValue(JsonNode n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n.ToJsonString();

    private static string Show(JsonNode n) =>
        n is JsonValue v && v.TryGetValue<string>(out var s) ? $"'{ResponseStore.Shorten(s, 80)}'" : ResponseStore.Shorten(n.ToJsonString(), 80);
}
