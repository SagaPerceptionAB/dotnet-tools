using System.Text.Encodings.Web;
using System.Text.Json;

namespace SystemMap;

/// <summary>Kartan som JSON (samma form i <c>--json karta.json</c> och i webbsidan).</summary>
public static class CodeMapJson
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Serialize(CodeMapModel map) => JsonSerializer.Serialize(ToDto(map), Options) + "\n";

    /// <param name="overview">Översikten om den redan är byggd, annars byggs den här.</param>
    public static object ToDto(CodeMapModel map, Overview? overview = null) => new
    {
        schemaVersion = CodeMapModel.SchemaVersion,
        solution = map.Solution,
        projects = map.Projects,
        integrations = map.Integrations.Select(i => new { id = i.Id, kind = KindCode(i.Kind), name = i.Name, detail = i.Detail }).ToList(),
        entryPoints = map.EntryPoints.Select(e => new
        {
            method = e.Method, path = e.Path, codePath = e.CodePath, operationId = e.OperationId, project = e.Project,
            handler = e.Handler, file = e.File, line = e.Line, input = Shape(e.Input), output = Shape(e.Output),
            steps = e.Steps.Select(Step).ToList(),
        }).ToList(),
        differences = map.Differences.Select(d => new
        {
            kind = d.Kind == DifferenceKind.OnlyInCode ? "only-in-code" : "only-in-spec",
            method = d.Method, path = d.Path, operationId = d.OperationId, handler = d.Handler,
        }).ToList(),
        warnings = map.Warnings,
        overview = Overview(overview ?? CodeMapOverview.Build(map)),
    };

    private static object Overview(Overview overview) => new
    {
        nodes = overview.Nodes.Select(n => new
        {
            id = n.Id, kind = KindCode(n.Kind), name = n.Name, parent = n.Parent, @ref = n.Ref, file = n.File, line = n.Line,
            @in = Shape(n.In), @out = Shape(n.Out), entries = n.Entries,
        }).ToList(),
        links = overview.Links.Select(l => new { from = l.From, to = l.To, file = l.File, line = l.Line, entries = l.Entries }).ToList(),
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
        project = s.Project, member = s.Member, file = s.File, line = s.Line, @in = Shape(s.In), @out = Shape(s.Out),
        integration = s.Integration is { } i ? new { id = i.Id, operation = i.Operation } : null,
        steps = s.Steps.Select(Step).ToList(),
    };

    private static object? Shape(DataShape? shape) => shape is null ? null : new { type = shape.Type, fields = shape.Fields };
}
