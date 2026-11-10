namespace SystemMap;

/// <summary>Kartan som text i terminalen: ett träd per ingång.</summary>
public static class CodeMapText
{
    /// <param name="hidden">Namnen på delarna som är dolda (kartan är redan utan dem, se <see cref="CodeMapOverview.Hide(CodeMapModel, IReadOnlySet{string})"/>), så att den som läser vet att kartan är ofullständig.</param>
    public static IEnumerable<string> Lines(CodeMapModel map, IReadOnlyList<string>? hidden = null)
    {
        yield return $"Systemkarta för {map.Solution}";
        yield return "Kartan visar vägar som är möjliga enligt koden, inte vilken väg ett visst anrop tog.";
        if (hidden is { Count: > 0 }) yield return $"Dolda delar: {string.Join(", ", hidden)}. Vägarna genom dem består.";
        yield return map.Projects.Count > 0 ? $"Projekt i den ordning anropen når dem: {string.Join(" → ", map.Projects)}" : "Hittade inga ingångar.";
        var overview = CodeMapOverview.Build(map);
        if (map.Integrations.Count > 0)
        {
            // Varje integration en gång, med det som görs mot den: läsningar och skrivningar i en databas, meddelandena till en kö.
            yield return "Integrationer:";
            foreach (var integration in map.Integrations)
            {
                yield return "  " + Label(integration);
                var operations = overview.Nodes.Where(n => n.Parent == "int:" + integration.Id).Select(n => n.Name).ToList();
                if (operations.Count > 0) yield return "    " + string.Join(", ", operations);
            }
        }

        if (map.Apps.Count > 0)
        {
            yield return "Appar:";
            foreach (var app in map.Apps)
                yield return "  " + AppLine(app);
        }

        foreach (var entry in CallGroups.Order(map.EntryPoints))
        {
            yield return "";
            // Det som inte är HTTP har sitt namn i stället för metod och sökväg, och namnet säger redan vad det är.
            yield return entry.Kind.IsHttp()
                ? $"{entry.Method} {entry.Path}{(entry.OperationId is null ? "" : $"  ({entry.OperationId})")}  {entry.Handler} [{entry.Project}]{(entry.Kind == EntryKind.Controller ? "" : $" {CodeMapJson.KindCode(entry.Kind)}")}"
                : $"{entry.Key}  {entry.Handler} [{entry.Project}]";
            if (entry.Input is not null || entry.Output is not null)
                yield return $"  in: {Shape(entry.Input) ?? "–"}   ut: {Shape(entry.Output) ?? "–"}";
            foreach (var line in StepLines(entry.Steps, map, 1)) yield return line;
            if (entry.Steps.Count == 0) yield return "  (inga anrop vidare)";
        }

        if (map.Differences.Count > 0)
        {
            yield return "";
            yield return "Skillnader mellan koden och specen:";
            foreach (var d in map.Differences)
                yield return d.Kind == DifferenceKind.OnlyInCode
                    ? $"  Bara i koden: {d.Method} {d.Path} ({d.Handler})"
                    : $"  Bara i specen: {d.Method} {d.Path}{(d.OperationId is null ? "" : $" ({d.OperationId})")}";
        }

        foreach (var line in UnreachedLines(overview)) yield return line;
    }

    /// <summary>
    /// Det som ingen ingång når: per projekt klasserna, och metoderna i klasser som nås. Kort: högst <see cref="MaxListed"/> av varje, resten räknas.
    /// </summary>
    private static IEnumerable<string> UnreachedLines(Overview overview)
    {
        var unreached = CodeMapOverview.Unreached(overview);
        if (unreached.Count == 0) yield break;
        var byId = overview.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        yield return "";
        yield return "Nås inte från någon ingång:";
        foreach (var project in overview.Nodes.Where(n => n.Kind == OverviewKind.Project))
        {
            var classes = unreached.Where(n => n.Kind == OverviewKind.Class && n.Parent == project.Id).Select(n => n.Name).ToList();
            var methods = unreached.Where(n => n.Kind == OverviewKind.Method && byId[n.Parent!].Parent == project.Id && byId[n.Parent!].Reached)
                .Select(n => $"{byId[n.Parent!].Name}.{n.Name}").ToList();
            if (project.Reached && classes.Count == 0 && methods.Count == 0) continue;
            var parts = new List<string>();
            if (classes.Count > 0) parts.Add(Listed(classes));
            if (methods.Count > 0) parts.Add("metoder i klasser som nås: " + Listed(methods));
            yield return $"  {project.Name}{(project.Reached ? "" : " (hela projektet)")}{(parts.Count == 0 ? "" : ": " + string.Join("; ", parts))}";
        }
    }

    private const int MaxListed = 8;

    private static string Listed(List<string> names) =>
        names.Count <= MaxListed ? string.Join(", ", names) : $"{string.Join(", ", names.Take(MaxListed))} och {names.Count - MaxListed} till";

    /// <summary>"ShopWeb: web, anropar ShopApi via ShopApi:BaseUrl".</summary>
    private static string AppLine(AppFacts app)
    {
        var parts = new List<string> { string.Join(" och ", app.Kinds.Select(CodeMapJson.KindCode)) };
        if (app.HealthPaths.Count > 0) parts.Add("hälsa " + string.Join(", ", app.HealthPaths));
        parts.AddRange(app.Databases.Select(d => $"databas {d.Provider ?? "okänd"} ({d.Context}{(d.ConfigKey is null ? "" : $", {d.ConfigKey}")})"));
        parts.AddRange(app.HttpClients.Select(c => $"{(c.TargetProject is null ? $"anropar {c.IntegrationId["http:".Length..]}" : $"anropar {c.TargetProject}")}{(c.ConfigKey is null ? "" : $" via {c.ConfigKey}")}"));
        return $"{app.Project}: {string.Join("; ", parts.Where(p => p.Length > 0))}";
    }

    /// <summary>"Betaltjanst (HTTP, adressen i Betaltjanst:BaseUrl)".</summary>
    public static string Label(Integration integration) =>
        integration.Detail.Length == 0 ? integration.Name : $"{integration.Name} ({integration.Detail})";

    private static IEnumerable<string> StepLines(IEnumerable<Step> steps, CodeMapModel map, int depth)
    {
        var indent = new string(' ', depth * 2);
        foreach (var step in steps)
        {
            if (step.Integration is { } use)
            {
                var integration = map.Integrations.FirstOrDefault(i => i.Id == use.Id);
                var data = (step.In, step.Out) switch
                {
                    ({ } sent, { } received) => $"  skickar {sent.Type}, får {received.Type}",
                    ({ } sent, null) => $"  skickar {sent.Type}",
                    (null, { } received) => $"  får {received.Type}",
                    _ => "",
                };
                yield return $"{indent}→ {(integration is null ? use.Id : Label(integration))}: {use.Operation}{data}";
                continue;
            }
            yield return $"{indent}{step.Member} [{step.Project}]";
            foreach (var line in StepLines(step.Steps, map, depth + 1)) yield return line;
        }
    }

    private static string? Shape(DataShape? shape) =>
        shape is null ? null : shape.Fields.Count == 0 ? shape.Type : $"{shape.Type} {{{string.Join(", ", shape.Fields)}}}";
}
