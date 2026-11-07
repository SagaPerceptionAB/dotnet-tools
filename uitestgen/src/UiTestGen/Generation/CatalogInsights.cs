using System.Runtime.CompilerServices;
using UiTestGen.Model;

namespace UiTestGen.Generation;

/// <summary>Slutsatser av hela vykatalogen, till exempel vyer som ingen länkar till.</summary>
public static class CatalogInsights
{
    /// <summary>
    /// Föräldralösa vyer: vyer som ingen annan vy länkar till, utom startsidan (<c>/</c>). En länk till sig själv räknas inte.
    /// Länkar i delade layouter (menyer) ingår bara om upptäckten läste dem, så en vy som bara nås från menyn kan hamna här.
    /// Sorterad på rutt och id.
    /// </summary>
    public static IReadOnlyList<View> Orphans(ViewCatalog catalog) => Cache.GetValue(catalog, FindOrphans);

    // Räknas en gång per katalog: sammanfattningen och rapportfilen delar resultatet.
    private static readonly ConditionalWeakTable<ViewCatalog, IReadOnlyList<View>> Cache = new();

    private static IReadOnlyList<View> FindOrphans(ViewCatalog catalog)
    {
        var linked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var view in catalog.Views)
            foreach (var link in view.Links)
                if (LinkTargets.Resolve(catalog, link.Href) is { } target && target.Id != view.Id)
                    linked.Add(target.Id);
        return catalog.Views.Where(v => v.Route != "/" && !linked.Contains(v.Id))
            .OrderBy(v => v.Route, StringComparer.Ordinal).ThenBy(v => v.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>Raderna att visa för användaren om föräldralösa vyer, eller en tom lista om det inte finns några.</summary>
    public static IReadOnlyList<string> OrphanSummary(ViewCatalog catalog)
    {
        var orphans = Orphans(catalog);
        if (orphans.Count == 0) return [];
        return
        [
            $"{orphans.Count} {(orphans.Count == 1 ? "vy" : "vyer")} som ingen annan vy länkar till:",
            .. orphans.Select(v => $"  {v.Route}  ({v.Source})"),
        ];
    }

    /// <summary>Filen <c>Tests/CatalogReport.g.cs</c>: de föräldralösa vyerna som data och kommentar.</summary>
    internal static string CatalogReport(ViewCatalog catalog, string ns)
    {
        var orphans = Orphans(catalog);
        var w = new CodeWriter();
        w.Line(CSharp.Header);
        w.Line();
        w.Line($"namespace {ns}.Tests;");
        w.Line();
        w.Line("// Vyer som ingen annan vy länkar till (utom startsidan). De nås bara genom att skriva adressen eller via länkar som");
        w.Line("// upptäckten inte läste (t.ex. en meny i en delad layout), så inga navigeringstester leder dit.");
        if (orphans.Count == 0) w.Line("// Inga: alla vyer nås från någon annan vy.");
        foreach (var v in orphans) w.Line($"// {CSharp.Comment(v.Route)}  ({CSharp.Comment(v.Source)})");
        using (w.Block("public static class CatalogReport"))
        {
            w.Line("/// <summary>Rutterna till vyerna som ingen annan vy länkar till.</summary>");
            if (orphans.Count == 0) w.Line("public static readonly string[] OrphanRoutes = [];");
            else
            {
                w.Line("public static readonly string[] OrphanRoutes =");
                w.Line("[");
                foreach (var v in orphans) w.Line($"    {CSharp.Literal(v.Route)},");
                w.Line("];");
            }
        }
        return w.ToString();
    }
}
