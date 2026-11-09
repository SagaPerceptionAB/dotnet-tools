using System.Text.RegularExpressions;
using TestGen.Core.Views;

namespace TestGen.Views.Discovery.Razor;

/// <summary>Delar som Blazor och Razor Pages läser likadant ur markup: rubrik, länkar, knappar och val.</summary>
internal static partial class ViewParts
{
    /// <summary>Den fasta texten i första elementet med namnet. Null om elementet saknas eller innehåller något dynamiskt.</summary>
    public static string? FirstFixedText(MarkupDoc doc, string element) =>
        doc.Tags.FirstOrDefault(t => t.Is(element)) is { } tag ? doc.FixedText(tag) : null;

    /// <summary>Etiketterna i taggarna: id (från <c>for</c>) till fast text.</summary>
    public static Dictionary<string, string> LabelsByFor(MarkupDoc doc, IEnumerable<Tag> tags, string attribute = "for")
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tag in tags.Where(t => t.Is("label")))
            if (tag[attribute] is { Length: > 0 } target && doc.FixedText(tag) is { } text) labels.TryAdd(target, text);
        return labels;
    }

    /// <summary>
    /// Knappen som skickar formuläret: första <c>&lt;button&gt;</c> utan annan typ än submit, eller <c>&lt;input type="submit"&gt;</c>,
    /// med dess fasta text (knappens text, eller inputens <c>value</c>). Null om formuläret saknar en sådan knapp.
    /// </summary>
    public static (Tag Tag, string? Text)? SubmitButton(MarkupDoc doc, IEnumerable<Tag> inside)
    {
        foreach (var tag in inside)
        {
            var type = doc.Fixed(tag, "type");
            if (tag.Is("button") && (type is null || type.Equals("submit", StringComparison.OrdinalIgnoreCase))) return (tag, doc.FixedText(tag));
            if (tag.Is("input") && string.Equals(type, "submit", StringComparison.OrdinalIgnoreCase)) return (tag, doc.Fixed(tag, "value"));
        }
        return null;
    }

    /// <summary>Väljaren för knappen som skickar formuläret: dess roll och text, eller <c>[type=submit]</c> om den saknar fast text.</summary>
    public static Locator Submit(MarkupDoc doc, IEnumerable<Tag> inside) =>
        SubmitButton(doc, inside) is { Text: { } text }
            ? new Locator(LocatorKind.Role, "button", text)
            : new Locator(LocatorKind.Css, "[type=submit]");

    /// <summary>
    /// Värdena i en select: <c>value</c> på varje <c>&lt;option&gt;</c>, annars dess text. Dynamiska val hoppas över,
    /// och <see cref="MarkupRules.KeepOption"/> avgör om ett värde får vara tomt eller förekomma två gånger.
    /// </summary>
    public static List<string> Options(MarkupDoc doc, Tag select)
    {
        var options = new List<string>();
        foreach (var option in doc.Inside(select).Where(t => t.Is("option")))
        {
            var attribute = doc.Rules.ValueOf(option, "value");
            var value = attribute.Present ? attribute.Fixed : doc.TextUntilNextTag(option);
            if (value is not null && doc.Rules.KeepOption(options, value)) options.Add(value);
        }
        return options;
    }

    /// <summary>Ett bindningsuttryck som <c>Input.Name</c> eller <c>@Input.Name</c> delat i led. Null om det inte är en enkel kedja av namn.</summary>
    public static string[]? Path(string? expression)
    {
        if (expression is null) return null;
        var e = expression.Trim().TrimStart('@').Replace("!", "", StringComparison.Ordinal).Replace("?", "", StringComparison.Ordinal);
        return SimpleChain().IsMatch(e) ? e.Split('.') : null;
    }

    /// <summary>Väljaren för ett fält utan etikett: id:t om det finns, annars <c>name</c>.</summary>
    public static Locator ForField(string? label, string? id, string? name)
    {
        if (label is not null) return new Locator(LocatorKind.Label, label);
        if (!string.IsNullOrEmpty(id)) return new Locator(LocatorKind.Css, ModelAttributes.CssId(id));
        return new Locator(LocatorKind.Css, $"[name=\"{name}\"]");
    }

    [GeneratedRegex(@"^[A-Za-z_]\w*(\.[A-Za-z_]\w*)*$")]
    private static partial Regex SimpleChain();
}
