using UiTestGen.Model;

namespace UiTestGen.Discovery;

/// <summary>Kör alla upptäckare och gör en sorterad katalog med unika id:n av resultatet.</summary>
public static class ViewDiscovery
{
    public static ViewCatalog Run(DiscoveryContext context, IEnumerable<IViewDiscoverer> discoverers)
    {
        return Finish(discoverers.SelectMany(d => d.Discover(context)), context.Warnings);
    }

    /// <summary>Normaliserar, sorterar och gör id:n unika. Används också efter sonderingen, så att en sonderad katalog ser ut som en statisk.</summary>
    public static ViewCatalog Finish(IEnumerable<View> views, IEnumerable<string> warnings)
    {
        var sorted = views
            .Select(Normalize)
            .OrderBy(v => v.Route, StringComparer.Ordinal).ThenBy(v => v.Source, StringComparer.Ordinal)
            .ToList();
        return new ViewCatalog(UniqueIds(sorted), warnings.Distinct().Order(StringComparer.Ordinal).ToList());
    }

    /// <summary>Sorterar listorna i en vy så att utdata blir densamma oavsett i vilken ordning upptäckaren hittade saker.</summary>
    private static View Normalize(View v) => v with
    {
        Links = v.Links.DistinctBy(l => l.Href).OrderBy(l => l.Href, StringComparer.Ordinal).ToList(),
        ApiCalls = v.ApiCalls.Distinct().OrderBy(c => c.Path, StringComparer.Ordinal).ThenBy(c => c.Method, StringComparer.Ordinal).ToList(),
        Forms = v.Forms.Select(f => f with { Fields = f.Fields.Select(x => x with { Rules = x.Rules.OrderBy(r => r.Kind).ToList() }).ToList() }).ToList(),
    };

    /// <summary>
    /// Gör id:n unika med suffix (2, 3 ...). Vyer ur källkoden får välja först, så att en vy som bara sonderingen hittade
    /// aldrig tar namnet från en känd vy. Ordningen i listan ändras inte.
    /// </summary>
    private static List<View> UniqueIds(List<View> views)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new string[views.Count];
        foreach (var i in Enumerable.Range(0, views.Count).OrderBy(i => views[i].Kind == ViewKind.Probed ? 1 : 0).ThenBy(i => i))
        {
            ids[i] = Names.Unique(used, views[i].Id);
        }
        return views.Select((v, i) => v with { Id = ids[i] }).ToList();
    }
}
