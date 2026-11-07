using TestGen.Core.Run;

namespace TestGen.Cli;

/// <summary>Kommandoradsflaggor för kommandot generate: --spec, --out, --config, --schema.</summary>
public sealed class CliOptions
{
    public string? Spec { get; }
    public string? Out { get; }
    public string? Config { get; }
    public string? SchemaFile { get; }
    public bool Help { get; }

    private CliOptions(Args a)
    {
        Spec = a.Get("spec");
        Out = a.Get("out");
        Config = a.Get("config");
        SchemaFile = a.Get("schema");
        Help = a.Help;
    }

    public static CliOptions Parse(string[] args) => new(CommandArgs.Parse("generate", args, new[] { "spec", "out", "config", "schema" }));
}

/// <summary>Flaggorna till ett testgen-kommando, t.ex. <c>map</c>. Felen hänvisar till <c>testgen map --help</c>.</summary>
internal static class CommandArgs
{
    public static Args Parse(string command, string[] args, string[] valueNames, params string[] flagNames) =>
        Args.Parse("testgen " + command, command, args, valueNames, flagNames);
}
