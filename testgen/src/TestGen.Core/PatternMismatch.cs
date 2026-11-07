using System.Text.RegularExpressions;

namespace TestGen.Core;

/// <summary>
/// Hittar ett deterministiskt värde som INTE matchar ett mönster ur specen (för negativa fall).
/// Mönstret används som det står (osankrat: söks var som helst i strängen), med .NET Regex.
/// </summary>
public static class PatternMismatch
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);
    private const int MaxPositions = 200;

    // Ersättningstecken, i prioritetsordning. Först tecken som sällan ingår i teckenklasser.
    private static readonly char[] Replacements = { '!', ' ', '#', '_', '-', '0', 'x', 'X' };
    private static readonly string[] Literals = { "!!!", "x", "0", " ", "-", "abc", "123", "ABC", "!", "" };

    /// <summary>
    /// Returnerar ett värde som inte matchar <paramref name="pattern"/>, eller null om inget hittas
    /// (t.ex. mönstret matchar allt, eller är ogiltigt). Värden som respekterar min/maxLength föredras.
    /// </summary>
    public static string? Find(string pattern, string? valid, int? minLength = null, int? maxLength = null)
    {
        Regex re;
        try { re = new Regex(pattern, RegexOptions.None, Timeout); }
        catch (ArgumentException) { return null; }

        var candidates = Candidates(valid ?? "").Distinct().ToList();
        bool Fits(string s) => (minLength is not { } mn || s.Length >= mn) && (maxLength is not { } mx || s.Length <= mx);

        // 1. Kandidater som uppfyller längdkraven (efter försök att anpassa), 2. vilken kandidat som helst.
        foreach (var c in candidates)
        {
            var fitted = Fit(c, minLength, maxLength);
            if (Fits(fitted) && !Matches(re, fitted)) return fitted;
        }
        foreach (var c in candidates)
            if (!Matches(re, c)) return c;
        return null;
    }

    private static IEnumerable<string> Candidates(string valid)
    {
        // Minsta möjliga ändring först: ett tecken byts, längden bevaras.
        for (var i = 0; i < Math.Min(valid.Length, MaxPositions); i++)
            foreach (var r in Replacements)
                if (valid[i] != r) yield return valid[..i] + r + valid[(i + 1)..];
        // Alla tecken byts.
        if (valid.Length > 0)
            foreach (var r in Replacements) yield return new string(r, valid.Length);
        foreach (var l in Literals) yield return l;
    }

    private static string Fit(string s, int? min, int? max)
    {
        if (max is { } mx && s.Length > mx) s = s[..mx];
        if (min is { } mn && s.Length < mn) s = s.PadRight(mn, s.Length > 0 ? s[^1] : '!');
        return s;
    }

    private static bool Matches(Regex re, string s)
    {
        try { return re.IsMatch(s); }
        catch (RegexMatchTimeoutException) { return true; }   // osäkert: räkna som match så att kandidaten inte används
    }
}
