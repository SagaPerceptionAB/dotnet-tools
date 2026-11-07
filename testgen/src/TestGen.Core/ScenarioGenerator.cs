using System.Text.Json.Nodes;
using Microsoft.OpenApi.Models;
using TestGen.Scenarios;

namespace TestGen.Core;

public sealed class ScenarioGenerator
{
    private readonly TestGenConfig _config;
    private readonly DependencyResolver _deps;
    private readonly FieldRules _rules;

    public ScenarioGenerator(IEnumerable<OperationInfo> ops, TestGenConfig config)
    {
        _config = config;
        _deps = new DependencyResolver(ops, config);
        _rules = new FieldRules(config.FieldRules);
    }

    /// <summary>
    /// Med <paramref name="salt"/> blandas en räknare in i seeden (se <see cref="Fnv"/>). Med
    /// <paramref name="includeNegatives"/> false returneras bara det giltiga fallet (samma data som annars).
    /// </summary>
    public List<Scenario> Generate(OperationInfo op, int? salt = null, bool includeNegatives = true) =>
        GenerateCases(op, salt, includeNegatives).Select(c => c.Scenario).ToList();

    /// <summary>Som <see cref="Generate"/>, men varje scenario följs av strukturerad metadata (regel, fält, gränsvärde, skickat värde).</summary>
    public List<GeneratedCase> GenerateCases(OperationInfo op, int? salt = null, bool includeNegatives = true)
    {
        var seed = salt is { } s ? Fnv.OperationSeed(op.OperationId, _config.Seed, s) : Fnv.OperationSeed(op.OperationId, _config.Seed);
        var gen = new SchemaDataGenerator(seed, _config.Locale, _rules);
        var deps = _deps.Resolve(op.OperationId);
        _config.Overrides.TryGetValue(op.OperationId, out var overrides);
        JsonNode? Fixed(string field) =>
            deps.Bindings.TryGetValue(field, out var b) ? JsonValue.Create(b)
            : overrides is not null && overrides.TryGetValue(field, out var o) ? ToNode(o) : null;

        var bodyProps = op.BodySchema is null ? null : SchemaUtil.Flatten(op.BodySchema).Props;
        if (salt is null)
            foreach (var f in deps.Bindings.Keys)
            {
                var known = op.Parameters.Any(p => p.Name == f) || bodyProps?.ContainsKey(f) == true;
                if (!known) Console.Error.WriteLine($"Varning: binding {op.OperationId}.{f} matchar inget fält.");
            }

        // Path/query/header
        var path = op.Path;
        var query = new Dictionary<string, string>();
        var headers = new Dictionary<string, string>();
        foreach (var p in op.Parameters.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            if (!p.Required) continue;
            var text = ResponseStore.ToText(gen.GenerateField(p.Name, p.Schema, Fixed(p.Name)));
            switch (p.In)
            {
                case "path": path = path.Replace("{" + p.Name + "}", text); break;
                case "query": query[p.Name] = text; break;
                case "header": headers[p.Name] = text; break;
            }
        }

        // Body (objekt, lista eller enkelt värde; readOnly-fält skickas aldrig)
        JsonNode? body = null;
        var effective = op.BodySchema is null || op.UnsupportedBody is not null ? null : SchemaUtil.Effective(op.BodySchema);
        if (effective is not null)
        {
            if (SchemaUtil.IsObject(effective))
            {
                var obj = new JsonObject();
                foreach (var (name, schema) in bodyProps!)
                    if (!schema.ReadOnly) obj[name] = gen.GenerateField(name, schema, Fixed(name));
                body = obj;
            }
            else body = gen.GenerateField("", effective);
        }
        var contentType = op.BodyContentType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ? null : op.BodyContentType;

        Scenario Make(string name, string kind, JsonNode? b, List<int> status) => new()
        {
            Name = name,
            OperationId = op.OperationId,
            Method = op.Method,
            Path = path,
            Kind = kind,
            Setup = new List<string>(deps.Setup),
            Body = b,
            ContentType = b is null ? null : contentType,
            Query = new Dictionary<string, string>(query),
            Headers = new Dictionary<string, string>(headers),
            ExpectedStatus = status,
        };

        var list = new List<GeneratedCase> { new(Make($"{op.OperationId}_valid", "valid", body, op.SuccessStatuses.ToList()), CaseMeta.Valid) };
        if (body is not null && includeNegatives)
        {
            // Ett giltigt fall till för varje annat alternativ (oneOf/anyOf) än det första.
            foreach (var (field, label, altBody) in AlternativeBodies(gen, op.BodySchema!, body))
                list.Add(new(Make($"{op.OperationId}_valid_{Safe(field.Length == 0 ? label : field + "_" + label)}", "valid", altBody, op.SuccessStatuses.ToList()),
                    CaseMeta.ValidAlternative(field, label)));

            var accepted = NegativeStatuses(op);
            var negatives = NegativeCaseGenerator.Generate(body, op.BodySchema!, out var capped);
            foreach (var n in negatives)
                list.Add(new(Make($"{op.OperationId}_{n.Suffix}", "negative", n.Body, accepted.ToList()), n.Meta));
            if (capped && salt is null)
                Console.Error.WriteLine($"Info: {op.OperationId} har fler än {NegativeCaseGenerator.MaxCases} negativa fall; bara de {NegativeCaseGenerator.MaxCases} grundaste (minst nästlade) skapas.");
        }
        return list;
    }

