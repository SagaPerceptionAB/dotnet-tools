using System.Text.RegularExpressions;
using SystemMap;
using TestGen.Core;
using TestGen.Core.Projects;
using TestGen.Core.Views;
using TestGen.Views.Discovery;

namespace TestGen.Views;

/// <summary>Resultatet av <see cref="ViewsBuilder"/>: filen och varningarna att visa.</summary>
public sealed record ViewsResult(ViewsFile File, IReadOnlyList<string> Warnings);

/// <summary>
/// Bygger <c>views.json</c>: vyerna ur källkoden, API:erna varje app anropar ur systemkartan, och kopplingen mellan formulär och
/// specens operationer. Läser filer och kartan, kör aldrig appen.
/// </summary>
public static partial class ViewsBuilder
{
    /// <param name="map">Systemkartan för solutionen (ger <c>apis</c> och vilket anrop ett formulär gör).</param>
    /// <param name="outDirectory">Katalogen där filen skrivs. Sökvägarna i filen är relativa till den.</param>
    public static ViewsResult Build(RepositoryFacts facts, CodeMapModel map, string outDirectory)
    {
        // En relativ sökväg finns inte mellan två enheter, och filen får inte innehålla absoluta sökvägar.
        if (!string.Equals(Path.GetPathRoot(Path.GetFullPath(outDirectory)), Path.GetPathRoot(facts.SolutionDirectory), StringComparison.OrdinalIgnoreCase))
            throw new UsageException($"views.json kan inte ligga på en annan enhet än solutionen ({Terminal.Relative(outDirectory)}).",
                "Låt --out peka på en fil på samma enhet som solutionen, t.ex. i testprojektet.");
        var warnings = new List<string>();
        var apps = new List<AppViews>();
        var specs = new Dictionary<string, IReadOnlyList<OperationInfo>?>(StringComparer.OrdinalIgnoreCase);

        foreach (var app in facts.WebApps.OrderBy(a => a.Name, StringComparer.Ordinal))
        {
            var found = new List<string>();
            var catalog = ViewDiscovery.Run(new DiscoveryContext(facts.SolutionDirectory, [app.Directory], found), Discoverers.All());
            warnings.AddRange(catalog.Warnings);
            if (catalog.Views.Count == 0)
            {
                warnings.Add($"{app.Name}: hittade inga vyer.");
                continue;
            }

            var appFacts = map.Apps.FirstOrDefault(a => a.Project.Equals(app.Name, StringComparison.OrdinalIgnoreCase));
            var clients = appFacts?.HttpClients ?? [];
            foreach (var client in clients.Where(c => c.TargetProject is null))
                warnings.Add($"{app.Name}: klienten {client.Client} anropar {client.ConfigKey ?? "en okänd adress"}, som inte är ett projekt i solutionen.");
            var apis = clients.Where(c => c.TargetProject is not null)
                .Select(c => new ViewsApi(c.TargetProject!, c.ConfigKey)).Distinct()
                .OrderBy(a => a.Project, StringComparer.Ordinal).ThenBy(a => a.ConfigKey, StringComparer.Ordinal).ToList();
            foreach (var api in apis)
                if (!specs.ContainsKey(api.Project)) specs[api.Project] = LoadSpec(facts, api.Project, warnings);

            var calls = PageCalls(map, app.Name, clients);
            var targets = new Dictionary<(string View, string Form), string>();
            var operations = apis.Select(a => a.Project).Distinct().Where(p => specs[p] is not null).ToDictionary(p => p, p => specs[p]!);
            var views = catalog.Views
                .Select(v => AddPageCall(v, calls, targets))
                .Select(v => Link(v, operations, targets, warnings))
                .Select(v => v with { Source = Rebase(facts.SolutionDirectory, v.Source, outDirectory) })
                .ToList();
            apps.Add(new AppViews(app.Name, views, apis));
        }
        return new ViewsResult(ViewsFile.Of(apps), warnings.Distinct().ToList());
    }

    private static IReadOnlyList<OperationInfo>? LoadSpec(RepositoryFacts facts, string apiProject, List<string> warnings)
    {
        var api = facts.Project(apiProject);
        var spec = api is null ? null : facts.SpecOf(api);
        if (spec is null)
        {
            warnings.Add($"{apiProject} saknar spec, så formulär som anropar det kopplas inte till operationer. Lägg en spec vid projektet.");
            return null;
        }
        return OpenApiLoader.Load(spec);
    }

    private static string Rebase(string root, string source, string outDirectory) =>
        Path.GetRelativePath(Path.GetFullPath(outDirectory), Path.GetFullPath(Path.Combine(root, source))).Replace('\\', '/');

    // --- Anropet en sida gör när formuläret skickas, ur systemkartan ---

    private sealed record PageCall(string Project, ApiCall Call);

