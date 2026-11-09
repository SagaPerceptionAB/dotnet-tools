
namespace SystemMap;

/// <summary>
/// Kopplar ingångarna i koden till operationerna i specen (HTTP-metod och sökväg, parametrarnas namn spelar ingen roll)
/// och listar det som bara finns på ena stället.
/// </summary>
internal static class SpecMatcher
{
    public static (Dictionary<EntryCandidate, ApiOperation> Matches, List<SpecDifference> Differences) Match(
        IReadOnlyList<EntryCandidate> entries, IReadOnlyList<ApiOperation>? operations)
    {
        var matches = new Dictionary<EntryCandidate, ApiOperation>();
        var differences = new List<SpecDifference>();
        if (operations is null) return (matches, differences);

        // Sidor är inga API-anrop och finns inte i specen.
        var api = entries.Where(e => e.Kind is not (EntryKind.RazorPage or EntryKind.BlazorPage)).ToList();
        foreach (var entry in api)
        {
            var key = RouteTemplate.Key(entry.CodePath);
            bool SameVerb(ApiOperation o) => entry.Method == "ANY" || o.Method.Equals(entry.Method, StringComparison.OrdinalIgnoreCase);
            var exact = operations.Where(o => SameVerb(o) && RouteTemplate.Key(o.Path) == key).ToList();
            // Specen kan utgå från en bas-sökväg (servers: .../api/v1) som koden har med i sin route.
            var candidates = exact.Count > 0 ? exact
                : operations.Where(o => SameVerb(o) && RouteTemplate.Key(o.Path) is var k && k.Length > 1 && key.Length > k.Length && key.EndsWith(k)).ToList();
            if (candidates.Count == 1) matches[entry] = candidates[0];
        }

        foreach (var entry in api.Where(e => !matches.ContainsKey(e)))
            differences.Add(new SpecDifference(DifferenceKind.OnlyInCode, entry.Method, entry.CodePath, null, entry.Handler));
        var matched = matches.Values.ToHashSet();
        foreach (var op in operations.Where(o => !matched.Contains(o)))
            differences.Add(new SpecDifference(DifferenceKind.OnlyInSpec, op.Method, op.Path, op.OperationId, null));

        differences.Sort((a, b) => a.Kind != b.Kind ? a.Kind.CompareTo(b.Kind)
            : string.CompareOrdinal(a.Path, b.Path) is var byPath and not 0 ? byPath : string.CompareOrdinal(a.Method, b.Method));
        return (matches, differences);
    }
}
