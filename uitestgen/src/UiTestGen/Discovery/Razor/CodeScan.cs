namespace UiTestGen.Discovery.Razor;

/// <summary>Enkel skanning av C#-text: hittar den avslutande parentesen eller klammern och hoppar över strängar, tecken och kommentarer.</summary>
internal static class CodeScan
{
    /// <summary>Index för tecknet som stänger <c>{</c>, <c>(</c> eller <c>[</c> på <paramref name="open"/>, eller -1 om det saknas.</summary>
    public static int Matching(string text, int open)
    {
        var opener = text[open];
        var closer = opener switch { '{' => '}', '(' => ')', '[' => ']', _ => throw new ArgumentException("Måste vara { ( eller [.", nameof(open)) };
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"') { i = SkipString(text, i); continue; }
            if (c == '\'') { i = SkipChar(text, i); continue; }
            if (c == '/' && i + 1 < text.Length)
            {
                if (text[i + 1] == '/') { i = LineEnd(text, i); continue; }
                if (text[i + 1] == '*')
                {
                    var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (end < 0) return -1;
                    i = end + 1;
                    continue;
                }
            }
            if (c == opener) depth++;
            else if (c == closer && --depth == 0) return i;
        }
        return -1;
    }

    // Returnerar indexet för strängens sista tecken (eller sista tecknet i texten om den aldrig stängs).
    private static int SkipString(string text, int quote)
    {
        if (quote + 2 < text.Length && text[quote + 1] == '"' && text[quote + 2] == '"')
        {
            var n = 3;
            while (quote + n < text.Length && text[quote + n] == '"') n++;
            var closing = new string('"', n);
            var end = text.IndexOf(closing, quote + n, StringComparison.Ordinal);
            return end < 0 ? text.Length - 1 : end + n - 1;
        }
        var verbatim = (quote > 0 && text[quote - 1] == '@') || (quote > 1 && text[quote - 1] == '$' && text[quote - 2] == '@');
        for (var i = quote + 1; i < text.Length; i++)
        {
            var c = text[i];
            if (verbatim)
            {
                if (c != '"') continue;
                if (i + 1 < text.Length && text[i + 1] == '"') { i++; continue; }
                return i;
            }
            if (c == '\\') { i++; continue; }
            if (c == '"' || c == '\n') return i;
        }
        return text.Length - 1;
    }

    private static int SkipChar(string text, int quote)
    {
        var close = quote + 1 < text.Length && text[quote + 1] == '\\' ? quote + 3 : quote + 2;
        return close < text.Length && text[close] == '\'' ? close : quote;
    }

    private static int LineEnd(string text, int from)
    {
        var end = text.IndexOf('\n', from);
        return end < 0 ? text.Length - 1 : end;
    }
}
