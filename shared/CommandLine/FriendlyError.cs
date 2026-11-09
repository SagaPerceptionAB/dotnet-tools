using System.Text.RegularExpressions;

namespace CommandLine;

/// <summary>Fel i användningen. Meddelandet är redan på svenska och visas som det är.</summary>
public sealed class UsageException(string message, string? tip = null, string? technical = null) : Exception(message)
{
    public const string MissingFileTip = "Kontrollera att sökvägen stämmer och att filen finns. En relativ sökväg utgår från katalogen du kör i.";

    public string? Tip { get; } = tip;
    public string? Technical { get; } = technical;

    /// <summary>"Hittar inte specen: C:\…", när en fil som angetts inte finns.</summary>
    public static UsageException MissingFile(string what, string fullPath) => new($"Hittar inte {what}: {fullPath}", MissingFileTip);
}

/// <summary>Vad som hände, vad man kan göra och teknisk information (från systemet, ofta engelska) samt exitkod.</summary>
/// <param name="Unexpected">Ett fel som inte borde hända: de tekniska detaljerna visas alltid, så att de kan skickas vidare.</param>
public sealed partial record FriendlyError(string What, string Action, string? Technical, int ExitCode = ExitCodes.Usage, bool Unexpected = false)
{
    /// <summary>Text för terminalen: vad som hände, vad du kan göra och (om <paramref name="verbose"/>) tekniska detaljer.</summary>
    public IEnumerable<string> Lines(bool verbose, string prefix = "Kunde inte köra: ")
    {
        yield return prefix + What;
        yield return "Gör så här: " + Action;
        if (Technical is { Length: > 0 } t)
            yield return verbose || Unexpected ? "Tekniskt: " + t : "(Fler tekniska detaljer: kör igen med --verbose)";
    }

    /// <summary>
    /// De fel alla kommandon kan få: fel i användningen, filer som saknas eller inte går att nå, avbrott och oväntade fel.
    /// <paramref name="program"/> är kommandot som hjälpen finns för, t.ex. "dotnet systemmap".
    /// </summary>
    public static FriendlyError From(Exception e, string program)
    {
        var path = PathOf(e) is { } p ? Path.GetFullPath(p) : null;
        return e switch
        {
            UsageException u => new(u.Message, u.Tip ?? $"Kontrollera flaggorna och försök igen. Se: {program} --help", u.Technical),
            FileNotFoundException or DirectoryNotFoundException => new(
                path is null ? "Hittar inte filen." : $"Hittar inte {(e is DirectoryNotFoundException ? "katalogen" : "filen")}: {path}",
                UsageException.MissingFileTip, e.Message),
            UnauthorizedAccessException => new(path is null ? "Saknar behörighet till en fil eller katalog." : $"Saknar behörighet till: {path}",
                "Välj en plats du har rättigheter i, eller be någon ändra rättigheterna.", e.Message),
            OperationCanceledException => new("Körningen avbröts.", "Starta den igen när du är redo.", null, ExitCodes.Interrupted),
            IOException => new("Det gick inte att läsa eller skriva en fil.",
                "Kontrollera att filen inte är öppen i ett annat program och att det finns plats på disken.", e.Message),
            _ => new($"Ett oväntat fel inträffade i {program}.", "Kör igen med --verbose och skicka utskriften till den som underhåller verktyget.",
                $"{e.GetType().Name}: {e.Message}", ExitCodes.Internal, Unexpected: true),
        };
    }

    /// <summary>Sökvägen ett fel gäller: filnamnet i en FileNotFoundException, annars det som står inom apostrofer i meddelandet.</summary>
    public static string? PathOf(Exception e)
    {
        if (e is FileNotFoundException { FileName: { Length: > 0 } f }) return f;
        var m = QuotedPath().Match(e.Message);
        return m.Success ? m.Groups[1].Value : null;
    }

    [GeneratedRegex("'([^']+)'")]
    private static partial Regex QuotedPath();
}

/// <summary>Filer och webbadresser som anges på kommandoraden, t.ex. en spec.</summary>
public static class Sources
{
    public static bool IsUrl(string s) =>
        s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>Texten i filen eller på webbadressen (30 sekunder, sedan ges det upp).</summary>
    public static string ReadText(string pathOrUrl)
    {
        if (!IsUrl(pathOrUrl)) return File.ReadAllText(pathOrUrl);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var response = http.GetAsync(pathOrUrl).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
    }
}
