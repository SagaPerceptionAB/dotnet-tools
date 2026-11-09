namespace TestGen.Browser.Probe;

// Det sonderingen läser ur DOM:en. Rena data utan koppling till webbläsaren, så att sammanslagningen går att testa utan en.

/// <summary>Vad som lästes på en sida. Länkarna har absoluta adresser, så som webbläsaren löser dem.</summary>
internal sealed record PageSnapshot(
    string? Title,
    string? Heading,
    IReadOnlyList<DomLink> Links,
    IReadOnlyList<DomForm> Forms);

internal sealed record DomLink(string Href, string? Text);

/// <param name="SubmitText">Texten på formulärets submit-knapp.</param>
/// <param name="SubmitTestId">Knappens <c>data-testid</c>.</param>
internal sealed record DomForm(IReadOnlyList<DomField> Fields, string? SubmitText, string? SubmitTestId);

/// <param name="Tag"><c>input</c>, <c>select</c> eller <c>textarea</c>.</param>
/// <param name="Type">Värdet i type-attributet med små bokstäver (för input), annars null.</param>
/// <param name="Label">Tillgängligt namn: etikett, <c>aria-label</c> eller <c>aria-labelledby</c>.</param>
/// <param name="Value">Värdet i value-attributet, för radioknappar.</param>
/// <param name="Options">Värdena i en select (utan tomma).</param>
internal sealed record DomField(
    string Tag, string? Type, string? Name, string? Id, string? Label, string? Value, string? TestId,
    bool Required, int? MinLength, int? MaxLength, string? Min, string? Max, string? Pattern,
    IReadOnlyList<string> Options);

/// <summary>Resultatet av att öppna en adress: HTTP-status och det som lästes, eller felet.</summary>
/// <param name="FinalPath">Sökvägen när sidan väl laddats (efter omdirigeringar).</param>
internal sealed record PageResult(int Status, string FinalPath, PageSnapshot? Snapshot, string? Error = null)
{
    public bool TimedOut { get; init; }
}
