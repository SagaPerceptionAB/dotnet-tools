using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TestGen.Core.Run;

/// <summary>Det som behövs för att förklara ett Excel-steg i klartext.</summary>
/// <param name="Scenario">Scenariots namn i bladet Tester.</param>
/// <param name="Number">Stegets nummer i scenariot (börjar på 1).</param>
/// <param name="ExpectedFields">Fält som stegets "Förväntat svar" talar om (namn, utan värden).</param>
/// <param name="StatusCell">Cellen för "Förväntad status" i Excel-filen, t.ex. "Tester!E2" (null om okänd).</param>
/// <param name="ExpectedCell">Cellen för "Förväntat svar" i Excel-filen, t.ex. "Tester!F2" (null om okänd).</param>
public sealed record ExcelStepInfo(string Scenario, int Number, IReadOnlyList<string> ExpectedFields, string? StatusCell = null, string? ExpectedCell = null);

/// <summary>
/// Allt som beskriver ett test (före körning): vilket anrop, vad som förväntas och, för genererade fall, vilken regel
/// som prövas. Skapas av <see cref="TestRunner"/> men kan också byggas för hand (t.ex. i ett webbgränssnitt eller i tester).
/// </summary>
/// <param name="Id">T-001 ...</param>
/// <param name="Group">Gruppen i rapporten, t.ex. "Genererade tester".</param>
/// <param name="Call">Teknisk anropsrad, t.ex. "POST /orders".</param>
/// <param name="Method">HTTP-metoden (kan vara tom om anropet är okänt).</param>
/// <param name="OperationId">operationId i specen.</param>
/// <param name="Summary">Operationens <c>summary</c> i specen, annars null.</param>
/// <param name="Description">Den korta tekniska beskrivningen som visas i terminalen ("quantity under minimum").</param>
/// <param name="ExpectedStatus">Statuskoder som testet räknar som rätt.</param>
/// <param name="Case">Metadata för genererade fall (regel, fält, gränsvärde, skickat värde); null för Excel-steg.</param>
/// <param name="Step">Metadata för Excel-steg; null för genererade fall.</param>
public sealed record TestInfo(
    string Id,
    string Group,
    string Call,
    string Method,
    string OperationId,
    string? Summary,
    string Description,
    IReadOnlyList<StatusExpectation> ExpectedStatus,
    CaseMeta? Case = null,
    ExcelStepInfo? Step = null)
{
    /// <summary>Testets stabila nyckel när den är given på förhand (UI-tester, <c>ui:/products:smoke</c>). Annars byggs den ur operation, regel och fält (<see cref="TestKeys"/>).</summary>
    public string? Key { get; init; }

    /// <summary>Projektet och miljön som platshållarna <c>&lt;api&gt;</c> och <c>&lt;miljö&gt;</c> i vägledningen byts mot, när motorn känner dem.</summary>
    public PlainNames? Names { get; init; }

    /// <summary>Operationens namn i klartext: summary om den finns, annars operationId.</summary>
    public string OperationName => string.IsNullOrWhiteSpace(Summary) ? OperationId : Summary;

    /// <summary>Minimalt TestInfo för ett test där bara den tekniska raden är känd.</summary>
    public static TestInfo Basic(string id, string group, string call, string description) =>
        new(id, group, call, call.Split(' ')[0], "", null, description, Array.Empty<StatusExpectation>());
}

/// <summary>Namnen som platshållarna i vägledningen (<c>apis.&lt;api&gt;.bindings</c>, <c>environments.&lt;miljö&gt;...</c>) byts mot. Saknas ett namn står platshållaren kvar.</summary>
public sealed record PlainNames(string? Project, string? Environment, bool InProcess = false)
{
    public static string Fill(string text, PlainNames? names) =>
        names is null ? text : text.Replace("<api>", names.Project ?? "<api>", StringComparison.Ordinal).Replace("<miljö>", names.Environment ?? "<miljö>", StringComparison.Ordinal);
}

/// <summary>Ett tests rubrik och förklaring i klartext (svenska). Hemligheter är redan maskade.</summary>
/// <param name="Title">Kort rubrik, t.ex. "Skapa order – quantity för lågt (under minimum)".</param>
/// <param name="Explanation">2–3 meningar om vad som skickades och vad som hände.</param>
/// <param name="Hint">Valfri tolkning ("Det tyder på ett fel i systemet, inte i testet."), annars null.</param>
/// <param name="Cause">Orsaken till FEL/HOPPAD (se <see cref="PlainLanguage.Cause"/>), null för OK.</param>
public sealed record PlainText(string Title, string Explanation, string? Hint, FailureCause? Cause = null);

/// <summary>
/// Översätter ett testresultat till klartext för personer som inte kan HTTP. Ren funktion utan I/O:
/// samma text används i terminalen (<c>--explain</c>), HTML-rapporten, Excel-rapporten och webbgränssnittet.
/// All text går genom <see cref="HeaderMask.Scrub"/>.
/// </summary>
public static partial class PlainLanguage
{
    /// <summary>Längsta visade värde (tecken) i en mening.</summary>
    public const int MaxValueChars = 60;

    /// <summary>Nyckeln för token i testgen.yaml.</summary>
    private const string TokenKey = "environments.<miljö>.apis.<api>.token";

    // --- Statuskoder ---------------------------------------------------------------------

    /// <summary>Kort betydelse på svenska, t.ex. 201 -> "skapad", 500 -> "internt fel i systemet".</summary>
    public static string StatusMeaning(int status) => status switch
    {
        200 => "OK",
        201 => "skapad",
        204 => "klart utan innehåll",
        400 => "sa nej till uppgifterna",
        401 => "inte inloggad",
        403 => "saknar behörighet",
        404 => "fanns inte",
        409 => "fanns redan / krock",
        422 => "sa nej till uppgifterna",
        429 => "för många anrop",
        500 => "internt fel i systemet",
        502 => "systemet svarar inte",
        503 => "systemet är inte tillgängligt",
        504 => "systemet svarar inte i tid",
        >= 100 and < 200 => "information",
        >= 200 and < 300 => "lyckades",
        >= 300 and < 400 => "hänvisade vidare",
        >= 400 and < 500 => "fel i anropet",
        >= 500 and < 600 => "fel i systemet",
        _ => "okänd statuskod",
    };

    /// <summary>"201 – skapad".</summary>
    public static string StatusText(int status) => $"{status} – {StatusMeaning(status)}";

    // --- Huvud-API -----------------------------------------------------------------------

    /// <summary>
    /// Rubrik, förklaring och tolkning för ett test. <paramref name="mask"/> döljer hemliga värden
    /// (utan mask används bara de alltid hemliga headers, vilket inte påverkar fri text).
    /// </summary>
    public static PlainText Explain(TestInfo test, CaseResult result, HeaderMask? mask = null)
    {
        mask ??= HeaderMask.Default;
        var title = TitleOf(test);
        var (explanation, hint) = Body(test, result);
        explanation += SpecAdditions(test, result);
        var names = test.Names;
        return new PlainText(mask.Scrub(PlainNames.Fill(title, names)), mask.Scrub(PlainNames.Fill(explanation, names)),
            hint is null ? null : mask.Scrub(PlainNames.Fill(hint, names)), Cause(test, result));
    }

