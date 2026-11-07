using UiTestGen.Model;

namespace UiTestGen.Discovery;

/// <summary>Hittar vyer av ett slag (Blazor, Razor Pages, MVC, SPA) genom att läsa filerna på disk. Kör aldrig koden.</summary>
public interface IViewDiscoverer
{
    /// <summary>Vyerna i katalogerna. <see cref="View.Id"/> behöver inte vara unikt här: <see cref="ViewDiscovery"/> gör det unikt.</summary>
    IEnumerable<View> Discover(DiscoveryContext context);
}

/// <param name="Root">Roten som <see cref="View.Source"/> skrivs relativt till (solutionens mapp).</param>
/// <param name="Directories">Projektmapparna (eller SPA-mappen) att leta i. Fullständiga sökvägar.</param>
/// <param name="Warnings">Varningar att visa för användaren, på svenska, t.ex. en vy vars rutt inte gick att läsa.</param>
public sealed record DiscoveryContext(string Root, IReadOnlyList<string> Directories, List<string> Warnings)
{
    /// <summary>Filerna med ändelsen under katalogerna, utan bin/, obj/, node_modules/ och dolda mappar, i sorterad ordning.</summary>
    public IEnumerable<string> Files(params string[] extensions) => SourceCache.For(this).Files(extensions);

    /// <summary>Projektmappen som filen ligger i (den längsta som passar), eller <see cref="Root"/>.</summary>
    internal string ProjectOf(string file) =>
        Directories
            .Where(d => file.StartsWith(d.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .OrderByDescending(d => d.Length).FirstOrDefault() ?? Root;

    /// <summary>Sökvägen relativt <see cref="Root"/>, med <c>/</c>.</summary>
    public string Relative(string path) => Path.GetRelativePath(Root, path).Replace('\\', '/');
}