    /// <summary>
    /// Statuskoderna ett negativt fall godtar: alltid 400, 422 om specen listar 422, och hela 4xx-gruppen (400–499)
    /// om specen anger "4XX".
    /// </summary>
    public static List<int> NegativeStatuses(OperationInfo op)
    {
        var codes = op.ResponseCodes ?? Array.Empty<string>();
        if (codes.Contains("4xx")) return Enumerable.Range(400, 100).ToList();
        return codes.Contains("422") ? new List<int> { 400, 422 } : new List<int> { 400 };
    }

    private const int MaxAlternativeCases = 10;

    /// <summary>
    /// Giltiga bodys för alternativ nummer 2 och framåt av varje oneOf/anyOf i bodyn (på toppnivå och i fält, högst
    /// <see cref="MaxAlternativeCases"/>). Alternativ som är objekt får sitt diskriminatorvärde (eller kind-enum) satt.
    /// </summary>
    private static List<(string Field, string Label, JsonNode Body)> AlternativeBodies(SchemaDataGenerator gen, OpenApiSchema root, JsonNode valid)
    {
        var result = new List<(string, string, JsonNode)>();

        void Visit(OpenApiSchema schema, List<object> path, int depth)
        {
            if (depth > NegativeCaseGenerator.MaxDepth || result.Count >= MaxAlternativeCases) return;
            var alts = SchemaUtil.Alternatives(schema);
            if (alts.Count > 1)
            {
                for (var i = 1; i < alts.Count && result.Count < MaxAlternativeCases; i++)
                {
                    var value = gen.GenerateField(path.Count == 0 ? "" : (path[^1] as string ?? ""), alts[i]);
                    if (value is JsonObject o) SetDiscriminator(schema, alts[i], o);
                    result.Add((NegativeCaseGenerator.DisplayPath(path), AlternativeLabel(schema, alts[i], i, value),
                        NegativeCaseGenerator.Mutate(valid, path, value)));
                }
            }
            var eff = SchemaUtil.Effective(schema);
            if (SchemaUtil.TypeOf(eff) == "object")
                foreach (var (name, child) in SchemaUtil.Flatten(eff).Props)
                {
                    if (child.ReadOnly || Get(valid, path, name) is null) continue;
                    path.Add(name);
                    Visit(child, path, depth + 1);
                    path.RemoveAt(path.Count - 1);
                }
        }
        Visit(root, new List<object>(), 0);
        return result;
    }

    private static JsonNode? Get(JsonNode valid, List<object> path, string name)
    {
        JsonNode? cur = valid;
        foreach (var seg in path) cur = cur is JsonObject o && seg is string k ? o[k] : null;
        return cur is JsonObject obj ? obj[name] : null;
    }

    /// <summary>Sätter diskriminatorfältet till alternativets värde (ur mapping, eller alternativets namn).</summary>
    private static void SetDiscriminator(OpenApiSchema parent, OpenApiSchema alt, JsonObject value)
    {
        if (parent.Discriminator?.PropertyName is not { Length: > 0 } prop) return;
        if (SchemaUtil.Flatten(alt).Props.TryGetValue(prop, out var own) && own.Enum.Count > 0) return;   // alternativet bestämmer värdet själv
        var id = alt.Reference?.Id;
        var mapped = parent.Discriminator.Mapping?.FirstOrDefault(kv => kv.Value.EndsWith("/" + id, StringComparison.Ordinal) || kv.Value == id).Key;
        var name = mapped ?? id;
        if (name is not null) value[prop] = name;
    }

    /// <summary>Namn på ett alternativ: diskriminator/kind-värde, annars titel, schemanamn eller "alternativ N".</summary>
    private static string AlternativeLabel(OpenApiSchema parent, OpenApiSchema alt, int index, JsonNode? value)
    {
        if (parent.Discriminator?.PropertyName is { Length: > 0 } prop && value is JsonObject o && o[prop] is JsonValue dv && dv.TryGetValue<string>(out var d)) return d;
        if (value is JsonObject obj)
            foreach (var (k, v) in SchemaUtil.Flatten(alt).Props)
                if (v.Enum.Count == 1 && obj[k] is JsonValue ev && ev.TryGetValue<string>(out var e)) return e;
        if (!string.IsNullOrWhiteSpace(alt.Title)) return alt.Title.Trim();
        if (!string.IsNullOrWhiteSpace(alt.Reference?.Id)) return alt.Reference!.Id;
        return $"alternativ {index + 1}";
    }

    /// <summary>Skriver en fil per endpoint (operation). Returnerar antal filer.</summary>
    public int WriteAll(IEnumerable<OperationInfo> ops, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var n = 0;
        foreach (var op in ops.Where(o => !_config.SkipOperations.Contains(o.OperationId)))
        {
            var file = Path.Combine(outDir, Safe(op.OperationId) + ".json");
            File.WriteAllText(file, ScenarioJson.Serialize(Generate(op)));
            n++;
        }
        return n;
    }

    private static string Safe(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));

    private static JsonNode? ToNode(object? o) => o switch
    {
        null => null,
        string s => JsonValue.Create(s),
        _ => JsonValue.Create(o.ToString()),
    };
}