    /// <summary>
    /// Svarskontrollens extra information: avvikelser från specen när statusen redan var fel. Varningarna (som aldrig ändrar utfallet) visas separat, se CaseResult.Warnings.
    /// Tom text om det inte finns något.
    /// </summary>
    private static string SpecAdditions(TestInfo test, CaseResult r)
    {
        var sb = new StringBuilder();
        var statusWrong = !r.Ok && !r.Skipped && r.Status is { } st && test.ExpectedStatus.Count > 0 && !test.ExpectedStatus.Any(e => e.Matches(st));
        if (statusWrong && r.Details.Count > 0) sb.Append(' ').Append(DetailSentences(r.Details));
        return sb.ToString();
    }

    /// <summary>Rubriken ensam: "Skapa order – quantity för lågt (under minimum)" eller "Stamkund, steg 2: Skapa produkt".</summary>
    public static string TitleOf(TestInfo test)
    {
        if (test.Step is { } step) return $"{step.Scenario}, steg {step.Number}: {test.OperationName}";
        if (test.Case is { } c) return $"{test.OperationName} – {RulePhrase(c)}";
        return test.OperationId.Length > 0 ? $"{test.OperationName} – {test.Description}" : $"{test.Call} – {test.Description}";
    }

    /// <summary>
    /// Sammanfattning överst i en rapport, t.ex. "1 av 34 tester visar ett problem." <paramref name="toolSkipped"/> är hur många av de
    /// hoppade testerna som hoppades över av verktygsskäl (fel i testgen), inte för att ett tidigare steg föll.
    /// </summary>
    public static string Summary(int ok, int fail, int skipped, int toolSkipped = 0)
    {
        var total = ok + fail + skipped;
        var sb = new StringBuilder();
        if (total == 0) return "Inga tester kördes.";
        if (fail == 0) sb.Append(skipped == 0 ? (total == 1 ? "Testet gick bra." : $"Alla {total} tester gick bra.") : $"Inget test visar något problem.");
        else sb.Append(fail == 1 ? $"1 av {total} tester visar ett problem." : $"{fail} av {total} tester visar problem.");
        var follow = skipped - toolSkipped;
        if (follow > 0)
            sb.Append(follow == 1 ? " 1 test hoppades över eftersom ett tidigare steg i flödet misslyckades."
                                  : $" {follow} tester hoppades över eftersom ett tidigare steg i flödet misslyckades.");
        if (toolSkipped > 0)
            sb.Append(toolSkipped == 1 ? " 1 test kunde inte byggas av testgen (fel i testgen, säger inget om systemet)."
                                       : $" {toolSkipped} tester kunde inte byggas av testgen (fel i testgen, säger inget om systemet).");
        return sb.ToString();
    }

    /// <summary>Antal hoppade tester med orsaken Fel i testgen.</summary>
    public static int ToolSkipped(IEnumerable<TestOutcome> outcomes) =>
        outcomes.Count(o => (o.Cause ?? Cause(o.Test, o.Result)) == FailureCause.Tool);

    /// <summary>Antal varningar från svarskontrollen i alla tester.</summary>
    public static int WarningCount(IEnumerable<CaseResult> results) => results.Sum(r => r.Warnings.Count);

    /// <summary>
    /// En tydlig rad om nästan alla svar är 401/403 (minst 90 %), annars null. Tar svarens statuskoder (null = inget svar).
    /// Gäller terminal, webbgränssnitt och rapporter; <paramref name="web"/> byter bara hänvisningen från flaggan till fältet i sidan.
    /// <paramref name="tokenSent"/> sant om en token skickades: då står det att token nekades i stället för att be om en token.
    /// </summary>
    public static string? AuthNote(IEnumerable<int?> statuses, bool web = false, bool tokenSent = false, PlainNames? names = null) =>
        AuthNoteText(statuses, web, tokenSent) is { } text ? PlainNames.Fill(text, names) : null;

    private static string? AuthNoteText(IEnumerable<int?> statuses, bool web, bool tokenSent)
    {
        var all = statuses.Where(s => s is not null).Select(s => s!.Value).ToList();
        if (all.Count == 0) return null;
        var n401 = all.Count(s => s == 401);
        var n403 = all.Count(s => s == 403);
        var denied = n401 + n403;
        if (denied * 10 < all.Count * 9) return null;
        var what = web ? "Skriv in en token i fältet Token, eller sätt token i testgen.yaml."
                       : $"Sätt token i testgen.yaml ({TokenKey}), eller ange --token för testgen run.";
        var again = web ? "Skriv in en ny token i fältet Token, eller kontrollera token i testgen.yaml."
                        : $"Kontrollera token i testgen.yaml ({TokenKey}), eller ange en ny med --token för testgen run.";
        var rejected401 = "Token skickades men nekades (401) – den kan vara fel eller ha gått ut.";
        if (denied == all.Count)
        {
            if (n403 == 0) return tokenSent ? $"Alla tester fick 401. {rejected401} {again}" : $"Alla tester fick 401 – inloggning saknas eller är ogiltig. {what}";
            if (n401 == 0) return $"Alla tester fick 403 – inloggningen saknar behörighet till det som testas. Kontrollera att token tillhör en användare med rätt roll. {what}";
            return tokenSent ? $"Alla tester fick 401 eller 403. {rejected401} {again}"
                             : $"Alla tester fick 401 eller 403 – inloggning saknas, är ogiltig eller saknar behörighet. {what}";
        }
        return tokenSent ? $"{denied} av {all.Count} tester fick 401 eller 403. {(n401 > 0 ? rejected401 + " " : "")}{again}"
                         : $"{denied} av {all.Count} tester fick 401 eller 403 – inloggning saknas, är ogiltig eller saknar behörighet. {what}";
    }

    /// <summary>Som <see cref="AuthNote(IEnumerable{int?}, bool, bool)"/> men läser också om en token skickades (ur anropens headers).</summary>
    public static string? AuthNote(IEnumerable<CaseResult> results, bool web = false, PlainNames? names = null)
    {
        var list = results.ToList();
        var sent = list.Any(r => r.Status is 401 or 403 && TokenWasSent(r));
        return AuthNote(list.Select(r => r.Status), web, sent, names);
    }

    // --- Orsaker -------------------------------------------------------------------------

    /// <summary>Visningsnamn på svenska: "Fel i systemet", "Förväntan i testet stämmer inte", "Inloggning", "Miljön svarar inte", "Testdata fanns redan", "Fel i testgen", "Följdfel".</summary>
    public static string CauseName(FailureCause cause) => cause switch
    {
        FailureCause.System => "Fel i systemet",
        FailureCause.Expectation => "Förväntan i testet stämmer inte",
        FailureCause.Auth => "Inloggning",
        FailureCause.Environment => "Miljön svarar inte",
        FailureCause.TestData => "Testdata fanns redan",
        FailureCause.MissingData => "Testdata saknas",
        FailureCause.TestUserRejected => "Testanvändaren godtogs inte",
        FailureCause.RateLimited => "För många anrop",
        FailureCause.Tool => "Fel i testgen",
        FailureCause.ReadOnly => "Skrivskyddad miljö",
        _ => "Följdfel",
    };

