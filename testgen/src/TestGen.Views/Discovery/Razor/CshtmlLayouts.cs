using System.Runtime.CompilerServices;

namespace TestGen.Views.Discovery.Razor;

/// <summary>
/// Layouter och partials för Razor Pages- och MVC-vyer, för <see cref="RazorLinks"/>. Layouten kommer från <c>Layout = "X";</c> i vyn,
/// annars i närmaste <c>_ViewStart.cshtml</c> uppåt i mapparna, och i en layout bara från dess egen <c>Layout = "X";</c>. En partial är
/// filen i <c>&lt;partial name="..." /&gt;</c>. Namnet letas i <c>Pages/Shared</c>, <c>Views/Shared</c> och <c>Pages</c>. Kör aldrig koden.
/// </summary>
internal sealed class CshtmlLayouts : IRazorProject
{
    private static readonly string[] SharedFolders = ["Pages/Shared", "Views/Shared", "Pages"];
    private static readonly ConditionalWeakTable<DiscoveryContext, CshtmlLayouts> Instances = new();

    private readonly DiscoveryContext _context;
    private readonly SourceCache _cache;
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);

    private CshtmlLayouts(DiscoveryContext context)
    {
        _context = context;
        _cache = SourceCache.For(context);
        Links = new RazorLinks(context, this);
        foreach (var file in context.Files(".cshtml")) _files.TryAdd(file, file);
    }

    /// <summary>Layouterna för alla vyer i körningen. Delas av Razor Pages och MVC, och därmed länkarna i layouterna.</summary>
    public static CshtmlLayouts For(DiscoveryContext context) => Instances.GetValue(context, c => new CshtmlLayouts(c));

    /// <summary>Länkarna för vyer och layouter.</summary>
    public RazorLinks Links { get; }

    public string? LayoutOf(string viewFile, RazorSource view)
    {
        var project = _context.ProjectOf(viewFile);
        var (assigned, name) = view.LayoutAssignment();
        if (!assigned) (_, name) = ViewStart(project, viewFile);
        return name is null ? null : Find(project, viewFile, name);
    }

    public string? ParentLayout(string layout, RazorSource source) =>
        source.LayoutAssignment() is (true, { } parent) ? Find(_context.ProjectOf(layout), layout, parent) : null;

    public IEnumerable<string> Uses(string file, RazorSource source)
    {
        var project = _context.ProjectOf(file);
        var seen = new HashSet<string>(StringComparer.Ordinal) { file };
        foreach (var tag in source.Doc.Tags.Where(t => t.Is("partial")))
            if (tag["name"] is { } partialName && !partialName.Contains('@') && Find(project, file, partialName) is { } partial && seen.Add(partial))
                yield return partial;
    }

    // Tilldelningarna i _ViewStart.cshtml från projektmappen ned till vyns mapp: den innersta sista tilldelningen gäller.
    private (bool Assigned, string? Name) ViewStart(string project, string viewFile)
    {
        var result = (Assigned: false, Name: (string?)null);
        var chain = new List<string>();
        for (var dir = Path.GetDirectoryName(viewFile); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            chain.Add(dir);
            if (SamePath(dir, project)) break;
        }
        chain.Reverse();
        foreach (var dir in chain)
            if (_files.TryGetValue(Path.Combine(dir, "_ViewStart.cshtml"), out var file) && _cache.Razor(file) is { } source && source.LayoutAssignment() is (true, var name))
                result = (true, name);
        return result;
    }

    // Namnet som i ASP.NET: en sökväg (~/ eller /) räknas från projektmappen, annars letas filen bredvid den som använder den
    // och i delade mappar (i området först, om filen ligger under Areas/X).
    private string? Find(string project, string from, string name)
    {
        var file = name.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase) ? name : name + ".cshtml";
        IEnumerable<string> candidates;
        if (name.StartsWith("~/", StringComparison.Ordinal) || name.StartsWith('/'))
            candidates = [Path.Combine(project, file.TrimStart('~').TrimStart('/'))];
        else
        {
            var bases = new List<string>();
            var relative = Path.GetRelativePath(project, from).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (relative.Length > 2 && relative[0].Equals("Areas", StringComparison.OrdinalIgnoreCase)) bases.Add(Path.Combine(project, relative[0], relative[1]));
            bases.Add(project);
            candidates = bases.SelectMany(b => SharedFolders.Select(f => Path.Combine(b, f, file)))
                .Prepend(Path.Combine(Path.GetDirectoryName(from)!, file));
        }
        foreach (var candidate in candidates)
            if (_files.TryGetValue(Path.GetFullPath(candidate.Replace('/', Path.DirectorySeparatorChar)), out var actual)) return actual;
        return null;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(a.TrimEnd(Path.DirectorySeparatorChar), b.TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal);
}
