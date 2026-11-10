using System.Text.Json;
using SystemMap;

namespace SystemMap.Tests;

/// <summary>Att dölja delar av kartan: regeln på översikten, namnen på kommandoraden och utdata i text, Mermaid och JSON.</summary>
public partial class CodeMapTests
{
    // En liten karta för regeln: anropet E1 går A → B → C → D → databasen, E2 går A → B → D, och Z är ett eget projekt.
    // Varje klass (A–D, Z) har en metod m.
    private static OverviewNode Node(string id, OverviewKind kind, string name, string? parent = null, params string[] entries) =>
        new(id, kind, name, parent, null, null, null, null, null, entries);

    private static OverviewLink Arrow(string from, string to, params string[] entries) => new(from, to, null, null, entries);

    private static string M(string cls) => $"method:P/{cls}.m";

    private static Overview Small(params OverviewLink[] links)
    {
        var nodes = new List<OverviewNode>
        {
            Node("in:E1", OverviewKind.Entry, "E1"), Node("in:E2", OverviewKind.Entry, "E2"),
            Node("proj:P", OverviewKind.Project, "P"), Node("proj:Q", OverviewKind.Project, "Q"),
        };
        foreach (var c in new[] { "A", "B", "C", "D" })
        {
            nodes.Add(Node($"class:P/{c}", OverviewKind.Class, c, "proj:P"));
            nodes.Add(Node(M(c), OverviewKind.Method, "m", $"class:P/{c}"));
        }
        nodes.Add(Node("class:Q/Z", OverviewKind.Class, "Z", "proj:Q"));
        nodes.Add(Node("method:Q/Z.m", OverviewKind.Method, "m", "class:Q/Z"));
        nodes.Add(Node("int:db", OverviewKind.Integration, "Db"));
        nodes.Add(Node("op:db|läsa", OverviewKind.Operation, "läsa", "int:db"));
        return new Overview(nodes, links.Length > 0 ? links :
        [
            Arrow("in:E1", M("A"), "E1"), Arrow("in:E2", M("A"), "E2"),
            Arrow(M("A"), M("B"), "E1", "E2"), Arrow(M("B"), M("C"), "E1"), Arrow(M("C"), M("D"), "E1"),
            Arrow(M("B"), M("D"), "E2"), Arrow(M("D"), "op:db|läsa", "E1", "E2"),
        ]);
    }

    private static IReadOnlySet<string> Ids(params string[] ids) => new HashSet<string>(ids, StringComparer.Ordinal);

    // "från>till[anrop]" för varje pil.
    private static string[] Show(Overview o) => o.Links.Select(l => $"{l.From}>{l.To}[{string.Join(",", l.Entries)}]").ToArray();

    [Test]
    public void Hiding_a_class_keeps_the_path_through_it_connected()
    {
        var hidden = CodeMapOverview.Hide(Small(), Ids("class:P/C"));
        Assert.That(hidden.Nodes.Select(n => n.Id), Does.Not.Contain("class:P/C").And.Not.Contain(M("C")), "klassen och dess metod är borta");
        Assert.That(hidden.Nodes.Select(n => n.Id), Does.Contain("class:P/B").And.Contain("proj:P"), "andra delar står kvar");
        Assert.That(Show(hidden), Is.EqualTo(new[]
        {
            $"in:E1>{M("A")}[E1]", $"in:E2>{M("A")}[E2]", $"{M("A")}>{M("B")}[E1,E2]",
            $"{M("B")}>{M("D")}[E1,E2]",   // B → C → D för E1 slås ihop med B → D för E2
            $"{M("D")}>op:db|läsa[E1,E2]",
        }), "pilen B → C → D blir B → D, och pilen som redan fanns får även anropet som gick genom C");
    }

    [Test]
    public void Hiding_a_chain_of_classes_connects_the_ends()
    {
        var hidden = CodeMapOverview.Hide(Small(), Ids("class:P/B", "class:P/C"));
        Assert.That(Show(hidden), Is.EqualTo(new[]
        {
            $"in:E1>{M("A")}[E1]", $"in:E2>{M("A")}[E2]", $"{M("A")}>{M("D")}[E1,E2]", $"{M("D")}>op:db|läsa[E1,E2]",
        }));
    }

