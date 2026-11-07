using System.Globalization;

namespace TestGen.Core.Run;

/// <summary>
/// Ett inspelat HTTP-anrop i ett test. Allt är redan maskat (hemliga headers som <c>***</c>, kända hemliga värden
/// utbytta i URL, bodies och svarshuvuden), så inspelningen kan skrivas ut och delas utan risk.
/// </summary>
/// <param name="Url">Fullständig URL efter ersättning av ${...}.</param>
/// <param name="Headers">Skickade headers, inklusive globala (--header/--token/miljö).</param>
/// <param name="Status">Null om inget svar kom (t.ex. timeout).</param>
/// <param name="Preparation">Operationen om anropet var förberedande (setup/beroende), annars null.</param>
/// <param name="ResponseHeaders">Svarshuvudena (Set-Cookie och andra hemliga är <c>***</c>). Null om inget svar kom.</param>
/// <param name="StartedAt">När anropet skickades (lokal tid med förskjutning).</param>
/// <param name="TestId">Testet anropet hör till, t.ex. T-018. För ett tidigare steg i ett Excel-scenario: stegets eget test.</param>
/// <param name="Step">Stegnummer i Excel-scenariot, annars null.</param>
/// <param name="EarlierStep">Sant om anropet är ett tidigare steg i scenariot (kopierat hit så att kedjan syns).</param>
/// <param name="UrlTemplate">Som <paramref name="Url"/> men med ${alias.fält} kvar. Null om URL:en inte har några.</param>
/// <param name="BodyTemplate">Som <paramref name="RequestBody"/> men med ${alias.fält} kvar. Null om bodyn inte har några.</param>
/// <param name="Uses">Värden som hämtades från tidigare svar (${alias.fält}) och användes i detta anrop.</param>
/// <param name="SavedAs">Namn som svaret sparades under (för ${namn.fält} i senare anrop).</param>
/// <param name="BaseUrl">API:ets bas-URL (med avslutande /).</param>
/// <param name="TimeoutSeconds">Tidsgränsen som gällde för anropet (0 = okänd).</param>
public sealed record Exchange(
    string Method,
    string Url,
    IReadOnlyDictionary<string, string> Headers,
    string? RequestBody,
    int? Status,
    string? ResponseBody,
    long ElapsedMs,
    string? Preparation = null,
    IReadOnlyDictionary<string, string>? ResponseHeaders = null,
    DateTimeOffset? StartedAt = null,
    string? TestId = null,
    int? Step = null,
    bool EarlierStep = false,
    string? UrlTemplate = null,
    string? BodyTemplate = null,
    IReadOnlyList<ValueUse>? Uses = null,
    IReadOnlyList<string>? SavedAs = null,
    string BaseUrl = "",
    int TimeoutSeconds = 0)
{
    /// <summary>Korrelations-id ur svarshuvudena (X-Request-Id m.fl.; trace-id ur traceparent), annars null.</summary>
    public string? CorrelationId => ExchangeCorrelation.Find(ResponseHeaders);

    /// <summary>Tidpunkten som ISO 8601 med millisekunder och förskjutning, t.ex. 2026-11-01T16:41:02.123+01:00.</summary>
    public string? StartedAtText => StartedAt?.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture);
}

/// <summary>
/// Ett värde ur ett tidigare svar som användes i ett anrop: <c>${kund.id}</c> gav <c>3</c>.
/// </summary>
/// <param name="Expression">Uttrycket utan ${ }, t.ex. <c>kund.id</c>.</param>
/// <param name="Value">Värdet som skickades.</param>
/// <param name="IsString">Sant om värdet är en JSON-sträng (annars tal/bool, som skrivs utan citattecken i en body).</param>
/// <param name="Target">Var värdet användes, t.ex. <c>customerId</c>, <c>sökvägen</c> eller <c>parametern limit</c>.</param>
public sealed record ValueUse(string Expression, string Value, bool IsString, string Target)
{
    /// <summary>Namnet före första punkten (svaret som värdet hämtades ur).</summary>
    public string Alias => Expression.Split('.')[0];

    /// <summary>Fältet utan namnet, t.ex. <c>id</c> (hela uttrycket om det inte har något fält).</summary>
    public string Field => Expression.Contains('.') ? Expression[(Expression.IndexOf('.') + 1)..] : Expression;
}
