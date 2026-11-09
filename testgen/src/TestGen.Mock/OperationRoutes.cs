using System.Text.RegularExpressions;
using CommandLine;
using TestGen.Core;

namespace TestGen.Mock;

/// <summary>En operation och mönstret för dess sökväg. <see cref="ParameterNames"/> står i samma ordning som grupperna p0, p1 ...</summary>
internal sealed record OperationRoute(OperationInfo Operation, Regex Pattern, IReadOnlyList<string> ParameterNames);

internal sealed record RouteMatch(OperationRoute Route, IReadOnlyDictionary<string, string> Values);

/// <summary>
/// Matchar metod och sökväg mot specens operationer. Mallar utan parametrar prövas före mallar med
/// (<c>/orders/latest</c> före <c>/orders/{id}</c>). Ett prefix framför mallen godtas (<c>/api/orders/5</c>), eftersom appar ofta lägger ett.
/// </summary>
internal sealed class OperationRoutes
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);
    private readonly List<OperationRoute> _routes;

    public OperationRoutes(IEnumerable<OperationInfo> operations) => _routes = operations
        .Select(Compile)
        .OrderBy(r => r.ParameterNames.Count)
        .ThenBy(r => r.Operation.Path, StringComparer.Ordinal)
        .ThenBy(r => r.Operation.Method, StringComparer.Ordinal)
        .ToList();

    public IReadOnlyList<OperationRoute> All => _routes;

    public RouteMatch? Match(string method, string path)
    {
        path = Normalize(path);
        foreach (var candidate in Suffixes(path))
            foreach (var route in _routes)
                if (route.Operation.Method.Equals(method, StringComparison.OrdinalIgnoreCase) && TryMatch(route, candidate) is { } match)
                    return match;
        return null;
    }

    /// <summary>En text som förklarar varför inget matchade, med närmaste operation om någon är nära.</summary>
    public string Explain(string method, string path)
    {
        path = Normalize(path);
        var other = Suffixes(path).SelectMany(candidate => _routes.Where(r => TryMatch(r, candidate) is not null)).FirstOrDefault();
        if (other is not null)
        {
            var methods = _routes.Where(r => r.Operation.Path == other.Operation.Path).Select(r => r.Operation.Method);
            return $"Specen har ingen {method.ToUpperInvariant()} för {other.Operation.Path}. Metoder i specen: {string.Join(", ", methods)}.";
        }
        var templates = _routes.Select(r => r.Operation.Path).Distinct().ToList();
        var closest = Suggest.Closest(path, templates, 3);
        return closest is null
            ? $"Specen har ingen operation för {method.ToUpperInvariant()} {path}."
            : $"Specen har ingen operation för {method.ToUpperInvariant()} {path}. Närmast: {closest}.";
    }

    private static string Normalize(string path)
    {
        var trimmed = path.TrimEnd('/');
        return trimmed.Length == 0 ? "/" : trimmed;
    }

    private static IEnumerable<string> Suffixes(string path)
    {
        yield return path;
        for (var i = 1; i < path.Length; i++)
            if (path[i] == '/') yield return path[i..];
    }

    private static RouteMatch? TryMatch(OperationRoute route, string path)
    {
        Match m;
        try { m = route.Pattern.Match(path); }
        catch (RegexMatchTimeoutException) { return null; }
        if (!m.Success) return null;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < route.ParameterNames.Count; i++)
            values[route.ParameterNames[i]] = Uri.UnescapeDataString(m.Groups[$"p{i}"].Value);
        return new RouteMatch(route, values);
    }

    private static OperationRoute Compile(OperationInfo op)
    {
        var names = new List<string>();
        var pattern = Regex.Replace(op.Path.TrimEnd('/'), @"\{([^}/]+)\}|[^{]+|[{}]", m =>
        {
            if (!m.Groups[1].Success) return Regex.Escape(m.Value);
            names.Add(m.Groups[1].Value);
            return $"(?<p{names.Count - 1}>[^/]+)";
        });
        if (pattern.Length == 0) pattern = "/";
        return new OperationRoute(op, new Regex($"^{pattern}$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, Timeout), names);
    }
}