    [Test]
    public void A_bypass_carries_only_the_calls_that_go_all_the_way()
    {
        // E1 går A → B, men bara E2 går vidare B → D: pilen A → D bär bara E2.
        var o = Small(Arrow(M("A"), M("B"), "E1", "E2"), Arrow(M("B"), M("D"), "E2"));
        Assert.That(Show(CodeMapOverview.Hide(o, Ids("class:P/B"))), Is.EqualTo(new[] { $"{M("A")}>{M("D")}[E2]" }));
    }

    [Test]
    public void A_cycle_of_hidden_classes_ends_and_a_path_back_to_the_start_is_not_an_arrow_to_itself()
    {
        // A → B ⇄ C → D, och A → B → A: B och C är dolda.
        var o = Small(Arrow(M("A"), M("B"), "E1"), Arrow(M("B"), M("C"), "E1"), Arrow(M("C"), M("B"), "E1"), Arrow(M("C"), M("D"), "E1"), Arrow(M("B"), M("A"), "E1"));
        Assert.That(Show(CodeMapOverview.Hide(o, Ids("class:P/B", "class:P/C"))), Is.EqualTo(new[] { $"{M("A")}>{M("D")}[E1]" }));
    }

    [Test]
    public void Hiding_a_project_removes_its_classes_and_methods_and_keeps_the_paths_through_it()
    {
        var o = Small(Arrow("in:E1", M("A"), "E1"), Arrow(M("A"), "method:Q/Z.m", "E1"), Arrow("method:Q/Z.m", M("D"), "E1"), Arrow(M("D"), "op:db|läsa", "E1"));
        var hidden = CodeMapOverview.Hide(o, Ids("proj:Q"));
        Assert.That(hidden.Nodes.Select(n => n.Id), Does.Not.Contain("proj:Q").And.Not.Contain("class:Q/Z").And.Not.Contain("method:Q/Z.m"));
        Assert.That(Show(hidden), Is.EqualTo(new[] { $"in:E1>{M("A")}[E1]", $"{M("A")}>{M("D")}[E1]", $"{M("D")}>op:db|läsa[E1]" }));
    }

    [Test]
    public void Hiding_an_integration_removes_it_its_rows_and_the_arrows_to_them()
    {
        var hidden = CodeMapOverview.Hide(Small(), Ids("int:db"));
        Assert.That(hidden.Nodes.Select(n => n.Id), Does.Not.Contain("int:db").And.Not.Contain("op:db|läsa"));
        Assert.That(hidden.Links.Select(l => l.To), Does.Not.Contain("op:db|läsa"));
        Assert.That(hidden.Links, Has.Count.EqualTo(6), "alla andra pilar står kvar");
    }

    [Test]
    public void Hiding_everything_leaves_only_the_calls_and_no_arrows()
    {
        var hidden = CodeMapOverview.Hide(Small(), Ids("proj:P", "proj:Q", "int:db"));
        Assert.That(hidden.Nodes.Select(n => n.Id), Is.EqualTo(new[] { "in:E1", "in:E2" }), "anrop går inte att dölja");
        Assert.That(hidden.Links, Is.Empty);
    }

    [Test]
    public void Hiding_names_that_are_not_in_the_map_or_are_not_hideable_changes_nothing()
    {
        var o = Small();
        Assert.That(CodeMapOverview.Hide(o, Ids("class:P/Finns-inte", "proj:Okänt")), Is.SameAs(o));
        Assert.That(CodeMapOverview.Hide(o, Ids("in:E1", M("B"), "op:db|läsa")), Is.SameAs(o), "anrop, metoder och rader döljs inte var för sig");
        Assert.That(CodeMapOverview.Hide(o, Ids()), Is.SameAs(o));
    }