    /// <summary>Kort förklaring av orsaken (en–två meningar). För Inloggning kan variant anges.</summary>
    public static string CauseExplanation(FailureCause cause, AuthVariant? variant = null, PlainNames? names = null) =>
        PlainNames.Fill(CauseExplanationText(cause, variant), names);

    private static string CauseExplanationText(FailureCause cause, AuthVariant? variant) => cause switch
    {
        FailureCause.System => "Systemet svarade med ett fel (5xx), med en annan status än specen anger eller med ett svar som inte följer specen.",
        FailureCause.Expectation => "Testet väntade sig något annat än systemet svarade, men systemets svar följer specen. Felet ligger antingen i systemet eller i Excel-raden.",
        FailureCause.Auth => variant switch
        {
            AuthVariant.NoToken => "Systemet nekade anropet (401/403) och ingen token skickades. Det säger inget om funktionen som testas.",
            AuthVariant.TokenRejected => "Token skickades men nekades (401/403) – den kan vara fel eller ha gått ut. Det säger inget om funktionen som testas.",
            _ => "Systemet nekade anropet (401/403). Det säger inget om funktionen som testas.",
        },
        FailureCause.Environment => "Systemet svarade inte (timeout, ingen kontakt eller svaren 502/503/504 från en gateway). Det säger inget om systemets funktioner.",
        FailureCause.TestData => "Uppgifterna fanns redan (409), oftast från en tidigare körning. Det säger inget om systemet.",
        FailureCause.MissingData => "Testet behöver ett värde som finns i systemet (till exempel ett id), men testgen kan inte hitta på ett. Ange det under apis.<api>.bindings i testgen.yaml. Det säger inget om systemet.",
        FailureCause.TestUserRejected => "Appen godtog inte testanvändaren som testgen loggar in som (401/403). Appen slår troligen upp användaren själv. Ange claims under apis.<api>.auth.claims, eller ett inloggningssteg under apis.<api>.login i testgen.yaml. Det säger inget om funktionen som testas.",
        FailureCause.RateLimited => "Systemet svarade 429 (för många anrop), även om testgen väntade enligt Retry-After när det angavs. Höj gränsen i miljön som testas, eller kör igen om en stund. Det säger inget om funktionen som testas.",
        FailureCause.Tool => "Verktyget kunde inte bygga eller skicka testet – rapportera till den som underhåller testgen. Det säger inget om systemet.",
        FailureCause.ReadOnly => "Miljön är skrivskyddad, så anrop som skriver (POST, PUT, PATCH, DELETE) skickades inte. Det säger inget om systemet.",
        _ => "Testet kunde inte köras eller hoppades över eftersom ett tidigare steg föll. Åtgärda det tidigare felet först.",
    };

    /// <summary>
    /// Orsaken till att ett test fick FEL eller HOPPAD, eller null för OK. Se <see cref="FailureCause"/>. Förberedande anrop som föll
    /// ger samma orsak som om anropet självt fått svaret (409 = Testdata, 401/403 = Inloggning, 502/503/504 = Miljö, övriga 5xx = System), annars Följdfel.
    /// </summary>
    public static FailureCause? Cause(TestInfo test, CaseResult result)
    {
        if (result.Skipped) return result.SkipCause ?? (result.SkipNote is not null ? FailureCause.Tool : FailureCause.Cascade);
        if (result.Ok) return null;
        if (result.TimedOut) return FailureCause.Environment;
        if (result.Setup is { } setup) return FromStatus(setup.Status) ?? FailureCause.Cascade;
        if (result.Status is not { } status)
            return result.Details.Count > 0 ? FailureCause.Expectation : FailureCause.Environment;   // Excel-radens egna fel (okänt fält m.m.) eller inget svar alls
        if (!test.ExpectedStatus.Any(e => e.Matches(status)))
            return FromStatus(status) ?? (test.Step is not null ? FailureCause.Expectation : FailureCause.System);
        // Rätt status men fel innehåll: i Excel-steg avviker det förväntade svaret, annars följer svaret inte specen.
        return test.Step is not null && !result.Verdict.Contains("inte specen", StringComparison.Ordinal) ? FailureCause.Expectation : FailureCause.System;
    }

    private static FailureCause? FromStatus(int status) => status switch
    {
        409 => FailureCause.TestData,
        429 => FailureCause.RateLimited,
        401 or 403 => FailureCause.Auth,
        502 or 503 or 504 => FailureCause.Environment,   // gateway/överbelastad: miljön, inte funktionen som testas
        >= 500 => FailureCause.System,
        _ => null,
    };

    /// <summary>
    /// Sant om en token skickades i anropet som fick nej (401/403), annars anropet i fokus: headern Authorization, eller en hemlig header
    /// (värdet är maskat som ***, t.ex. en apiKey). Läser bara de maskade headers som finns i <see cref="Exchange"/>.
    /// </summary>
    public static bool TokenWasSent(CaseResult result)
    {
        var ex = result.Exchanges.FirstOrDefault(e => e.Status is 401 or 403) ?? ExchangeText.Main(result.Exchanges);
        return ex is not null && ex.Headers.Any(h => h.Value.Length > 0 &&
            (h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || h.Value.Contains(HeaderMask.Hidden, StringComparison.Ordinal)));
    }

    /// <summary>Variant av <see cref="FailureCause.Auth"/>: ingen token skickades eller token nekades.</summary>
    public static AuthVariant AuthVariantOf(CaseResult result) => TokenWasSent(result) ? AuthVariant.TokenRejected : AuthVariant.NoToken;

    private static string TokenRejectedText(int status) => status == 403
        ? "Token skickades men nekades (403) – användaren saknar behörighet till det som testas."
        : $"Token skickades men nekades ({status}) – den kan vara fel eller ha gått ut.";

    /// <summary>
    /// Ett resultat per orsak (bara orsaker som förekommer), i ordningen System, Förväntan, Inloggning, Miljö, Testdata, Fel i testgen, Följdfel.
    /// Varje rad har antal, test-id (högst 10, resten som "och N till") och en mening.
    /// </summary>
    public static IReadOnlyList<CauseLine> CauseSummary(IEnumerable<TestOutcome> outcomes)
    {
        var withCause = outcomes.Select(o => (Outcome: o, Cause: o.Cause ?? Cause(o.Test, o.Result))).Where(x => x.Cause is not null).ToList();
        var lines = new List<CauseLine>();
        foreach (var cause in Enum.GetValues<FailureCause>())
        {
            var group = withCause.Where(x => x.Cause == cause).Select(x => x.Outcome).ToList();
            if (group.Count == 0) continue;
            var ids = group.Select(o => o.Id).ToList();
            var shown = ids.Take(10).ToList();
            var idsText = ids.Count > shown.Count ? string.Join(", ", shown) + $" och {ids.Count - shown.Count} till" : string.Join(", ", shown);
            lines.Add(new CauseLine(cause, CauseName(cause), group.Count, shown, ids.Count - shown.Count,
                PlainNames.Fill(CauseSentence(cause, group, idsText), group.Select(o => o.Test.Names).FirstOrDefault(n => n is not null))));
        }
        return lines;
    }

