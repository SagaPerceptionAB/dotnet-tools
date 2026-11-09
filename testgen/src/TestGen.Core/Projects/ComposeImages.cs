using YamlDotNet.RepresentationModel;

namespace TestGen.Core.Projects;

/// <summary>Avbildningarna i repots compose-fil: lokala tjänster testgen ersätter med containrar av samma slag.</summary>
public static class ComposeImages
{
    private static readonly string[] FileNames = ["docker-compose.yml", "docker-compose.yaml", "compose.yml", "compose.yaml"];

    /// <summary>
    /// Avbildningarna (<c>image:</c>) i compose-filerna från <paramref name="startDirectory"/> och uppåt till repots rot (där .git ligger), närmaste
    /// fil först. Avbildningar med <c>${...}</c> hoppas över, liksom filer som inte går att läsa.
    /// </summary>
    public static IReadOnlyList<string> Find(string startDirectory)
    {
        var images = new List<string>();
        for (var dir = new DirectoryInfo(Path.GetFullPath(startDirectory)); dir is not null; dir = dir.Parent)
        {
            foreach (var name in FileNames)
                images.AddRange(ImagesIn(Path.Combine(dir.FullName, name)));
            if (Path.Exists(Path.Combine(dir.FullName, ".git"))) break;
        }
        return images;
    }

    /// <summary>
    /// Första avbildningen vars namn (utan version och register-prefix som docker.io) är något av <paramref name="repositories"/>,
    /// t.ex. <c>postgres</c> eller <c>mcr.microsoft.com/mssql/server</c>. Null om ingen passar.
    /// </summary>
    public static string? Match(IEnumerable<string> images, params string[] repositories) =>
        images.FirstOrDefault(image => repositories.Contains(Repository(image), StringComparer.OrdinalIgnoreCase));

    internal static string Repository(string image)
    {
        var name = image.Trim();
        var digest = name.IndexOf('@', StringComparison.Ordinal);
        if (digest >= 0) name = name[..digest];
        var colon = name.LastIndexOf(':');
        if (colon > name.LastIndexOf('/')) name = name[..colon];
        foreach (var prefix in new[] { "docker.io/", "library/" })
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) name = name[prefix.Length..];
        return name;
    }

    private static IEnumerable<string> ImagesIn(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > JsonFiles.MaxBytes) return [];
            var stream = new YamlStream();
            stream.Load(new StringReader(File.ReadAllText(path)));
            if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root
                || !root.Children.TryGetValue(new YamlScalarNode("services"), out var services) || services is not YamlMappingNode map) return [];
            return map.Children.Values.OfType<YamlMappingNode>()
                .Select(service => service.Children.TryGetValue(new YamlScalarNode("image"), out var image) ? (image as YamlScalarNode)?.Value : null)
                .OfType<string>().Where(image => image.Length > 0 && !image.Contains("${", StringComparison.Ordinal)).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or YamlDotNet.Core.YamlException)
        {
            return [];
        }
    }
}
