using System.Text;
using Microsoft.OpenApi;

namespace TestGen.Core;

/// <summary>Jämför den incheckade specen med den som appen serverar (code-first): operationer, parametrar, bodys form och svarskoder.</summary>
public static class SpecComparison
{
    private const int MaxLines = 20;
    private const int MaxDepth = 6;

    /// <summary>Skillnaderna, en per rad, högst <see cref="MaxLines"/> (sist en rad om fler). Tom lista om specarna stämmer.</summary>
    public static IReadOnlyList<string> Diff(ApiSpec checkedIn, ApiSpec served)
    {
        var lines = new List<string>();
        var repo = checkedIn.Operations.ToDictionary(Key);
        var app = served.Operations.ToDictionary(Key);

        foreach (var key in repo.Keys.Where(k => !app.ContainsKey(k)).Order(StringComparer.Ordinal))
            lines.Add($"{key}: finns i specen i repot men inte i appen.");
        foreach (var key in app.Keys.Where(k => !repo.ContainsKey(k)).Order(StringComparer.Ordinal))
            lines.Add($"{key}: finns i appen men inte i specen i repot.");

        foreach (var key in repo.Keys.Where(app.ContainsKey).Order(StringComparer.Ordinal))
        {
            var (r, a) = (repo[key], app[key]);
            var repoParams = Parameters(r);
            var appParams = Parameters(a);
            if (!repoParams.SequenceEqual(appParams))
                lines.Add($"{key}: parametrarna skiljer sig (repo: {Join(repoParams)}; app: {Join(appParams)}).");
            var (repoBody, appBody) = (Shape(r.BodySchema, 0), Shape(a.BodySchema, 0));
            if (repoBody != appBody)
                lines.Add($"{key}: bodyn skiljer sig (repo: {repoBody}; app: {appBody}).");
            var repoCodes = (r.ResponseCodes ?? []).Order(StringComparer.Ordinal).ToList();
            var appCodes = (a.ResponseCodes ?? []).Order(StringComparer.Ordinal).ToList();
            if (!repoCodes.SequenceEqual(appCodes))
                lines.Add($"{key}: svarskoderna skiljer sig (repo: {Join(repoCodes)}; app: {Join(appCodes)}).");
            var repoResponses = Responses(r);
            var appResponses = Responses(a);
            foreach (var code in repoResponses.Keys.Where(appResponses.ContainsKey).Order())
                if (repoResponses[code] != appResponses[code])
                    lines.Add($"{key}: svaret {code} skiljer sig (repo: {repoResponses[code]}; app: {appResponses[code]}).");
        }

        if (lines.Count <= MaxLines) return lines;
        var more = lines.Count - MaxLines;
        return [.. lines.Take(MaxLines), $"… och {more} till."];
    }

    private static string Key(OperationInfo op) => $"{op.Method} {op.Path}";

    private static string Join(IEnumerable<string> items) => items.Any() ? string.Join(", ", items) : "inga";

    private static List<string> Parameters(OperationInfo op) =>
        op.Parameters.Select(p => $"{p.Name} ({p.In}{(p.Required ? ", krävs" : "")}, {Shape(p.Schema, MaxDepth - 1)})").Order(StringComparer.Ordinal).ToList();

    private static SortedDictionary<int, string> Responses(OperationInfo op) =>
        new((op.ResponseSchemas ?? new Dictionary<int, IOpenApiSchema>()).ToDictionary(kv => kv.Key, kv => Shape(kv.Value, 0)));

    /// <summary>Schemats form som text: <c>{id:string, name:string!}</c> där ! betyder att fältet krävs. Namn, typer och krav jämförs, inte beskrivningar.</summary>
    private static string Shape(IOpenApiSchema? schema, int depth)
    {
        if (schema is null) return "ingen";
        var type = schema.TypeName() ?? (schema.Props().Count > 0 ? "object" : schema.Items is not null ? "array" : "any");
        var format = schema.Format is { Length: > 0 } f ? $"({f})" : "";
        if (schema.EnumValues().Count > 0) format += "[" + string.Join("|", schema.EnumValues().Select(e => e.ToString()).Order(StringComparer.Ordinal)) + "]";
        if (type == "array") return $"[{(depth >= MaxDepth ? "…" : Shape(schema.Items, depth + 1))}]";
        if (type != "object" || schema.Props().Count == 0) return type + format;
        if (depth >= MaxDepth) return "{…}";
        var sb = new StringBuilder("{");
        sb.AppendJoin(", ", schema.Props().OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => $"{p.Key}:{Shape(p.Value, depth + 1)}{(schema.RequiredNames().Contains(p.Key) ? "!" : "")}"));
        return sb.Append('}').ToString();
    }
}
