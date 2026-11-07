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
        var projects = solution.Projects.Where(p => p.Language == LanguageNames.CSharp && !IsTestProject(p)).ToList();
        var warnings = new List<string>(loadWarnings ?? []);
        var models = await ModelCache.CreateAsync(projects, ct);
        var resolver = ServiceResolver.Build(projects, models);
        var walker = new CallWalker(resolver, models, new Origins(models.For), root, warnings);

        var entries = EntryPointFinder.Find(projects, models, root, warnings);
        if (entries.Count == 0)
            warnings.Add("Hittade inga controllers med route-attribut. Kartan känner än så länge igen controllers ([ApiController], [HttpGet] ...), inte minimal API.");

        var stepsByAction = new Dictionary<string, IReadOnlyList<Step>>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var key = Symbols.Key(entry.Action);
            if (!stepsByAction.ContainsKey(key)) stepsByAction[key] = await walker.StepsFromAsync(entry.Action, ct);
        }

        var (matches, differences) = SpecMatcher.Match(entries, operations);
        var entryPoints = entries
            .Select(e => matches.TryGetValue(e, out var op)
                ? new EntryPoint(e.Method == "ANY" ? op.Method : e.Method, op.Path, e.CodePath, op.OperationId, e.Project, e.Handler, e.File, e.Line, e.Input, e.Output, stepsByAction[Symbols.Key(e.Action)])
                : new EntryPoint(e.Method, e.CodePath, e.CodePath, null, e.Project, e.Handler, e.File, e.Line, e.Input, e.Output, stepsByAction[Symbols.Key(e.Action)]))
            .OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Path, StringComparer.Ordinal)
            .ThenBy(e => Array.IndexOf(MethodOrder, e.Method) is var i and >= 0 ? i : MethodOrder.Length).ThenBy(e => e.Method, StringComparer.Ordinal)
            .ThenBy(e => e.Handler, StringComparer.Ordinal)
            .ToList();

        warnings.AddRange(resolver.Warnings);
        return new CodeMapModel(
            solutionPath,
            ProjectOrder(entryPoints),
            walker.Integrations.Values.OrderBy(i => i.Kind).ThenBy(i => i.Name, StringComparer.Ordinal).ThenBy(i => i.Id, StringComparer.Ordinal).ToList(),
            entryPoints,
            differences,
            warnings.Select(w => Relative(w, root)).Distinct().Order(StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// Sökvägar i en varning (t.ex. från MSBuild) relativt solutionens katalog, som filerna i kartan: kartan kan delas, och
    /// sökvägen på den här datorn hör inte hemma där.
    /// </summary>
    private static string Relative(string warning, string root) =>
        warning.Replace(root + Path.DirectorySeparatorChar, "", StringComparison.OrdinalIgnoreCase)
               .Replace(root.Replace('\\', '/') + "/", "", StringComparison.OrdinalIgnoreCase);

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
