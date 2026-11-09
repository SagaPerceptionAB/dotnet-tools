using TestGen.Core.Views;

namespace TestGen.Views.Discovery;

/// <summary>Kör alla upptäckare och gör en sorterad katalog med unika id:n av resultatet.</summary>
public static class ViewDiscovery
{
    public static ViewCatalog Run(DiscoveryContext context, IEnumerable<IViewDiscoverer> discoverers) =>
        Finish(discoverers.SelectMany(d => d.Discover(context)), context.Warnings);

    /// <summary>Normaliserar, sorterar och gör id:n unika.</summary>
    public static ViewCatalog Finish(IEnumerable<View> views, IEnumerable<string> warnings)
    {
        var sorted = views
            .Select(Normalize)
            .OrderBy(v => v.Route, StringComparer.Ordinal).ThenBy(v => v.Source, StringComparer.Ordinal)
            .ToList();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unique = sorted.Select(v => v with { Id = Names.Unique(used, v.Id, "#") }).ToList();
        return new ViewCatalog(unique, warnings.Distinct().Order(StringComparer.Ordinal).ToList());
    }

    /// <summary>Sorterar listorna i en vy så att utdata blir densamma oavsett i vilken ordning upptäckaren hittade saker.</summary>
    private static View Normalize(View v) => v with
    {
        Links = v.Links.DistinctBy(l => l.Href).OrderBy(l => l.Href, StringComparer.Ordinal).ToList(),
        ApiCalls = v.ApiCalls.Distinct().OrderBy(c => c.Path, StringComparer.Ordinal).ThenBy(c => c.Method, StringComparer.Ordinal).ToList(),
        Forms = v.Forms.Select(f => f with { Fields = f.Fields.Select(x => x with { Rules = x.Rules.OrderBy(r => r.Kind).ToList() }).ToList() }).ToList(),
    };
}
