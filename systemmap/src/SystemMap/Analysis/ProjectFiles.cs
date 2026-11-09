using System.Text.Json;
using System.Text.RegularExpressions;

namespace SystemMap;

/// <summary>Filerna bredvid koden som säger var en app lyssnar: <c>appsettings*.json</c> och <c>Properties/launchSettings.json</c>.</summary>
internal static partial class ProjectFiles
{
    private const long MaxBytes = 1_000_000;

    /// <summary>Värdet för en nyckel som <c>ShopApi:BaseUrl</c> i projektets <c>appsettings.json</c>, annars i någon annan <c>appsettings*.json</c>.</summary>
    public static string? ConfigValue(string projectDir, string key)
    {
        if (!Directory.Exists(projectDir)) return null;
        var files = Directory.GetFiles(projectDir, "appsettings*.json")
            .OrderBy(f => Path.GetFileName(f).Equals("appsettings.json", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(f => f, StringComparer.Ordinal);
        foreach (var file in files)
            if (Read(file) is { } json)
                using (json)
                {
                    var node = json.RootElement;
                    foreach (var part in key.Split(':'))
                        if (node.ValueKind != JsonValueKind.Object || !TryGet(node, part, out node)) { node = default; break; }
                    if (node.ValueKind == JsonValueKind.String) return node.GetString();
                }
        return null;
    }

    /// <summary>Portarna i <c>applicationUrl</c> i launchSettings.json.</summary>
    public static IEnumerable<int> LaunchPorts(string projectDir)
    {
        if (Read(Path.Combine(projectDir, "Properties", "launchSettings.json")) is not { } json) yield break;
        using (json)
        {
            if (!json.RootElement.TryGetProperty("profiles", out var profiles) || profiles.ValueKind != JsonValueKind.Object) yield break;
            foreach (var profile in profiles.EnumerateObject())
                if (profile.Value.ValueKind == JsonValueKind.Object && profile.Value.TryGetProperty("applicationUrl", out var urls) && urls.ValueKind == JsonValueKind.String)
                    foreach (var url in urls.GetString()!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        if (LocalPort(url, listening: true) is { } port) yield return port;
        }
    }

    /// <summary>
    /// Porten i en adress till den här datorn (<c>http://localhost:5080</c>), annars null. En adress att lyssna på får också ha
    /// <c>*</c>, <c>+</c> eller <c>0.0.0.0</c> som värd.
    /// </summary>
    public static int? LocalPort(string url, bool listening = false)
    {
        var match = Address().Match(url);
        if (!match.Success || !int.TryParse(match.Groups[2].Value, out var port) || port is < 1 or > 65535) return null;
        var host = match.Groups[1].Value.ToLowerInvariant();
        var local = host is "localhost" or "127.0.0.1" or "[::1]" || (listening && host is "*" or "+" or "0.0.0.0" or "[::]");
        return local ? port : null;
    }

    private static JsonDocument? Read(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaxBytes) return null;
            return JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 32 });
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool TryGet(JsonElement parent, string name, out JsonElement value)
    {
        foreach (var property in parent.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
        value = default;
        return false;
    }

    [GeneratedRegex(@"^https?://(\[[^\]]+\]|[^/:]+):(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex Address();
}
