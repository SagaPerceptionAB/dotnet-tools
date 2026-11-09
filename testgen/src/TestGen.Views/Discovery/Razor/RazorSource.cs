using System.Text.RegularExpressions;

namespace TestGen.Views.Discovery.Razor;

/// <summary>En <c>@page</c>-rad. <paramref name="Readable"/> är false när rutten inte är en fast sträng (t.ex. <c>@page @Konstant</c>).</summary>
internal sealed record PageDirective(bool Readable, string? Template, string Raw);

/// <summary>En .razor- eller .cshtml-fil, läst som text och förberedd för sökning. Kommentarer är borttagna.</summary>
internal sealed partial class RazorSource
{
    private MarkupDoc? _doc;

    private RazorSource(string path, string text, string markup, IReadOnlyList<string> codeBlocks)
    {
        Path = path;
        Text = text;
        Markup = markup;
        CodeBlocks = codeBlocks;
    }

    public string Path { get; }

    /// <summary>Markupen som taggdokument. Skapas en gång och delas av alla som läser filen.</summary>
    public MarkupDoc Doc => _doc ??= new MarkupDoc(Markup, RazorMarkupRules.Instance);

    /// <summary>Hela filen utan Razor- och HTML-kommentarer.</summary>
    public string Text { get; }

    /// <summary>Som <see cref="Text"/> men utan <c>@code</c>-block och utan innehållet i script och style, så att bara markup finns kvar.</summary>
    public string Markup { get; }

    /// <summary>Innehållet i <c>@code { ... }</c> och <c>@functions { ... }</c>.</summary>
    public IReadOnlyList<string> CodeBlocks { get; }

    /// <summary>Förbereder filens text: tar bort kommentarer och plockar ut kodblocken. Filen läses av <see cref="SourceCache"/>.</summary>
    public static RazorSource Parse(string path, string raw)
    {
        var text = Blank(HtmlComment(), Blank(RazorComment(), raw));
        var blocks = new List<string>();
        var markup = text.ToCharArray();
        foreach (Match m in CodeStart().Matches(text))
        {
            var open = m.Index + m.Length - 1;
            if (markup[m.Index] == ' ') continue; // ligger inuti ett block som redan tagits bort
            var close = CodeScan.Matching(text, open);
            if (close < 0) close = text.Length - 1;
            blocks.Add(text[(open + 1)..close]);
            Array.Fill(markup, ' ', m.Index, close - m.Index + 1);
        }
        return new RazorSource(path, text, Blank(ScriptOrStyle(), new string(markup)), blocks);
    }

    /// <summary>Alla <c>@page</c>-rader i filen.</summary>
    public IReadOnlyList<PageDirective> Pages()
    {
        var result = new List<PageDirective>();
        foreach (Match m in PageLine().Matches(Markup))
        {
            var rest = m.Groups["rest"].Value.Trim();
            if (rest.Length == 0) { result.Add(new PageDirective(true, null, rest)); continue; }
            var q = QuotedStart().Match(rest);
            result.Add(q.Success && !q.Groups[1].Value.Contains('@')
                ? new PageDirective(true, Unescape(q.Groups[1].Value), rest)
                : new PageDirective(false, null, rest));
        }
        return result;
    }

    /// <summary>Det första värdet som sätts med <c>ViewData["Title"] = "...";</c>, om det är en fast sträng.</summary>
    public string? ViewDataTitle()
    {
        var m = ViewDataTitleSet().Match(Text);
        return m.Success ? Unescape(m.Groups[1].Value) : null;
    }

    /// <summary>Typnamnet i <c>@model X</c>, utan namnrymd.</summary>
    public string? ModelType()
    {
        var m = ModelLine().Match(Markup);
        if (!m.Success) return null;
        var name = m.Groups[1].Value.TrimEnd('?');
        var generic = name.IndexOf('<', StringComparison.Ordinal);
        if (generic >= 0) name = name[..generic];
        return name[(name.LastIndexOf('.') + 1)..];
    }

    /// <summary><c>@attribute [Authorize]</c> finns (och inte bara <c>[AllowAnonymous]</c>).</summary>
    public bool HasAuthorize() => AuthorizeAttribute().IsMatch(Markup);

    /// <summary>Namnet i <c>@layout X</c> utan namnrymd (för <c>@layout null</c> texten "null"), eller null om raden saknas.</summary>
    public string? LayoutDirective()
    {
        var m = LayoutLine().Match(Markup);
        if (!m.Success) return null;
        var name = m.Groups[1].Value;
        return name[(name.LastIndexOf('.') + 1)..];
    }

    /// <summary>
    /// Den sista tilldelningen <c>Layout = "X";</c> eller <c>Layout = null;</c> i filen (Razor Pages och MVC). Första värdet är om en
    /// tilldelning finns, det andra namnet (null för <c>Layout = null</c>).
    /// </summary>
    public (bool Assigned, string? Name) LayoutAssignment()
    {
        var all = LayoutSet().Matches(Text);
        if (all.Count == 0) return (false, null);
        var m = all[^1];
        return (true, m.Groups[1].Success ? Unescape(m.Groups[1].Value) : null);
    }

    private static string Unescape(string s) => s.Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);

    // Byter ut träffarna mot blanktecken (radbrytningar behålls) så att lägen i texten inte flyttas.
    private static string Blank(Regex pattern, string text) =>
        pattern.Replace(text, m => string.Concat(m.Value.Select(c => c is '\r' or '\n' ? c : ' ')));

    [GeneratedRegex(@"@\*.*?\*@", RegexOptions.Singleline)]
    private static partial Regex RazorComment();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex HtmlComment();

    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptOrStyle();

    [GeneratedRegex(@"@(?:code|functions)\s*\{")]
    private static partial Regex CodeStart();

    [GeneratedRegex(@"^[ \t]*@page\b(?<rest>[^\r\n]*)", RegexOptions.Multiline)]
    private static partial Regex PageLine();

    [GeneratedRegex(@"^""((?:[^""\\]|\\.)*)""")]
    private static partial Regex QuotedStart();

    [GeneratedRegex(@"ViewData\s*\[\s*""Title""\s*\]\s*=\s*""((?:[^""\\]|\\.)*)""\s*;")]
    private static partial Regex ViewDataTitleSet();

    [GeneratedRegex(@"^[ \t]*@model[ \t]+([\w.<>?]+)", RegexOptions.Multiline)]
    private static partial Regex ModelLine();

    [GeneratedRegex(@"^[ \t]*@layout[ \t]+([\w.]+)", RegexOptions.Multiline)]
    private static partial Regex LayoutLine();

    [GeneratedRegex(@"(?<![\w.])Layout\s*=\s*(?:""((?:[^""\\]|\\.)*)""|null)\s*;")]
    private static partial Regex LayoutSet();

    [GeneratedRegex(@"@attribute\s*\[\s*(?:[\w.]*\.)?Authorize(?:Attribute)?\b")]
    private static partial Regex AuthorizeAttribute();
}
