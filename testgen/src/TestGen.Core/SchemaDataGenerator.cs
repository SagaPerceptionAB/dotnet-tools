using TestGen.Scenarios;
using System.Text.Json.Nodes;
using Bogus;
using Microsoft.OpenApi.Models;

namespace TestGen.Core;

/// <summary>
/// Genererar värden från JSON Schema. Prioritet: enum → override → fältregel → example → typ/format/constraints.
/// </summary>
public sealed class SchemaDataGenerator
{
    private static readonly DateTime DateFrom = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime DateTo = new(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc);

    // En Faker per locale och tråd (att bygga den är dyrt); seeden sätts om vid varje användning.
    [ThreadStatic] private static Dictionary<string, Faker>? _fakers;

    private readonly Faker _faker;
    private readonly FieldRules _rules;

    public SchemaDataGenerator(int seed, string locale, FieldRules rules)
    {
        _fakers ??= new Dictionary<string, Faker>();
        if (!_fakers.TryGetValue(locale, out _faker!)) _fakers[locale] = _faker = new Faker(locale);
        _faker.Random = new Randomizer(seed);
        _rules = rules;
    }

    public Faker Faker => _faker;

    public JsonNode? GenerateField(string name, OpenApiSchema schema, JsonNode? fixedValue = null, int depth = 0)
    {
        schema = SchemaUtil.Effective(schema);   // oneOf/anyOf: första alternativet (andra alternativ genereras av ScenarioGenerator)
        var enumValues = schema.Enum.Select(SchemaUtil.ToNode).Where(n => n is not null).ToList();
        if (enumValues.Count > 0) return enumValues[_faker.Random.Number(0, enumValues.Count - 1)]!.DeepClone();

        if (fixedValue is not null) return Coerce(fixedValue, schema);

        var type = SchemaUtil.TypeOf(schema);
        if (type is null or "string")
        {
            var ruled = _rules.TryGenerate(name, schema, _faker);
            if (ruled is not null) return JsonValue.Create(ruled);
        }

        if (schema.Example is not null) return SchemaUtil.ToNode(schema.Example);
        if (schema.Default is not null) return SchemaUtil.ToNode(schema.Default);

        return type switch
        {
            "integer" => JsonValue.Create(GenInteger(schema)),
            "number" => JsonValue.Create(GenNumber(schema)),
            "boolean" => JsonValue.Create(_faker.Random.Bool()),
            "array" => GenArray(name, schema, depth),
            "object" => GenObject(schema, depth),
            _ => JsonValue.Create(GenString(schema)),
        };
    }

    private JsonNode GenObject(OpenApiSchema schema, int depth)
    {
        var obj = new JsonObject();
        if (depth > 6) return obj;
        var (props, _) = SchemaUtil.Flatten(schema);
        foreach (var (k, v) in props)
            if (!v.ReadOnly) obj[k] = GenerateField(k, v, null, depth + 1);   // readOnly skickas aldrig i en request
        return obj;
    }

    private JsonNode GenArray(string name, OpenApiSchema schema, int depth)
    {
        var arr = new JsonArray();
        if (schema.Items is null || depth > 6) return arr;
        var count = Math.Max(schema.MinItems ?? 1, 1);
        if (schema.MaxItems is { } mx) count = Math.Min(count, mx);
        for (var i = 0; i < count; i++) arr.Add(GenerateField(name, schema.Items, null, depth + 1));
        return arr;
    }

    private long GenInteger(OpenApiSchema s)
    {
        var min = s.Minimum is { } mn ? (long)Math.Ceiling(mn) + (s.ExclusiveMinimum == true && mn == Math.Ceiling(mn) ? 1 : 0) : 1;
        var max = s.Maximum is { } mx ? (long)Math.Floor(mx) - (s.ExclusiveMaximum == true && mx == Math.Floor(mx) ? 1 : 0) : Math.Max(min, 1000);
        if (s.Minimum is null && max < min) min = max;
        if (max < min) max = min;
        return _faker.Random.Long(min, max);
    }

    private double GenNumber(OpenApiSchema s)
    {
        var min = s.Minimum is { } mn ? (double)mn : 1.0;
        var max = s.Maximum is { } mx ? (double)mx : Math.Max(min, 1000.0);
        if (s.Minimum is null && max < min) min = max;
        if (max < min) max = min;
        var v = Math.Round(_faker.Random.Double(min, max), 2);
        return Math.Min(Math.Max(v, min), max);
    }

    private string GenString(OpenApiSchema s)
    {
        string v;
        if (!string.IsNullOrEmpty(s.Pattern)) return FieldRules.Fit(PatternGenerator.Generate(s.Pattern, _faker.Random), s);
        switch (s.Format?.ToLowerInvariant())
        {
            case "email": v = _faker.Internet.Email(); break;
            case "uuid" or "guid": v = Uuid4(_faker.Random); break;
            case "date-time": v = _faker.Date.Between(DateFrom, DateTo).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"); break;
            case "date": v = _faker.Date.Between(DateFrom, DateTo).ToString("yyyy-MM-dd"); break;
            case "uri" or "url": v = _faker.Internet.Url(); break;
            case "hostname": v = _faker.Internet.DomainName(); break;
            case "ipv4": v = _faker.Internet.Ip(); break;
            case "ipv6": v = _faker.Internet.Ipv6(); break;
            case "password": v = _faker.Internet.Password(); break;
            case "byte": v = Convert.ToBase64String(_faker.Random.Bytes(8)); break;
            default: v = _faker.Lorem.Word(); break;
        }
        return FieldRules.Fit(v, s);
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

    private static JsonNode? Coerce(JsonNode value, OpenApiSchema schema)
    {
        var text = ResponseStore.ToText(value);
        var type = SchemaUtil.TypeOf(schema);
        if (value is JsonValue && type == "integer" && long.TryParse(text, out var l)) return JsonValue.Create(l);
        if (value is JsonValue && type == "number" &&
            double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
            return JsonValue.Create(d);
        if (value is JsonValue && type == "boolean" && bool.TryParse(text, out var b)) return JsonValue.Create(b);
        return value.DeepClone();
    }
}
