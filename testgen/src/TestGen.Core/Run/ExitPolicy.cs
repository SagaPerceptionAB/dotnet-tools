using TestGen.Core;
using TestGen.Core.Run;

namespace TestGen.Core.Run;

/// <summary>
/// Koderna för orsakerna i maskinläsbara format (JSON, JUnit, --fail-on): alltid de här engelska orden i ASCII.
/// </summary>
public static class CauseCodes
{
    public static string Code(FailureCause cause) => cause switch
    {
        FailureCause.System => "system",
        FailureCause.Expectation => "expectation",
        FailureCause.Auth => "auth",
        FailureCause.Environment => "environment",
        FailureCause.TestData => "test-data",
        FailureCause.MissingData => "missing-data",
        FailureCause.TestUserRejected => "test-user-rejected",
        FailureCause.RateLimited => "rate-limited",
        FailureCause.Tool => "tool",
        FailureCause.ReadOnly => "read-only",
        _ => "dependency",
    };

    public static readonly IReadOnlyList<FailureCause> All = Enum.GetValues<FailureCause>();

    /// <summary>Orsakerna som ger exitkod 1 om inget annat anges: Fel i systemet och Förväntan i testet stämmer inte.</summary>
    public static readonly IReadOnlySet<FailureCause> DefaultFailOn = new HashSet<FailureCause> { FailureCause.System, FailureCause.Expectation };

    /// <summary>Tar emot koden i valfritt skiftläge.</summary>
    public static bool TryParse(string text, out FailureCause cause)
    {
        var t = text.Trim().ToLowerInvariant();
        foreach (var c in All)
            if (Code(c) == t) { cause = c; return true; }
        cause = default;
        return false;
    }

    /// <summary>Tolkar --fail-on (komma- eller mellanslagsseparerad lista). Kastar <see cref="UsageException"/> på svenska vid okänd kod.</summary>
    public static IReadOnlySet<FailureCause> ParseFailOn(string text)
    {
        var set = new HashSet<FailureCause>();
        var parts = text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            throw new UsageException("--fail-on är tom.", $"Skriv orsakskoder med komma emellan, t.ex. --fail-on system,expectation. Koder: {string.Join(", ", All.Select(Code))}.");
        foreach (var p in parts)
        {
            if (!TryParse(p, out var c))
                throw new UsageException($"--fail-on: '{p}' är ingen orsakskod." + Suggest.DidYouMean(p, All.Select(Code).ToList()),
                    $"Koder: {string.Join(", ", All.Select(Code))}. Skriv dem med komma emellan, t.ex. --fail-on system,expectation,inloggning.");
            set.Add(c);
        }
        return set;
    }
}

/// <summary>Vad slutsatsen av en körning är, som ord för maskiner (verdictKind i JSON och JUnit).</summary>
public static class VerdictKinds
{
    /// <summary>
    /// ok = alla OK; system = minst ett FEL i systemet; expectation = inget fel i systemet men minst ett där förväntan inte stämde;
    /// uncertain = bara osäkra svar (inloggning, testdata, miljö, fel i testgen, följdfel); empty = inga tester kördes.
    /// </summary>
    public static string Of(IReadOnlyList<TestOutcome> outcomes)
    {
        if (outcomes.Count == 0) return "empty";
        var causes = outcomes.Select(o => o.Cause ?? PlainLanguage.Cause(o.Test, o.Result)).Where(c => c is not null and not FailureCause.ReadOnly).Select(c => c!.Value).ToList();
        if (causes.Count == 0) return "ok";
        if (causes.Contains(FailureCause.System)) return "system";
        if (causes.Contains(FailureCause.Expectation)) return "expectation";
        return "uncertain";
    }
}

/// <summary>Exitkoden för en färdig körning (0, 1 eller 4). 2, 3 och avbrott avgörs på annat håll.</summary>
public static class ExitPolicy
{
    /// <summary>
    /// 1 om något test har en orsak i <paramref name="failOn"/> (standard: Fel i systemet, Förväntan), annars 4 om något test har FEL eller
    /// HOPPAD av annan orsak, annars 0. Tester som hoppats över i en skrivskyddad miljö (read-only) räknas inte. Med <paramref name="comparison"/> och <paramref name="onlyNew"/> räknas bara NYA fel
    /// (sådana som inte fanns i förra körningen) som 1; fel som redan fanns förra gången ger 0 (eller 4 om det finns osäkra svar).
    /// </summary>
    public static int Decide(IReadOnlyList<TestOutcome> outcomes, IReadOnlySet<FailureCause>? failOn = null,
        BaselineComparison? comparison = null, bool onlyNew = false)
    {
        failOn ??= CauseCodes.DefaultFailOn;
        var withCause = outcomes.Select(o => (o.Id, Cause: o.Cause ?? PlainLanguage.Cause(o.Test, o.Result))).Where(x => x.Cause is not null and not FailureCause.ReadOnly).ToList();
        if (withCause.Count == 0) return ExitCodes.Ok;
        var failing = withCause.Where(x => failOn.Contains(x.Cause!.Value)).ToList();
        if (onlyNew && comparison is not null)
        {
            var newIds = comparison.New.Select(i => i.Id).ToHashSet();
            if (failing.Any(x => newIds.Contains(x.Id))) return ExitCodes.Failed;
            // Fel som redan fanns förra gången stoppar inte bygget; det som inte gick att avgöra gör det inte heller säkert grönt.
            return withCause.Any(x => !failOn.Contains(x.Cause!.Value)) ? ExitCodes.Inconclusive : ExitCodes.Ok;
        }
        if (failing.Count > 0) return ExitCodes.Failed;
        return ExitCodes.Inconclusive;
    }
}
