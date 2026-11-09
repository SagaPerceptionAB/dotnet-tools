using System.Text.Json.Nodes;

namespace TestGen.Core;

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
