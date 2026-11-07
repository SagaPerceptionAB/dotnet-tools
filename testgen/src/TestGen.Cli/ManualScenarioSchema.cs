using System.Text.Json;
using System.Text.Json.Nodes;

namespace TestGen.Cli;

/// <summary>JSON Schema för manuella YAML-scenarier (editorstöd, t.ex. yaml-language-server).</summary>
public static class ManualScenarioSchema
{
    public static string Build(IEnumerable<string>? operationIds)
    {
        var call = new JsonObject
        {
            ["type"] = "string",
            ["description"] = "operationId från OpenAPI-specen.",
        };
        var ids = operationIds?.OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (ids is { Count: > 0 }) call["enum"] = new JsonArray(ids.Select(i => (JsonNode)JsonValue.Create(i)!).ToArray());

        var stringMap = new JsonObject { ["type"] = "object", ["additionalProperties"] = new JsonObject() };

        var step = new JsonObject
        {
            ["type"] = "object",
            ["required"] = new JsonArray("call"),
            ["additionalProperties"] = false,
            ["properties"] = new JsonObject
            {
                ["call"] = call,
                ["description"] = new JsonObject { ["type"] = "string" },
                ["pathParams"] = Desc(stringMap, "Värden för {param} i path; stöder ${var} från tidigare save."),
                ["query"] = Desc(stringMap, "Query-parametrar."),
                ["headers"] = Desc(stringMap, "HTTP-headers."),
                ["body"] = new JsonObject
                {
                    ["description"] = "Request-body. Utelämnade fält fylls från genererad giltig data. Stöder ${var}.",
                },
                ["save"] = new JsonObject
                {
                    ["type"] = "object",
                    ["description"] = "Spara värden ur svaret: variabelnamn -> JSON-path (t.ex. $.id). Används som ${namn}.",
                    ["additionalProperties"] = new JsonObject { ["type"] = "string" },
                },
                ["expect"] = new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["properties"] = new JsonObject
                    {
                        ["status"] = new JsonObject
                        {
                            ["description"] = "Förväntad HTTP-status (ett tal eller en lista).",
                            ["oneOf"] = new JsonArray(
                                new JsonObject { ["type"] = "integer", ["minimum"] = 100, ["maximum"] = 599 },
                                new JsonObject
                                {
                                    ["type"] = "array", ["minItems"] = 1,
                                    ["items"] = new JsonObject { ["type"] = "integer", ["minimum"] = 100, ["maximum"] = 599 },
                                }),
                        },
                        ["body"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["description"] = "Förväntade fält i svaret (delmatchning).",
                        },
                    },
                },
            },
        };

        var root = new JsonObject
        {
            ["$schema"] = "http://json-schema.org/draft-07/schema#",
            ["title"] = "testgen manuellt scenario",
            ["type"] = "object",
            ["required"] = new JsonArray("name", "steps"),
            ["additionalProperties"] = false,
            ["properties"] = new JsonObject
            {
                ["name"] = new JsonObject { ["type"] = "string" },
                ["description"] = new JsonObject { ["type"] = "string" },
                ["steps"] = new JsonObject { ["type"] = "array", ["minItems"] = 1, ["items"] = step },
            },
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    private static JsonObject Desc(JsonObject basis, string d)
    {
        var c = (JsonObject)basis.DeepClone();
        c["description"] = d;
        return c;
    }
}
