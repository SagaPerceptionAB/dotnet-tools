using System.Text.RegularExpressions;

namespace TestGen.Views.Discovery.Razor;

/// <summary>
/// Blazors layouter och komponenter för <see cref="RazorLinks"/>. Layouten är en <c>@layout X</c> i sidan, annars i närmaste
/// <c>_Imports.razor</c> uppåt i mapparna, annars <c>DefaultLayout</c> i <c>&lt;RouteView&gt;</c>. En komponent är filen som heter som taggen
/// (<c>&lt;NavMenu /&gt;</c> är NavMenu.razor). Kör aldrig koden.
/// </summary>
internal sealed partial class BlazorLayouts : IRazorProject
{
    private readonly DiscoveryContext _context;
    private readonly SourceCache _cache;
    private readonly Dictionary<string, string> _components = new(StringComparer.Ordinal); // komponentnamn (filnamn utan ändelse) till sökväg
    private readonly Dictionary<string, string> _defaults = new(StringComparer.Ordinal);   // projektmapp till standardlayoutens namn

    public BlazorLayouts(DiscoveryContext context)
    {
        _context = context;
        _cache = SourceCache.For(context);
        Links = new RazorLinks(context, this);
        foreach (var file in context.Files(".razor"))
        {
            _components.TryAdd(Path.GetFileNameWithoutExtension(file), file);
            if (_cache.Razor(file) is not { } source) continue;
            var project = context.ProjectOf(file);
            if (_defaults.ContainsKey(project)) continue;
            foreach (var route in source.Doc.Tags.Where(t => t.Is("RouteView") || t.Is("AuthorizeRouteView")))
                if (route["DefaultLayout"] is { } value && TypeOf().Match(value) is { Success: true } m)
                {
                    _defaults[project] = LastName(m.Groups[1].Value);
                    break;
                }
        }
    }

    /// <summary>Länkarna för sidor och layouter.</summary>
    public RazorLinks Links { get; }

    public string? LayoutOf(string file, RazorSource page)
    {
        var name = LayoutName(file, page);
        return name is null || name == "null" ? null : _components.GetValueOrDefault(name);
    }

    // En layout kan ligga i en annan layout (@layout i layoutfilen).
    public string? ParentLayout(string layout, RazorSource source) =>
        source.LayoutDirective() is { } parent && parent != "null" ? _components.GetValueOrDefault(parent) : null;

    // Komponenterna filen använder, ett steg djupt: <NavMenu /> är NavMenu.razor.
    public IEnumerable<string> Uses(string file, RazorSource source)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { file };
        foreach (var tag in source.Doc.Tags.Where(t => !t.Closing))
            if (_components.TryGetValue(LastName(tag.Name), out var path) && seen.Add(path)) yield return path;
    }

    // Sidans egen @layout, annars närmaste _Imports.razor uppåt till projektmappen, annars standardlayouten.
    private string? LayoutName(string file, RazorSource page)
    {
        if (page.LayoutDirective() is { } own) return own;
        var project = _context.ProjectOf(file);
        for (var dir = Path.GetDirectoryName(file); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            var imports = Path.Combine(dir, "_Imports.razor");
            if (File.Exists(imports) && _cache.Razor(imports)?.LayoutDirective() is { } name) return name;
            if (string.Equals(dir.TrimEnd(Path.DirectorySeparatorChar), project.TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal)) break;
        }
        return _defaults.GetValueOrDefault(project);
    }

    private static string LastName(string name) => name[(name.LastIndexOf('.') + 1)..];

    [GeneratedRegex(@"typeof\(\s*([\w.]+)\s*\)")]
    private static partial Regex TypeOf();
}
