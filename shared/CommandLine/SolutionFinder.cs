namespace SagaPerception.CommandLine;

/// <summary>Hittar solutionen (eller projektet) som ett kommando ska arbeta mot. Ren filsystemslogik, ingen MSBuild.</summary>
public static class SolutionFinder
{
    /// <summary>Filändelserna för en solution. Ett projekt (<see cref="ProjectExtension"/>) går också att använda.</summary>
    public static readonly string[] SolutionExtensions = [".sln", ".slnx"];
    public const string ProjectExtension = ".csproj";

    /// <summary>
    /// Solutionen i <paramref name="start"/> eller närmaste mapp ovanför, till och med repots rot (där .git ligger): den visar hela
    /// systemet. Finns ingen solution används närmaste projekt (.csproj). Flera i samma mapp: fel, så att fel system inte väljs.
    /// Sökvägen är relativ till <paramref name="start"/>. <paramref name="see"/> är kommandot som visas i tipsen, t.ex. <c>testgen</c>.
    /// </summary>
    public static string Find(string start, string see) =>
        TryFind(start, see)
        ?? throw new UsageException($"Hittade ingen solution (.sln, .slnx) eller .csproj i {start} eller mapparna ovanför.",
            HintBelow(start, see) ?? $"Gå till mappen där solutionen ligger, eller ange den: {see} --solution src/Butik.sln");

    /// <summary>Mappar som aldrig innehåller en solution att peka på. Dolda mappar (.git, .vs ...) hoppas också över.</summary>
    private static readonly string[] SkippedFolders = ["bin", "obj", "node_modules"];

    /// <summary>
    /// Solutionerna (.sln, .slnx) i mapparna under <paramref name="start"/>, som sökvägar relativa till den, till och med
    /// <paramref name="maxDepth"/> nivåer ner. Grundast först, därefter i bokstavsordning. Bara för tips: <see cref="TryFind"/> väljer aldrig
    /// en solution nedåt, eftersom det vore att gissa vilket system som menas.
    /// </summary>
    public static IReadOnlyList<string> FindBelow(string start, int maxDepth = 3)
    {
        var found = new List<string>();
        for (var level = new List<DirectoryInfo> { new(start) }; level.Count > 0 && maxDepth-- > 0;)
        {
            var next = new List<DirectoryInfo>();
            foreach (var parent in level)
                foreach (var dir in Folders(parent))
                {
                    next.Add(dir);
                    found.AddRange(Files(dir).Where(f => SolutionExtensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase))
                        .Select(f => Path.GetRelativePath(start, f.FullName)).Order(StringComparer.Ordinal));
                }
            level = next;
        }
        return found;
    }

    /// <summary>Tipset när ingenting hittades uppåt: <c>Gör så här: systemmap --solution backend/Butik.slnx</c>. Null när det inte finns någon solution i en undermapp.</summary>
    public static string? HintBelow(string start, string see)
    {
        var below = FindBelow(start);
        if (below.Count == 0) return null;
        var more = below.Count > 1 ? $" (andra: {string.Join(", ", below.Skip(1).Take(3))}{(below.Count > 4 ? ", ..." : "")})" : "";
        return $"Gör så här: {see} --solution {below[0].Replace('\\', '/')}{more}";
    }

    private static IEnumerable<DirectoryInfo> Folders(DirectoryInfo parent)
    {
        try
        {
            return parent.GetDirectories()
                .Where(d => !d.Name.StartsWith('.') && !SkippedFolders.Contains(d.Name, StringComparer.OrdinalIgnoreCase) && d.LinkTarget is null)
                .OrderBy(d => d.Name, StringComparer.Ordinal).ToList();
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException) { return []; }
    }

    private static IEnumerable<FileInfo> Files(DirectoryInfo dir)
    {
        try { return dir.GetFiles(); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException) { return []; }
    }

    /// <summary>Som <see cref="Find"/>, men null när det inte finns någon solution eller något projekt. Flera att välja på är fortfarande ett fel.</summary>
    public static string? TryFind(string start, string see)
    {
        IReadOnlyList<FileInfo>? project = null;
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            // Filändelsen jämförs själv: i Windows träffar mönstret *.sln även .slnx.
            var files = dir.GetFiles();
            var solutions = files.Where(f => SolutionExtensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase)).ToList();
            if (solutions.Count > 0) return Pick(start, solutions, see);
            if (project is null && files.Where(f => f.Extension.Equals(ProjectExtension, StringComparison.OrdinalIgnoreCase)).ToList() is { Count: > 0 } found)
                project = found;
            if (Path.Exists(Path.Combine(dir.FullName, ".git"))) break;
        }
        return project is not null ? Pick(start, project, see) : null;
    }

    private static string Pick(string start, IReadOnlyList<FileInfo> found, string see)
    {
        if (found.Count == 1) return Path.GetRelativePath(start, found[0].FullName);
        var names = found.Select(f => f.Name).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var dir = found[0].DirectoryName!;
        throw new UsageException($"Det finns flera att välja på i {dir}: {string.Join(", ", names)}.",
            $"Ange vilken som ska användas, t.ex. {see} --solution {Path.GetRelativePath(start, Path.Combine(dir, names[0]))}");
    }
}
