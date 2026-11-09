using System.Net.Http;

namespace TestGen.Core.Run;

public enum ApiProblem { Refused, NameNotFound, Timeout, Tls, Other }

/// <summary>API:et som ska testas gick inte att nå (exitkod 3).</summary>
public sealed class ApiUnreachableException(string api, ApiProblem problem, string? technical, string? message = null)
    : Exception(message ?? Describe(api, problem))
{
    public string Api { get; } = api;
    public ApiProblem Problem { get; } = problem;
    public string? Technical { get; } = technical;

    private static string Describe(string api, ApiProblem p) => $"Kan inte nå API:et på {api}. " + p switch
    {
        ApiProblem.Refused => "Ingen svarar på den adressen – är det startat?",
        ApiProblem.NameNotFound => "Servernamnet i adressen går inte att slå upp.",
        ApiProblem.Timeout => "Det svarade inte i tid.",
        ApiProblem.Tls => "Den säkra anslutningen (https) kunde inte upprättas.",
        _ => "Anslutningen misslyckades.",
    };

    public string Tip => Problem switch
    {
        ApiProblem.NameNotFound => "Kontrollera stavningen av servernamnet i API-adressen och att du är ansluten till rätt nätverk.",
        ApiProblem.Timeout => "Kontrollera att systemet körs och är nåbart. Är det långsamt kan du öka tiden med --timeout (sekunder).",
        ApiProblem.Tls => "Kontrollera att adressen ska vara https, att certifikatet är giltigt, eller prova http://.",
        _ => "Kontrollera att systemet som ska testas är startat och att API-adressen stämmer (port och http/https). Du kan också välja en annan miljö eller ändra adressen och köra igen.",
    };
}

/// <summary>Specens webbadress svarar inte (nätverksfel eller tidsgräns). Exitkod 3, till skillnad från en spec som svarar men har fel innehåll (exitkod 2).</summary>
public sealed class SpecUnreachableException(string message, string tip, string? technical) : Exception(message)
{
    public string Tip { get; } = tip;
    public string? Technical { get; } = technical;
}

public enum SourceKind { Spec, Excel, Config, Report }

/// <summary>En fil eller adress (spec, Excel, config) som finns men inte går att läsa.</summary>
public sealed class SourceException(SourceKind kind, string location, string message, string tip, string? technical)
    : Exception(message)
{
    public SourceKind Kind { get; } = kind;
    public string Location { get; } = location;
    public string Tip { get; } = tip;
    public string? Technical { get; } = technical;
}

/// <summary>En fil (spec, Excel, config) som inte finns. Sökvägen är absolut.</summary>
public sealed class MissingSourceException(SourceKind kind, string fullPath)
    : FileNotFoundException($"Hittar inte {FriendlyErrors.Label(kind)}: {fullPath}", fullPath)
{
    public SourceKind Kind { get; } = kind;
}

/// <summary>Vilka filer och adresser körningen gäller; används för att veta vad ett "hittar inte" syftar på.</summary>
public sealed class ErrorContext
{
    public string? Spec { get; set; }
    public string? Excel { get; set; }
    public string? Config { get; set; }
    public string? Report { get; set; }
    public string? Api { get; set; }
}

/// <summary>
/// Den gemensamma översättaren från undantag till klartext på enkel svenska. Används av kommandoraden och
/// webbgränssnittet så att samma fel ger samma text.
/// </summary>
public static class FriendlyErrors
{
    public static string Label(SourceKind k) => k switch
    {
        SourceKind.Spec => "specen",
        SourceKind.Excel => "Excel-filen",
        SourceKind.Config => "konfigurationsfilen",
        _ => "rapporten",
    };

    // --- Läsning av källor ---------------------------------------------------------------

    /// <summary>
    /// Kör <paramref name="read"/>; alla systemfel (korrupt fil, trasig YAML, nätfel ...) blir svenska
    /// <see cref="SourceException"/>/<see cref="MissingSourceException"/> med absolut sökväg.
    /// </summary>
    public static T Guard<T>(SourceKind kind, string location, Func<T> read)
    {
        try { return read(); }
        catch (Exception e) when (e is not (UsageException or SourceException or MissingSourceException or ApiUnreachableException or SpecUnreachableException or ExcelValidationException)
                                  && (e is not OperationCanceledException || e is TaskCanceledException))
        {
            throw Wrap(kind, location, e);
        }
    }

