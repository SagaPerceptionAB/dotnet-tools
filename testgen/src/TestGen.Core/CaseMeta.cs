using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TestGen.Core;

/// <summary>Vilken regel i specen ett genererat testfall prövar.</summary>
public enum TestRule
{
    /// <summary>Giltig indata (inget regelbrott).</summary>
    Valid,
    /// <summary>Ett obligatoriskt fält skickas inte.</summary>
    Missing,
    BelowMinimum,
    AboveMaximum,
    TooLong,
    TooShort,
    NotInEnum,
    WrongType,
    PatternMismatch,
    /// <summary>Texten har fel format (e-post, datum, uuid, uri).</summary>
    BadFormat,
    /// <summary>Ett obligatoriskt textfält skickas som tom text.</summary>
    Empty,
    /// <summary>Listan har färre element än minItems.</summary>
    TooFewItems,
    /// <summary>Listan har fler element än maxItems.</summary>
    TooManyItems,
}

/// <summary>
/// Strukturerad information om ett genererat testfall (hålls i minnet; skrivs aldrig till scenariofilerna).
/// </summary>
/// <param name="Rule">Regeln som prövas.</param>
/// <param name="Field">Fältet som ändrats (null för giltiga fall).</param>
/// <param name="Limit">
/// Gränsvärdet ur specen som text: minimum/maximum (tal), längd (heltal), mönstret, typnamnet (t.ex. "integer")
/// eller de tillåtna värdena som JSON-lista med komma ("active", "inactive"). Null för giltiga fall och saknade fält.
/// </param>
/// <param name="SentValue">Värdet som skickades, som JSON-text (strängar med citattecken). Null om fältet inte skickades.</param>
/// <param name="Exclusive">Minimum/maximum är exklusivt (värdet får inte vara lika med gränsen).</param>
public sealed record CaseMeta(TestRule Rule, string? Field = null, string? Limit = null, string? SentValue = null, bool Exclusive = false)
{
    public static readonly CaseMeta Valid = new(TestRule.Valid);

    /// <summary>
    /// Giltigt fall för ett annat alternativ (oneOf/anyOf) än det första: <paramref name="field"/> är fältet
    /// ("betalning", tomt för hela bodyn) och <paramref name="alternative"/> alternativets namn ("faktura").
    /// </summary>
    public static CaseMeta ValidAlternative(string field, string alternative) => new(TestRule.Valid, field.Length == 0 ? null : field, alternative);

    private static readonly JsonSerializerOptions Plain = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>JSON-text för ett värde, utan att åäö m.m. kodas om.</summary>
    public static string? Json(JsonNode? node) => node?.ToJsonString(Plain);
}

/// <summary>Ett genererat scenario tillsammans med sin metadata.</summary>
public sealed record GeneratedCase(TestGen.Scenarios.Scenario Scenario, CaseMeta Meta);
