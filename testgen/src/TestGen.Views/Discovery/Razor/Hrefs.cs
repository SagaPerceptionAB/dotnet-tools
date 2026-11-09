using System.Text;
using System.Text.RegularExpressions;

namespace TestGen.Views.Discovery.Razor;

/// <summary>Gör om länkadresser i Razor-markup till kataloginnehåll: bara interna adresser, och dynamiska delar blir <c>{}</c>.</summary>
internal static partial class Hrefs
{
    /// <summary>Adressen med <c>{}</c> för det dynamiska, eller null om länken är extern, bara ett ankare eller helt dynamisk.</summary>
    public static string? Internal(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var href = Dynamic(raw.Trim());
        if (href.StartsWith("~/", StringComparison.Ordinal)) href = href[1..];
        return IsInternal(href) ? href : null;
    }

    /// <summary>Adressen pekar på en sida i appen: inte tom, inte bara ett ankare, inte helt dynamisk (<c>{}</c>), inte protokollrelativ (<c>//</c>) och utan schema (<c>https:</c>, <c>mailto:</c>).</summary>
    public static bool IsInternal(string? href)
    {
        if (string.IsNullOrWhiteSpace(href)) return false;
        href = href.Trim();
        if (href[0] == '#' || href.StartsWith("//", StringComparison.Ordinal) || href == "{}") return false;
        return !Scheme().IsMatch(href);
    }

    /// <summary><c>/products/@p.Id</c> och <c>@($"/products/{p.Id}")</c> blir båda <c>/products/{}</c>.</summary>
    public static string Dynamic(string raw)
    {
        var sb = new StringBuilder();
        var i = 0;
        while (i < raw.Length)
        {
            if (raw[i] != '@') { sb.Append(raw[i++]); continue; }
            if (i + 1 < raw.Length && raw[i + 1] == '@') { sb.Append('@'); i += 2; continue; }
            if (i + 1 < raw.Length && raw[i + 1] == '(')
            {
                var close = CodeScan.Matching(raw, i + 1);
                if (close < 0) { sb.Append("{}"); break; }
                sb.Append(FromExpression(raw[(i + 2)..close]));
                i = close + 1;
                continue;
            }
            var implicitExpression = ImplicitExpression().Match(raw, i);
            if (implicitExpression.Success) { sb.Append("{}"); i += implicitExpression.Length; }
            else sb.Append(raw[i++]);
        }
        return sb.ToString();
    }

    // Ett uttryck som är en sträng ($"..." eller "...") behåller sin text och tappar bara det som står inom { }. Annat blir {}.
    private static string FromExpression(string expression)
    {
        var m = StringLiteral().Match(expression.Trim());
        return m.Success ? Hole().Replace(m.Groups[1].Value, "{}") : "{}";
    }

    [GeneratedRegex(@"\G@[A-Za-z_]\w*(?:\.[A-Za-z_]\w*|\([^)]*\)|\[[^\]]*\])*")]
    private static partial Regex ImplicitExpression();

    [GeneratedRegex(@"^(?:\$@|@\$|\$|@)?""(.*)""$", RegexOptions.Singleline)]
    private static partial Regex StringLiteral();

    [GeneratedRegex(@"\{[^{}]*\}")]
    private static partial Regex Hole();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.\-]*:")]
    private static partial Regex Scheme();
}