    [Test]
    public void A_bypass_keeps_the_call_site_of_the_last_arrow_and_does_not_change_the_original()
    {
        var o = new Overview(Small().Nodes,
        [
            Arrow(M("A"), M("B"), "E1"), new OverviewLink(M("B"), "op:db|läsa", "B.cs", 12, ["E1"]),
        ]);
        var hidden = CodeMapOverview.Hide(o, Ids("class:P/B"));
        var link = hidden.Links.Single();
        Assert.That((link.From, link.To, link.File, link.Line), Is.EqualTo((M("A"), "op:db|läsa", "B.cs", 12)), "platsen där integrationen anropas");
        Assert.That(o.Links, Has.Count.EqualTo(2), "översikten som ges ändras inte");
    }

    // --- Namnen på kommandoraden ------------------------------------------------------------------

    [Test]
    public void Hide_names_find_projects_classes_and_integrations_ignoring_case_and_namespace()
    {
        var o = new Overview(Small().Nodes.Append(Node("class:P/Orders.Service", OverviewKind.Class, "Orders.Service", "proj:P")).ToList(), []);
        Assert.That(CodeMapOverview.ResolveHidden(o, ["p", "z", "DB", "Orders.Service", "service"]), Is.EquivalentTo(
            new[] { "proj:P", "class:Q/Z", "int:db", "class:P/Orders.Service" }), "projekt, klass, integration, med och utan namnrymd");
        Assert.That(CodeMapOverview.ResolveHidden(o, ["Min.Namnrymd.Z"]), Is.EquivalentTo(new[] { "class:Q/Z" }), "en klass anges med namnrymd, som inte finns i kartan");
        Assert.That(CodeMapOverview.ResolveHidden(o, ["Q/Z", "klass:b", "projekt:Q"]), Is.EquivalentTo(new[] { "class:Q/Z", "class:P/B", "proj:Q" }));
    }

    [Test]
    public void Hide_names_that_fit_several_parts_name_them_all()
    {
        var o = new Overview(Small().Nodes.Append(Node("class:Q/A", OverviewKind.Class, "A", "proj:Q")).Append(Node("class:P/P", OverviewKind.Class, "P", "proj:P")).ToList(), []);
        var e = Assert.Throws<UsageException>(() => CodeMapOverview.ResolveHidden(o, ["a"]))!;
        Assert.That(e.Message, Is.EqualTo("--hide 'a' kan vara flera delar: P/A, Q/A."));
        Assert.That(e.Tip, Does.Contain("P/A"), "tipset visar hur man skriver vilken man menar");
        Assert.That(Assert.Throws<UsageException>(() => CodeMapOverview.ResolveHidden(o, ["P"]))!.Message, Is.EqualTo("--hide 'P' kan vara flera delar: projekt:P, P/P."));
        Assert.That(CodeMapOverview.ResolveHidden(o, ["projekt:P", "P/P"]), Is.EquivalentTo(new[] { "proj:P", "class:P/P" }), "med projekt: och Projekt/Klass blir det entydigt");
    }

    [Test]
    public void Hide_name_that_does_not_exist_names_the_closest()
    {
        var o = Small();
        var e = Assert.Throws<UsageException>(() => CodeMapOverview.ResolveHidden(o, ["Db", "Zz"]))!;
        Assert.That(e.Message, Is.EqualTo("--hide 'Zz' finns inte i kartan."));
        Assert.That(e.Tip, Does.Contain("Q/Z"), "Z ligger nära Zz");
        var far = Assert.Throws<UsageException>(() => CodeMapOverview.ResolveHidden(o, ["Helt annat"]))!;
        Assert.That(far.Tip, Does.Contain("t.ex.").And.Contain("projekt:P"), "inget nära: några namn som finns");
        Assert.Throws<UsageException>(() => CodeMapOverview.ResolveHidden(o, [" "]), "ett tomt namn finns inte");
    }

    // --- Samma regel i texten, Mermaid och JSON -----------------------------------------------------

