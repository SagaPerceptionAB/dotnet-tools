namespace CommandLine;

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
            $"Gå till mappen där solutionen ligger, eller ange den: {see} --solution src/Butik.sln");

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
