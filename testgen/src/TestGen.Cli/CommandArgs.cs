namespace TestGen.Cli;

/// <summary>Flaggorna till ett testgen-kommando, t.ex. <c>map</c>. Felen hänvisar till <c>testgen map --help</c>.</summary>
internal static class CommandArgs
{
    public static Args Parse(string command, string[] args, string[] valueNames, params string[] flagNames) =>
        Args.Parse("testgen " + command, command, args, valueNames, flagNames);
}