    [Test]
    public async Task Hiding_a_class_gives_the_same_map_in_the_overview_and_in_the_steps()
    {
        var map = await MapAsync(Build(true, new Proj("Api", Kunder)));
        var overview = CodeMapOverview.Build(map);
        var hidden = CodeMapOverview.ResolveHidden(overview, ["Skrivare", "Handelser"]);

        // Pilarna mellan metoderna, ur översikten med regeln och ur översikten av kartan med regeln på stegen.
        var byRule = CodeMapOverview.Hide(overview, hidden);
        var bySteps = CodeMapOverview.Build(CodeMapOverview.Hide(map, hidden));
        string[] Arrows(Overview o) => o.Links.Select(l => $"{l.From}>{l.To}[{string.Join(",", l.Entries)}]").Order(StringComparer.Ordinal).ToArray();
        Assert.That(Arrows(byRule), Is.EqualTo(Arrows(bySteps)));
        Assert.That(byRule.Links.Select(l => l.To), Does.Contain("op:db:Db|lägga till i Kunder"), "databasen anropas nu direkt av anropets klass");

        var steps = CodeMapOverview.Hide(map, hidden).EntryPoints.Single(e => e.Method == "POST").Steps;
        Assert.That(steps.SelectMany(Flatten).Select(s => s.Member), Does.Not.Contain("Skrivare.SparaAsync").And.Not.Contain("Handelser.LagdAsync"));
        Assert.That(CodeMapOverview.Hide(map, Ids("int:queue:ordrar")).Integrations.Select(i => i.Id), Is.EqualTo(new[] { "db:Db" }));
        Assert.That(CodeMapOverview.Hide(map, Ids("int:queue:ordrar")).EntryPoints.SelectMany(e => e.Steps).SelectMany(Flatten).Select(s => s.Integration?.Id), Does.Not.Contain("queue:ordrar"));
    }

    private static IEnumerable<Step> Flatten(Step s) => new[] { s }.Concat(s.Steps.SelectMany(Flatten));

    [Test]
    public async Task Text_with_hidden_parts_says_so_and_leaves_them_out()
    {
        var map = await MapAsync(Build(true, new Proj("Api", Kunder)));
        var hidden = CodeMapOverview.ResolveHidden(CodeMapOverview.Build(map), ["Handelser", "ordrar"]);
        var text = string.Join("\n", CodeMapText.Lines(CodeMapOverview.Hide(map, hidden), ["Handelser", "ordrar"]));
        Assert.That(text, Does.Contain("Dolda delar: Handelser, ordrar. Vägarna genom dem består."));
        Assert.That(text, Does.Not.Contain("Handelser.").And.Not.Contain("OrderLagd").And.Not.Contain("kö, "), "varken klassen, meddelandena eller kön");
        Assert.That(text, Does.Contain("Skrivare.SparaAsync").And.Contain("läsa Kunder"), "resten står kvar");
        Assert.That(string.Join("\n", CodeMapText.Lines(map)), Does.Not.Contain("Dolda delar"), "utan dolda delar står ingen rad om dem");
    }

    [Test]
    public async Task Mermaid_with_hidden_parts_leaves_them_out_keeps_the_paths_and_says_so()
    {
        var map = await MapAsync(Build(true, new Proj("Api", Kunder)));
        var hidden = CodeMapOverview.ResolveHidden(CodeMapOverview.Build(map), ["KunderController", "ordrar"]);
        var text = CodeMapMermaid.Write(map, new MermaidOptions(Hidden: hidden));
        Assert.That(text, Does.Contain("%% Dolda delar: KunderController, ordrar. Vägarna genom dem består."));
        Assert.That(text, Does.Not.Contain("class_KunderController").And.Not.Contain("int_ordrar").And.Not.Contain("OrderLagd"));
        Assert.That(text, Does.Contain("in_GET_kunder_id --> class_Lasare"), "anropet går nu direkt till klassen som controllern anropade");
        Assert.That(text, Does.Contain("in_POST_kunder --> class_Skrivare").And.Contain("class_Lasare -->|\"läsa Kunder\"| int_Db"));

        var all = CodeMapMermaid.Write(map, new MermaidOptions(MapLevel.All, Hidden: CodeMapOverview.ResolveHidden(CodeMapOverview.Build(map), ["Db"])));
        Assert.That(all, Does.Not.Contain("int_Db").And.Not.Contain("op_Db_"), "en dold integration tas bort med sina rader");

        var none = CodeMapMermaid.Write(map, new MermaidOptions(Hidden: Ids()));
        Assert.That(none, Is.EqualTo(CodeMapMermaid.Write(map, new MermaidOptions())).And.Not.Contain("Dolda delar"), "inget dolt: samma text");
    }

