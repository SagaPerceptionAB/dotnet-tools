namespace UiTestGen.Discovery;

internal static class SourceFiles
{
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", "node_modules", "dist", "wwwroot" };

    /// <summary>Alla filer under katalogen (fullständiga sökvägar), utan bin/, obj/, node_modules/, dist/, wwwroot/ och dolda mappar.</summary>
    public static IEnumerable<string> Enumerate(string directory)
    {
        if (!Directory.Exists(directory)) yield break;
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(dir)) yield return Path.GetFullPath(file);
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(sub);
                if (!name.StartsWith('.') && !Skipped.Contains(name)) pending.Push(sub);
            }
        }
    }
}
