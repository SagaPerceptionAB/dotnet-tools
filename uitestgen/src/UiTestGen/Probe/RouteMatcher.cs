using UiTestGen.Model;

namespace UiTestGen.Probe;

/// <summary>
/// Känner igen sökvägar som någon av de kända rutterna med andra parametervärden: <c>/products/42</c> hör till <c>/products/{id:int}</c>
/// och skrivs då <c>/products/{}</c>. Villkoren gäller (<c>/products/create</c> är inget <c>int</c>), och bland flera träffar vinner
/// rutten med flest fasta segment, så att <c>/products/create</c> inte tas för <c>/products/{id}</c>.
/// </summary>
internal sealed class RouteMatcher
{
    private readonly List<RouteTemplate> _routes;

    public RouteMatcher(IEnumerable<string> routes) =>
        _routes = routes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(RouteTemplate.Parse).ToList();

    /// <summary>Rutten som sökvägen hör till, eller null om ingen känd rutt passar.</summary>
    public string? Match(string path) => Best(RouteTemplate.Split(path))?.Route;

    /// <summary>Sökvägen med de dynamiska segmenten som <c>{}</c>. Okänd sökväg lämnas som den är.</summary>
    public string Normalize(string path)
    {
        var segments = RouteTemplate.Split(path);
        return Best(segments)?.Normalize(segments) ?? path;
    }

    /// <summary>Rutten med flest fasta segment. Vid lika vinner den som kommer först i ordningen.</summary>
    private RouteTemplate? Best(string[] segments)
    {
        RouteTemplate? best = null;
        var bestScore = -1;
        foreach (var route in _routes)
        {
            if (route.Match(segments) is not { } match || match.Literals <= bestScore) continue;
            best = route;
            bestScore = match.Literals;
        }
        return best;
    }
}
