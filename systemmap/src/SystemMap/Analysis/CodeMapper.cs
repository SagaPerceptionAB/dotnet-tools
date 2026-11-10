using Microsoft.CodeAnalysis;

namespace SystemMap;

/// <summary>Bygger systemkartan för en inläst solution.</summary>
public static class CodeMapper
{
    private static readonly string[] MethodOrder = ["GET", "POST", "PUT", "PATCH", "DELETE"];
    private static readonly string[] TestFrameworks =
        ["nunit.framework.dll", "xunit.core.dll", "xunit.v3.core.dll", "Microsoft.VisualStudio.TestPlatform.TestFramework.dll"];

    /// <param name="solutionPath">Sökvägen som visas i kartan (som användaren skrev den).</param>
    /// <param name="operations">Specens operationer, för att koppla ingångarna till operationId och visa skillnader. Null = ingen spec.</param>
    /// <param name="loadWarnings">Varningar från inläsningen (t.ex. projekt som inte gick att läsa).</param>
    public static async Task<CodeMapModel> AnalyzeAsync(
        Solution solution, string solutionPath, IReadOnlyList<ApiOperation>? operations = null,
        IEnumerable<string>? loadWarnings = null, CancellationToken ct = default)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(solution.FilePath ?? solutionPath)) ?? ".";
        var projects = SystemProjects(solution, solutionPath);
        var warnings = new List<string>(loadWarnings ?? []);
        var models = await ModelCache.CreateAsync(projects, ct);
        var origins = new Origins(models.For);
        var resolver = ServiceResolver.Build(projects, models, origins);
        var walker = new CallWalker(resolver, models, origins, root, warnings);
        var sites = new CallSites(projects, models);

        var entries = EntryPointFinder.Find(projects, models, sites, resolver, root, warnings);
        if (entries.Count == 0)
            warnings.Add("Hittade inga ingångar. Kartan känner igen controllers ([ApiController], [HttpGet] ...), minimal API (MapGet ...), Razor Pages och Blazor-sidor (@page), " +
                         "bakgrundstjänster (BackgroundService, IHostedService), konsolprogram (Main), meddelandehanterare (MassTransit, NServiceBus, Service Bus), " +
                         "jobb (Hangfire, Quartz), gRPC-tjänster och SignalR-hubbar.");

        var stepsByRoots = new Dictionary<string, IReadOnlyList<Step>>(StringComparer.Ordinal);
        foreach (var entry in entries)
            if (!stepsByRoots.ContainsKey(entry.RootsKey)) stepsByRoots[entry.RootsKey] = await walker.StepsFromAsync(entry.Roots, ct);

        var (matches, differences) = SpecMatcher.Match(entries, operations);
        var entryPoints = entries
            .Select(e => matches.TryGetValue(e, out var op)
                ? new EntryPoint(e.Kind, e.Method == "ANY" ? op.Method : e.Method, op.Path, e.CodePath, op.OperationId, e.Project, e.Handler, e.File, e.Line, e.Input, e.Output, stepsByRoots[e.RootsKey], e.Name)
                : new EntryPoint(e.Kind, e.Method, e.CodePath, e.CodePath, null, e.Project, e.Handler, e.File, e.Line, e.Input, e.Output, stepsByRoots[e.RootsKey], e.Name))
            // HTTP först (sökväg, metod), sedan det andra efter sort och namn.
            .OrderBy(e => e.Kind.IsHttp() ? 0 : 1).ThenBy(e => e.Kind.IsHttp() ? 0 : (int)e.Kind)
            .ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Path, StringComparer.Ordinal)
            .ThenBy(e => Array.IndexOf(MethodOrder, e.Method) is var i and >= 0 ? i : MethodOrder.Length).ThenBy(e => e.Method, StringComparer.Ordinal)
            .ThenBy(e => e.Name, StringComparer.Ordinal).ThenBy(e => e.Handler, StringComparer.Ordinal)
            .ToList();

        // En app utan någon sort (inga ingångar) är bara ett bibliotek som råkar använda WebApplication.
        var apps = AppFinder.Find(projects, solution, models, resolver, sites, entries, walker.Integrations).Where(a => a.Kinds.Count > 0).ToList();
        // Appens HTTP-klient pekar på ett projekt i solutionen: integrationen får veta vilket.
        var targets = apps.SelectMany(a => a.HttpClients).Where(c => c.TargetProject is not null)
            .GroupBy(c => c.IntegrationId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().TargetProject, StringComparer.Ordinal);
        var integrations = walker.Integrations.Values
            .Select(i => targets.TryGetValue(i.Id, out var target) ? i with { TargetProject = target } : i)
            .OrderBy(i => i.Kind).ThenBy(i => i.Name, StringComparer.Ordinal).ThenBy(i => i.Id, StringComparer.Ordinal).ToList();

        warnings.AddRange(resolver.Warnings);
        var order = ProjectOrder(entryPoints);
        var code = await CodeInventoryFinder.FindAsync(projects, models, walker, new CallWalker(resolver, models, origins, root, []), order.ToHashSet(StringComparer.Ordinal), root, ct);
        return new CodeMapModel(
            solutionPath,
            order,
            integrations,
            entryPoints,
            apps,
            differences,
            warnings.Select(w => Relative(w, root)).Distinct().Order(StringComparer.Ordinal).ToList(),
            code);
    }

    /// <summary>
    /// Sökvägar i en varning (t.ex. från MSBuild) relativt solutionens katalog, som filerna i kartan: kartan kan delas, och
    /// sökvägen på den här datorn hör inte hemma där.
    /// </summary>
    private static string Relative(string warning, string root) =>
        warning.Replace(root + Path.DirectorySeparatorChar, "", StringComparison.OrdinalIgnoreCase)
               .Replace(root.Replace('\\', '/') + "/", "", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Systemets projekt: de som står i solutionsfilen (eller det enda projektet om ett .csproj öppnades), utom testprojekt, och
    /// allt de refererar. Projekt som MSBuild läser in bara för att ett testprojekt refererar dem hör inte till systemet.
    /// Utan solutionsfil (kod i minnet) räknas alla projekt som listade.
    /// </summary>
    private static List<Project> SystemProjects(Solution solution, string solutionPath)
    {
        var file = Path.GetFullPath(solution.FilePath ?? solutionPath);
        var listed = File.Exists(file) ? SolutionFile.ProjectPaths(file).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
        bool IsSystem(Project p) => p.Language == LanguageNames.CSharp && !IsTestProject(p);

        var seen = new HashSet<ProjectId>();
        var pending = new Stack<Project>(solution.Projects.Where(p => IsSystem(p) && (listed is null || (p.FilePath is not null && listed.Contains(p.FilePath)))));
        while (pending.Count > 0)
        {
            var project = pending.Pop();
            if (!seen.Add(project.Id)) continue;
            foreach (var reference in project.ProjectReferences)
                if (solution.GetProject(reference.ProjectId) is { } referenced && IsSystem(referenced)) pending.Push(referenced);
        }
        return solution.Projects.Where(p => seen.Contains(p.Id)).ToList();
    }

    /// <summary>Testprojekt (NUnit, xUnit, MSTest) hoppas över: deras kod är inte en del av systemet.</summary>
    private static bool IsTestProject(Project project) =>
        project.MetadataReferences.OfType<PortableExecutableReference>().Any(r => TestFrameworks.Contains(Path.GetFileName(r.FilePath ?? ""), StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Projekten i den ordning anropen når dem: ingångens projekt först, sedan efter hur djupt i kedjan projektet först
    /// dyker upp. (Projektreferenserna säger inte det: i en clean architecture pekar Infrastructure på Application.)
    /// </summary>
    private static List<string> ProjectOrder(IEnumerable<EntryPoint> entries)
    {
        var depth = new Dictionary<string, int>(StringComparer.Ordinal);
        void Visit(string project, int d)
        {
            if (!depth.TryGetValue(project, out var known) || d < known) depth[project] = d;
        }
        void Walk(IEnumerable<Step> steps, int d)
        {
            foreach (var step in steps)
            {
                Visit(step.Project, d);
                Walk(step.Steps, d + 1);
            }
        }
        foreach (var entry in entries)
        {
            Visit(entry.Project, 0);
            Walk(entry.Steps, 1);
        }
        return depth.OrderBy(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key).ToList();
    }
}
