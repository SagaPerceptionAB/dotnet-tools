using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;
using Microsoft.OpenApi.YamlReader;
using TestGen.Core.Run;

namespace TestGen.Core;

public sealed record ParamInfo(string Name, string In, bool Required, IOpenApiSchema Schema);

public sealed record OperationInfo(
    string OperationId,
    string Method,
    string Path,
    IReadOnlyList<ParamInfo> Parameters,
    IOpenApiSchema? BodySchema,
    IReadOnlyList<int> SuccessStatuses,
    /// <summary>
    /// Alternativ för inloggning (namn på securityScheman; alla i ett alternativ krävs tillsammans).
    /// Null = ingen inloggning krävs (eller den är valfri, dvs. specen tillåter även tomt alternativ).
    /// </summary>
    IReadOnlyList<IReadOnlyList<string>>? AuthAlternatives = null,
    /// <summary>Svarsschema (application/json) per statuskod ur specen (2xx, men även t.ex. 400/404/500 om specen beskriver innehållet). Saknas en kod finns inget schema att kontrollera mot.</summary>
    IReadOnlyDictionary<int, IOpenApiSchema>? ResponseSchemas = null,
    /// <summary>Statuskoder som specen anger under responses (t.ex. "201", "400", "4xx"; "default" utelämnas), i spec-ordning.</summary>
    IReadOnlyList<string>? ResponseCodes = null,
    /// <summary>Operationens <c>summary</c> i specen (kort beskrivning), annars null.</summary>
    string? Summary = null,
    /// <summary>Mediatyp för bodyn: "application/json" (eller annan json-typ) eller "application/x-www-form-urlencoded".</summary>
    string BodyContentType = "application/json",
    /// <summary>Mediatypen om bodyn är av en typ testgen inte kan skicka än (t.ex. "multipart/form-data"), annars null.</summary>
    string? UnsupportedBody = null);

/// <summary>Ett securityScheme ur specen. Kind: "bearer", "apiKey" (i header) eller "other" (stöds ej av --token).</summary>
public sealed record SecuritySchemeInfo(string Name, string Kind, string? HeaderName);

public sealed record ApiSpec(IReadOnlyList<OperationInfo> Operations, IReadOnlyList<SecuritySchemeInfo> Schemes, string? Title = null);

public static class OpenApiLoader
{
    public static List<OperationInfo> Load(string specPathOrUrl) => LoadSpec(specPathOrUrl).Operations.ToList();

