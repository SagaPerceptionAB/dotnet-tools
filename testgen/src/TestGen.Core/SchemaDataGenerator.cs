using System.Globalization;
using System.Text.Json.Nodes;
using Bogus;
using Microsoft.OpenApi;
using TestGen.Core;
using TestGen.Core.Run;

namespace TestGen.Core;

/// <summary>
/// Genererar värden ur schemat. Ordning: enum, override, example, format, pattern, gränser, fältnamn, typ.
/// Personnummer, e-post och telefon kommer alltid ur de fiktiva serierna (<see cref="FictionalData"/>).
/// </summary>
public sealed class SchemaDataGenerator
{
    private static readonly DateTime DateFrom = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime DateTo = new(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc);

    // En Faker per locale och tråd (att bygga den är dyrt); seeden sätts om vid varje användning.
    [ThreadStatic] private static Dictionary<string, Faker>? _fakers;

    private readonly Faker _faker;

    public SchemaDataGenerator(int seed, string locale)
    {
        _fakers ??= new Dictionary<string, Faker>();
        if (!_fakers.TryGetValue(locale, out _faker!)) _fakers[locale] = _faker = new Faker(locale);
        _faker.Random = new Randomizer(seed);
    }

    public Faker Faker => _faker;

    public JsonNode? GenerateField(string name, IOpenApiSchema schema, JsonNode? fixedValue = null, int depth = 0, string? owner = null)
    {
        schema = SchemaUtil.Effective(schema);   // oneOf/anyOf: första alternativet (andra alternativ genereras av ScenarioGenerator)
        var enumValues = schema.EnumValues().Select(SchemaUtil.ToNode).Where(n => n is not null).ToList();
        // Ett fast värde (binding, override) som är ett av de tillåtna gäller; annars väljs ett av dem.
        if (fixedValue is not null && enumValues.Count > 0 && enumValues.FirstOrDefault(n => ResponseStore.ToText(n!) == ResponseStore.ToText(fixedValue)) is { } chosen) return chosen.DeepClone();
        if (enumValues.Count > 0) return enumValues[_faker.Random.Number(0, enumValues.Count - 1)]!.DeepClone();

        if (fixedValue is not null) return Coerce(fixedValue, schema);

        var type = SchemaUtil.TypeOf(schema);
        string? rejected = null;   // ett exempel som inte får användas, men vars form efterliknas
        foreach (var candidate in new[] { schema.ExampleNode(), schema.Default })
        {
            if (SchemaUtil.ToNode(candidate) is not { } node) continue;
            if (UsableExample(name, schema, node)) return node;
            rejected ??= node is JsonValue ? ResponseStore.ToText(node) : null;
        }

        return type switch
        {
            "integer" => JsonValue.Create(GenInteger(schema)),
            "number" => JsonValue.Create(GenNumber(schema)),
            "boolean" => JsonValue.Create(_faker.Random.Bool()),
            "array" => GenArray(name, schema, depth, owner),
            "object" => GenObject(schema, depth),
            _ => JsonValue.Create(GenString(name, schema, rejected, owner)),
        };
    }

    /// <summary>Ett exempel används bara om det uppfyller schemat, och om det är personnummer, e-post eller telefon, ligger i en fiktiv serie.</summary>
    private static bool UsableExample(string name, IOpenApiSchema schema, JsonNode example)
    {
        if (ResponseSchemaCheck.Check(example, schema).HasErrors) return false;
        if (example is JsonValue && SchemaUtil.TypeOf(schema) is null or "string"
            && FictionalData.KindOf(name, schema.Format) is { } kind)
            return FictionalData.IsFictional(kind, ResponseStore.ToText(example));
        return true;
    }

    private JsonNode GenObject(IOpenApiSchema schema, int depth)
    {
        var obj = new JsonObject();
        if (depth > 6) return obj;
        var owner = SchemaUtil.SchemaName(schema);
        var (props, _) = SchemaUtil.Flatten(schema);
        foreach (var (k, v) in props)
            if (!v.ReadOnly) obj[k] = GenerateField(k, v, null, depth + 1, owner);   // readOnly skickas aldrig i en request
        return obj;
    }

    private JsonNode GenArray(string name, IOpenApiSchema schema, int depth, string? owner)
    {
        var arr = new JsonArray();
        if (schema.Items is null || depth > 6) return arr;
        var count = Math.Max(schema.MinItems ?? 1, 1);
        if (schema.MaxItems is { } mx) count = Math.Min(count, mx);
        for (var i = 0; i < count; i++) arr.Add(GenerateField(name, schema.Items, null, depth + 1, owner));
        return arr;
    }

