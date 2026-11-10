using System.Globalization;
using System.Text;

namespace SystemMap;

/// <summary>
/// Hur mycket som ritas: projekten, klasserna i projekten, metoderna i klasserna (med läsningar, skrivningar och meddelanden på
/// pilarna till integrationerna), eller allt (även raderna i integrationerna).
/// </summary>
public enum MapLevel { Projects, Classes, Methods, All }

/// <param name="TopDown">Uppifrån och ned i stället för vänster till höger.</param>
/// <param name="Entry">Bara vägen för det här anropet, t.ex. <c>POST /orders</c>, eller null för hela systemet.</param>
/// <param name="Hidden">Id:n på delar som inte ritas (se <see cref="CodeMapOverview.Hide(Overview, IReadOnlySet{string})"/>), eller null.</param>
public sealed record MermaidOptions(MapLevel Level = MapLevel.Classes, bool TopDown = false, string? Entry = null, IReadOnlySet<string>? Hidden = null)
{
    /// <summary>Nivåerna som de skrivs på kommandoraden och i webbsidan, i ordning från minst till mest.</summary>
    public static readonly string[] Levels = ["projects", "classes", "methods", "all"];
    /// <summary>Riktningarna: vänster till höger och uppifrån och ned.</summary>
    public static readonly string[] Directions = ["LR", "TB"];

    /// <summary>Värdena som de skrivs på kommandoraden och skickas från webbsidan: <see cref="Levels"/> och <see cref="Directions"/>.</summary>
    public static MermaidOptions Parse(string? level, string? direction, string? entry, IEnumerable<string>? hidden = null) => new(
        level?.Trim().ToLowerInvariant() switch
        {
            null or "" or "classes" => MapLevel.Classes,
            "projects" => MapLevel.Projects,
            "methods" => MapLevel.Methods,
            "all" => MapLevel.All,
            _ => throw new UsageException($"--level måste vara projects, classes, methods eller all, inte '{level}'.", "Skriv t.ex. --level classes"),
        },
        direction?.Trim().ToUpperInvariant() switch
        {
            null or "" or "LR" => false,
            "TB" => true,
            _ => throw new UsageException($"--direction måste vara LR (vänster till höger) eller TB (uppifrån och ned), inte '{direction}'.",
                "Skriv t.ex. --direction TB"),
        },
        string.IsNullOrWhiteSpace(entry) ? null : entry.Trim(),
        hidden is null ? null : new HashSet<string>(hidden, StringComparer.Ordinal));
}

/// <summary>
/// Kartan som Mermaid-flödesschema, att lägga i dokumentation (t.ex. i ett <c>```mermaid</c>-block på GitHub). Ritas från
/// översikten, så varje del och integration finns en gång, i samma färger som i webbsidan. Det som ingen ingång når ritas dämpat och
/// streckat (<c>unreached</c>). Samma karta ger samma text.
/// </summary>
public static class CodeMapMermaid
{
    public static string Write(CodeMapModel map, MermaidOptions options)
    {
        var overview = CodeMapOverview.Build(map);
        var hidden = options.Hidden ?? new HashSet<string>();
        return Write(map, options, CodeMapOverview.Hide(overview, hidden), CodeMapOverview.HiddenNames(overview, hidden));
    }

