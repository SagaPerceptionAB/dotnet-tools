using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ShopApi.Validation;

/// <summary>En regel för ett fält i indata, samma som i specen (samples/shop.openapi.yaml).</summary>
public sealed record Rule(string Type, bool Required, double? Min, double? Max, string[]? OneOf, string? Pattern = null, bool Email = false)
{
    public static Rule Str(bool required = false, int? min = null, int? max = null, string[]? oneOf = null, string? pattern = null, bool email = false) =>
        new("string", required, min, max, oneOf, pattern, email);

    // Enkel men rimlig regel: något@domän.ändelse utan mellanslag och med exakt ett @.
    private static readonly Regex EmailShape = new(@"^[^@\s]+@[^@\s.]+(\.[^@\s.]+)+$");
    public static Rule Int(bool required = false, int? min = null, int? max = null) => new("integer", required, min, max, null);
    public static Rule Num(bool required = false, double? min = null, double? max = null) => new("number", required, min, max, null);

    public string? Check(JsonNode value)
    {
        if (value is not JsonValue v) return "fel typ";
        switch (Type)
        {
            case "string":
                if (!v.TryGetValue<string>(out var s)) return "ska vara text";
                if (Min is { } mn && s.Length < mn) return "för kort";
                if (Max is { } mx && s.Length > mx) return "för lång";
                if (OneOf is not null && !OneOf.Contains(s)) return "ogiltigt värde";
                if (Email && !EmailShape.IsMatch(s)) return "är ingen giltig e-postadress";
                if (Pattern is not null && !Regex.IsMatch(s, Pattern)) return $"följer inte mönstret {Pattern}";
                return null;
            case "integer":
                if (!v.TryGetValue<long>(out var l)) return "ska vara heltal";
                if (l is < int.MinValue or > int.MaxValue) return "utanför intervall";
                return Range(l);
            default:
                return v.TryGetValue<double>(out var d) ? Range(d) : "ska vara tal";
        }
    }

    private string? Range(double d) =>
        Min is { } mn && d < mn ? "under minimum" : Max is { } mx && d > mx ? "över maximum" : null;
}
