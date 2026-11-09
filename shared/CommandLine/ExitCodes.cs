namespace SagaPerception.CommandLine;

/// <summary>Exitkoderna för alla kommandon. Ett kommando använder de som passar det.</summary>
public static class ExitCodes
{
    /// <summary>Klart.</summary>
    public const int Ok = 0;
    /// <summary>Tester fallerade.</summary>
    public const int Failed = 1;
    /// <summary>Fel i användning eller konfiguration (flaggor, filer).</summary>
    public const int Usage = 2;
    /// <summary>Målet (API, adress) gick inte att nå.</summary>
    public const int Unreachable = 3;
    /// <summary>Osäkert resultat: inga säkra fel, men något kunde inte avgöras.</summary>
    public const int Inconclusive = 4;
    /// <summary>Internt fel: ett oväntat undantag.</summary>
    public const int Internal = 70;
    /// <summary>Avbrutet med Ctrl+C (128 + SIGINT).</summary>
    public const int Interrupted = 130;
    /// <summary>Avbrutet med SIGTERM (128 + 15), t.ex. av CI.</summary>
    public const int Terminated = 143;
}
