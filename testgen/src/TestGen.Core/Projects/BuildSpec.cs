using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TestGen.Core.Projects;

/// <summary>
/// Specen som bygget skriver (Microsoft.Extensions.ApiDescription.Server): egenskaperna läses ur csproj utan MSBuild.
/// Standard enligt Microsofts dokumentation: katalogen <c>obj</c> vid projektet och filnamnet <c>&lt;projekt&gt;.json</c>.
/// </summary>
internal static partial class BuildSpec
{
    private const string Package = "Microsoft.Extensions.ApiDescription.Server";

    /// <summary>Fullständig sökväg dit bygget skriver specen, eller null om projektet inte genererar någon.</summary>
    public static string? PathOf(XElement? project, string projectPath, IReadOnlyList<string> packages)
    {
        if (project is null) return null;
        var generate = Property(project, "OpenApiGenerateDocuments", projectPath);
        var enabled = generate is null ? packages.Contains(Package, StringComparer.OrdinalIgnoreCase) : generate.Equals("true", StringComparison.OrdinalIgnoreCase);
        if (!enabled) return null;

        var directory = System.IO.Path.GetDirectoryName(projectPath)!;
        var documents = Property(project, "OpenApiDocumentsDirectory", projectPath);
        var folder = documents is null ? System.IO.Path.Combine(directory, "obj") : System.IO.Path.GetFullPath(documents, directory);
        var options = Property(project, "OpenApiGenerateDocumentsOptions", projectPath);
        var name = options is not null && FileName().Match(options) is { Success: true } m ? m.Groups[1].Value : System.IO.Path.GetFileNameWithoutExtension(projectPath);
        return System.IO.Path.Combine(folder, name + ".json");
    }

    /// <summary>Egenskapens värde med <c>$(MSBuildProjectDirectory)</c> och <c>$(MSBuildThisFileDirectory)</c> ersatta; null om den saknas eller har andra <c>$(...)</c>.</summary>
    private static string? Property(XElement project, string name, string projectPath)
    {
        var value = project.Descendants(name).LastOrDefault()?.Value.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        var directory = System.IO.Path.GetDirectoryName(projectPath)!;
        value = value.Replace("$(MSBuildProjectDirectory)", directory, StringComparison.OrdinalIgnoreCase)
            .Replace("$(MSBuildThisFileDirectory)", directory + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        return value.Contains("$(", StringComparison.Ordinal) ? null : value;
    }

    [GeneratedRegex(@"--file-name\s+(\S+)", RegexOptions.None, 1000)]
    private static partial Regex FileName();
}
