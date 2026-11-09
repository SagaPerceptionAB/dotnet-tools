using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace CommandLine;

/// <summary>Projekten i en solution (<c>.slnx</c> som XML, <c>.sln</c> som text) eller ett enskilt <c>.csproj</c>. Ingen MSBuild.</summary>
public static partial class SolutionFile
{
    /// <summary>Fulla sökvägar till de <c>.csproj</c> som finns på disk, sorterade. Projekt som pekas ut men saknas hoppas över.</summary>
    public static IReadOnlyList<string> ProjectPaths(string solutionPath)
    {
        var full = Path.GetFullPath(solutionPath);
        var dir = Path.GetDirectoryName(full)!;
        var extension = Path.GetExtension(full);
        IEnumerable<string> relative = extension.ToLowerInvariant() switch
        {
            ".csproj" => [Path.GetFileName(full)],
            ".slnx" => FromSlnx(full),
            ".sln" => FromSln(full),
            _ => throw new UsageException($"{full} är ingen solution eller projektfil.", "Ange en .sln-, .slnx- eller .csproj-fil."),
        };
        return relative
            .Select(p => Path.GetFullPath(Path.Combine(dir, p.Replace('\\', '/'))))
            .Where(p => p.EndsWith(SolutionFinder.ProjectExtension, StringComparison.OrdinalIgnoreCase) && File.Exists(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> FromSlnx(string path)
    {
        var document = XmlFiles.Load(path);
        return document?.Descendants("Project").Select(p => (string?)p.Attribute("Path")).OfType<string>().ToList() ?? [];
    }

    private static List<string> FromSln(string path)
    {
        if (!XmlFiles.IsSmall(path)) return [];
        return SlnProject().Matches(File.ReadAllText(path)).Select(m => m.Groups[1].Value).ToList();
    }

    [GeneratedRegex("""^\s*Project\("\{[^}]+\}"\)\s*=\s*"[^"]*"\s*,\s*"([^"]+)"\s*,""", RegexOptions.Multiline)]
    private static partial Regex SlnProject();
}

/// <summary>Läser XML ur repot som opålitlig indata: storleksgräns och ingen DTD.</summary>
public static class XmlFiles
{
    public const long MaxBytes = 2 * 1024 * 1024;

    public static bool IsSmall(string path) => new FileInfo(path).Length <= MaxBytes;

    /// <summary>XML-filen, eller null om den är för stor eller inte går att läsa.</summary>
    public static XDocument? Load(string path)
    {
        if (!IsSmall(path)) return null;
        try
        {
            using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            return XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }
    }
}