    public static Exception Wrap(SourceKind kind, string location, Exception e)
    {
        var url = Sources.IsUrl(location);
        var shown = url ? location : Path.GetFullPath(location);
        var label = Label(kind);
        switch (e)
        {
            case FileNotFoundException or DirectoryNotFoundException:
                return new MissingSourceException(kind, FriendlyError.PathOf(e) ?? shown);
            case UnauthorizedAccessException:
                return new SourceException(kind, location, $"Saknar behörighet att läsa {label}: {shown}",
                    "Kontrollera filens rättigheter och att den inte är låst av ett annat program.", e.Message);
        }

        switch (kind)
        {
            case SourceKind.Spec:
                {
                    var (reason, tip) = SpecReason(e, url);
                    // Adressen svarar inte alls (nätverksfel, tidsgräns): det är miljön, inte specen, och ger exitkod 3 (inte 2).
                    if (url && (e is TaskCanceledException || e is HttpRequestException { StatusCode: null }))
                        return new SpecUnreachableException($"Kan inte hämta OpenAPI-spec från {shown}: {reason}", tip, e.Message);
                    return new SourceException(kind, location, $"Kunde inte läsa OpenAPI-spec '{shown}': {reason}", tip, e.Message);
                }
            case SourceKind.Excel:
                return new SourceException(kind, location, $"Filen går inte att läsa som en Excel-arbetsbok (.xlsx): {shown}",
                    "Välj en .xlsx-fil, t.ex. en mall skapad med 'testgen template'. Filer från andra program måste sparas som .xlsx.", e.Message);
            default:
                {
                    var where = e is YamlDotNet.Core.YamlException y ? $" (rad {y.Start.Line})" : "";
                    return new SourceException(kind, location, $"Konfigurationsfilen går inte att läsa{where}: {shown}",
                        "Kontrollera att filen är giltig YAML (indrag med mellanslag, kolon följt av mellanslag), eller kör utan --config.", e.Message);
                }
        }
    }

    private static (string Reason, string Tip) SpecReason(Exception e, bool url)
    {
        const string generic = "Kontrollera att filen är en OpenAPI-spec (.yaml eller .json); den ska börja med t.ex. 'openapi: 3.0.1'.";
        switch (e)
        {
            case HttpRequestException h when h.StatusCode is { } code:
                return ($"servern svarade {(int)code} ({((int)code == 404 ? "sidan finns inte" : "begäran nekades eller misslyckades")}).",
                    "Öppna adressen i webbläsaren och kontrollera att den visar specen. Kräver den inloggning kan du hämta filen och ange sökvägen i stället.");
            case HttpRequestException h when h.HttpRequestError == HttpRequestError.NameResolutionError:
                return ("servernamnet i adressen går inte att slå upp.", "Kontrollera stavningen av adressen och din nätverksanslutning.");
            case HttpRequestException h when h.HttpRequestError == HttpRequestError.ConnectionError:
                return ("ingen svarar på adressen.", "Kontrollera adressen (port och http/https) och att servern är igång.");
            case HttpRequestException:
                return ("det gick inte att hämta adressen.", "Kontrollera adressen och din nätverksanslutning, eller hämta filen och ange sökvägen i stället.");
            case TaskCanceledException:
                return ("servern svarade inte i tid.", "Kontrollera adressen och din nätverksanslutning.");
            case Microsoft.OpenApi.OpenApiUnsupportedSpecVersionException:
                return ("versionen saknas eller stöds inte (OpenAPI 2 och 3 stöds).", generic);
            default:
                return (url ? "adressen visar ingen giltig OpenAPI-spec." : "innehållet är ingen giltig OpenAPI-spec.", generic);
        }
    }

    // --- Översättning --------------------------------------------------------------------

