using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.OpenApi;

using TestGen.Core.Run;

namespace TestGen.Excel;

/// <summary>
/// Jämför alla rader på bladet Tester med specen innan något test körs: okänt anrop, okänt fält, ogiltig förväntad status
/// och referenser <c>${namn.fält}</c> till något som inte sparats tidigare i scenariot. Samlar alla fel (och varningar).
/// </summary>
public static partial class ExcelValidator
{
    /// <summary>Visa alla anrop i felet om specen har högst så här många.</summary>
    public const int ListAllUpTo = 15;

    [GeneratedRegex(@"\$\{([^}]+)\}")]
    private static partial Regex Placeholder();

    private sealed record Saved(int Row, OperationInfo? Op, bool Named = false);

    /// <returns>Stegen med anropsnamn rättade till specens skiftläge, och alla funna fel/varningar.</returns>
    public static (List<ExcelScenario> Scenarios, List<ExcelIssue> Issues) Validate(IEnumerable<ExcelScenario> scenarios, IReadOnlyList<OperationInfo> ops)
    {
        var issues = new List<ExcelIssue>();
        var byId = ops.ToDictionary(o => o.OperationId, StringComparer.Ordinal);
        var result = new List<ExcelScenario>();

        foreach (var sc in scenarios)
        {
            var saved = new Dictionary<string, Saved>(StringComparer.OrdinalIgnoreCase);
            var savedAt = new Dictionary<string, ExcelStep>(StringComparer.OrdinalIgnoreCase);   // "Spara som"-namn -> första steget som sparar det
            var steps = new List<ExcelStep>();
            for (var i = 0; i < sc.Steps.Count; i++)
            {
                var st = sc.Steps[i];
                OperationInfo? op = null;
                if (st.Operation.Length > 0)
                {
                    if (byId.TryGetValue(st.Operation, out op)) { }
                    else
                    {
                        var ci = ops.Where(o => o.OperationId.Equals(st.Operation, StringComparison.OrdinalIgnoreCase)).ToList();
                        if (ci.Count == 1) { op = ci[0]; st = st with { Operation = op.OperationId }; }   // bara skiftläget skilde
                        else issues.Add(st.Issue("Anrop", UnknownOperation(st.Operation, ops)));
                    }
                }
                steps.Add(st);

                if (st.StatusInvalid)
                {
                    var codes = op is null ? null : (op.ResponseCodes is { Count: > 0 } rc ? rc : op.SuccessStatuses.Select(n => n.ToString()).ToList());
                    issues.Add(st.Issue("Förväntad status",
                        $"förväntad status '{st.StatusText}' är inte en statuskod. Skriv t.ex. 201, eller 2xx/4xx för en hel grupp." +
                        (codes is { Count: > 0 } ? $" Koderna för {op!.OperationId} är {string.Join(", ", codes)}." : "")));
                }

                // Indata: fält och referenser.
                if (op is not null) CheckFields(st, op, issues);
                var autoRun = new List<OperationInfo>();
                foreach (var (_, value) in st.Input)
                    foreach (var expr in References(value))
                        CheckRef(st, "Indata", expr, true);
                foreach (var (_, value) in st.ExpectedBody)
                    foreach (var expr in References(value))
                        CheckRef(st, "Förväntat svar", expr, false);

                // Spara som: dubblett i samma scenario.
                if (st.SaveAs is { } save)
                {
                    if (savedAt.TryGetValue(save, out var first))
                        issues.Add(st.Issue("Spara som",
                            $"namnet '{save}' sparas redan på rad {first.Row} ({first.CellRef("Spara som").Split(' ')[0]}) i samma scenario ('{sc.Name}'). " +
                            $"Det här svaret ersätter det från rad {first.Row}; rader efter rad {st.Row} ser bara det nya. Välj ett annat namn om båda behövs.", error: false));
                    else savedAt[save] = st;
                }

                foreach (var a in autoRun) saved.TryAdd(a.OperationId, new Saved(st.Row, a));
                if (st.SaveAs is not null) saved[st.SaveAs] = new Saved(st.Row, op, true);
                if (op is not null && (st.SaveAs is null || !saved.ContainsKey(st.Operation))) saved[st.Operation] = new Saved(st.Row, op);

                void CheckRef(ExcelStep step, string header, string expr, bool allowAuto)
                {
                    if (expr.StartsWith("env:", StringComparison.OrdinalIgnoreCase)) return;
                    var parts = expr.Split('.');
                    var alias = parts[0];
                    if (!saved.TryGetValue(alias, out var s))
                    {
                        if (allowAuto && byId.TryGetValue(alias, out var auto)) { autoRun.Add(auto); s = new Saved(step.Row, auto); }   // anropet körs automatiskt som förberedelse
                        else
                        {
                            var later = sc.Steps.Skip(i + 1).FirstOrDefault(x => x.SaveAs is not null && x.SaveAs.Equals(alias, StringComparison.OrdinalIgnoreCase));
                            issues.Add(step.Issue(header, later is not null
                                ? $"Platshållaren ${{{expr}}}: namnet '{alias}' sparas först på rad {later.Row} (Spara som), men den här raden körs före. Flytta raden efter rad {later.Row}."
                                : ResponseStore.UnknownAlias(expr, alias, saved.Any(kv => kv.Value.Named) ? saved.Where(kv => kv.Value.Named).Select(kv => kv.Key) : saved.Keys)));
                            return;
                        }
                    }
                    if (s.Op is null || FirstSuccessSchema(s.Op) is not { } schema) return;
                    var fields = SchemaFields(schema);
                    if (parts.Length == 1)
                    {
                        if (schema.TypeName() == "array") issues.Add(step.Issue(header, ResponseStore.WholeResponse(expr, new JsonArray())));
                        else if (fields.Count > 0) issues.Add(step.Issue(header, ResponseStore.WholeResponse(expr, new JsonObject(), fields)));
                        return;
                    }
                    if (fields.Count == 0 || schema.TypeName() == "array" || int.TryParse(parts[1], out _)) return;
                    if (!fields.Contains(parts[1], StringComparer.OrdinalIgnoreCase))
                        issues.Add(step.Issue(header,
                            $"Platshållaren ${{{expr}}}: svaret för '{alias}' har inget fält '{parts[1]}'." + Suggest.DidYouMean(parts[1], fields) +
                            $" Fält i svaret: {Suggest.List(fields)}."));
                }
            }
            result.Add(sc with { Steps = steps });
        }
        return (result, issues);
    }

