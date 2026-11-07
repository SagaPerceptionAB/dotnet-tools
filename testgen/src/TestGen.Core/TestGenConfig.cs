using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace TestGen.Core;

/// <summary>Innehållet i testgen.yaml.</summary>
public sealed class TestGenConfig
{
    public string? Spec { get; set; }
    public string? Output { get; set; }
    public int Seed { get; set; } = 12345;
    public string Locale { get; set; } = "sv";
    public Dictionary<string, string> FieldRules { get; set; } = new();
    public Dictionary<string, Dictionary<string, string>> Bindings { get; set; } = new();
    public Dictionary<string, Dictionary<string, object>> Overrides { get; set; } = new();
    public List<string> SkipOperations { get; set; } = new();

    /// <summary>Katalogen config lästes från; relativa sökvägar i filen utgår därifrån.</summary>
    [YamlIgnore] public string BaseDir { get; set; } = Directory.GetCurrentDirectory();

    public static TestGenConfig Load(string? path)
    {
        if (path is null) return new TestGenConfig();
        var yaml = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        var cfg = Run.FriendlyErrors.Guard(Run.SourceKind.Config, path, () => yaml.Deserialize<TestGenConfig>(File.ReadAllText(path))) ?? new TestGenConfig();
        cfg.BaseDir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        return cfg;
    }

    public string Resolve(string p) => Path.IsPathRooted(p) ? p : Path.Combine(BaseDir, p);
}
