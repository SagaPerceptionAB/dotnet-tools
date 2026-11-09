using TestGen.Core;

namespace SagaPerception.TestGen.NUnit;

/// <summary>Hämtar den spec som en code-first-app serverar och jämför den med den incheckade.</summary>
internal static class SpecCheck
{
    /// <summary>Orsaken när jämförelsen hoppas över: specen skrivs av bygget, så den stämmer med appen per definition.</summary>
    public const string GeneratedAtBuild = "Specen skrivs av bygget, så det finns inget att jämföra med.";

    public static readonly string[] Paths = ["/openapi/v1.json", "/swagger/v1/swagger.json"];

    /// <summary>Appens spec som text, eller null om den inte serverar någon på de vanliga adresserna.</summary>
    public static async Task<string?> FetchAsync(HttpMessageHandler handler, CancellationToken ct)
    {
        using var client = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(TestGenTarget.InProcessUrl) };
        foreach (var path in Paths)
        {
            using var response = await client.GetAsync(path, ct);
            if (response.IsSuccessStatusCode) return await response.Content.ReadAsStringAsync(ct);
        }
        return null;
    }

    /// <summary>Skillnaderna mellan den incheckade specen (fil) och den som appen serverar (JSON-text). Tom lista om de stämmer.</summary>
    public static IReadOnlyList<string> Compare(string checkedInSpec, string servedJson)
    {
        var temporary = Path.Combine(Path.GetTempPath(), $"testgen-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(temporary, servedJson);
            return SpecComparison.Diff(OpenApiLoader.LoadSpec(checkedInSpec), OpenApiLoader.LoadSpec(temporary));
        }
        finally { File.Delete(temporary); }
    }
}
