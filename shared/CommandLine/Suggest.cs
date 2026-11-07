namespace CommandLine;

/// <summary>Förslag på "Menade du ...?" med Levenshtein-avstånd högst 2 (skiftläge spelar ingen roll).</summary>
public static class Suggest
{
    public static string? Closest(string word, IEnumerable<string> candidates, int max = 2) =>
        candidates.Select(c => (c, d: Distance(word.ToLowerInvariant(), c.ToLowerInvariant()))).Where(x => x.d <= max)
            .OrderBy(x => x.d).Select(x => x.c).FirstOrDefault();

    public static int Distance(string a, string b)
    {
        var prev = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var cur = new int[b.Length + 1];
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            prev = cur;
        }
        return prev[b.Length];
    }

    /// <summary>" Menade du X?" eller tom text.</summary>
    public static string DidYouMean(string word, IEnumerable<string> candidates) =>
        Closest(word, candidates) is { } m ? $" Menade du {m}?" : "";

    /// <summary>Högst <paramref name="max"/> namn, kommaseparerade, med "..." om fler finns.</summary>
    public static string List(IEnumerable<string> names, int max = 10)
    {
        var all = names.ToList();
        return string.Join(", ", all.Take(max)) + (all.Count > max ? ", ..." : "");
    }
}
