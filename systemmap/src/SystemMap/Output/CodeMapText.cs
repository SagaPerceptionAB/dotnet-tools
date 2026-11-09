namespace SystemMap;

/// <summary>Kartan som text i terminalen: ett träd per ingång.</summary>
public static class CodeMapText
{
    public static IEnumerable<string> Lines(CodeMapModel map)
    {
        yield return $"Systemkarta för {map.Solution}";
        yield return "Kartan visar vägar som är möjliga enligt koden, inte vilken väg ett visst anrop tog.";
        yield return map.Projects.Count > 0 ? $"Projekt i den ordning anropen når dem: {string.Join(" → ", map.Projects)}" : "Hittade inga ingångar.";
        if (map.Integrations.Count > 0)
        {
            // Varje integration en gång, med det som görs mot den: läsningar och skrivningar i en databas, meddelandena till en kö.
            var overview = CodeMapOverview.Build(map);
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
            yield return $"{entry.Method} {entry.Path}{(entry.OperationId is null ? "" : $"  ({entry.OperationId})")}  {entry.Handler} [{entry.Project}]{(entry.Kind == EntryKind.Controller ? "" : $" {CodeMapJson.KindCode(entry.Kind)}")}";
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
    }

    /// <summary>"ShopWeb: web, anropar ShopApi via ShopApi:BaseUrl".</summary>
    private static string AppLine(AppFacts app)
    {
        var parts = new List<string> { string.Join(" och ", app.Kinds.Select(k => k == AppKind.Api ? "api" : "web")) };
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
