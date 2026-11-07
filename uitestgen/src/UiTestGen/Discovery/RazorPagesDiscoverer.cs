using UiTestGen.Discovery.Mvc;
using UiTestGen.Discovery.Razor;
using UiTestGen.Model;

namespace UiTestGen.Discovery;

/// <summary>
/// Hittar Razor Pages: .cshtml-filer med <c>@page</c> under en mapp <c>Pages/</c>. Rutten följer sökvägen, formulären läses ur
/// <c>asp-for</c> och reglerna ur <c>PageModel</c>-klassen. Layouter, <c>_ViewImports</c> och partials (<c>_*.cshtml</c>) blir inga vyer, men länkarna i sidans layout
/// (och i partials som sidan och layouten använder) läggs till på sidan. Länkar med <c>asp-page</c> får sidans rutt.
/// </summary>
internal sealed class RazorPagesDiscoverer : IViewDiscoverer
{
    /// <summary>Sidans plats under <c>Pages/</c>: sökvägen (utan Index) och mappen den ligger i, båda med området först om det finns ett.</summary>
    private sealed record PageLocation(string[] Path, string[] Folder);

    private sealed record PageFile(string File, RazorSource Source, string Relative, PageLocation Location, string Route);

    public IEnumerable<View> Discover(DiscoveryContext context)
    {
        var cache = SourceCache.For(context);
        var index = TypeIndex.For(context);
        var layouts = CshtmlLayouts.For(context);

        // Först alla sidor och deras rutter, så att en länk med asp-page kan peka på en annan sidas rutt.
        var pages = new List<PageFile>();
        var routes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in context.Files(".cshtml"))
        {
            if (Path.GetFileName(file).StartsWith('_')) continue;
            if (PagesPath(context, file) is not { } location) continue;
            if (cache.Razor(file) is not { } source) continue;
            var directives = source.Pages();
            if (directives.Count == 0) continue;

            var relative = context.Relative(file);
            var directive = directives[0];
            if (!directive.Readable)
            {
                context.Warnings.Add($"Rutten i {relative} går inte att läsa (@page {directive.Raw}). Vyn hoppas över.");
                continue;
            }
            var route = Route(location.Path, directive.Template);
            pages.Add(new PageFile(file, source, relative, location, route));
            routes.TryAdd(PageKey(location.Path), route);
        }

        var views = new List<View>();
        foreach (var page in pages)
        {
            var doc = page.Source.Doc;
            var local = new TypeIndex(index);
            local.AddCodeBehind(page.File); // klasserna i .cshtml.cs bredvid sidan går före andra klasser med samma namn (många sidor har en IndexModel)
            var modelType = page.Source.ModelType();
            var forms = FormReader.Read(context, local, doc, modelType, page.Relative);
            var auth = page.Source.HasAuthorize()
                || (modelType is not null && ModelAttributes.RequiresAuth(local.Classes(modelType).SelectMany(c => c.Attributes)));
            // Sidans egna länkar och partials den använder först, därefter länkarna i layouten och dess partials. Dubbletter tas bort av ViewDiscovery.
            var links = layouts.Links.Read(page.File, page.Source, tag => PageLink(tag, page.Location.Folder, routes));
            var title = page.Source.ViewDataTitle() ?? ViewParts.FirstFixedText(doc, "title");

            views.Add(new View(Names.IdFromRoute(page.Route), page.Route, ViewKind.RazorPage, page.Relative, title, ViewParts.FirstFixedText(doc, "h1"),
                Names.Parameters(page.Route), forms, links, [], auth));
        }
        return views;
    }

    // Sökvägen under Pages/ (och området, om filen ligger under Areas/X/Pages/), utan filändelse och utan Index. Null om filen inte ligger under Pages/.
    private static PageLocation? PagesPath(DiscoveryContext context, string file)
    {
        var segments = Path.GetRelativePath(context.ProjectOf(file), file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var pages = Array.FindIndex(segments, s => s.Equals("Pages", StringComparison.OrdinalIgnoreCase));
        if (pages < 0 || pages >= segments.Length - 1) return null;

        var path = segments[(pages + 1)..].ToList();
        path[^1] = Path.GetFileNameWithoutExtension(path[^1]);
        var folder = path.Take(path.Count - 1).ToList();
        if (path[^1].Equals("Index", StringComparison.OrdinalIgnoreCase)) path.RemoveAt(path.Count - 1);
        if (pages >= 2 && segments[pages - 2].Equals("Areas", StringComparison.OrdinalIgnoreCase))
        {
            path.Insert(0, segments[pages - 1]);
            folder.Insert(0, segments[pages - 1]);
        }
        return new PageLocation([.. path], [.. folder]);
    }

    // @page ger sökvägen; @page "{id:int}" läggs till sist; @page "/abs" ersätter hela sökvägen.
    private static string Route(string[] path, string? template)
    {
        var basePath = "/" + string.Join('/', path);
        if (string.IsNullOrWhiteSpace(template)) return basePath;
        template = template.Trim();
        if (template.StartsWith("~/", StringComparison.Ordinal)) template = template[1..];
        if (template.StartsWith('/')) return template;
        return basePath.TrimEnd('/') + "/" + template;
    }

    // Sidans namn som i asp-page: sökvägen utan Index, med / först.
    private static string PageKey(IEnumerable<string> path) => "/" + string.Join('/', path);

    // <a asp-page="/Products/Details" asp-route-id="5">: sidans rutt, där parametrar som får ett asp-route-x blir {}. En sida som inte hittats
    // får adressen ur namnet, med ett {} för varje asp-route-x. Ett namn utan / först räknas från sidans mapp, och asp-area ersätter den.
    // Området arvs inte från sidan. Null för länkar utan asp-page.
    private static string? PageLink(Tag tag, string[] folder, Dictionary<string, string> routes)
    {
        if (tag["asp-page"] is not { } page || page.Contains('@')) return null;
        var area = tag["asp-area"] is { Length: > 0 } a && !a.Contains('@') ? a : null;
        page = page.Trim();
        var absolute = page.StartsWith('/') || page.StartsWith("~/", StringComparison.Ordinal);

        var path = new List<string>();
        if (area is not null) path.Add(area);
        else if (!absolute) path.AddRange(folder);
        foreach (var part in page.TrimStart('~').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..") { if (path.Count > 0) path.RemoveAt(path.Count - 1); }
            else path.Add(part);
        }
        if (path.Count > 0 && path[^1].Equals("Index", StringComparison.OrdinalIgnoreCase)) path.RemoveAt(path.Count - 1);

        var values = tag.Attrs.Where(x => x.Name.StartsWith("asp-route-", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Name["asp-route-".Length..].ToLowerInvariant()).ToHashSet();
        if (routes.TryGetValue(PageKey(path), out var known)) return MvcRoutes.FillParameters(known, values);
        var href = PageKey(path).TrimEnd('/') + string.Concat(values.Select(_ => "/{}"));
        return href.Length == 0 ? "/" : href;
    }
}
