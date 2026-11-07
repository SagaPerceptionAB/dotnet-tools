using UiTestGen.Model;

namespace UiTestGen.Discovery.Razor;

/// <summary>
/// En <c>&lt;a&gt;</c> eller <c>&lt;NavLink&gt;</c> i en Razor-fil. <paramref name="Href"/> är adressen när den står i <c>href</c> (redan gjord om till
/// kataloginnehåll). Utan <c>href</c> är det <paramref name="TagHelper"/>, taggen med <c>asp-*</c>, som ramverket löser till en adress.
/// </summary>
internal readonly record struct RawLink(string? Href, Tag? TagHelper, string? Text);

/// <summary>Adressen för en länk med tag helpers (<c>asp-page</c>, <c>asp-controller</c> och <c>asp-action</c>), eller null om taggen inte är en sådan länk.</summary>
internal delegate string? TagHelperLink(Tag tag);

/// <summary>Hur ett ramverk (Blazor, Razor Pages och MVC) hittar det en Razor-fil använder.</summary>
internal interface IRazorProject
{
    /// <summary>Layoutfilen för en vy, eller null om vyn saknar layout.</summary>
    string? LayoutOf(string file, RazorSource view);

    /// <summary>Layoutfilen som en layout själv ligger i, eller null.</summary>
    string? ParentLayout(string layout, RazorSource source);

    /// <summary>Filerna för komponenterna (Blazor) eller partials (<c>&lt;partial name="..." /&gt;</c>) som filen använder, var och en en gång.</summary>
    IEnumerable<string> Uses(string file, RazorSource source);
}

/// <summary>
/// Länkarna i en Razor-fil: filens egen markup, komponenterna eller partials som filen använder (ett steg djupt) och layouten, som i sin tur
/// läses på samma sätt. Samma läsning gäller sidor och layouter, så en sidas egen <c>&lt;NavMenu /&gt;</c> eller <c>&lt;partial name="..." /&gt;</c>
/// ger länkar som layoutens. Det som inte beror på vyn (länkarna i en layout) cachas per layoutfil; tag helpers löses för varje vy.
/// </summary>
internal sealed class RazorLinks(DiscoveryContext context, IRazorProject project)
{
    private readonly SourceCache _cache = SourceCache.For(context);
    private readonly Dictionary<string, IReadOnlyList<RawLink>> _own = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<RawLink>> _layouts = new(StringComparer.Ordinal);

    /// <summary>Länkarna för vyn i filen, i ordningen egna länkar, använda komponenter eller partials, och layouten. Dubbletter tas bort senare av <see cref="ViewDiscovery"/>.</summary>
    /// <param name="tagHelpers">Löser länkar utan <c>href</c>. Null om ramverket inte har några (Blazor).</param>
    public List<Link> Read(string file, RazorSource view, TagHelperLink? tagHelpers)
    {
        var raw = new List<RawLink>();
        AddWithUses(raw, file, view);
        if (project.LayoutOf(file, view) is { } layout) raw.AddRange(Layout(layout, []));

        var links = new List<Link>();
        foreach (var link in raw)
        {
            var href = link.Href ?? (link.TagHelper is { } tag ? tagHelpers?.Invoke(tag) : null);
            if (href is not null) links.Add(new Link(href, link.Text));
        }
        return links;
    }

    // Layoutens länkar, och layoutens layouts. Cachas per layoutfil.
    private IReadOnlyList<RawLink> Layout(string layout, HashSet<string> visited)
    {
        if (!visited.Add(layout) || _cache.Razor(layout) is not { } source) return [];
        if (_layouts.TryGetValue(layout, out var cached)) return cached;

        var raw = new List<RawLink>();
        AddWithUses(raw, layout, source);
        if (project.ParentLayout(layout, source) is { } parent) raw.AddRange(Layout(parent, visited));
        return _layouts[layout] = raw;
    }

    private void AddWithUses(List<RawLink> raw, string file, RazorSource source)
    {
        raw.AddRange(Own(source));
        foreach (var used in project.Uses(file, source))
            if (_cache.Razor(used) is { } usedSource) raw.AddRange(Own(usedSource));
    }

    // Filens egna länkar: <a href> och <NavLink href> med interna adresser, och länkar med tag helpers (asp-*) utan href.
    private IReadOnlyList<RawLink> Own(RazorSource source)
    {
        if (_own.TryGetValue(source.Path, out var cached)) return cached;
        var doc = source.Doc;
        var links = new List<RawLink>();
        foreach (var tag in doc.Tags.Where(t => t.Is("a") || t.Is("NavLink")))
        {
            if (tag.Has("href"))
            {
                if (Hrefs.Internal(tag["href"]) is { } href) links.Add(new RawLink(href, null, doc.FixedText(tag)));
            }
            else if (tag.Attrs.Any(a => a.Name.StartsWith("asp-", StringComparison.OrdinalIgnoreCase)))
                links.Add(new RawLink(null, tag, doc.FixedText(tag)));
        }
        return _own[source.Path] = links;
    }
}