    /// <summary>
    /// Med en översikt som redan är byggd och redan utan de dolda delarna (<see cref="MermaidOptions.Hidden"/> gäller inte här), när flera
    /// varianter skrivs för samma karta. <paramref name="hiddenNames"/> är namnen på de dolda delarna (se <see cref="CodeMapOverview.HiddenNames"/>).
    /// </summary>
    internal static string Write(CodeMapModel map, MermaidOptions options, Overview overview, IReadOnlyList<string> hiddenNames)
    {
        var entry = options.Entry is null ? null : FindEntry(map, options.Entry);
        // Bara det som ingången går genom; föräldrarna ingår alltid, eftersom en del som ritas har gått igenom sin klass och sitt projekt.
        var nodes = overview.Nodes.Where(n => entry is null || n.Entries.Contains(entry)).ToList();
        var byId = nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var integrations = map.Integrations.ToDictionary(i => i.Id, StringComparer.Ordinal);
        var kids = nodes.Where(n => n.Parent is not null).ToLookup(n => n.Parent!, StringComparer.Ordinal);
        var level = options.Level;
        var ids = new Ids();

        // Delen som noden ritas som på nivån: en metod i sin klass (på projektnivå i sitt projekt), en rad i sin integration.
        string Shown(OverviewNode n) => n.Kind switch
        {
            OverviewKind.Method when level is MapLevel.Projects or MapLevel.Classes => Shown(byId[n.Parent!]),
            OverviewKind.Class when level == MapLevel.Projects => n.Parent!,
            OverviewKind.Operation when level != MapLevel.All => n.Parent!,
            _ => n.Id,
        };
        // En del med delar i är en ruta; en utan (t.ex. ett projekt vars klasser är dolda) ritas som en vanlig nod.
        bool IsBox(OverviewNode n) => kids[n.Id].Any() && n.Kind switch
        {
            OverviewKind.Project => level != MapLevel.Projects,
            OverviewKind.Class => level is MapLevel.Methods or MapLevel.All,
            OverviewKind.Integration => level == MapLevel.All,
            _ => false,
        };
        string IdOf(OverviewNode n) => ids.Of(n.Id, n.Kind switch
        {
            OverviewKind.Entry => "in_" + n.Name,
            OverviewKind.Project => "proj_" + n.Name,
            OverviewKind.Class => "class_" + n.Name,
            OverviewKind.Method => $"method_{byId[n.Parent!].Name}_{n.Name}",
            OverviewKind.Integration => "int_" + n.Name,
            _ => $"op_{byId[n.Parent!].Name}_{CodeMapOverview.DisplayName(n)}",
        });
        Integration? IntegrationOf(OverviewNode n) => n.Ref is null ? null : integrations.GetValueOrDefault(n.Ref);
        // Namnet, och för en integration vad den är på en egen rad (på en rad blir rubriken bredare än ytan och kapas).
        string Label(OverviewNode n) => Escape(CodeMapOverview.DisplayName(n)) + (n.Kind == OverviewKind.Method ? "()"
            : IntegrationOf(n) is { Detail.Length: > 0 } i ? "<br/>" + Escape(i.Detail) : "");

        // Bara solutionens filnamn: filen hamnar i dokumentation, och sökvägen på den här datorn hör inte hemma där.
        var solution = Path.GetFileName(map.Solution);
        var lines = new List<string>
        {
            "flowchart " + (options.TopDown ? "TB" : "LR"),
            $"  %% Systemkarta för {solution}: vägar som är möjliga enligt koden.",
        };
        if (entry is not null) lines.Add($"  %% Bara vägen för {entry}.");
        if (hiddenNames.Count > 0) lines.Add($"  %% Dolda delar: {string.Join(", ", hiddenNames)}. Vägarna genom dem består.");
        var styled = new List<(string Id, string Style)>();

        void Add(OverviewNode n, string indent)
        {
            if (Shown(n) != n.Id) return;
            var id = IdOf(n);
            var outside = n.Kind is OverviewKind.Integration or OverviewKind.Operation;
            if (IsBox(n))
            {
                lines.Add($"{indent}subgraph {id}[\"{Label(n)}\"]");
                foreach (var kid in kids[n.Id]) Add(kid, indent + "  ");
                lines.Add(indent + "end");
                styled.Add((id, !n.Reached ? "unreached" : outside ? "externalarea" : "codearea"));
                return;
            }
            // Anropen in som flaggor (sidor som parallellogram, det som inte är HTTP som sexhörningar), en databas som cylinder och andra
            // integrationer med rundade hörn.
            lines.Add(indent + id + string.Format(n.Kind switch
            {
                OverviewKind.Entry => n.Ref switch
                {
                    "razor-page" or "blazor-page" or "mvc-view" => "[/\"{0}\"/]",
                    "controller" or "minimal-api" => ">\"{0}\"]",
                    _ => "{{{{\"{0}\"}}}}",
                },
                OverviewKind.Integration => IntegrationOf(n)?.Kind == IntegrationKind.Database ? "[(\"{0}\")]" : "(\"{0}\")",
                _ => "[\"{0}\"]",
            }, Label(n)));
            styled.Add((id, n.Kind == OverviewKind.Entry ? "entry" : outside ? "external" : !n.Reached ? "unreached" : "code"));
        }

        foreach (var n in nodes.Where(n => n.Kind == OverviewKind.Entry)) Add(n, "  ");
        var projects = nodes.Where(n => n.Kind == OverviewKind.Project).ToList();
        if (projects.Count > 0)
        {
            lines.Add($"  subgraph system[\"Systemet ({Escape(solution)})\"]");
            foreach (var n in projects) Add(n, "    ");
            lines.Add("  end");
        }
        foreach (var n in nodes.Where(n => n.Kind == OverviewKind.Integration)) Add(n, "  ");

        // Pilarna mellan det som ritas. På nivåerna Projekt, Klasser och Funktioner står läsningarna, skrivningarna och meddelandena på
        // pilen till integrationen.
        var edges = new List<(string From, string To, List<string> Labels)>();
        var index = new Dictionary<(string, string), int>();
        foreach (var link in overview.Links)
        {
            if (entry is not null && !link.Entries.Contains(entry)) continue;
            var to = byId[link.To];
            var key = (Shown(byId[link.From]), Shown(to));
            if (key.Item1 == key.Item2) continue;
            if (!index.TryGetValue(key, out var i)) { index[key] = i = edges.Count; edges.Add((key.Item1, key.Item2, [])); }
            if (to.Kind == OverviewKind.Operation && level != MapLevel.All && CodeMapOverview.DisplayName(to) is var text && !edges[i].Labels.Contains(text)) edges[i].Labels.Add(text);
        }
        foreach (var (from, to, labels) in edges)
            lines.Add($"  {IdOf(byId[from])} -->{(labels.Count == 0 ? "" : $"|\"{string.Join("<br/>", labels.Select(Escape))}\"|")} {IdOf(byId[to])}");

