using System.Text;

namespace UiTestGen.Discovery.Spa;

internal enum TokKind { Ident, Number, String, Template, Regex, Punct }

/// <summary>En token. För <see cref="TokKind.String"/> och <see cref="TokKind.Template"/> är <see cref="Text"/> värdet utan citattecken; i en template literal blir <c>${...}</c> till <c>{}</c>.</summary>
internal readonly record struct Tok(TokKind Kind, string Text, int Start, int End, bool Holes = false)
{
    public bool IsPunct(char c) => Kind == TokKind.Punct && Text.Length == 1 && Text[0] == c;
    public bool IsIdent(string name) => Kind == TokKind.Ident && Text == name;
    public bool IsText => Kind is TokKind.String or TokKind.Template;
}

/// <summary>
/// En liten tokeniserare för JavaScript/TypeScript (och JSX): tillräckligt för att hitta objektlitteraler, anrop och taggar.
/// Hoppar över kommentarer, förstår strängar, template literals (med nästlade <c>${}</c>) och reguljära uttryck.
/// Kraschar aldrig: det den inte förstår blir <see cref="TokKind.Punct"/>.
/// </summary>
internal static class JsTokenizer
{
    private static readonly HashSet<string> RegexAfterKeyword = new(StringComparer.Ordinal)
        { "return", "typeof", "case", "in", "of", "delete", "void", "throw", "new", "else", "do", "yield", "await" };

    public static List<Tok> Tokenize(string t)
    {
        var tokens = new List<Tok>();
        var i = 0;
        while (i < t.Length)
        {
            var c = t[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < t.Length && t[i + 1] == '/') { while (i < t.Length && t[i] != '\n') i++; continue; }
            if (c == '/' && i + 1 < t.Length && t[i + 1] == '*')
            {
                var close = t.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? t.Length : close + 2;
                continue;
            }
            if (c is '\'' or '"')
            {
                var end = ScanString(t, i, out var value);
                if (end < 0) { tokens.Add(new Tok(TokKind.Punct, c.ToString(), i, i + 1)); i++; continue; }
                tokens.Add(new Tok(TokKind.String, value, i, end));
                i = end;
                continue;
            }
            if (c == '`')
            {
                var end = ScanTemplate(t, i, out var value, out var holes);
                if (end < 0) { tokens.Add(new Tok(TokKind.Punct, "`", i, i + 1)); i++; continue; }
                tokens.Add(new Tok(TokKind.Template, value, i, end, holes));
                i = end;
                continue;
            }
            if (char.IsLetter(c) || c is '_' or '$')
            {
                var s = i;
                while (i < t.Length && (char.IsLetterOrDigit(t[i]) || t[i] is '_' or '$')) i++;
                tokens.Add(new Tok(TokKind.Ident, t[s..i], s, i));
                continue;
            }
            if (char.IsDigit(c))
            {
                var s = i;
                while (i < t.Length && (char.IsLetterOrDigit(t[i]) || t[i] is '_' or '.')) i++;
                if (t[i - 1] == '.') i--;
                tokens.Add(new Tok(TokKind.Number, t[s..i], s, i));
                continue;
            }
            if (c == '/' && RegexAllowed(tokens) && ScanRegex(t, i, out var rEnd, out var body))
            {
                tokens.Add(new Tok(TokKind.Regex, body, i, rEnd));
                i = rEnd;
                continue;
            }
            tokens.Add(new Tok(TokKind.Punct, c.ToString(), i, i + 1));
            i++;
        }
        return tokens;
    }

    /// <summary>Index för den avslutande parentesen som hör till <c>(</c>, <c>[</c> eller <c>{</c> på <paramref name="open"/>, eller -1.</summary>
    public static int Matching(IReadOnlyList<Tok> tokens, int open)
    {
        var depth = 0;
        for (var i = open; i < tokens.Count; i++)
        {
            var tok = tokens[i];
            if (tok.Kind != TokKind.Punct) continue;
            if (tok.Text is "(" or "[" or "{") depth++;
            else if (tok.Text is ")" or "]" or "}") { if (--depth == 0) return i; }
        }
        return -1;
    }

    private static bool RegexAllowed(List<Tok> tokens)
    {
        if (tokens.Count == 0) return true;
        var prev = tokens[^1];
        return prev.Kind switch
        {
            TokKind.Punct => !(prev.Text is ")" or "]" or "}" or "<"),
            TokKind.Ident => RegexAfterKeyword.Contains(prev.Text),
            _ => false,
        };
    }

    private static bool ScanRegex(string t, int start, out int end, out string body)
    {
        end = start; body = "";
        if (start + 1 >= t.Length || t[start + 1] is '>' or '*' or '/') return false;
        var i = start + 1;
        var inClass = false;
        while (i < t.Length && t[i] != '\n')
        {
            var c = t[i];
            if (c == '\\') { i += 2; continue; }
            if (c == '[') inClass = true;
            else if (c == ']') inClass = false;
            else if (c == '/' && !inClass)
            {
                body = t[(start + 1)..i];
                i++;
                while (i < t.Length && char.IsLetter(t[i])) i++;
                end = i;
                return true;
            }
            i++;
        }
        return false;
    }

    // Läser en sträng med ' eller ". Returnerar slutläget, eller -1 om raden tar slut först (då var citattecknet text i JSX, t.ex. don't).
    private static int ScanString(string t, int start, out string value)
    {
        var quote = t[start];
        var sb = new StringBuilder();
        var i = start + 1;
        while (i < t.Length)
        {
            var c = t[i];
            if (c == '\n') break;
            if (c == '\\' && i + 1 < t.Length) { sb.Append(Unescape(t[i + 1])); i += 2; continue; }
            if (c == quote) { value = sb.ToString(); return i + 1; }
            sb.Append(c);
            i++;
        }
        value = "";
        return -1;
    }

    private static int ScanTemplate(string t, int start, out string value, out bool holes)
    {
        var sb = new StringBuilder();
        holes = false;
        var i = start + 1;
        while (i < t.Length)
        {
            var c = t[i];
            if (c == '\\' && i + 1 < t.Length) { sb.Append(Unescape(t[i + 1])); i += 2; continue; }
            if (c == '`') { value = sb.ToString(); return i + 1; }
            if (c == '$' && i + 1 < t.Length && t[i + 1] == '{')
            {
                var close = SkipBraces(t, i + 1);
                if (close < 0) break;
                sb.Append("{}");
                holes = true;
                i = close + 1;
                continue;
            }
            sb.Append(c);
            i++;
        }
        value = "";
        return -1;
    }

    // Index för } som hör till { på open, med hänsyn till strängar och nästlade template literals. -1 om den saknas.
    internal static int SkipBraces(string t, int open)
    {
        var depth = 0;
        var i = open;
        while (i < t.Length)
        {
            var c = t[i];
            if (c is '\'' or '"')
            {
                var end = ScanString(t, i, out _);
                i = end < 0 ? i + 1 : end;
                continue;
            }
            if (c == '`')
            {
                var end = ScanTemplate(t, i, out _, out _);
                i = end < 0 ? i + 1 : end;
                continue;
            }
            if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return i;
            i++;
        }
        return -1;
    }

    private static char Unescape(char c) => c switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => c };
}
