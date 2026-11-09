namespace TestGen.Views.Tests;

/// <summary>Roten och mapparna att leta i för en mapp eller en projektfil.</summary>
internal static class TestRoots
{
    public static (string Root, IReadOnlyList<string> Directories) From(string path)
    {
        var full = Path.GetFullPath(path);
        var dir = Directory.Exists(full) ? full : Path.GetDirectoryName(full)!;
        return (dir, [dir]);
    }
}