    private long GenInteger(IOpenApiSchema s)
    {
        var lower = s.Lower();
        var upper = s.Upper();
        var min = lower is var (mn, minExclusive) ? (long)Math.Ceiling(mn) + (minExclusive && mn == Math.Ceiling(mn) ? 1 : 0) : 1;
        var max = upper is var (mx, maxExclusive) ? (long)Math.Floor(mx) - (maxExclusive && mx == Math.Floor(mx) ? 1 : 0) : Math.Max(min, 1000);
        if (string.Equals(s.Format, "int32", StringComparison.OrdinalIgnoreCase)) { min = Math.Max(min, int.MinValue); max = Math.Min(max, int.MaxValue); }
        if (lower is null && max < min) min = max;
        if (max < min) max = min;
        if (s.MultipleOf is >= 1 and { } m && m == Math.Floor(m))
        {
            var step = (long)m;
            long lo = (long)Math.Ceiling((double)min / step), hi = (long)Math.Floor((double)max / step);
            if (lo <= hi) return _faker.Random.Long(lo, hi) * step;
        }
        return _faker.Random.Long(min, max);
    }

    private double GenNumber(IOpenApiSchema s)
    {
        var lower = s.Lower();
        var min = lower is { } mn ? (double)mn.Value : 1.0;
        var max = s.Upper() is { } mx ? (double)mx.Value : Math.Max(min, 1000.0);
        if (lower is null && max < min) min = max;
        if (max < min) max = min;
        if (s.MultipleOf is > 0 and { } m)
        {
            double step = (double)m, lo = Math.Ceiling(min / step), hi = Math.Floor(max / step);
            if (lo <= hi) return Math.Round(_faker.Random.Long((long)lo, (long)hi) * step, 10);
        }
        var v = Math.Round(_faker.Random.Double(min, max), 2);
        return Math.Min(Math.Max(v, min), max);
    }

    /// <summary>format, pattern, fältnamn och typ i den ordningen. <paramref name="like"/> är ett förkastat exempel vars form efterliknas.</summary>
    private string GenString(string name, IOpenApiSchema s, string? like, string? owner)
    {
        if (FromFormat(name, s, like) is { } formatted) return formatted;
        if (!string.IsNullOrEmpty(s.Pattern)) return FieldRules.Fit(PatternGenerator.Generate(s.Pattern, _faker.Random), s);
        if (FieldRules.TryGenerate(name, s, _faker, owner) is { } named) return named;
        return FieldRules.Fit(_faker.Lorem.Word(), s);
    }

    private string? FromFormat(string name, IOpenApiSchema s, string? like)
    {
        switch (FictionalData.KindOf(name, s.Format))
        {
            case FictionalData.Kind.Personnummer: return FieldRules.First(FictionalData.PersonnummerCandidates(_faker.Random, like), s);
            case FictionalData.Kind.Phone: return FieldRules.First(FictionalData.PhoneCandidates(_faker.Random, like), s);
            case FictionalData.Kind.Email: return FieldRules.First(Enumerable.Range(0, 5).Select(_ => FictionalData.Email(_faker)), s);
        }
        var v = s.Format?.ToLowerInvariant() switch
        {
            "uuid" or "guid" => Uuid4(_faker.Random),
            "date-time" => _faker.Date.Between(DateFrom, DateTo).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            "date" => _faker.Date.Between(DateFrom, DateTo).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "time" => _faker.Date.Between(DateFrom, DateTo).ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            "uri" or "url" => "https://example.com/" + _faker.Lorem.Word(),
            "hostname" => _faker.Lorem.Word() + ".example.com",
            "ipv4" => $"192.0.2.{_faker.Random.Number(1, 254)}",   // RFC 5737
            "ipv6" => $"2001:db8::{_faker.Random.Number(1, 0xffff):x}",   // RFC 3849
            "password" => _faker.Internet.Password(),
            "byte" => Convert.ToBase64String(_faker.Random.Bytes(8)),
            "postnummer" => $"{_faker.Random.Number(100, 999)} {_faker.Random.Number(0, 99):D2}",
            "organisationsnummer" => FieldRules.OrganizationNumber(_faker),
            _ => null,
        };
        if (v is null) return null;
        var fitted = FieldRules.Fit(v, s);
        return FieldRules.Meets(fitted, s) ? fitted : null;
    }

    /// <summary>UUID version 4 (RFC 4122, variant 10xx) ur slumpgeneratorn, alltså deterministiskt för en given seed.</summary>
    public static string Uuid4(Randomizer random)
    {
        var b = random.Bytes(16);
        b[6] = (byte)((b[6] & 0x0F) | 0x40);
        b[8] = (byte)((b[8] & 0x3F) | 0x80);
        var h = Convert.ToHexString(b).ToLowerInvariant();
        return $"{h[..8]}-{h[8..12]}-{h[12..16]}-{h[16..20]}-{h[20..]}";
    }

    private static JsonNode? Coerce(JsonNode value, IOpenApiSchema schema)
    {
        var text = ResponseStore.ToText(value);
        var type = SchemaUtil.TypeOf(schema);
        if (value is JsonValue && type == "integer" && long.TryParse(text, out var l)) return JsonValue.Create(l);
        if (value is JsonValue && type == "number" &&
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return JsonValue.Create(d);
        if (value is JsonValue && type == "boolean" && bool.TryParse(text, out var b)) return JsonValue.Create(b);
        return value.DeepClone();
    }
}
