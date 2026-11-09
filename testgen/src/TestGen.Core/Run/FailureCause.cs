namespace TestGen.Core.Run;

/// <summary>
/// Varför ett test fick FEL eller HOPPAD. Samma orsak ger samma namn och förklaring i terminal, HTML, Excel och webbgränssnitt
/// (se <see cref="PlainLanguage.CauseName"/> och <see cref="PlainLanguage.CauseExplanation"/>). Ordningen är den som
/// sammanfattningar visar: det viktigaste först.
/// </summary>
public enum FailureCause
{
    /// <summary>"Fel i systemet": 5xx (utom 502/503/504, som är Environment), fel status mot specen eller svar som inte följer specen/mönster.</summary>
    System,
    /// <summary>"Förväntan i testet stämmer inte": Excel-raden väntade sig något annat, men systemets svar följer specen.</summary>
    Expectation,
    /// <summary>"Inloggning": 401/403. Se <see cref="AuthVariant"/> för om en token skickades.</summary>
    Auth,
    /// <summary>"Miljön svarar inte": timeout, inget svar, anslutningsfel eller 502/503/504.</summary>
    Environment,
    /// <summary>"Testdata fanns redan": 409 i testet eller i ett förberedande anrop.</summary>
    TestData,
    /// <summary>"Testdata saknas": HOPPAD för att ett giltigt anrop med påhittade värden fick ett dokumenterat 404. Värdet måste anges i testgen.yaml (bindings).</summary>
    MissingData,
    /// <summary>"Testanvändaren godtogs inte": HOPPAD för att ett giltigt, inloggat anrop med testgens testanvändare fick ett dokumenterat 401 eller 403. Appen slår upp användaren själv; ange claims eller ett inloggningssteg.</summary>
    TestUserRejected,
    /// <summary>"För många anrop": HOPPAD för att systemet svarade 429 (för många anrop) även efter att Retry-After följts.</summary>
    RateLimited,
    /// <summary>"Fel i testgen": verktyget kunde inte bygga eller skicka testet (internt fel, eller en bodytyp det inte stöder ännu). Säger inget om systemet.</summary>
    Tool,
    /// <summary>"Följdfel": HOPPAD, eller FEL för att ett tidigare steg/förberedande anrop föll av annan orsak.</summary>
    Cascade,
    /// <summary>"Skrivskyddad miljö": HOPPAD för att miljön bara tillåter läsande anrop (GET, HEAD, OPTIONS). Räknas inte som fel och påverkar inte exitkoden.</summary>
    ReadOnly,
}

/// <summary>De två varianterna av <see cref="FailureCause.Auth"/>.</summary>
public enum AuthVariant
{
    /// <summary>Ingen token (Authorization eller hemlig header) skickades.</summary>
    NoToken,
    /// <summary>En token skickades men nekades.</summary>
    TokenRejected,
}

/// <summary>En rad i orsakssammanfattningen: en orsak, hur många tester och vilka.</summary>
/// <param name="Cause">Orsaken.</param>
/// <param name="Name">Visningsnamn, t.ex. "Fel i systemet".</param>
/// <param name="Count">Antal tester med orsaken.</param>
/// <param name="TestIds">De första testerna (högst 10) i körordning.</param>
/// <param name="More">Antal tester utöver <paramref name="TestIds"/>.</param>
/// <param name="Sentence">En mening om orsaken, t.ex. "2 tester krockade med befintlig testdata (409) – säger inget om systemet."</param>
public sealed record CauseLine(FailureCause Cause, string Name, int Count, IReadOnlyList<string> TestIds, int More, string Sentence)
{
    /// <summary>Raden i listform, t.ex. "Testdata fanns redan (409): T-001, T-012 – säger inget om systemet".</summary>
    public string ListText => PlainLanguage.CauseLineText(this);

    /// <summary>"T-001, T-002 och 3 till" (eller bara id:na om alla ryms).</summary>
    public string IdsText => More > 0 ? string.Join(", ", TestIds) + $" och {More} till" : string.Join(", ", TestIds);
}
