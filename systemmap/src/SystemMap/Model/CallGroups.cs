namespace SystemMap;

/// <summary>
/// Anropen i grupper efter sökvägsområde, som sidan visar dem: den gemensamma början för alla anrop (t.ex. <c>/v1</c>) tas bort och
/// nästa segment blir gruppen (<c>/v1/auth</c>). En grupp har minst två anrop; ett ensamt anrop står för sig. Samma regler finns i
/// <c>callGroups</c> i systemmap.js.
/// </summary>
internal static class CallGroups
{
    /// <summary>Anropen så att varje grupp står samlad där dess första anrop står, i kartans ordning (sökväg, metod) inom gruppen.</summary>
    public static IReadOnlyList<EntryPoint> Order(IReadOnlyList<EntryPoint> entries)
    {
        var segments = entries.Select(e => e.Path.Split('/', StringSplitOptions.RemoveEmptyEntries)).ToList();
        var common = 0;
        while (segments.Count > 0 && segments.All(s => s.Length > common && s[common] == segments[0][common])) common++;

        var items = new List<List<EntryPoint>>();
        var groups = new Dictionary<string, List<EntryPoint>>(StringComparer.Ordinal);
        for (var i = 0; i < entries.Count; i++)
        {
            if (segments[i].Length <= common) { items.Add([entries[i]]); continue; }
            if (!groups.TryGetValue(segments[i][common], out var group))
            {
                groups[segments[i][common]] = group = [];
                items.Add(group);
            }
            group.Add(entries[i]);
        }
        return items.SelectMany(g => g).ToList();
    }
}
