using SystemMap;
using TestGen.Core.Projects;

namespace TestGen.Views;

/// <summary>Läser solutionen med MSBuild (systemkartan) och bygger <c>views.json</c> med <see cref="ViewsBuilder"/>.</summary>
public static class ViewsGenerator
{
    /// <param name="restore">Kör <c>dotnet restore</c> först så att paketens typer går att läsa.</param>
    /// <param name="progress">Får en rad om vad som händer.</param>
    public static async Task<ViewsResult> GenerateAsync(RepositoryFacts facts, string outDirectory, bool restore, Action<string>? progress = null, CancellationToken ct = default)
    {
        if (!facts.WebApps.Any())
            throw new UsageException($"Hittade ingen webbapp i {Path.GetFileName(facts.SolutionPath)}.",
                "Vyer läses ur webbprojekt som inte är API:er (Blazor, Razor Pages, MVC). Peka på rätt solution med --solution.");
        using var loaded = await SolutionLoader.LoadAsync(facts.SolutionPath, restore, progress, ct);
        progress?.Invoke("Följer anropen ...");
        var map = await CodeMapper.AnalyzeAsync(loaded.Solution, facts.SolutionPath, operations: null, loaded.Warnings, ct);
        var result = ViewsBuilder.Build(facts, map, outDirectory);
        return result with { Warnings = [.. loaded.Warnings, .. result.Warnings] };
    }
}