    private static string UnknownOperation(string name, IReadOnlyList<OperationInfo> ops)
    {
        var ids = ops.Select(o => o.OperationId).ToList();
        return $"okänt anrop '{name}'." + Suggest.DidYouMean(name, ids) +
               (ids.Count <= ListAllUpTo ? $" Anrop i specen: {string.Join(", ", ids)}." : " Välj ett anrop i listan i kolumnen.");
    }

    private static void CheckFields(ExcelStep st, OperationInfo op, List<ExcelIssue> issues)
    {
        var known = op.Parameters.Select(p => p.Name)
            .Concat(op.BodySchema is not null ? SchemaUtil.Flatten(op.BodySchema).Props.Keys : Enumerable.Empty<string>()).Distinct().ToList();
        foreach (var (field, _) in st.Input)
            if (!known.Contains(field, StringComparer.OrdinalIgnoreCase))
                issues.Add(st.Issue("Indata", $"'{field}' är inget fält för {op.OperationId}." + Suggest.DidYouMean(field, known) +
                    $" Giltiga fält: {(known.Count == 0 ? "(inga)" : string.Join(", ", known))}. Se bladet Fält."));
    }

    private static IEnumerable<string> References(JsonNode? node)
    {
        switch (node)
        {
            case JsonValue v when v.TryGetValue<string>(out var s):
                foreach (Match m in Placeholder().Matches(s)) yield return m.Groups[1].Value;
                break;
            case JsonObject o:
                foreach (var kv in o) foreach (var r in References(kv.Value)) yield return r;
                break;
            case JsonArray a:
                foreach (var x in a) foreach (var r in References(x)) yield return r;
                break;
        }
    }

    private static IOpenApiSchema? FirstSuccessSchema(OperationInfo op) =>
        op.ResponseSchemas?.Where(kv => kv.Key is >= 200 and <= 299).OrderBy(kv => kv.Key).Select(kv => kv.Value).FirstOrDefault();

    /// <summary>Fält i svarsschemat (plus de under "data", som också prövas vid körning). Tom lista = okänt, kontrollera inte.</summary>
    private static List<string> SchemaFields(IOpenApiSchema schema)
    {
        if (schema.TypeName() == "array") return new();
        var props = SchemaUtil.Flatten(schema).Props;
        var names = props.Keys.ToList();
        if (props.TryGetValue("data", out var data) && data.TypeName() != "array")
            names.AddRange(SchemaUtil.Flatten(data).Props.Keys.Where(k => !names.Contains(k, StringComparer.OrdinalIgnoreCase)));
        return names;
    }
}
