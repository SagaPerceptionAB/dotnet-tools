using TestGen.Scenarios;

namespace Api.Tests;

/// <summary>Hittar och läser scenarios/generated.</summary>
public static class ScenarioCatalog
{
    /// <summary>Hittar scenarios/&lt;sub&gt;. Styrs av TESTGEN_SCENARIOS (rotkatalog) annars sökning uppåt från bin-katalogen.</summary>
    public static string? FindDir(string sub)
    {
        var env = Environment.GetEnvironmentVariable("TESTGEN_SCENARIOS");
        if (!string.IsNullOrWhiteSpace(env))
        {
            var p = Path.Combine(env, sub);
            return Directory.Exists(p) ? p : null;
        }
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            foreach (var rel in new[] { "scenarios", Path.Combine("tests", "Api.Tests", "scenarios") })
            {
                var p = Path.Combine(d.FullName, rel, sub);
                if (Directory.Exists(p)) return p;
            }
        }
        return null;
    }

    /// <summary>Alla genererade scenarier (läses en gång).</summary>
    public static IReadOnlyList<Scenario> All => Loaded.Value.All;

    private static readonly Lazy<(List<Scenario> All, Dictionary<string, Scenario> Valid)> Loaded = new(() =>
    {
        var all = LoadGenerated();
        var valid = new Dictionary<string, Scenario>();
        foreach (var s in all.Where(s => s.Kind == "valid")) valid.TryAdd(s.OperationId, s);
        return (all, valid);
    });

    private static List<Scenario> LoadGenerated()
    {
        var dir = FindDir("generated");
        if (dir is null) return new();
        var result = new List<Scenario>();
        foreach (var f in Directory.EnumerateFiles(dir, "*.json").Order(StringComparer.Ordinal))
        {
            var text = File.ReadAllText(f);
            if (text.AsSpan().TrimStart().StartsWith("["))
                result.AddRange(ScenarioJson.DeserializeList(text));
            else if (ScenarioJson.Deserialize(text) is { } s)
                result.Add(s);
        }
        return result;
    }

    /// <summary>Giltigt (kind=valid) scenario för en operation, om något finns.</summary>
    public static Scenario? FindValid(string operationId) => Loaded.Value.Valid.GetValueOrDefault(operationId);
}