    /// <summary>Anropen till andra projekt som appens sidors skicka-hanterare gör, per sidans rutt (normaliserad).</summary>
    private static Dictionary<string, List<PageCall>> PageCalls(CodeMapModel map, string app, IReadOnlyList<HttpClientFacts> clients)
    {
        var targets = clients.Where(c => c.TargetProject is not null).GroupBy(c => c.IntegrationId).ToDictionary(g => g.Key, g => g.First().TargetProject!);
        var result = new Dictionary<string, List<PageCall>>(StringComparer.OrdinalIgnoreCase);
        var pages = map.EntryPoints.Where(e => e.Project.Equals(app, StringComparison.OrdinalIgnoreCase)
            && e.Kind is EntryKind.RazorPage or EntryKind.BlazorPage or EntryKind.MvcView && !e.Method.Equals("GET", StringComparison.OrdinalIgnoreCase));
        foreach (var page in pages)
        {
            var key = Pattern(page.CodePath);
            var list = result.TryGetValue(key, out var existing) ? existing : result[key] = [];
            foreach (var use in Uses(page.Steps))
                if (targets.TryGetValue(use.Id, out var project) && ParseOperation(use.Operation) is { } call && !list.Contains(new PageCall(project, call)))
                    list.Add(new PageCall(project, call));
        }
        return result;
    }

    private static IEnumerable<IntegrationUse> Uses(IReadOnlyList<Step> steps) =>
        steps.SelectMany(s => (s.Integration is null ? [] : new[] { s.Integration }).Concat(Uses(s.Steps)));

    /// <summary><c>POST customers</c> blir <c>POST /customers</c>. Annat (som "läsa Customers") är inget HTTP-anrop.</summary>
    private static ApiCall? ParseOperation(string operation)
    {
        var match = HttpOperation().Match(operation);
        return match.Success ? new ApiCall(match.Groups[1].Value, "/" + match.Groups[2].Value.TrimStart('/')) : null;
    }

    /// <summary>En sida med ett enda formulär utan känt anrop får det enda anrop som dess skicka-hanterare gör.</summary>
    private static View AddPageCall(View view, Dictionary<string, List<PageCall>> calls, Dictionary<(string, string), string> targets)
    {
        if (view.Forms is not [{ SubmitsTo: null } form] || !calls.TryGetValue(Pattern(view.Route), out var list) || list is not [var only]) return view;
        targets[(view.Id, form.Id)] = only.Project;
        return view with { Forms = [form with { SubmitsTo = only.Call }] };
    }

    // --- Formulär till operation och fält till specens fält ---

    private static View Link(View view, Dictionary<string, IReadOnlyList<OperationInfo>> specs, Dictionary<(string View, string Form), string> targets, List<string> warnings) =>
        view with { Forms = view.Forms.Select(f => Link(view, f, specs, targets, warnings)).ToList() };

    private static Form Link(View view, Form form, Dictionary<string, IReadOnlyList<OperationInfo>> specs, Dictionary<(string View, string Form), string> targets, List<string> warnings)
    {
        if (form.SubmitsTo is not { } call) return form;
        var projects = targets.TryGetValue((view.Id, form.Id), out var project) ? [project] : specs.Keys.Order(StringComparer.Ordinal).ToList();
        var known = projects.Where(specs.ContainsKey).ToList();
        var operation = known.SelectMany(p => specs[p]).FirstOrDefault(o =>
            o.Method.Equals(call.Method, StringComparison.OrdinalIgnoreCase) && Pattern(o.Path).Equals(Pattern(call.Path), StringComparison.OrdinalIgnoreCase));
        if (operation is null)
        {
            if (known.Count > 0)
                warnings.Add($"{view.Route}: formuläret {form.Id} anropar {call.Method} {call.Path}, men specen har ingen sådan operation.");
            return form;
        }
        var properties = operation.BodySchema?.Props().Keys.ToList() ?? [];
        return form with
        {
            OperationId = operation.OperationId,
            Fields = form.Fields.Select(f => f with { SpecField = SpecField(f.Name, properties) }).ToList(),
        };
    }

    /// <summary>Specens fält som heter som formulärfältet (skiftlägesokänsligt), eller som sista delen av namnet: <c>Input.Name</c> motsvarar <c>name</c>.</summary>
    private static string? SpecField(string name, IReadOnlyList<string> properties)
    {
        var last = name[(name.LastIndexOf('.') + 1)..];
        return properties.FirstOrDefault(p => p.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? properties.FirstOrDefault(p => p.Equals(last, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Sökvägen med parametrarna som <c>{}</c> och utan avslutande snedstreck, så att <c>/p/{id:int}</c> och <c>/p/{productId}</c> är lika.</summary>
    private static string Pattern(string path)
    {
        var p = Placeholder().Replace(path, "{}").TrimEnd('/');
        return p.Length == 0 ? "/" : p;
    }

    [GeneratedRegex(@"\{[^}]*\}|:[A-Za-z_]\w*\??")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"^(GET|POST|PUT|PATCH|DELETE) (\S+)$")]
    private static partial Regex HttpOperation();
}
