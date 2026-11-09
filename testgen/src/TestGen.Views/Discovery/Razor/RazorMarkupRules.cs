namespace TestGen.Views.Discovery.Razor;

/// <summary>
/// Markup i Razor: attributvärden kan innehålla uttryck som <c>@($"/products/{p.Id}")</c> med citattecken inuti, och allt med
/// <c>@</c> är dynamiskt.
/// </summary>
internal sealed class RazorMarkupRules : MarkupRules
{
    public static readonly RazorMarkupRules Instance = new();

    private RazorMarkupRules() { }

    // Citerade värden kan innehålla @( ... ) med citattecken inuti.
    public override (string Value, int Next) ReadValue(string text, int k)
    {
        var quote = text[k] is '"' or '\'' ? text[k] : '\0';
        var m = quote == '\0' ? k : k + 1;
        while (true)
        {
            if (m >= text.Length) return ("", -1);
            var c = text[m];
            if (c == '@' && m + 1 < text.Length && text[m + 1] == '(')
            {
                var close = CodeScan.Matching(text, m + 1);
                if (close < 0) return ("", -1);
                m = close + 1;
                continue;
            }
            if (quote != '\0' ? c == quote : char.IsWhiteSpace(c) || c == '>') break;
            m++;
        }
        return quote == '\0' ? (text[k..m], m) : (text[(k + 1)..m], m + 1);
    }

    public override bool IsDynamic(ReadOnlySpan<char> text) => text.Contains('@');

    public override AttrValue ValueOf(Tag tag, string name) =>
        tag[name] is { } value ? new AttrValue(true, value.Contains('@') ? null : value) : default;
}
