using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SystemMap;

/// <summary>
/// Kartan som JSON (samma form i <c>--json karta.json</c> och i webbsidan). schemaVersion först, camelCase, null utelämnas och
/// listorna sorterade på stabilt id (ordinalt). <c>projects</c> och <c>steps</c> behåller sin ordning, eftersom den är anropens.
/// En ingång som inte är HTTP har <c>name</c> (t.ex. <c>Bakgrund: OrderCleanupWorker</c>) men ingen <c>method</c>, <c>path</c> eller <c>codePath</c>.
/// En del i översikten som ingen ingång når har <c>reached: false</c>; saknas fältet nås den.
/// </summary>
public static class CodeMapJson
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <param name="overview">Översikten om den redan är byggd (och i så fall redan utan de dolda delarna), annars byggs den här.</param>
    /// <param name="hidden">Id:n på delarna som är dolda i kartan (se <see cref="CodeMapOverview.Hide(CodeMapModel, IReadOnlySet{string})"/>), så att den som läser
    /// vet att kartan är ofullständig. Utelämnas när inget är dolt.</param>
    public static string Serialize(CodeMapModel map, Overview? overview = null, IReadOnlySet<string>? hidden = null) =>
        JsonSerializer.Serialize(ToDto(map, overview, hidden), Options) + "\n";

    /// <param name="overview">Översikten om den redan är byggd, annars byggs den här.</param>
    /// <param name="hidden">Se <see cref="Serialize"/>.</param>
    public static object ToDto(CodeMapModel map, Overview? overview = null, IReadOnlySet<string>? hidden = null) => new
    {
        schemaVersion = CodeMapModel.SchemaVersion,
        solution = Path.GetFileName(map.Solution.Replace('\\', '/')),   // bara filnamnet: kartan innehåller inga sökvägar från den här datorn
        projects = map.Projects,
        integrations = map.Integrations.OrderBy(i => i.Id, StringComparer.Ordinal).Select(i => new { id = i.Id, kind = KindCode(i.Kind), name = i.Name, detail = i.Detail, targetProject = i.TargetProject }).ToList(),
        apps = map.Apps.OrderBy(a => a.Project, StringComparer.Ordinal).Select(a => new
        {
            project = a.Project,
            kinds = a.Kinds.Select(KindCode).ToList(),
            healthPaths = a.HealthPaths,
            databases = a.Databases.Select(d => new { provider = d.Provider, context = d.Context, configKey = d.ConfigKey }).ToList(),
            httpClients = a.HttpClients.Select(c => new { client = c.Client, configKey = c.ConfigKey, integrationId = c.IntegrationId, targetProject = c.TargetProject }).ToList(),
        }).ToList(),
        entryPoints = map.EntryPoints.OrderBy(e => e.Kind.IsHttp() ? 0 : 1).ThenBy(e => e.Path, StringComparer.Ordinal).ThenBy(e => e.Method, StringComparer.Ordinal)
            .ThenBy(e => e.Name, StringComparer.Ordinal).ThenBy(e => e.Handler, StringComparer.Ordinal).Select(e => new
            {
                kind = KindCode(e.Kind),
                name = e.Kind.IsHttp() ? null : e.Name,
                method = e.Kind.IsHttp() ? e.Method : null,
                path = e.Kind.IsHttp() ? e.Path : null,
                codePath = e.Kind.IsHttp() ? e.CodePath : null,
                operationId = e.OperationId,
                project = e.Project,
                handler = e.Handler,
                file = e.File,
                line = e.Line,
                input = Shape(e.Input),
                output = Shape(e.Output),
                steps = e.Steps.Select(Step).ToList(),
            }).ToList(),
        differences = map.Differences.OrderBy(d => d.Path, StringComparer.Ordinal).ThenBy(d => d.Method, StringComparer.Ordinal).ThenBy(d => d.Kind).Select(d => new
        {
            kind = d.Kind == DifferenceKind.OnlyInCode ? "only-in-code" : "only-in-spec",
            method = d.Method,
            path = d.Path,
            operationId = d.OperationId,
            handler = d.Handler,
        }).ToList(),
        warnings = map.Warnings.Order(StringComparer.Ordinal).ToList(),
        hidden = hidden is { Count: > 0 } ? hidden.Order(StringComparer.Ordinal).ToList() : null,
        overview = Overview(overview ?? CodeMapOverview.Build(map)),
    };

    private static object Overview(Overview overview) => new
    {
        nodes = overview.Nodes.OrderBy(n => n.Id, StringComparer.Ordinal).Select(n => new
        {
            id = n.Id,
            kind = KindCode(n.Kind),
            name = n.Name,
            parent = n.Parent,
            @ref = n.Ref,
            file = n.File,
            line = n.Line,
            @in = Shape(n.In),
            @out = Shape(n.Out),
            entries = n.Entries,
            reached = n.Reached ? (bool?)null : false,
        }).ToList(),
        links = overview.Links.OrderBy(l => l.From, StringComparer.Ordinal).ThenBy(l => l.To, StringComparer.Ordinal).Select(l => new { from = l.From, to = l.To, file = l.File, line = l.Line, entries = l.Entries }).ToList(),
    };

    public static string KindCode(OverviewKind kind) => kind switch
    {
        OverviewKind.Entry => "entry",
        OverviewKind.Project => "project",
        OverviewKind.Class => "class",
        OverviewKind.Method => "method",
        OverviewKind.Integration => "integration",
        _ => "operation",
    };

    public static string KindCode(EntryKind kind) => kind switch
    {
        EntryKind.Controller => "controller",
        EntryKind.MinimalApi => "minimal-api",
        EntryKind.RazorPage => "razor-page",
        EntryKind.BlazorPage => "blazor-page",
        EntryKind.MvcView => "mvc-view",
        EntryKind.Background => "background",
        EntryKind.Console => "console",
        EntryKind.Consumer => "consumer",
        EntryKind.Job => "job",
        EntryKind.Grpc => "grpc",
        _ => "signalr",
    };

    public static string KindCode(AppKind kind) => kind switch
    {
        AppKind.Api => "api",
        AppKind.Web => "web",
        AppKind.Worker => "worker",
        _ => "console",
    };

    public static string KindCode(IntegrationKind kind) => kind switch
    {
        IntegrationKind.Database => "database",
        IntegrationKind.Http => "http",
        IntegrationKind.Wcf => "wcf",
        IntegrationKind.Queue => "queue",
        IntegrationKind.Storage => "storage",
        _ => "email",
    };

    private static object Step(Step s) => new
    {
        project = s.Project,
        member = s.Member,
        file = s.File,
        line = s.Line,
        @in = Shape(s.In),
        @out = Shape(s.Out),
        integration = s.Integration is { } i ? new { id = i.Id, operation = i.Operation } : null,
        steps = s.Steps.Select(Step).ToList(),
    };

    private static object? Shape(DataShape? shape) => shape is null ? null : new { type = shape.Type, fields = shape.Fields };
}
