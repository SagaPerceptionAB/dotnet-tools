using Microsoft.OpenApi.Readers;

namespace SystemMap;

/// <summary>Läser anropen (metod, sökväg och operationId) ur en OpenAPI-spec i en fil eller på en webbadress.</summary>
public static class SpecReader
{
    public static IReadOnlyList<ApiOperation> Load(string pathOrUrl)
    {
        var url = Sources.IsUrl(pathOrUrl);
        var shown = url ? pathOrUrl : Path.GetFullPath(pathOrUrl);
        if (!url && !File.Exists(shown)) throw UsageException.MissingFile("specen", shown);
        string text;
        try { text = Sources.ReadText(pathOrUrl); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            throw new UsageException($"Kunde inte läsa specen {shown}.",
                url ? "Kontrollera adressen och nätverket, eller spara specen som fil och ange filen." : "Kontrollera att filen går att läsa.", e.Message);
        }

        var doc = new OpenApiStringReader().Read(text, out var diagnostic);
        if (doc is null || diagnostic.Errors.Count > 0)
            throw new UsageException($"{shown} är ingen giltig OpenAPI-spec.",
                "Kontrollera specen i ett OpenAPI-verktyg, t.ex. editor.swagger.io.", string.Join("; ", diagnostic.Errors.Select(e => e.ToString())));

        return doc.Paths.SelectMany(p => p.Value.Operations.Select(o => new ApiOperation(
                string.IsNullOrWhiteSpace(o.Value.OperationId) ? null : o.Value.OperationId, o.Key.ToString().ToUpperInvariant(), p.Key)))
            .ToList();
    }
}
