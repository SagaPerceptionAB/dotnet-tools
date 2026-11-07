using System.Text.Json.Nodes;
using YamlDotNet.Serialization;

namespace Api.Tests;

public sealed class ManualScenario
{
    public string Name { get; set; } = "";
    public string File { get; set; } = "";
    public List<ManualStep> Steps { get; set; } = new();
    public override string ToString() => string.IsNullOrEmpty(Name) ? File : Name;
}

public sealed class ManualStep
{
    public string Call { get; set; } = "";
    public JsonNode? Body { get; set; }
    /// <summary>Alias som svaret sparas under (standard: operationId).</summary>
    public string? Save { get; set; }
    public int? ExpectStatus { get; set; }
    public JsonNode? ExpectBody { get; set; }
}

/// <summary>Läser scenarios/manual/*.yaml.</summary>
public static class ManualScenarioReader
{
    public static List<ManualScenario> LoadAll()
    {
        var dir = ScenarioCatalog.FindDir("manual");
        if (dir is null) return new();
        return Directory.EnumerateFiles(dir, "*.y*ml").Order(StringComparer.Ordinal)
            .Where(f => f.EndsWith(".yaml") || f.EndsWith(".yml"))
            .Select(Parse).ToList();
    }

    private static readonly IDeserializer Yaml = new DeserializerBuilder().Build();

    public static ManualScenario Parse(string file)
    {
        var root = ToNode(Yaml.Deserialize<object?>(File.ReadAllText(file))) as JsonObject
                   ?? throw new InvalidDataException($"{file}: roten måste vara ett objekt.");
        var sc = new ManualScenario
        {
            File = Path.GetFileName(file),
            Name = root["name"]?.GetValue<string>() ?? Path.GetFileNameWithoutExtension(file),
        };
        foreach (var s in root["steps"] as JsonArray ?? new JsonArray())
        {
            var o = s as JsonObject ?? throw new InvalidDataException($"{file}: ogiltigt steg.");
            var expect = o["expect"] as JsonObject;
            sc.Steps.Add(new ManualStep
            {
                Call = o["call"]?.GetValue<string>() ?? throw new InvalidDataException($"{file}: steg saknar 'call'."),
                Body = o["body"]?.DeepClone(),
                Save = o["save"]?.GetValue<string>(),
                ExpectStatus = (int?)expect?["status"]?.GetValue<long>(),
                ExpectBody = expect?["body"]?.DeepClone(),
            });
        }
        return sc;
    }

    // YamlDotNet ger Dictionary<object,object>/List<object>/string; tal och bool kommer som strängar
    // om inte taggade, så skalärer typtolkas här (JSON-typen ska bevaras).
    private static JsonNode? ToNode(object? v) => v switch
    {
        null => null,
        IDictionary<object, object> d => new JsonObject(d.Select(kv =>
            KeyValuePair.Create(kv.Key.ToString()!, ToNode(kv.Value)))),
        IList<object> l => new JsonArray(l.Select(ToNode).ToArray()),
        string s => Scalar(s),
        _ => JsonValue.Create(v.ToString()),
    };

    private static JsonNode? Scalar(string s)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (s is "true" or "false") return JsonValue.Create(s == "true");
        if (s is "null" or "~") return null;
        var plain = !s.TrimStart('-').StartsWith('0') || s.TrimStart('-') == "0";
        if (plain && long.TryParse(s, System.Globalization.NumberStyles.AllowLeadingSign, inv, out var l))
            return JsonValue.Create(l);
        if (s.Contains('.') && (plain || s.TrimStart('-').StartsWith("0.")) &&
            double.TryParse(s, System.Globalization.NumberStyles.Float, inv, out var d))
            return JsonValue.Create(d);
        return JsonValue.Create(s);
    }
}
