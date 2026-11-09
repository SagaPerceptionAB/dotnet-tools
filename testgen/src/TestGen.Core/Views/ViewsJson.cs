using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using TestGen.Core.Projects;

namespace TestGen.Core.Views;

/// <summary>views.json som JSON: camelCase, enum som text med bindestreck (<c>razor-page</c>), utan null, LF och ett radslut sist.</summary>
public static class ViewsJson
{
    public const string FileName = "views.json";

    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false) },
        // å, ä och ö skrivs som de är.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(ViewsFile file) => JsonSerializer.Serialize(file, Options).ReplaceLineEndings("\n") + "\n";

    /// <summary>Filen, eller null om den saknas, är för stor, är trasig eller har en annan <c>schemaVersion</c>.</summary>
    public static ViewsFile? TryRead(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > JsonFiles.MaxBytes) return null;
            var file = JsonSerializer.Deserialize<ViewsFile>(File.ReadAllText(path), Options);
            return file is { SchemaVersion: ViewsFile.CurrentSchemaVersion, Apps: not null } ? file : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }
}