    /// <summary>Etikett för ett enskilt test: "Orsak: Testdata fanns redan". Null när orsak saknas (testet är OK).</summary>
    public static string? CauseLabel(FailureCause? cause) => cause is { } c ? "Orsak: " + CauseName(c) : null;

    /// <summary>"Följd av T-001" när testet föll för att ett annat test föll, annars null.</summary>
    public static string? FollowLabel(CaseResult result) => result.RootCauseTestId is { } id ? "Följd av " + id : null;

    /// <summary>Etiketten och rotorsaken på en rad: "Orsak: Följdfel. Följd av T-001". Null för OK.</summary>
    public static string? CauseLabel(FailureCause? cause, CaseResult result) =>
        CauseLabel(cause) is { } label ? (FollowLabel(result) is { } follow ? $"{label}. {follow}" : label) : null;

    /// <summary>Orsaksradens början före test-id: "Fel i systemet" eller "Testdata fanns redan (409)".</summary>
    public static string CauseHead(FailureCause cause) => cause switch
    {
        FailureCause.Auth => CauseName(cause) + " (401/403)",
        FailureCause.TestData => CauseName(cause) + " (409)",
        _ => CauseName(cause),
    };

    /// <summary>Orsaksradens slut efter test-id, t.ex. " – säger inget om systemet". Tom för orsaker som är fel i systemet.</summary>
    public static string CauseTail(FailureCause cause) => cause switch
    {
        FailureCause.Auth => " – säger inget om funktionen som testas",
        FailureCause.Environment => " – säger inget om systemets funktioner",
        FailureCause.TestData => " – säger inget om systemet",
        FailureCause.MissingData => " – ange värdet under bindings i testgen.yaml",
        FailureCause.TestUserRejected => " – ange claims eller ett inloggningssteg i testgen.yaml",
        FailureCause.RateLimited => " – hoppades över, säger inget om funktionen som testas",
        FailureCause.Tool => " – säger inget om systemet, rapportera till den som underhåller testgen",
        FailureCause.Cascade => " – följer av ett tidigare fel",
        FailureCause.ReadOnly => " – hoppades över, säger inget om systemet",
        _ => "",
    };

    /// <summary>
    /// Orsaksraden i listform: "Fel i systemet: T-020" eller "Testdata fanns redan (409): T-001, T-012 – säger inget om systemet".
    /// </summary>
    public static string CauseLineText(CauseLine line) => $"{CauseHead(line.Cause)}: {line.IdsText}{CauseTail(line.Cause)}";

    /// <summary>
    /// Kort form av <see cref="Verdict"/> för rubriker, t.ex. "1 fel i systemet, 2 kunde inte köras färdigt",
    /// "inget säkert svar (testdata)" eller "alla 34 tester gick som väntat".
    /// </summary>
    public static string VerdictShort(IEnumerable<TestOutcome> outcomes)
    {
        var list = outcomes.ToList();
        if (list.Count == 0) return "inga tester kördes";
        var lines = CauseSummary(list).Where(l => l.Cause != FailureCause.ReadOnly).ToList();
        if (lines.Count == 0) return list.Count == 1 ? "testet gick som väntat" : $"alla {list.Count} tester gick som väntat";
        var parts = new List<string>();
        foreach (var l in lines.Where(l => IsRealOutcome(l.Cause)))
            parts.Add(l.Cause == FailureCause.System
                ? (l.Count == 1 ? "1 fel i systemet" : $"{l.Count} fel i systemet")
                : $"{l.Count} där förväntan inte stämde");
        var unfinished = lines.Where(l => !IsRealOutcome(l.Cause)).ToList();
        if (unfinished.Count == 0) return string.Join(", ", parts);
        if (parts.Count == 0) return "inget säkert svar (" + string.Join(", ", unfinished.Select(l => ShortReason(l.Cause))) + ")";
        parts.Add($"{unfinished.Sum(l => l.Count)} kunde inte köras färdigt");
        return string.Join(", ", parts);
    }

    /// <summary>Orsaker som är ett faktiskt utfall av testet (fel i systemet eller i förväntan), till skillnad från tester som inte kunde köras färdigt.</summary>
    private static bool IsRealOutcome(FailureCause cause) => cause is FailureCause.System or FailureCause.Expectation;

    private static string ShortReason(FailureCause cause) => cause switch
    {
        FailureCause.TestData => "testdata",
        FailureCause.MissingData => "testdata saknas",
        FailureCause.TestUserRejected => "testanvändaren godtogs inte",
        FailureCause.RateLimited => "för många anrop",
        FailureCause.Auth => "inloggning",
        FailureCause.Environment => "miljö",
        FailureCause.Tool => "fel i testgen",
        _ => "följdfel",
    };

    private static string LongReason(FailureCause cause) => cause switch
    {
        FailureCause.TestData => "testdata fanns redan",
        FailureCause.MissingData => "testdata saknades",
        FailureCause.TestUserRejected => "testanvändaren godtogs inte",
        FailureCause.RateLimited => "systemet svarade för många anrop",
        FailureCause.Auth => "inloggningen nekades",
        FailureCause.Environment => "miljön svarade inte",
        FailureCause.Tool => "fel i testgen",
        _ => "ett tidigare test föll",
    };

    /// <summary>" (502/503)" när testerna fick svar från en gateway (502/503/504) i stället för tidsgräns: texten "fick inget svar" ska inte dölja att något svarade.</summary>
    private const string MissingServiceHint = "Appen saknar troligen en tjänst den behöver i testprocessen (en databas, ett API eller en nyckel). Ange den under apis.<api>.settings i testgen.yaml.";

    /// <summary>Slutet på orsaksmeningen för miljön: körs appen i testprocessen är det appen som saknar något, annars miljön.</summary>
    private static string UpTail(IEnumerable<TestOutcome> group) =>
        group.Select(o => o.Test.Names).FirstOrDefault(n => n is not null) is { InProcess: true } names
            ? " " + PlainNames.Fill(MissingServiceHint, names)
            : " Kör igen när den är uppe.";

    private static string GatewayNote(IEnumerable<TestOutcome> group)
    {
        var codes = group.Select(o => o.Result.Status ?? o.Result.Setup?.Status).Where(c => c is 502 or 503 or 504).Select(c => c!.Value).Distinct().Order().ToList();
        return codes.Count == 0 ? "" : $" (svar {string.Join("/", codes)} från en gateway eller ett system som inte är uppe)";
    }

    /// <summary>Varningstexten utan den interna markeringen " (varning)".</summary>
    public static string WarningText(string warning) => warning.Replace(" (varning)", "");

