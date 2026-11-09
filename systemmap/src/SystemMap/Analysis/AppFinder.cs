using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SystemMap;

/// <summary>
/// Fakta per app (projekt med <c>Sdk.Web</c> eller <c>WebApplication</c>): sort, hälsovägar, databaser och utgående HTTP-klienter.
/// Registreringar i projekt som appen refererar räknas till appen.
/// </summary>
internal static partial class AppFinder
{
    public static List<AppFacts> Find(
        IReadOnlyList<Project> projects, Solution solution, ModelCache models, ServiceResolver resolver, CallSites sites, IReadOnlyList<EntryCandidate> entries,
        IReadOnlyDictionary<string, Integration> integrations)
    {
        var ports = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        var apps = projects.Where(p => IsApp(p, models)).OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
        var result = new List<AppFacts>();
        foreach (var app in apps)
        {
            var names = Closure(app, solution, projects).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            var kinds = entries.Where(e => names.Contains(e.Project)).Select(e => e.Kind switch
            {
                EntryKind.RazorPage or EntryKind.BlazorPage or EntryKind.MvcView => AppKind.Web,
                _ => AppKind.Api,
            }).Distinct().Order().ToList();

            var databases = resolver.DbContexts.Where(d => names.Contains(d.Project))
                .Select(d => new DatabaseFacts(d.Provider, d.Context, d.ConfigKey)).Distinct()
                .OrderBy(d => d.Context, StringComparer.Ordinal).ThenBy(d => d.Provider, StringComparer.Ordinal).ToList();

            var dir = Path.GetDirectoryName(app.FilePath ?? "") ?? "";
            var clients = resolver.HttpClients.Where(c => names.Contains(c.Project))
                .Select(c => (c.Client, Info: c.Info with { ConfigKey = c.Info.ConfigKey ?? integrations.GetValueOrDefault("http:" + c.Info.Name)?.ConfigKey }))   // annars nyckeln som anropen hittade
                .Select(c => new HttpClientFacts(c.Client, c.Info.ConfigKey, "http:" + c.Info.Name, Target(app, dir, c.Info, projects, sites, models, ports)))
                .Distinct().OrderBy(c => c.Client, StringComparer.Ordinal).ThenBy(c => c.IntegrationId, StringComparer.Ordinal).ToList();

            result.Add(new AppFacts(app.Name, kinds, HealthPaths(names, sites, models), databases, clients));
        }
        return result;
    }

    /// <summary>Projektet och projekten det refererar, direkt och indirekt (bara de som kartan läser).</summary>
    private static IEnumerable<Project> Closure(Project app, Solution solution, IReadOnlyList<Project> projects)
    {
        var seen = new HashSet<ProjectId>();
        var pending = new Stack<Project>([app]);
        while (pending.Count > 0)
        {
            var project = pending.Pop();
            if (!seen.Add(project.Id)) continue;
            if (projects.Any(p => p.Id == project.Id)) yield return project;
            foreach (var reference in project.ProjectReferences)
                if (solution.GetProject(reference.ProjectId) is { } referenced) pending.Push(referenced);
        }
    }

    private static bool IsApp(Project project, ModelCache models)
    {
        if (project.FilePath is { } path && File.Exists(path) && Sdk().Match(ReadStart(path)) is { Success: true } sdk &&
            sdk.Groups[1].Value.StartsWith("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase))
            return true;
        foreach (var tree in models.Trees(project))
            if (models.For(tree) is { } model)
                foreach (var id in tree.GetRoot().DescendantNodes().OfType<IdentifierNameSyntax>())
                    if (id.Identifier.ValueText == "WebApplication" && model.GetSymbolInfo(id).Symbol is INamedTypeSymbol { Name: "WebApplication" })
                        return true;
        return false;
    }

    private static string ReadStart(string path)
    {
        using var reader = new StreamReader(path);
        var buffer = new char[2000];
        return new string(buffer, 0, reader.Read(buffer, 0, buffer.Length));
    }

    /// <summary><c>MapHealthChecks("/x")</c>, med prefix från <c>MapGroup</c>.</summary>
    private static List<string> HealthPaths(HashSet<string> projects, CallSites sites, ModelCache models)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (call, model) in sites.Named("MapHealthChecks"))
        {
            if (models.ProjectOf(call.SyntaxTree) is not { } project || !projects.Contains(project)) continue;
            var own = call.ArgumentList.Arguments.FirstOrDefault() is { } first ? Symbols.ConstantString(first.Expression, model) : null;
            if (own is null) continue;
            foreach (var prefix in sites.Prefixes(Symbols.Receiver(call), model))
                paths.Add(RouteTemplate.Combine(prefix, own.TrimStart('/'), "", ""));
        }
        return [.. paths.Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Projektet i solutionen som en HTTP-klient pekar på: namnet ur nyckeln (<c>ShopApi:BaseUrl</c> ger ShopApi) eller, om inget
    /// projekt heter så, projektet som lyssnar på adressen i appens appsettings (launchSettings.json eller <c>UseUrls</c>).
    /// </summary>
    private static string? Target(
        Project app, string appDir, HttpClientInfo info, IReadOnlyList<Project> projects, CallSites sites, ModelCache models, Dictionary<string, HashSet<int>> ports)
    {
        if (info.ConfigKey is null) return null;
        var others = projects.Where(p => p.Name != app.Name).OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
        var name = Origins.NameFromConfigKey(info.ConfigKey);
        if (others.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } byName) return byName.Name;
        if (ProjectFiles.ConfigValue(appDir, info.ConfigKey) is not { } url || ProjectFiles.LocalPort(url) is not { } port) return null;
        return others.FirstOrDefault(p => ListeningPorts(p, sites, models, ports).Contains(port))?.Name;
    }

    private static HashSet<int> ListeningPorts(Project project, CallSites sites, ModelCache models, Dictionary<string, HashSet<int>> cache)
    {
        if (cache.TryGetValue(project.Name, out var known)) return known;
        var found = new HashSet<int>(ProjectFiles.LaunchPorts(Path.GetDirectoryName(project.FilePath ?? "") ?? ""));
        foreach (var (call, model) in sites.Named("UseUrls"))
            if (models.ProjectOf(call.SyntaxTree) == project.Name)
                foreach (var argument in call.ArgumentList.Arguments)
                    if (Symbols.ConstantString(argument.Expression, model) is { } urls)
                        foreach (var url in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            if (ProjectFiles.LocalPort(url, listening: true) is { } port) found.Add(port);
        return cache[project.Name] = found;
    }

    [GeneratedRegex("""<Project\s+Sdk="([^"]+)""")]
    private static partial Regex Sdk();
}
