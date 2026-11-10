namespace SystemMap;

/// <summary>
/// Att dölja delar av kartan: <see cref="Hide(Overview, IReadOnlySet{string})"/> är regeln, och den gäller likadant i
/// Mermaid-filen, i texten, i JSON och i webbsidan (systemmap.js har samma regel, <c>SystemMap.hide</c>).
/// </summary>
public static partial class CodeMapOverview
{
    private static bool Hideable(OverviewKind kind) => kind is OverviewKind.Project or OverviewKind.Class or OverviewKind.Integration;

    /// <summary>
    /// Kartan utan de dolda delarna. Ett dolt projekt eller en dold klass tas bort med sina metoder, men vägarna genom dem består:
    /// en pil A → dold → B blir A → B (kedjor av dolda delar, och cykler, räknas också igenom). Pilen bär de anrop som går hela
    /// vägen, och finns pilen redan slås de ihop. En dold integration tas bort med sina rader och pilarna dit. Anrop (ingångar)
    /// går inte att dölja.
    /// </summary>
    /// <param name="hidden">Id:n på delarna som döljs (<see cref="ProjectId"/>, <see cref="ClassId"/>, <see cref="IntegrationNodeId"/>).
    /// Id:n som inte finns i kartan, och id:n på anrop, metoder och rader, gör ingenting.</param>
    public static Overview Hide(Overview overview, IReadOnlySet<string> hidden)
    {
        if (hidden.Count == 0) return overview;
        var byId = overview.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        // Borta: de dolda delarna och allt som ligger i dem.
        var gone = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in overview.Nodes)
            for (var n = node; n is not null; n = n.Parent is { } p && byId.TryGetValue(p, out var up) ? up : null)
                if (Hideable(n.Kind) && hidden.Contains(n.Id)) { gone.Add(node.Id); break; }
        if (gone.Count == 0) return overview;

        var outgoing = overview.Links.ToLookup(l => l.From, StringComparer.Ordinal);
        var kept = new List<(string From, string To, string? File, int? Line, List<string> Entries)>();
        var index = new Dictionary<(string, string), int>();
        void Emit(string from, string to, string? file, int? line, IEnumerable<string> entries)
        {
            if (from == to) return;
            if (!index.TryGetValue((from, to), out var i)) { index[(from, to)] = i = kept.Count; kept.Add((from, to, file, line, [])); }
            foreach (var e in entries) if (!kept[i].Entries.Contains(e)) kept[i].Entries.Add(e);
        }