        lines.Add("  classDef entry fill:#eef0f3,stroke:#6b7a8f,color:#1f2933");
        lines.Add("  classDef code fill:#dceafa,stroke:#2a78d6,color:#1f2933");
        lines.Add("  classDef codearea fill:#f3f8fd,stroke:#2a78d6,color:#1f2933");
        lines.Add("  classDef external fill:#dff0df,stroke:#008300,color:#1f2933");
        lines.Add("  classDef externalarea fill:#f2f9f2,stroke:#008300,color:#1f2933");
        // Det som ingen ingång når: dämpat och streckat.
        if (styled.Any(s => s.Style == "unreached")) lines.Add("  classDef unreached fill:#f4f5f7,stroke:#8a96a5,color:#6b7785,stroke-dasharray:4 3");
        if (projects.Count > 0) lines.Add("  style system fill:none,stroke:#6b7a8f,stroke-dasharray:5 5");
        foreach (var group in styled.GroupBy(s => s.Style))
            lines.Add($"  class {string.Join(",", group.Select(s => s.Id))} {group.Key}");
        return string.Join("\n", lines) + "\n";
    }

    /// <summary>Anropets nyckel som den står i kartan. Stora och små bokstäver och extra mellanslag spelar ingen roll.</summary>
    private static string FindEntry(CodeMapModel map, string wanted)
    {
        var normalized = string.Join(' ', wanted.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (map.EntryPoints.Select(e => e.Key).FirstOrDefault(k => string.Equals(k, normalized, StringComparison.OrdinalIgnoreCase)) is { } found) return found;
        // Namnet utan projekt när flera anrop heter så: de heter med projektet i kartan.
        if (map.EntryPoints.Where(e => e.Qualifier is not null && string.Equals(e.BaseKey, normalized, StringComparison.OrdinalIgnoreCase)).Select(e => e.Key).ToList() is { Count: > 0 } several)
            throw new UsageException($"Anropet '{wanted}' finns i flera projekt.", $"Skriv det med projektet: {string.Join(", ", several.Select(k => $"\"{k}\""))}.");
        throw new UsageException($"Anropet '{wanted}' finns inte i kartan.", map.EntryPoints.Count == 0
                ? "Kartan hittade inga anrop i koden."
                : $"Skriv metoden och sökvägen som i kartan, t.ex. \"{map.EntryPoints[0].Key}\".");
    }

    /// <summary>Tecken som Mermaid läser som syntax i en text inom citattecken skrivs som Mermaids teckenkoder.</summary>
    private static string Escape(string text) =>
        text.Replace("#", "#35;").Replace("\"", "#quot;").Replace("<", "#lt;").Replace(">", "#gt;");

    /// <summary>Läsbara id:n (<c>klass_OrderService</c>) som är unika i schemat: å, ä och ö blir a och o, andra tecken understreck.</summary>
    private sealed class Ids
    {
        private readonly Dictionary<string, string> _ids = new(StringComparer.Ordinal);
        private readonly HashSet<string> _used = new(StringComparer.Ordinal) { "system" };

        public string Of(string node, string name)
        {
            if (_ids.TryGetValue(node, out var id)) return id;
            var text = new StringBuilder();
            foreach (var c in name.Normalize(NormalizationForm.FormD))
            {
                if (char.IsAsciiLetterOrDigit(c)) text.Append(c);
                else if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && text.Length > 0 && text[^1] != '_') text.Append('_');
            }
            var basis = text.ToString().TrimEnd('_');
            id = basis;
            for (var n = 2; !_used.Add(id); n++) id = $"{basis}_{n}";
            return _ids[node] = id;
        }
    }
}