    /// <summary>"1 varning – påverkar inte resultatet" / "2 varningar – påverkar inte resultatet", eller null när det inte finns några.</summary>
    public static string? WarningSummary(int count) =>
        count <= 0 ? null : (count == 1 ? "1 varning" : $"{count} varningar") + " – påverkar inte resultatet";

    private static string Tests(int n) => n == 1 ? "1 test" : $"{n} tester";

    private static string CauseSentence(FailureCause cause, List<TestOutcome> group, string ids)
    {
        var n = group.Count;
        switch (cause)
        {
            case FailureCause.System:
                return $"{n} fel i systemet ({ids}).";
            case FailureCause.Expectation:
                return $"{Tests(n)} väntade sig något annat än systemet svarade ({ids}). Svaret följer specen, så felet ligger antingen i systemet eller i Excel-raden.";
            case FailureCause.Auth:
                var rejected = group.Count(o => TokenWasSent(o.Result));
                var none = n - rejected;
                if (rejected == 0) return $"{Tests(n)} nekades (401/403) – ingen token skickades. Ange en token och kör igen.";
                if (none == 0) return $"{Tests(n)} nekades (401/403) – token skickades men nekades, den kan vara fel eller ha gått ut. Byt token och kör igen.";
                return $"{Tests(n)} nekades (401/403): {none} utan token och {rejected} där token nekades. Kontrollera token och kör igen.";
            case FailureCause.Environment:
                return $"{Tests(n)} fick inget svar – miljön svarade inte{GatewayNote(group)}.{UpTail(group)}";
            case FailureCause.TestData:
                return $"{Tests(n)} krockade med befintlig testdata (409) – säger inget om systemet.";
            case FailureCause.MissingData:
                return $"{Tests(n)} hoppades över eftersom ett värde som finns i systemet inte kunde hittas på ({ids}). Ange det under apis.<api>.bindings i testgen.yaml.";
            case FailureCause.TestUserRejected:
                return $"{Tests(n)} hoppades över eftersom appen inte godtog testanvändaren (401/403, {ids}). Ange claims under apis.<api>.auth.claims, eller ett inloggningssteg under apis.<api>.login i testgen.yaml.";
            case FailureCause.RateLimited:
                return $"{Tests(n)} hoppades över eftersom systemet svarade 429 (för många anrop, {ids}). Höj gränsen i miljön som testas, eller kör igen om en stund.";
            case FailureCause.ReadOnly:
                return $"{Tests(n)} hoppades över eftersom miljön är skrivskyddad ({ids}).";
            case FailureCause.Tool:
                return $"{Tests(n)} kunde inte byggas eller skickas av testgen ({ids}) – säger inget om systemet. Rapportera till den som underhåller testgen.";
            default:
                return $"{Tests(n)} är följdfel av ett tidigare fel – de säger inget eget om systemet.";
        }
    }

    /// <summary>
    /// Huvudmeningen för beslut överst i en rapport, t.ex. "1 fel i systemet (T-020). Dessutom kunde 2 tester inte köras färdigt (testdata fanns redan)."
    /// eller "Alla 34 tester gick som väntat." Finns inget fel i systemet men tester som inte kunde köras färdigt (testdata, inloggning, miljö, följdfel)
    /// sägs det rakt ut: "Inget säkert svar: …". Vid delkörning inleds den med "Delkörning: 11 av 34 möjliga tester kördes."
    /// </summary>
    /// <param name="possibleTotal">Antal tester hela körningen hade haft; är det fler än de som kördes är det en delkörning.</param>
    /// <param name="partial">Sant vid delkörning när antalet möjliga tester inte är känt.</param>
    public static string Verdict(IEnumerable<TestOutcome> outcomes, int? possibleTotal = null, bool partial = false)
    {
        var list = outcomes.ToList();
        var sb = new StringBuilder();
        if (possibleTotal is { } all && all > list.Count) sb.Append($"Delkörning: {list.Count} av {all} möjliga tester kördes. ");
        else if (partial) sb.Append("Delkörning: bara en del av testerna kördes. ");
        if (list.Count == 0) return sb.Append("Inga tester kördes.").ToString();

        var allLines = CauseSummary(list);
        var lines = allLines.Where(l => l.Cause != FailureCause.ReadOnly).ToList();
        var readOnly = allLines.Where(l => l.Cause == FailureCause.ReadOnly).Sum(l => l.Count);
        var readOnlyNote = readOnly == 0 ? "" : $" {Tests(readOnly)} hoppades över eftersom miljön är skrivskyddad.";
        if (lines.Count == 0)
        {
            var ran = list.Count - readOnly;
            if (readOnly == 0) return sb.Append(list.Count == 1 ? "Testet gick som väntat." : $"Alla {list.Count} tester gick som väntat.").ToString();
            return sb.Append(ran == 0 ? readOnlyNote.TrimStart() : $"{(ran == 1 ? "1 test" : $"{ran} tester")} gick som väntat.{readOnlyNote}").ToString();
        }

        var real = lines.Where(l => IsRealOutcome(l.Cause)).ToList();
        var unfinished = lines.Where(l => !IsRealOutcome(l.Cause)).ToList();
        sb.Append(string.Join(' ', real.Select(l => l.Sentence)));
        if (unfinished.Count > 0 && real.Count > 0)
        {
            var reasons = string.Join(", ", unfinished.Select(l => LongReason(l.Cause)));
            sb.Append($" Dessutom kunde {Tests(unfinished.Sum(l => l.Count))} inte köras färdigt ({reasons}).");
        }
        else if (unfinished.Count > 0)
        {
            sb.Append("Inget säkert svar: ");
            sb.Append(string.Join(' ', unfinished.Select(l => UnfinishedSentence(l, list))));
            var okCount = list.Count - allLines.Sum(l => l.Count);
            if (okCount > 0) sb.Append($" {Tests(okCount)} kördes färdigt utan fel.");
        }
        sb.Append(readOnlyNote);
        return sb.ToString();
    }

    private static string UnfinishedSentence(CauseLine line, List<TestOutcome> all)
    {
        var group = all.Where(o => (o.Cause ?? Cause(o.Test, o.Result)) == line.Cause).ToList();
        var n = Tests(group.Count);
        switch (line.Cause)
        {
            case FailureCause.TestData:
                return $"{n} kunde inte testa systemet eftersom testdata redan fanns (409). Kör igen med nytt slumpfrö.";
            case FailureCause.Auth:
                var codes = group.Select(o => o.Result.Status).Where(c => c is 401 or 403).Select(c => c!.Value).Distinct().Order().ToList();
                var code = codes.Count == 0 ? "401/403" : string.Join("/", codes);
                var rejected = group.Count(o => TokenWasSent(o.Result));
                if (rejected == 0) return $"{n} nekades ({code}) – ingen token skickades. Ange en token och kör igen.";
                if (rejected == group.Count) return $"{n} nekades ({code}) – token skickades men nekades, den kan vara fel eller ha gått ut. Byt token och kör igen.";
                return $"{n} nekades ({code}): {group.Count - rejected} utan token och {rejected} där token nekades. Kontrollera token och kör igen.";
            case FailureCause.Environment:
                return $"{n} fick inget svar – miljön svarade inte{GatewayNote(group)}.{UpTail(group)}";
            case FailureCause.Tool:
                return $"{n} kunde inte byggas eller skickas av testgen – fel i testgen, inte i systemet. Rapportera till den som underhåller testgen.";
            default:
                return $"{n} är följdfel av ett tidigare fel – de säger inget eget om systemet.";
        }
    }

