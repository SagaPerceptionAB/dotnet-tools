using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UiTestGen.Model;

/// <summary>Vykatalogen som JSON (vykatalog.json): camelCase, indraget, enum som text och utan null-värden.</summary>
public static class ViewCatalogJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // å, ä och ö skrivs som de är, inte som å.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(ViewCatalog catalog) => JsonSerializer.Serialize(catalog, Options).ReplaceLineEndings("\n") + "\n";

    public static ViewCatalog Deserialize(string json) =>
        JsonSerializer.Deserialize<ViewCatalog>(json, Options) ?? throw new UsageException("Vykatalogen är tom.", "Skapa den med uitestgen discover.");

    public static ViewCatalog Read(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw UsageException.MissingFile("vykatalogen", full);
        try { return Deserialize(File.ReadAllText(full)); }
        catch (JsonException e) { throw new UsageException($"{full} är ingen giltig vykatalog: {e.Message}", "Skapa den igen med uitestgen discover."); }
    }

    public static void Write(ViewCatalog catalog, string path)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, Serialize(catalog));
    }
}
