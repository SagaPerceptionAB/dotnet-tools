using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace TestGen.Views.Discovery;

internal sealed record Attr(string Name, string? Value);

/// <summary>
/// En start-, slut- eller självstängande tagg. <see cref="Start"/> och <see cref="End"/> är lägen i texten (End är efter <c>&gt;</c>),
/// <see cref="Index"/> är taggens plats i <see cref="MarkupDoc.Tags"/>.
/// </summary>
internal sealed record Tag(string Name, IReadOnlyList<Attr> Attrs, int Start, int End, bool Closing, bool SelfClosing, int Index)
{
    /// <summary>Attributets värde (stora och små bokstäver spelar ingen roll), eller null om det saknas eller saknar värde.</summary>
    public string? this[string name] => Attrs.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

    public bool Has(string name) => Attrs.Any(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Starttagg (eller självstängande) med det namnet.</summary>
    public bool Is(string name) => !Closing && Name.Equals(name, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Taggskanning av markup: tillräckligt för att hitta taggar, attribut och fast text. Tolkar inte uttryck. Hoppar över
/// <c>&lt;!-- --&gt;</c> och tar bara namn som ser ut som taggnamn (<c>a</c>, <c>NavLink</c>, <c>router-link</c>, <c>Foo.Bar</c>).
/// Hur attributvärden läses och vad som räknas som dynamiskt avgörs av <see cref="MarkupRules"/>: Razor (<c>@</c>) eller
/// JSX, Angular-mallar och Vue (<c>{}</c>, <c>{{ }}</c>, <c>$</c>).
/// </summary>
internal sealed partial class MarkupDoc
{
    private readonly Tag[] _tags;
    private readonly int[] _closeIndex; // per tagg: platsen för slutaggen (eller den första taggen efter elementet för självstängande), annars antalet taggar

    public MarkupDoc(string text, MarkupRules rules)
    {
        Text = text;
        Rules = rules;
        _tags = Scan(text, rules);
        _closeIndex = MatchClosingTags(_tags);
    }

    public string Text { get; }

    public MarkupRules Rules { get; }

    public IReadOnlyList<Tag> Tags => _tags;

    /// <summary>Läget där slutaggen för <paramref name="open"/> börjar (eller slutet av texten om den saknas).</summary>
    public int CloseStart(Tag open)
    {
        if (open.SelfClosing) return open.End;
        var close = _closeIndex[open.Index];
        return close < _tags.Length ? _tags[close].Start : Text.Length;
    }

    /// <summary>Taggarna mellan <paramref name="open"/> och dess slutagg.</summary>
    public IReadOnlyList<Tag> Inside(Tag open) =>
        new ArraySegment<Tag>(_tags, open.Index + 1, Math.Max(0, _closeIndex[open.Index] - open.Index - 1));

    /// <summary>Texten i elementet om den är fast (inget dynamiskt enligt <see cref="MarkupRules.IsDynamic"/>), annars null. Taggar inuti tas bort och blanktecken slås ihop.</summary>
    public string? FixedText(Tag open)
    {
        if (open.SelfClosing) return null;
        var close = CloseStart(open);
        var sb = new StringBuilder();
        var pos = open.End;
        foreach (var t in Inside(open))
        {
            sb.Append(Text, pos, t.Start - pos);
            pos = t.End;
        }
        if (pos < close) sb.Append(Text, pos, close - pos);
        return Clean(sb.ToString());
    }

    /// <summary>Texten fram till nästa tagg, för element som kan sakna slutagg (<c>&lt;option&gt;</c>).</summary>
    public string? TextUntilNextTag(Tag open)
    {
        var next = Text.IndexOf('<', open.End);
        return Clean(next < 0 ? Text[open.End..] : Text[open.End..next]);
    }

    /// <summary>Attributets värde som fast text, eller null om det saknas eller är dynamiskt.</summary>
    public string? Fixed(Tag tag, string name) => Rules.ValueOf(tag, name).Fixed;

    private string? Clean(string inner)
    {
        if (Rules.IsDynamic(inner)) return null;
        inner = Spaces().Replace(WebUtility.HtmlDecode(inner), " ").Trim();
        return inner.Length == 0 ? null : inner;
    }

    // Slutaggarna för alla element i ett svep, med en stack per taggnamn: O(antalet taggar), i stället för att söka framåt för varje element.
    private static int[] MatchClosingTags(Tag[] tags)
    {
        var close = new int[tags.Length];
        Array.Fill(close, tags.Length);
        var open = new Dictionary<string, Stack<int>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < tags.Length; i++)
        {
            var tag = tags[i];
            if (tag.Closing)
            {
                if (open.TryGetValue(tag.Name, out var stack) && stack.Count > 0) close[stack.Pop()] = i;
            }
            else if (tag.SelfClosing) close[i] = i + 1;
            else
            {
                if (!open.TryGetValue(tag.Name, out var stack)) open[tag.Name] = stack = new Stack<int>();
                stack.Push(i);
            }
        }
        return close;
    }

    private static Tag[] Scan(string t, MarkupRules rules)
    {
        var tags = new List<Tag>();
        var i = 0;
        while (i < t.Length)
        {
            var lt = t.IndexOf('<', i);
            if (lt < 0 || lt + 1 >= t.Length) break;
            if (string.CompareOrdinal(t, lt, "<!--", 0, 4) == 0)
            {
                var close = t.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                if (close < 0) break;
                i = close + 3;
                continue;
            }
            var closing = t[lt + 1] == '/';
            var nameStart = lt + (closing ? 2 : 1);
            if (nameStart >= t.Length || !char.IsLetter(t[nameStart])) { i = lt + 1; continue; }
            var tag = Read(t, rules, lt, nameStart, closing, tags.Count);
            if (tag is null) { i = lt + 1; continue; }
            tags.Add(tag);
            i = tag.End;
        }
        return [.. tags];
    }

    private static Tag? Read(string t, MarkupRules rules, int start, int nameStart, bool closing, int index)
    {
        var j = nameStart;
        while (j < t.Length && (char.IsLetterOrDigit(t[j]) || t[j] is '-' or '_' or '.' or ':')) j++;
        if (j >= t.Length || !(char.IsWhiteSpace(t[j]) || t[j] is '/' or '>')) return null;
        var name = t[nameStart..j];
        var attrs = new List<Attr>();
        var selfClosing = false;
        while (true)
        {
            while (j < t.Length && char.IsWhiteSpace(t[j])) j++;
            if (j >= t.Length) return null;
            if (t[j] == '>') { j++; break; }
            if (t[j] == '/')
            {
                if (j + 1 < t.Length && t[j + 1] == '>') { selfClosing = true; j += 2; break; }
                j++;
                continue;
            }
            if (rules.SkipExpression(t, j) is { } afterExpression)
            {
                if (afterExpression < 0) return null;
                j = afterExpression;
                continue;
            }
            var s = j;
            while (j < t.Length && !char.IsWhiteSpace(t[j]) && t[j] != '=' && t[j] != '>' && !(t[j] == '/' && j + 1 < t.Length && t[j + 1] == '>')) j++;
            if (j == s) { j++; continue; }
            var attrName = t[s..j];
            var k = j;
            while (k < t.Length && char.IsWhiteSpace(t[k])) k++;
            if (k >= t.Length || t[k] != '=') { attrs.Add(new Attr(attrName, null)); continue; }
            k++;
            while (k < t.Length && char.IsWhiteSpace(t[k])) k++;
            if (k >= t.Length) return null;
            var (value, next) = rules.ReadValue(t, k);
            if (next < 0) return null;
            attrs.Add(new Attr(attrName, value));
            j = next;
        }
        return new Tag(name, attrs, start, j, closing, selfClosing, index);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
