using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace TestGen.Scenarios;

/// <summary>Ett genererat testscenario för en API-operation.</summary>
public sealed record Scenario
{
    public string Name { get; set; } = "";
    public string OperationId { get; set; } = "";
    public string Method { get; set; } = "";
    public string Path { get; set; } = "";

    /// <summary>"valid" eller "negative".</summary>
    public string Kind { get; set; } = "valid";

    /// <summary>Operationer som måste köras före (t.ex. "createCustomer").</summary>
    public List<string> Setup { get; set; } = new();

    public JsonNode? Body { get; set; }

    /// <summary>Bodyns mediatyp när den inte är JSON (t.ex. "application/x-www-form-urlencoded"); null = JSON.</summary>
    public string? ContentType { get; set; }
    public Dictionary<string, string> Query { get; set; } = new();
    public Dictionary<string, string> Headers { get; set; } = new();
    public List<int> ExpectedStatus { get; set; } = new();
}

public static class ScenarioJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(IEnumerable<Scenario> scenarios) =>
        JsonSerializer.Serialize(Normalize(scenarios), Options) + "\n";

    public static string Serialize(Scenario scenario) =>
        JsonSerializer.Serialize(Normalize(scenario), Options) + "\n";

    public static List<Scenario> DeserializeList(string json) =>
        JsonSerializer.Deserialize<List<Scenario>>(json, Options) ?? new();

    public static Scenario? Deserialize(string json) =>
        JsonSerializer.Deserialize<Scenario>(json, Options);

    // Stabil ordning: scenarier sorteras, dictionaries sorteras på nyckel.
    private static List<Scenario> Normalize(IEnumerable<Scenario> scenarios) =>
        scenarios.Select(Normalize)
                 .OrderBy(s => s.OperationId, StringComparer.Ordinal)
                 .ThenBy(s => s.Kind, StringComparer.Ordinal)
                 .ThenBy(s => s.Name, StringComparer.Ordinal)
                 .ToList();

    private static Scenario Normalize(Scenario s) => new()
    {
        Name = s.Name,
        OperationId = s.OperationId,
        Method = s.Method,
        Path = s.Path,
        Kind = s.Kind,
        Setup = s.Setup,
        Body = s.Body is null ? null : SortKeys(s.Body),
        ContentType = s.ContentType,
        Query = Sorted(s.Query),
        Headers = Sorted(s.Headers),
        ExpectedStatus = s.ExpectedStatus,
    };

    private static Dictionary<string, string> Sorted(Dictionary<string, string> d) =>
        new(d.OrderBy(kv => kv.Key, StringComparer.Ordinal));

    private static JsonNode? SortKeys(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => KeyValuePair.Create(kv.Key, SortKeys(kv.Value)))),
        JsonArray a => new JsonArray(a.Select(SortKeys).ToArray()),
        null => null,
        _ => node.DeepClone(),
    };
}