    /// <summary>testgens egna fel, och filer som körningen gäller (<paramref name="ctx"/>). Resten som för alla kommandon (<see cref="FriendlyError.From"/>).</summary>
    public static FriendlyError Translate(Exception e, ErrorContext? ctx = null)
    {
        ctx ??= new ErrorContext();
        if (IsOpenApiVersionConflict(e))
            return new("Appen och testgen använder olika huvudversioner av Microsoft.OpenApi, och båda kan inte laddas i samma testprocess.",
                "Låt appen använda Microsoft.OpenApi 2: AddOpenApi() i .NET 10, eller Swashbuckle 10 eller senare.", Describe(e));
        switch (e)
        {
            case UsageException u:
                return new(u.Message, u.Tip ?? DefaultTip, u.Technical);
            case ExcelValidationException x:
                return new(x.Message, ExcelValidationException.Action(x.FileName), x.Technical);
            case ApiUnreachableException a:
                return new(a.Message, a.Tip, a.Technical, ExitCodes.Unreachable);
            case SpecUnreachableException su:
                return new(su.Message, su.Tip, su.Technical, ExitCodes.Unreachable);
            case MissingSourceException m:
                return new(m.Message, UsageException.MissingFileTip, null);
            case SourceException s:
                return new(s.Message, s.Tip, s.Technical);
            case FileNotFoundException or DirectoryNotFoundException when FriendlyError.PathOf(e) is var p && (p is not null || ctx.Report is not null):
                {
                    var full = p is null ? null : Path.GetFullPath(p);
                    var kind = KindOf(full, ctx);
                    if (kind == SourceKind.Report || (kind is null && ctx.Report is not null && e is DirectoryNotFoundException))
                        return new($"Rapporten kan inte skrivas: katalogen finns inte och går inte att skapa ({Path.GetDirectoryName(Path.GetFullPath(ctx.Report ?? full ?? "."))}).",
                            "Välj en annan plats för --report, t.ex. en katalog som du har skrivrättigheter i.", e.Message);
                    if (kind is { } k) return new($"Hittar inte {Label(k)}: {full}", UsageException.MissingFileTip, e.Message);
                    return FriendlyError.From(e, "testgen");
                }
            case HttpRequestException h:
                return new($"Anslutningen misslyckades{(ctx.Api is null ? "" : " mot " + ctx.Api)}.",
                    "Kontrollera adressen och din nätverksanslutning.", h.Message, ExitCodes.Unreachable);
            case ArgumentException { ParamName: null } or InvalidOperationException:
                return new(e.Message, TipFor(e.Message), null);
            default:
                return FriendlyError.From(e, "testgen");
        }
    }

    /// <summary>Ett typ-, metod- eller filladdningsfel som gäller Microsoft.OpenApi, direkt eller längre ner i felkedjan: appen har en annan huvudversion än testgen.</summary>
    public static bool IsOpenApiVersionConflict(Exception e) => Conflicting(e) is not null;

    private static Exception? Conflicting(Exception? e) => e switch
    {
        null => null,
        TypeLoadException or MissingMemberException or FileLoadException or BadImageFormatException when Mentions(e) => e,
        AggregateException a => a.InnerExceptions.Select(Conflicting).FirstOrDefault(x => x is not null),
        _ => Conflicting(e.InnerException),
    };

    private static bool Mentions(Exception e) =>
        e.Message.Contains("Microsoft.OpenApi", StringComparison.Ordinal) || (e as FileLoadException)?.FileName?.Contains("Microsoft.OpenApi", StringComparison.Ordinal) == true;

    private static string Describe(Exception e) =>
        Conflicting(e)! is var c && c is FileLoadException { FileName: { } file } ? $"{c.GetType().Name}: {c.Message} ({file})" : $"{c!.GetType().Name}: {c.Message}";

    public const string DefaultTip = "Kontrollera inställningarna och försök igen. Se: testgen --help";

    private static string TipFor(string msg)
    {
        if (msg.StartsWith("Miljövariabeln")) return "Sätt variabeln i skalet, eller som hemlighet i CI-systemet. I webbgränssnittet: sätt den innan du startar testgen web, eller skriv värdet i fältet Token.";
        if (msg.StartsWith("Okänd miljö")) return "Välj en av miljöerna som visas, eller ange API-adressen själv.";
        if (msg.StartsWith("--only:")) return "Välj ett av anropen som visas.";
        return DefaultTip;
    }

    private static SourceKind? KindOf(string? full, ErrorContext ctx)
    {
        if (full is null) return null;
        bool Same(string? p) => p is not null && !Sources.IsUrl(p) && Path.GetFullPath(p) == full;
        if (Same(ctx.Spec)) return SourceKind.Spec;
        if (Same(ctx.Excel)) return SourceKind.Excel;
        if (Same(ctx.Config)) return SourceKind.Config;
        if (Same(ctx.Report)) return SourceKind.Report;
        return null;
    }
}
