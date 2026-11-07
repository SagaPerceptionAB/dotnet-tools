using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace UiTestGen.Discovery;

/// <summary>Var vyerna ska letas: projektmapparna i en solution, mappen för ett projekt, eller en mapp som den är.</summary>
public static partial class ProjectDirectories
{
    /// <summary>
    /// Roten (solutionens eller projektets mapp, eller mappen själv) och mapparna att leta i, som fullständiga sökvägar.
    /// Läser <c>.slnx</c> och <c>.sln</c> som text och startar inte MSBuild.
    /// </summary>
    public static (string Root, IReadOnlyList<string> Directories) From(string path)
    {
        var full = Path.GetFullPath(path);
        if (Directory.Exists(full)) return (full, [full]);
        if (!File.Exists(full)) throw UsageException.MissingFile("solutionen eller projektet", full);
        var root = Path.GetDirectoryName(full)!;
        var ext = Path.GetExtension(full).ToLowerInvariant();
        IEnumerable<string> projects = ext switch
        {
            ".slnx" => XDocument.Load(full).Descendants("Project").Select(p => (string?)p.Attribute("Path")).OfType<string>(),
            ".sln" => SlnProject().Matches(File.ReadAllText(full)).Select(m => m.Groups[1].Value),
            ".csproj" => [Path.GetFileName(full)],
            _ => throw new UsageException($"{full} är ingen solution, projektfil eller mapp.",
                "Ange en .sln, .slnx, .csproj eller en mapp, t.ex. --solution samples/ShopWeb/ShopWeb.csproj."),
        };
        var dirs = projects
            .Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Select(p => Path.GetDirectoryName(Path.GetFullPath(Path.Combine(root, p.Replace('\\', Path.DirectorySeparatorChar))))!)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return (root, dirs);
    }

    [GeneratedRegex(@"^Project\([^)]*\)\s*=\s*""[^""]*""\s*,\s*""([^""]+)""", RegexOptions.Multiline)]
    private static partial Regex SlnProject();
}