    /// <summary>Resultat utan svar alls: timeout, eller inget svar och inget förberedande anrop som föll.</summary>
    private static bool IsNoResponse(CaseResult r) =>
        !r.Skipped && !r.Ok && (r.TimedOut || (r.Status is null && r.Setup is null && r.Details.Count == 0));

    /// <summary>
    /// En tydlig rad när nästan alla tester (minst 90 % av dem som kördes, hoppade räknas inte) fick timeout/inget svar, annars null.
    /// <paramref name="web"/> byter hänvisningen från flaggor till fält i sidan.
    /// </summary>
    public static string? EnvironmentNote(IEnumerable<CaseResult> results, bool web = false)
    {
        var ran = results.Where(r => !r.Skipped).ToList();
        var none = ran.Count(IsNoResponse);
        if (none == 0 || none * 10 < ran.Count * 9) return null;
        var what = web ? "Kontrollera att systemet är igång och att API-adressen stämmer, eller öka Timeout. Kör sedan igen."
                       : "Kontrollera att systemet är igång och att --api stämmer, eller öka --timeout. Kör sedan igen.";
        if (ran.Count == 1) return $"Testet fick inget svar – miljön svarade inte. {what}";
        return none == ran.Count
            ? $"Alla {ran.Count} tester fick inget svar – miljön svarade inte. {what}"
            : $"{none} av {ran.Count} tester fick inget svar – miljön svarade inte. {what}";
    }

    // --- Rubriker ------------------------------------------------------------------------

    /// <summary>
    /// Fallets del av rubriken: "quantity för lågt (under minimum)". Den enda källan till rubrikfrasen: <see cref="TitleOf"/>
    /// bygger på den, och terminalens beskrivningskolumn (<see cref="TestInfo.Description"/>) sätts ur den.
    /// </summary>
    public static string RulePhrase(CaseMeta c)
    {
        var f = c.Field ?? "";
        return c.Rule switch
        {
            TestRule.Valid => c.Field is null && c.Limit is null ? "giltig indata" : $"giltig indata – {(c.Field is null ? "" : c.Field + " ")}{c.Limit}",
            TestRule.Missing => $"{f} saknas",
            TestRule.BelowMinimum => $"{f} för lågt (under minimum)",
            TestRule.AboveMaximum => $"{f} för högt (över maximum)",
            TestRule.TooLong => $"{f} för lång (över maxlängd)",
            TestRule.TooShort => $"{f} för kort (under minlängd)",
            TestRule.NotInEnum => $"{f} utanför tillåtna värden",
            TestRule.WrongType => $"{f} har fel typ",
            TestRule.PatternMismatch => $"{f} följer inte mönstret",
            TestRule.BadFormat => $"{f} har fel format",
            TestRule.Empty => $"{f} är tom",
            TestRule.TooFewItems => $"{f} har för få element (under minItems)",
            TestRule.TooManyItems => $"{f} har för många element (över maxItems)",
            _ => c.Rule.ToString(),
        };
    }

    // --- Förklaringar --------------------------------------------------------------------

    private static (string Explanation, string? Hint) Body(TestInfo test, CaseResult r)
    {
        if (r.Skipped && r.SkipNote is { } note) return (char.ToUpperInvariant(note[0]) + note[1..], null);
        if (r.Skipped) return ("Hoppades över eftersom ett tidigare steg i flödet misslyckades." + RootSentence(r), null);

        if (r.Setup is { } setup)
        {
            var text = $"Testet kunde inte köras: först behövde vi köra \"{setup.Name}\", men det misslyckades (svar {StatusText(setup.Status)})." + RootSentence(r);
            return (text, StatusHint(setup.Status, test, r) ?? "Felet ligger i det förberedande anropet, inte i själva testet.");
        }

        if (r.TimedOut)
            return ("Systemet svarade inte inom tidsgränsen, så testet fick inget svar.",
                    "Kontrollera att systemet är igång och inte överbelastat, eller ge det längre tid med --timeout.");

        if (r.Status is not { } status)
        {
            var why = DetailSentences(r.Details);
            return (why.Length > 0 ? "Testet kunde inte köras. " + why : "Testet kunde inte köras och fick inget svar från systemet.",
                    why.Length > 0 ? null : "Kontrollera att systemet är igång och att adressen stämmer.");
        }

        if (test.Step is { } step) return ExcelStep(test, step, r, status);
        if (test.Case is { } c) return c.Rule == TestRule.Valid ? Valid(test, r, status) : Negative(test, c, r, status);
        return Generic(test, r, status);
    }

    private static (string, string?) Valid(TestInfo test, CaseResult r, int status)
    {
        var read = test.Method.Equals("GET", StringComparison.OrdinalIgnoreCase);   // inget skickas in, bara en förfrågan
        var start = read ? "Vi skickade en giltig förfrågan." : "Vi skickade giltiga uppgifter.";
        var expected = ExpectedText(test.ExpectedStatus);
        if (r.Ok) return (read ? $"{start} Systemet svarade {StatusText(status)}." : $"{start} Systemet tog emot dem ({StatusText(status)}).", null);
        var accepted = test.ExpectedStatus.Any(e => e.Matches(status));
        if (accepted)   // rätt status men svaret följer inte specen
            return ($"{start} Systemet svarade {StatusText(status)}, men svaret följer inte specen. {DetailSentences(r.Details)}",
                    "Antingen skickar systemet tillbaka fel data, eller så beskriver specen svaret fel.");
        if (status is 400 or 422)
            return ($"{start} Systemet sa nej till {(read ? "en förfrågan" : "uppgifter")} som enligt specen är giltig{(read ? "" : "a")} ({StatusText(status)}).",
                    "Antingen är det ett fel i systemet (det kräver mer än specen säger) eller i specen (den beskriver inte alla krav). "
                    + "Kräver operationen data som inte går att hitta på (till exempel en riktig inloggningskod), hoppa över den med skipOperations eller ge den värden med bindings under apis.<api> i testgen.yaml.");
        if (status is >= 200 and < 300)
            return ($"{start} Systemet svarade {StatusText(status)} i stället för {expected}.",
                    "Systemet och specen är oense om vilken kod som ska användas – fel i systemet eller i specen.");
        return ($"{start} Vi väntade oss {expected}, men systemet svarade {StatusText(status)}.", StatusHint(status, test, r));
    }

