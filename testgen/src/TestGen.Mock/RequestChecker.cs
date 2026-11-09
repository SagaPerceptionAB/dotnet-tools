using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.OpenApi;
using TestGen.Core;
using TestGen.Core.Run;

namespace TestGen.Mock;

/// <summary>En skillnad mellan anropet och specen. <see cref="Field"/> är tom när felet gäller hela anropet.</summary>
internal sealed record MockProblem(string Field, string Message)
{
    public override string ToString() => Field.Length == 0 ? Message : $"{Field}: {Message}";
}

/// <summary>Kontrollerar ett anrop mot specen: parametrar (sökväg, fråga, header) och kroppen. Reglerna kommer från <see cref="ResponseSchemaCheck"/>.</summary>
internal static class RequestChecker
{
    private const string BodyRoot = "kropp";

    public static List<MockProblem> Check(OperationInfo op, IReadOnlyDictionary<string, string> pathValues, HttpRequest request, string? body)
    {
        var problems = new List<MockProblem>();
        foreach (var p in op.Parameters)
        {
            var values = p.In switch
            {
                "path" => pathValues.TryGetValue(p.Name, out var v) ? [v] : [],
                "query" => request.Query.TryGetValue(p.Name, out var q) ? q.OfType<string>().ToArray() : [],
                "header" => request.Headers.TryGetValue(p.Name, out var h) ? h.OfType<string>().ToArray() : [],
                _ => null,   // cookies kontrolleras inte
            };
            if (values is null) continue;
            if (values.Length == 0)
            {
                if (p.Required) problems.Add(new(p.Name, "saknas (obligatoriskt enligt specen)"));
                continue;
            }
            Add(problems, ResponseSchemaCheck.Check(Coerce(values, p.Schema), p.Schema, p.Name, request: true), p.Name);
        }

        if (op.BodySchema is { } schema && op.UnsupportedBody is null) CheckBody(op, schema, body, problems);
        return problems;
    }

    /// <summary>Texten (eller texterna) som ett värde av schemats typ: "5" blir ett heltal om schemat säger heltal, annars förblir det text.</summary>
    internal static JsonNode Coerce(string[] values, IOpenApiSchema schema)
    {
        if (SchemaUtil.TypeOf(schema) == "array")
        {
            var items = values.Length == 1 ? values[0].Split(',') : values;
            return new JsonArray(items.Select(i => Coerce([i], schema.Items ?? new OpenApiSchema { Type = JsonSchemaType.String })).ToArray());
        }
        return CoerceOne(values[0], SchemaUtil.TypeOf(schema));
    }

    private static JsonNode CoerceOne(string text, string? type) => type switch
    {
        "integer" when long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) => JsonValue.Create(n),
        "number" when decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => JsonValue.Create(d),
        "boolean" when bool.TryParse(text, out var b) => JsonValue.Create(b),
        _ => JsonValue.Create(text),
    };

    private static void CheckBody(OperationInfo op, IOpenApiSchema schema, string? body, List<MockProblem> problems)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            if (SchemaUtil.Flatten(schema).Required.Count > 0) problems.Add(new(BodyRoot, "saknas (obligatoriskt enligt specen)"));
            return;
        }

        JsonNode? node;
        if (op.BodyContentType == "application/x-www-form-urlencoded")
        {
            var (props, _) = SchemaUtil.Flatten(schema);
            var form = new JsonObject();
            foreach (var (key, value) in QueryHelpers.ParseQuery(body))
                form[key] = CoerceOne(value.ToString(), props.TryGetValue(key, out var ps) ? SchemaUtil.TypeOf(ps) : null);
            node = form;
        }
        else
        {
            try { node = JsonNode.Parse(body); }
            catch (JsonException)
            {
                problems.Add(new(BodyRoot, "är inte giltig JSON"));
                return;
            }
            if (node is null)
            {
                problems.Add(new(BodyRoot, "saknas (obligatoriskt enligt specen)"));
                return;
            }
        }
        Add(problems, ResponseSchemaCheck.Check(node, schema, BodyRoot, request: true), BodyRoot);
    }

    /// <summary>"kropp.name saknas (...)" blir fältet "name" och texten "saknas (...)".</summary>
    private static void Add(List<MockProblem> problems, SchemaCheckResult result, string root)
    {
        foreach (var line in result.Errors)
        {
            if (!line.StartsWith(root, StringComparison.Ordinal))
            {
                problems.Add(new("", line));
                continue;
            }
            var end = line.AsSpan(root.Length).IndexOfAny(' ', ':');
            var field = end < 0 ? line : line[..(root.Length + end)];
            var message = end < 0 ? "" : line[(root.Length + end)..].TrimStart(':', ' ');
            if (field.Length > root.Length && field[root.Length] == '.') field = field[(root.Length + 1)..];
            problems.Add(new(field, message));
        }
    }
}