        foreach (var link in overview.Links)
        {
            if (gone.Contains(link.From)) continue;
            if (!gone.Contains(link.To)) { Emit(link.From, link.To, link.File, link.Line, link.Entries); continue; }
            // Pilen går in i en dold del: följ den vidare tills den kommer ut. Vid varje del noteras vilka anrop som redan gått igenom
            // den, så att en cykel av dolda delar tar slut.
            var seen = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal) { [link.To] = [.. link.Entries] };
            var work = new Stack<(string Node, List<string> Entries)>();
            work.Push((link.To, [.. link.Entries]));
            while (work.Count > 0)
            {
                var (node, entries) = work.Pop();
                foreach (var next in outgoing[node])
                {
                    var through = entries.Where(next.Entries.Contains).ToList();
                    if (through.Count == 0) continue;
                    if (!gone.Contains(next.To)) { Emit(link.From, next.To, next.File, next.Line, through); continue; }
                    if (!seen.TryGetValue(next.To, out var done)) seen[next.To] = done = [];
                    var fresh = through.Where(done.Add).ToList();
                    if (fresh.Count > 0) work.Push((next.To, fresh));
                }
            }
        }
        // Anropen i pilarna i anropens ordning, oavsett i vilken ordning vägarna följdes.
        var order = overview.Nodes.Where(n => n.Kind == OverviewKind.Entry).Select((n, i) => (n.Name, i)).ToDictionary(x => x.Name, x => x.i, StringComparer.Ordinal);
        return new Overview(overview.Nodes.Where(n => !gone.Contains(n.Id)).ToList(),
            kept.Select(k => new OverviewLink(k.From, k.To, k.File, k.Line, k.Entries.OrderBy(e => order.GetValueOrDefault(e, int.MaxValue)).ToList())).ToList());
    }

    /// <summary>
    /// Kartan utan de dolda delarna, som <see cref="Hide(Overview, IReadOnlySet{string})"/> men för stegen: ett steg i en dold klass
    /// eller ett dolt projekt byts mot sina egna steg, och anrop till en dold integration tas bort. Anropens egna rader behåller
    /// sin hanterare.
    /// </summary>
    public static CodeMapModel Hide(CodeMapModel map, IReadOnlySet<string> hidden)
    {
        if (hidden.Count == 0) return map;
        bool Hidden(Step s)
        {
            if (s.Integration is { } use) return hidden.Contains(IntegrationNodeId(use.Id));
            return hidden.Contains(ProjectId(s.Project)) || hidden.Contains(ClassId(s.Project, SplitMember(s.Member).Type));
        }
        IReadOnlyList<Step> Without(IReadOnlyList<Step> steps) => steps.SelectMany(s =>
            Hidden(s) ? Without(s.Steps) : [s with { Steps = Without(s.Steps) }]).ToList();
        return map with
        {
            Projects = map.Projects.Where(p => !hidden.Contains(ProjectId(p))).ToList(),
            Integrations = map.Integrations.Where(i => !hidden.Contains(IntegrationNodeId(i.Id))).ToList(),
            EntryPoints = map.EntryPoints.Select(e => e with { Steps = Without(e.Steps) }).ToList(),
            Apps = map.Apps.Where(a => !hidden.Contains(ProjectId(a.Project)))
                .Select(a => a with { HttpClients = a.HttpClients.Where(c => !hidden.Contains(IntegrationNodeId(c.IntegrationId))).ToList() }).ToList(),
            Code = map.Code is null ? null : new CodeInventory(
                map.Code.Projects.Where(p => !hidden.Contains(ProjectId(p.Name))).ToList(),
                map.Code.Classes.Where(c => !hidden.Contains(ProjectId(c.Project)) && !hidden.Contains(ClassId(c.Project, c.Name))).ToList()),
        };
    }

    /// <summary>Namnet som används på kommandoraden och i felen: <c>projekt:Api</c>, <c>Api/OrderService</c> eller <c>integration:ShopDatabase</c>.</summary>
    public static string HideName(OverviewNode node, Overview overview) => node.Kind switch
    {
        OverviewKind.Project => "projekt:" + node.Name,
        OverviewKind.Class => (overview.Nodes.FirstOrDefault(n => n.Id == node.Parent)?.Name ?? "") + "/" + node.Name,
        _ => "integration:" + node.Name,
    };

    /// <summary>
    /// Id:n på delarna som namnen på kommandoraden står för. Ett namn är ett projekt, en klass eller en integration, utan hänsyn till
    /// stora och små bokstäver; en klass kan skrivas med sitt korta namn eller med namnrymden (<c>Orders.OrderService</c>).
    /// Stämmer ett namn med flera delar måste man skriva vilken, med <c>projekt:</c>, <c>klass:</c>, <c>integration:</c> eller
    /// <c>Projekt/Klass</c>. Ett namn som inte finns ger ett <see cref="UsageException"/> med de närmaste namnen.
    /// </summary>
    public static IReadOnlySet<string> ResolveHidden(Overview overview, IEnumerable<string> names)
    {
        var parts = overview.Nodes.Where(n => Hideable(n.Kind)).ToList();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in names)
        {
            var name = raw.Trim();
            var kind = (OverviewKind?)null;
            foreach (var (prefix, k) in new[] { ("projekt:", OverviewKind.Project), ("klass:", OverviewKind.Class), ("integration:", OverviewKind.Integration) })
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { kind = k; name = name[prefix.Length..].Trim(); break; }
            var found = parts.Where(n => (kind is null || n.Kind == kind) && Matches(n, name, overview, loose: false)).ToList();
            // Hittas inget med exakt namn går en klass också att ange med namnrymd (Orders.OrderService); namnrymden ingår inte i kartan.
            if (found.Count == 0) found = parts.Where(n => (kind is null || n.Kind == kind) && Matches(n, name, overview, loose: true)).ToList();
            if (found.Count == 1) { ids.Add(found[0].Id); continue; }
            if (found.Count > 1)
                throw new UsageException($"--hide '{raw}' kan vara flera delar: {string.Join(", ", found.Select(n => HideName(n, overview)))}.",
                    "Skriv vilken du menar, t.ex. " + HideName(found[0], overview) + ".");
            var close = parts.Select(n => (Node: n, D: Suggest.Distance(name.ToLowerInvariant(), n.Name.ToLowerInvariant())))
                .Where(x => x.D <= Math.Max(2, name.Length / 4)).OrderBy(x => x.D).Select(x => HideName(x.Node, overview)).Take(5).ToList();
            throw new UsageException($"--hide '{raw}' finns inte i kartan.", close.Count > 0
                ? $"Menade du {string.Join(", ", close)}? Skriv namnet på ett projekt, en klass eller en integration som det står i kartan."
                : parts.Count == 0 ? "Kartan har inga projekt, klasser eller integrationer att dölja."
                : $"Skriv namnet på ett projekt, en klass eller en integration som det står i kartan, t.ex. {Suggest.List(parts.Select(n => HideName(n, overview)), 5)}.");
        }
        return ids;
    }

    private static bool Matches(OverviewNode node, string name, Overview overview, bool loose)
    {
        if (name.Length == 0) return false;
        bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        if (node.Kind != OverviewKind.Class) return !loose && Same(node.Name, name);
        string Last(string text) => text[(text.LastIndexOf('.') + 1)..];
        // Klassen: namnet eller med sitt projekt (Projekt/Klass); löst: bara sista delen av namnet jämförs.
        return loose ? Same(Last(node.Name), Last(name)) : Same(node.Name, name) || Same(HideName(node, overview), name);
    }
}
