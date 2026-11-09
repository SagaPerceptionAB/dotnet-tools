using System.Text.RegularExpressions;
using TestGen.Core.Run;

namespace TestGen.Cli.Web;

/// <summary>
/// Webbgränssnittets version av felöversättningen. Texten kommer från <see cref="FriendlyErrors"/> (samma som på
/// kommandoraden), men flaggnamn byts mot fältnamnen i formuläret eftersom ingen skriver flaggor där.
/// </summary>
internal static class WebErrors
{
    /// <summary>Byter kommandoradsflaggor mot de namn som står vid fälten i sidan.</summary>
    public static string FieldNames(string s) => CleanUp(s
        .Replace("--timeout", "Timeout").Replace("--api", "API-adress").Replace("--token", "Token")
        .Replace("--env", "Miljö").Replace("--excel", "Excel").Replace("--seed", "Slumpfrö")
        .Replace("--only", "Bara ett anrop").Replace("--scenario", "Scenarier").Replace("--excel-only", "Bara Excel-testerna").Replace("--report", "rapportplatsen")
        .Replace("--config", "konfigurationsfilen")
        .Replace("Kunde inte läsa OpenAPI-spec", "Kunde inte läsa API-beskrivningen")
        .Replace("specen", "API-beskrivningen"));

    private static readonly Regex DuplicateLabel = new(@"(API-adressen '[^']*') \(API-adress\)", RegexOptions.Compiled);
    private static readonly Regex HelpHint = new(@"\s*Se: testgen( \w+)? --help\.?", RegexOptions.Compiled);

    /// <summary>Tar bort det som bara betyder något på kommandoraden: "Se: testgen --help" och en etikett som sägs två gånger.</summary>
    private static string CleanUp(string s) => HelpHint.Replace(DuplicateLabel.Replace(s, "$1"), "").TrimEnd();

    /// <summary>
    /// Text på korten i resultatlistan (förklaring och tolkning). Samma text som på kommandoraden, men utan flaggor:
    /// 409-tipset pekar på knappen i sidan i stället för --seed.
    /// </summary>
    public static string CardText(string s) => FieldNames(s
        .Replace("Välj ett annat slumpfrö (--seed, eller Slumpfrö under Fler inställningar)", "Tryck på Kör igen med nytt slumpfrö")
        .Replace("(--token, eller fältet Token i webbgränssnittet)", "(fältet Token)"));

    /// <summary>
    /// Fel för sidan: rubrikraden, en punktlista (en rad per fel, t.ex. cellerna i Excel-filen) och varningar som inte stoppade.
    /// Ett Excel-fel med flera rader delas upp så att sidan kan visa dem som lista i stället för en lång rad.
    /// </summary>
    public sealed record Failure(string What, string Action, string? Technical, IReadOnlyList<string> Items, IReadOnlyList<string> Warnings, int ExitCode);

    public static Failure Describe(Exception e, ErrorContext? ctx = null)
    {
        var f = Translate(e, ctx);
        if (e is ExcelValidationException x)
        {
            var first = x.Message.Split('\n')[0].TrimEnd('\r');
            return new(FieldNames(first), f.Action, f.Technical,
                x.Errors.Select(i => FieldNames(i.Text)).ToList(), x.Warnings.Select(i => FieldNames(i.Text)).ToList(), f.ExitCode);
        }
        return new(f.What, f.Action, f.Technical, Array.Empty<string>(), Array.Empty<string>(), f.ExitCode);
    }

    public static FriendlyError Translate(Exception e, ErrorContext? ctx = null)
    {
        var f = FriendlyErrors.Translate(e, ctx);
        // Tipset om --verbose gäller bara kommandoraden; i sidan finns de tekniska detaljerna redan i felrutan.
        var action = f.Action.StartsWith("Kör igen med --verbose")
            ? "Försök igen. Om felet kvarstår: öppna Tekniska detaljer nedan och skicka texten till den som underhåller verktyget."
            : f.Action;
        return f with { What = FieldNames(f.What), Action = FieldNames(action) };
    }
}