    [Test]
    public async Task Hiding_every_class_of_a_project_draws_the_project_as_a_plain_node()
    {
        var map = await MapAsync(Build(true, new Proj("Api", Kunder)));
        var overview = CodeMapOverview.Build(map);
        var classes = overview.Nodes.Where(n => n.Kind == OverviewKind.Class).Select(n => n.Name).ToList();
        var text = CodeMapMermaid.Write(map, new MermaidOptions(Hidden: CodeMapOverview.ResolveHidden(overview, classes)));
        Assert.That(text, Does.Contain("proj_Api[\"Api\"]").And.Not.Contain("subgraph proj_Api"), "en ruta utan något i är en nod");
    }

    [Test]
    public async Task Json_with_hidden_parts_lists_them_and_stays_valid()
    {
        var map = await MapAsync(Build(true, new Proj("Api", Kunder)));
        var overview = CodeMapOverview.Build(map);
        var hidden = CodeMapOverview.ResolveHidden(overview, ["Handelser", "ordrar"]);
        var json = CodeMapJson.Serialize(CodeMapOverview.Hide(map, hidden), CodeMapOverview.Hide(overview, hidden), hidden);
        using var doc = JsonDocument.Parse(json);
        Assert.That(doc.RootElement.EnumerateObject().First().Name, Is.EqualTo("schemaVersion"));
        Assert.That(doc.RootElement.GetProperty("hidden").EnumerateArray().Select(e => e.GetString()), Is.EqualTo(new[] { "class:Api/Handelser", "int:queue:ordrar" }));
        var nodes = doc.RootElement.GetProperty("overview").GetProperty("nodes").EnumerateArray().Select(n => n.GetProperty("id").GetString()!).ToList();
        Assert.That(nodes, Is.Ordered.Using<string>(StringComparer.Ordinal).And.Not.Contain("class:Api/Handelser").And.Not.Contain("int:queue:ordrar"));
        Assert.That(doc.RootElement.GetProperty("integrations").EnumerateArray().Select(i => i.GetProperty("id").GetString()), Is.EqualTo(new[] { "db:Db" }));
        Assert.That(json, Does.Not.Contain("OrderLagd"), "inte heller i stegen");
        Assert.That(JsonDocument.Parse(CodeMapJson.Serialize(map)).RootElement.TryGetProperty("hidden", out _), Is.False, "utan dolda delar finns ingen lista");
    }

    // --- Nivån Funktioner ---------------------------------------------------------------------------

    [Test]
    public async Task Mermaid_at_the_level_methods_draws_the_methods_in_their_classes_and_the_operations_on_the_arrows()
    {
        var map = await MapAsync(Build(true, new Proj("Api", Kunder)));
        var text = CodeMapMermaid.Write(map, MermaidOptions.Parse("methods", null, null));
        Assert.That(text, Does.Contain("subgraph class_Skrivare[\"Skrivare\"]").And.Contain("method_Skrivare_SparaAsync[\"SparaAsync()\"]"), "klasserna är rutor med metoderna i");
        Assert.That(text, Does.Contain("subgraph proj_Api[\"Api\"]").And.Contain("in_POST_kunder --> method_KunderController_Spara"));
        Assert.That(text, Does.Contain("method_KunderController_Spara --> method_Skrivare_SparaAsync"), "anropen mellan metoderna");
        Assert.That(text, Does.Contain("method_Skrivare_SparaAsync -->|\"lägga till i Kunder\"| int_Db"), "raderna i databasen står på pilen");
        Assert.That(text, Does.Not.Contain("subgraph int_").And.Not.Contain("op_Db_"), "raderna i integrationerna ritas bara på nivån all");
        Assert.That(text, Does.Contain("int_Db[(\"Db<br/>databas, EF Core\")]"), "integrationen är en egen nod");
        Assert.That(CodeMapMermaid.Write(map, new MermaidOptions(MapLevel.Methods)), Is.EqualTo(text));
        Assert.That(MermaidOptions.Levels, Is.EqualTo(new[] { "projects", "classes", "methods", "all" }));
    }
}