    private static (string, string?) Negative(TestInfo test, CaseMeta c, CaseResult r, int status)
    {
        var sent = SentSentence(c);
        if (r.Ok) return ($"{sent} Systemet sa nej, som det ska.", null);
        if (status is >= 200 and < 300)
            return ($"{sent} Systemet borde ha sagt nej till uppgifterna ({NoCodes(test)}), men tog emot dem ({StatusText(status)}).",
                    "Antingen saknar systemet en kontroll som specen kräver, eller så beskriver specen en regel som systemet inte har.");
        if (status == 422)
            return ($"{sent} Systemet sa nej, men med {StatusText(status)} i stället för {NoCodes(test)} som specen anger.",
                    "Systemet och specen använder olika koder för nej – fel i systemet eller i specen.");
        return ($"{sent} Systemet borde ha sagt nej till uppgifterna ({NoCodes(test)}), men svarade {StatusText(status)}.", StatusHint(status, test, r));
    }

    /// <summary>Koderna ett nej får ha enligt specen: "400", "400 eller 422" eller "4xx".</summary>
    private static string NoCodes(TestInfo test) =>
        test.ExpectedStatus.Count == 0 ? "400" : string.Join(" eller ", test.ExpectedStatus.Select(e => e.ToString()));

    private static (string, string?) ExcelStep(TestInfo test, ExcelStepInfo step, CaseResult r, int status)
    {
        var start = $"Steget körde \"{test.OperationName}\".";
        var expected = ExpectedText(test.ExpectedStatus);
        var statusOk = test.ExpectedStatus.Count == 0 || test.ExpectedStatus.Any(e => e.Matches(status));
        var expectation = Cause(test, r) == FailureCause.Expectation;
        if (!statusOk)
            return ($"{start} Vi väntade oss {expected}{(expectation ? ExcelAt(step.StatusCell) : "")}, men systemet svarade {StatusText(status)}.",
                    StatusHint(status, test, r) ?? (expectation ? "Antingen är det ett fel i systemet, eller så stämmer inte den förväntade statusen i Excel-raden." : null));
        if (r.Ok)
        {
            var extra = step.ExpectedFields.Count > 0 ? $" Svaret innehöll också det som förväntades ({string.Join(", ", step.ExpectedFields)})." : "";
            return ($"{start} Systemet svarade {StatusText(status)}, som förväntat.{extra}", null);
        }
        var both = r.Verdict.Contains("Excel", StringComparison.Ordinal);
        var specOnly = !both && r.Verdict.Contains("inte specen", StringComparison.Ordinal);
        var what = both ? "följer inte specen och stämmer inte med det förväntade svaret" + ExcelAt(step.ExpectedCell)
                 : specOnly ? "följer inte specen"
                 : "stämmer inte" + ExcelAt(step.ExpectedCell);
        return ($"{start} Systemet svarade {StatusText(status)} som förväntat, men innehållet i svaret {what}: {DetailSentences(r.Details)}",
                both ? "Antingen skickar systemet tillbaka fel data, eller så beskriver specen och Excel-raden svaret fel."
                : specOnly ? "Antingen skickar systemet tillbaka fel data, eller så beskriver specen svaret fel."
                : "Antingen är det ett fel i systemet, eller så stämmer inte det förväntade svaret i Excel-raden.");
    }

    private static (string, string?) Generic(TestInfo test, CaseResult r, int status)
    {
        if (r.Ok) return ($"Testet gick bra: systemet svarade {StatusText(status)}.", null);
        var details = DetailSentences(r.Details);
        return ($"Testet visar ett problem: systemet svarade {StatusText(status)}, och det var inte som förväntat." + (details.Length > 0 ? " " + details : ""),
                StatusHint(status, test, r));
    }

    // --- Delar ---------------------------------------------------------------------------

    private static string SentSentence(CaseMeta c)
    {
        var f = c.Field ?? "";
        var v = Show(c.SentValue);
        var limit = c.Limit ?? "";
        switch (c.Rule)
        {
            case TestRule.Missing:
                return $"Vi skickade inte med {f}, som är obligatoriskt.";
            case TestRule.BelowMinimum:
                return c.Exclusive ? $"Vi skickade {f} = {v}, men värdet måste vara större än {limit}." : $"Vi skickade {f} = {v}, men minsta tillåtna är {limit}.";
            case TestRule.AboveMaximum:
                return c.Exclusive ? $"Vi skickade {f} = {v}, men värdet måste vara mindre än {limit}." : $"Vi skickade {f} = {v}, men största tillåtna är {limit}.";
            case TestRule.TooLong:
                return $"Vi skickade {f} med {LengthOf(c.SentValue)} tecken, men längsta tillåtna är {limit} tecken.";
            case TestRule.TooShort:
                return $"Vi skickade {f} med {LengthOf(c.SentValue)} tecken, men minst {limit} tecken krävs.";
            case TestRule.NotInEnum:
                return $"Vi skickade {f} = {v}, som inte är ett av de tillåtna värdena ({Shorten(limit)}).";
            case TestRule.WrongType:
                return $"Vi skickade {f} = {v}, men fältet ska vara {TypeText(limit)}.";
            case TestRule.PatternMismatch:
                return $"Vi skickade {f} = {v}, som inte följer formatet {Shorten(limit)}.";
            case TestRule.BadFormat:
                return $"Vi skickade {f} = {v}, men fältet ska vara {FormatText(limit)}.";
            case TestRule.Empty:
                return $"Vi skickade {f} som tom text, men fältet är obligatoriskt.";
            case TestRule.TooFewItems:
                return $"Vi skickade {f} med {CountOf(c.SentValue)} element, men minst {limit} krävs.";
            case TestRule.TooManyItems:
                return $"Vi skickade {f} med {CountOf(c.SentValue)} element, men högst {limit} är tillåtna.";
            default:
                return "Vi skickade giltiga uppgifter.";
        }
    }

    private static string FormatText(string format) => format switch
    {
        "email" => "en e-postadress",
        "date" => "ett datum (åååå-mm-dd)",
        "date-time" => "ett datum och en tid",
        "uuid" or "guid" => "ett uuid",
        "uri" or "url" => "en webbadress",
        _ => $"i formatet {format}",
    };

    private static int CountOf(string? json)
    {
        try { if (json is not null && JsonNode.Parse(json) is JsonArray a) return a.Count; }
        catch (System.Text.Json.JsonException) { }
        return 0;
    }

    private static string TypeText(string type) => type switch
    {
        "integer" => "ett heltal",
        "number" => "ett tal",
        "boolean" => "sant eller falskt",
        "array" => "en lista",
        "object" => "ett objekt",
        _ => "text",
    };

    private static int LengthOf(string? json)
    {
        try { if (json is not null && JsonNode.Parse(json) is JsonValue v && v.TryGetValue<string>(out var s)) return s.Length; }
        catch (System.Text.Json.JsonException) { }
        return json?.Length ?? 0;
    }

    private static string Show(string? json) => json is null ? "(inget)" : Shorten(json);

    /// <summary>Högst <see cref="MaxValueChars"/> tecken; en avkortad text slutar på … (och citattecken om den började med ett).</summary>
    public static string Shorten(string s)
    {
        if (s.Length <= MaxValueChars) return s;
        return s.StartsWith('"') ? s[..(MaxValueChars - 2)] + "…\"" : s[..(MaxValueChars - 1)] + "…";
    }