    public static ApiSpec LoadSpec(string specPathOrUrl)
    {
        var text = FriendlyErrors.Guard(SourceKind.Spec, specPathOrUrl, () => Sources.ReadText(specPathOrUrl));
        var (doc, diag) = FriendlyErrors.Guard(SourceKind.Spec, specPathOrUrl, () => Parse(text));
        if (doc is null || diag is null || diag.Errors.Count > 0)
        {
            var msgs = string.Join("; ", diag?.Errors.Select(e => e.ToString()) ?? []);
            var shown = Sources.IsUrl(specPathOrUrl) ? specPathOrUrl : Path.GetFullPath(specPathOrUrl);
            var line = System.Text.RegularExpressions.Regex.Match(msgs, @"Lin: (\d+)|LineNumber: (\d+)");
            var reason = line.Success
                ? $"texten är trasig på rad {int.Parse(line.Groups[line.Groups[1].Success ? 1 : 2].Value) + 1} (inte giltig YAML eller JSON)."
                : $"innehållet följer inte OpenAPI-formatet ({diag?.Errors.Count ?? 0} fel).";
            throw new SourceException(SourceKind.Spec, specPathOrUrl,
                $"Kunde inte läsa OpenAPI-spec '{shown}': {reason}",
                "Kontrollera specen i ett OpenAPI-verktyg, t.ex. editor.swagger.io. Kör igen med --verbose för felens exakta text.", msgs);
        }

        var schemes = (doc.Components?.SecuritySchemes ?? new Dictionary<string, IOpenApiSecurityScheme>())
            .Select(kv => ToSchemeInfo(kv.Key, kv.Value)).ToList();

        var ops = new List<OperationInfo>();
        foreach (var (path, item) in doc.Paths)
        {
            foreach (var (type, op) in item.Operations ?? [])
            {
                var method = type.ToString().ToUpperInvariant();
                var id = string.IsNullOrWhiteSpace(op.OperationId) ? Synthesize(method, path) : op.OperationId;

                var parameters = (item.Parameters ?? []).Concat(op.Parameters ?? [])
                    .GroupBy(p => (p.In?.ToString() ?? "", p.Name)).Select(g => g.Last())
                    .Select(p => new ParamInfo(p.Name ?? "", (p.In?.ToString() ?? "query").ToLowerInvariant(),
                        p.Required || p.In == ParameterLocation.Path, p.Schema ?? new OpenApiSchema { Type = JsonSchemaType.String }))
                    .ToList();

                IOpenApiSchema? body = null;
                var bodyType = "application/json";
                string? unsupported = null;
                if (op.RequestBody?.Content is { Count: > 0 } content)
                {
                    if (content.TryGetValue("application/json", out var mt)) body = mt.Schema;
                    else if (content.FirstOrDefault(kv => kv.Key.Contains("json", StringComparison.OrdinalIgnoreCase)) is { Key: not null } j)
                    {
                        body = j.Value.Schema;
                        bodyType = j.Key;
                    }
                    else if (content.TryGetValue("application/x-www-form-urlencoded", out var form))
                    {
                        body = form.Schema;
                        bodyType = "application/x-www-form-urlencoded";
                    }
                    else unsupported = content.Keys.First();
                }

                var responses = op.Responses ?? [];
                var success = responses.Keys
                    .Select(k => int.TryParse(k, out var n) ? n : -1)
                    .Where(n => n is >= 200 and < 300).OrderBy(n => n).ToList();
                if (success.Count == 0) success.Add(200);

                var responseSchemas = new Dictionary<int, IOpenApiSchema>();
                foreach (var (code, response) in responses)
                {
                    if (!int.TryParse(code, out var n) || n is < 100 or > 599 || response.Content is not { Count: > 0 } rc) continue;
                    var media = rc.TryGetValue("application/json", out var json) ? json : rc.FirstOrDefault(kv => kv.Key.Contains("json")).Value;
                    if (media?.Schema is { } rs) responseSchemas[n] = rs;
                }

                ops.Add(new OperationInfo(id, method, path, parameters, body, success,
                    AuthOf(op.Security is { Count: > 0 } ? op.Security : doc.Security), responseSchemas,
                    responses.Keys.Where(k => !string.Equals(k, "default", StringComparison.OrdinalIgnoreCase))
                        .Select(k => k.Trim().ToLowerInvariant()).Distinct().ToList(),
                    string.IsNullOrWhiteSpace(op.Summary) ? null : op.Summary.Trim(), bodyType, unsupported));
            }
        }
        return new ApiSpec(ops.OrderBy(o => o.OperationId, StringComparer.Ordinal).ToList(), schemes,
            string.IsNullOrWhiteSpace(doc.Info?.Title) ? null : doc.Info.Title.Trim());
    }

    private static (OpenApiDocument? Doc, OpenApiDiagnostic? Diagnostic) Parse(string text)
    {
        var settings = new OpenApiReaderSettings();
        settings.AddYamlReader();
        try
        {
            var result = OpenApiDocument.Parse(text, text.TrimStart().StartsWith('{') ? "json" : "yaml", settings);
            return (result.Document, result.Diagnostic);
        }
        catch (SharpYaml.YamlException e)
        {
            return (null, new OpenApiDiagnostic { Errors = [new OpenApiError("", e.Message)] });
        }
    }

    private static SecuritySchemeInfo ToSchemeInfo(string name, IOpenApiSecurityScheme s) => s.Type switch
    {
        SecuritySchemeType.Http when string.Equals(s.Scheme, "bearer", StringComparison.OrdinalIgnoreCase) => new(name, "bearer", "Authorization"),
        SecuritySchemeType.OAuth2 or SecuritySchemeType.OpenIdConnect => new(name, "bearer", "Authorization"),
        SecuritySchemeType.ApiKey when s.In == ParameterLocation.Header && !string.IsNullOrEmpty(s.Name) => new(name, "apiKey", s.Name),
        _ => new(name, "other", null),
    };

    /// <summary>Null om inget krav finns eller om något alternativ är tomt ({}), dvs. inloggning är valfri.</summary>
    private static IReadOnlyList<IReadOnlyList<string>>? AuthOf(IList<OpenApiSecurityRequirement>? reqs)
    {
        if (reqs is null || reqs.Count == 0 || reqs.Any(r => r.Count == 0)) return null;
        return reqs.Select(r => (IReadOnlyList<string>)r.Keys.Select(k => k.Reference?.Id ?? "").ToList()).ToList();
    }

    private static string Synthesize(string method, string path) =>
        method.ToLowerInvariant() + "_" + string.Concat(path.Select(c => char.IsLetterOrDigit(c) ? c : '_')).Trim('_');
}
