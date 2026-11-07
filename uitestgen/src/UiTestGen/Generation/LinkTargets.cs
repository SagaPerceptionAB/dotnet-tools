using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using UiTestGen.Model;

namespace UiTestGen.Generation;

/// <summary>
/// Kopplar en länk till vyn den pekar på, och skriver väljare och adressmönster för navigeringstesterna.
/// Länkar som inte går att koppla till en känd vy (externa, relativa, okända adresser) får inget test.
/// </summary>
internal static class LinkTargets
{
    private static readonly ConditionalWeakTable<ViewCatalog, Resolver> Resolvers = new();

    /// <summary>Adressen som sökväg med tvärstreck och utan frågesträng och ankare, eller null för externa och relativa adresser.</summary>
    public static string? Normalize(string href)
    {
        var path = href.Trim();
        var cut = path.IndexOfAny(['?', '#']);
        if (cut >= 0) path = path[..cut];
        if (!path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal)) return null;
        return path.Length > 1 ? path.TrimEnd('/') : path;
    }

    /// <summary>Vyn en länk pekar på, eller null. En fast adress föredrar en vy med fast rutt framför en med parametrar.</summary>
    public static View? Resolve(ViewCatalog catalog, string href) => For(catalog).Resolve(href);

    /// <summary>Resolvern för katalogen. Den byggs en gång per katalog, så att rutterna bara tolkas en gång.</summary>
    internal static Resolver For(ViewCatalog catalog) => Resolvers.GetValue(catalog, static c => new Resolver(c));

    /// <summary>
    /// Kopplar länkar till vyer i en katalog. Rutterna är tolkade i förväg och svaret för en adress kommer ihåg.
    /// En länk till en parameter måste uppfylla parameterns villkor (<c>create</c> är inget <c>int</c>), även för SPA-rutter som <c>/products/:id</c>.
    /// </summary>
    internal sealed class Resolver
    {
        private readonly (View View, RouteTemplate Template)[] _views;
        private readonly ConcurrentDictionary<string, View?> _memo = new(StringComparer.Ordinal);

        public Resolver(ViewCatalog catalog)
        {
            _views = catalog.Views.Select(v => (v, RouteTemplate.Parse(v.Route))).ToArray();
            // Rutterna utan parametrar, sorterade en gång: en dynamisk länk till en annan vy ska inte välja dem.
            FixedRoutes = catalog.Views.Where(v => v.Route != "/" && !v.Route.Contains('{'))
                .OrderBy(v => v.Route, StringComparer.Ordinal).ToList();
        }

        /// <summary>Vyerna med fast rutt (utom startsidan), sorterade på rutt.</summary>
        public IReadOnlyList<View> FixedRoutes { get; }

        public View? Resolve(string href) => _memo.GetOrAdd(href, Find);

        private View? Find(string href)
        {
            var path = Normalize(href);
            if (path is null) return null;
            var segments = RouteTemplate.Split(path);
            View? best = null;
            var bestParameters = int.MaxValue;
            foreach (var (view, template) in _views)
            {
                if (template.Match(segments) is not { } match) continue;
                if (match.Parameters < bestParameters || (match.Parameters == bestParameters && string.CompareOrdinal(view.Id, best!.Id) < 0))
                {
                    best = view;
                    bestParameters = match.Parameters;
                }
            }
            return best;
        }
    }

    /// <summary>
    /// Regexen (som C#-sträng) för hela webbadressen när man är framme på vyn. Fasta segment skrivs ut, parametrar blir
    /// <c>[^/]+</c>, valfria parametrar och fångar-resten blir valfria.
    /// </summary>
    public static string UrlPattern(string route) => RouteTemplate.Parse(route).ToRegex("^https?://[^/]+", "/?(?:[?#].*)?$");

    /// <summary>
    /// Väljaren för länken, som C#-kod. Fast text: länkroll med exakt namn. Annars fast adress: <c>a[href='...']</c>.
    /// Dynamisk adress: <c>a[href^='/products/']</c> utan de andra kända vyernas fasta adresser, så att <c>/products/create</c>
    /// inte väljs som <c>/products/{id}</c>.
    /// </summary>
    public static string Locator(ViewCatalog catalog, Link link, View target)
    {
        var path = Normalize(link.Href)!;
        if (link.Text is { Length: > 0 } text)
            return $"Page.GetByRole(AriaRole.Link, new() {{ Name = {CSharp.Literal(text)}, Exact = true }}).First";
        if (!path.Contains("{}", StringComparison.Ordinal))
            return $"Page.Locator({CSharp.Literal($"a[href={Css(path)}]")}).First";

        var first = path.IndexOf("{}", StringComparison.Ordinal);
        var last = path.LastIndexOf("{}", StringComparison.Ordinal) + 2;
        var prefix = path[..first];
        var suffix = path[last..];
        var css = new StringBuilder($"a[href^={Css(prefix)}]");
        if (suffix.Length > 0) css.Append($"[href$={Css(suffix)}]");
        foreach (var other in For(catalog).FixedRoutes)
        {
            if (!ReferenceEquals(other, target) && other.Route.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && other.Route.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                css.Append($":not([href={Css(other.Route)}])");
        }
        return $"Page.Locator({CSharp.Literal(css.ToString())}).First";
    }

    /// <summary>Värdet som CSS-sträng med enkla citattecken.</summary>
    private static string Css(string value) => "'" + value.Replace("\\", "\\\\").Replace("'", "\\'") + "'";
}