    /// <summary>Förväntad status i klartext: "201 – skapad", "valfri lyckad kod (2xx)", "200 – OK eller 201 – skapad".</summary>
    public static string ExpectedText(IReadOnlyList<StatusExpectation> expected)
    {
        if (expected.Count == 0) return "ett annat svar";
        return string.Join(" eller ", expected.Select(e =>
            e.From == e.To ? StatusText(e.From)
            : e.To - e.From == 99 ? e.From switch
            {
                100 => "information (1xx)",
                200 => "valfri lyckad kod (2xx)",
                300 => "en hänvisning (3xx)",
                400 => "ett nej från systemet (4xx)",
                500 => "ett fel i systemet (5xx)",
                _ => $"{e.From}–{e.To}",
            }
            : $"{e.From}–{e.To}"));
    }

    /// <summary>Tolkning utifrån statuskoden, eller null om koden inte säger något särskilt.</summary>
    private static string? StatusHint(int status, TestInfo test, CaseResult r)
    {
        if (status is 502 or 503 or 504 && test.Names is { InProcess: true })
            return MissingServiceHint + (r.BlockedHosts.Count == 0 ? "" :
                $" testgen blockerade appens anrop till {string.Join(", ", r.BlockedHosts)} under testet: sätt värdet mock eller allow för värden under hosts i testgen.yaml.");
        if (status is 502 or 503 or 504) return "Det tyder på ett problem med miljön (en gateway eller ett system som inte är uppe), inte på ett fel i funktionen som testas. Kör igen när miljön är uppe.";
        if (status >= 500) return "Det tyder på ett fel i systemet, inte i testet.";
        if (status is 401 or 403)
            return TokenWasSent(r)
                ? TokenRejectedText(status) + $" Byt token i testgen.yaml ({TokenKey}), eller med --token för testgen run."
                : $"Ingen token skickades. Sätt token i testgen.yaml ({TokenKey}), eller ange --token för testgen run.";
        if (status == 409) return "Uppgifterna fanns redan i systemet (troligen från en tidigare körning). Välj ett annat slumpfrö (--seed, eller Slumpfrö under Fler inställningar) eller be någon nollställa testsystemet.";
        if (status == 404 && test.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) && r.Exchanges.Any(x => x.Preparation is not null))
            return "Det som skapades hittades inte när vi sökte efter det.";
        if (status == 429) return "Systemet tycker att vi anropar för ofta. Vänta en stund och kör igen.";
        return null;
    }

    private static string ExcelAt(string? cell) => cell is null ? "" : $" (Excel: {cell})";

    private static string RootSentence(CaseResult r) =>
        r.RootCauseTestId is null ? "" : r.RootCauseText is null ? $" Följd av {r.RootCauseTestId}." : $" Följd av {r.RootCauseTestId} ({r.RootCauseText}).";

    // --- Detaljrader -> meningar ---------------------------------------------------------

    [GeneratedRegex("""^svar\.(?<p>[^:]+): (?<v>".*") följer inte mönstret (?<pat>.+) \(enligt specen\)$""")]
    private static partial Regex SpecPattern();

    [GeneratedRegex("""^svar\.(?<p>[^:]+): väntade ~(?<pat>.+), fick \(saknas\)$""")]
    private static partial Regex MissingForPattern();

    [GeneratedRegex("""^svar\.(?<p>[^:]+): (?<v>.+) matchar inte ~(?<pat>.+)$""")]
    private static partial Regex NoMatch();

    [GeneratedRegex("""^svar\.(?<p>[^:]+): regexet ~(?<pat>.+) tog för lång tid""")]
    private static partial Regex TooSlow();

    [GeneratedRegex("""^svar\.(?<p>[^:]+): väntade (?<want>.+), fick \(saknas\)$""")]
    private static partial Regex MissingField();

    [GeneratedRegex("""^svar\.(?<p>[^:]+): väntade (?<want>.+), fick (?<got>.+)$""")]
    private static partial Regex Different();

    /// <summary>Översätter detaljrader från svarskontroller till meningar; okända rader behålls som de är.</summary>
    public static string DetailSentence(string line)
    {
        line = line.Trim();
        if (SpecPattern().Match(line) is { Success: true } m1)
            return $"Systemet svarade med {m1.Groups["p"].Value} = {Shorten(m1.Groups["v"].Value)}, men specen säger att den ska följa {Shorten(m1.Groups["pat"].Value)}.";
        if (MissingForPattern().Match(line) is { Success: true } m2)
            return $"Svaret saknar {m2.Groups["p"].Value}, som skulle följa {Shorten(m2.Groups["pat"].Value)}.";
        if (TooSlow().Match(line) is { Success: true } m3)
            return $"Kontrollen av {m3.Groups["p"].Value} mot {Shorten(m3.Groups["pat"].Value)} tog för lång tid och kunde inte avgöras.";
        if (NoMatch().Match(line) is { Success: true } m4)
            return $"Systemet svarade med {m4.Groups["p"].Value} = {Shorten(m4.Groups["v"].Value)}, men det skulle följa {Shorten(m4.Groups["pat"].Value)}.";
        if (MissingField().Match(line) is { Success: true } m5)
            return $"Förväntat {m5.Groups["p"].Value} = {Shorten(m5.Groups["want"].Value)}, men svaret saknar fältet.";
        if (Different().Match(line) is { Success: true } m6)
            return $"Förväntat {m6.Groups["p"].Value} = {Shorten(m6.Groups["want"].Value)}, men systemet svarade {Shorten(m6.Groups["got"].Value)}.";
        if (MoreLine().Match(line) is { Success: true } m7)
            return $"Och {m7.Groups["n"].Value} avvikelser till.";
        return line;
    }

    [GeneratedRegex("""^och (?<n>\d+) till$""")]
    private static partial Regex MoreLine();

    private static string DetailSentences(IEnumerable<string> details) =>
        string.Join(" ", details.Select(DetailSentence).Where(s => s.Length > 0).Select(EndWithPeriod));

    private static string EndWithPeriod(string s) => s.EndsWith('.') || s.EndsWith('!') || s.EndsWith('?') || s.EndsWith(':') ? s : s + ".";
}

/// <summary>Ett färdigt test: info, tekniskt resultat och klartext. Skickas till <see cref="RunOptions.OnResult"/>.</summary>
/// <param name="Index">Testets plats i körningen (börjar på 1).</param>
/// <param name="Total">Antal tester i körningen.</param>
/// <param name="Outcome">"OK", "FEL" eller "HOPPAD".</param>
/// <param name="TechnicalLines">Tekniska detaljer som rader (resultatrad, detaljer, anrop och svar), maskade.</param>
/// <param name="Curl">Curl-raden för anropet i fokus (maskad), eller null.</param>
public sealed record TestOutcome(int Index, int Total, TestInfo Test, CaseResult Result, PlainText Text, string Outcome,
    IReadOnlyList<string> TechnicalLines, string? Curl)
{
    public string Id => Test.Id;
    public string Group => Test.Group;
    /// <summary>Orsaken till FEL/HOPPAD (null för OK), se <see cref="PlainLanguage.Cause"/>.</summary>
    public FailureCause? Cause => Text.Cause;
}
