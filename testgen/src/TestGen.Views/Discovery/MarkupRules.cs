namespace TestGen.Views.Discovery;

/// <summary>Ett attribut som fast text. <paramref name="Present"/> är true när attributet finns med ett värde, också när värdet är ett uttryck (då är <paramref name="Fixed"/> null).</summary>
internal readonly record struct AttrValue(bool Present, string? Fixed);

/// <summary>
/// Det som skiljer markup åt mellan ramverken i <see cref="MarkupDoc"/>: hur ett attributvärde läses, vad som räknas som dynamiskt
/// (Razor mot JSX och mallar) och vad ett attribut betyder som fast text.
/// </summary>
internal abstract class MarkupRules
{
    /// <summary>Läser attributvärdet som börjar på <paramref name="k"/>. Ger värdet och läget efter det, eller -1 som läge om det aldrig stängs.</summary>
    public abstract (string Value, int Next) ReadValue(string text, int k);

    /// <summary>Texten innehåller något som bara körningen vet, så den är inte fast text.</summary>
    public abstract bool IsDynamic(ReadOnlySpan<char> text);

    /// <summary>Attributet <paramref name="name"/> på taggen, med bundna former (<c>:name</c>, <c>[name]</c>) om ramverket har dem.</summary>
    public abstract AttrValue ValueOf(Tag tag, string name);

    /// <summary>
    /// Ett uttryck som står där ett attribut förväntas (JSX <c>{...props}</c>). Ger läget efter uttrycket, -1 om det aldrig stängs,
    /// och null om det inte är något uttryck och det som står där är ett vanligt attribut.
    /// </summary>
    public virtual int? SkipExpression(string text, int index) => null;

    /// <summary>Kontrollen har attributet <c>required</c>.</summary>
    public virtual bool IsRequired(Tag tag) => tag.Has("required");

    /// <summary>Om värdet ska med i valen i en select, givet de som redan finns.</summary>
    public virtual bool KeepOption(IReadOnlyList<string> chosen, string value) => true;
}
