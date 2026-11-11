using System.Text.Json;
using Microsoft.Playwright;

namespace SystemMap.Tests;

/// <summary>
/// Ingångar som annars skulle heta lika (två <c>Worker</c> i olika projekt, samma rutt i två webbappar) får projektet i namnet,
/// och bara då: ett namn som är unikt är som det alltid varit (kod i minnet).
/// </summary>
public partial class CodeMapTests
{
    private static Proj WorkerIn(string project, string extra = "") => new(project, $$"""
        public class Worker : Microsoft.Extensions.Hosting.BackgroundService
        {
            protected override Task ExecuteAsync(CancellationToken ct) { System.IO.File.WriteAllText("{{project}}.txt", "x"); return Task.CompletedTask; }
        }
        {{extra}}
        """);

    private static Proj HealthIn(string project) => Web(project, """
        [ApiController, Route("health")]
        public class HealthController : ControllerBase { [HttpGet] public int Get() { System.IO.File.ReadAllText("h.txt"); return 1; } }
        [ApiController, Route("only")]
        public class OnlyController : ControllerBase { [HttpGet] public int Get() => 1; }
        """);

    [Test]
    public async Task Two_background_services_with_the_same_name_in_different_projects_get_the_project_in_the_key()
    {
        var map = await MapAsync(Build(true, WorkerIn("Jobs", "public class Cleaner : Microsoft.Extensions.Hosting.BackgroundService { protected override Task ExecuteAsync(CancellationToken ct) => Task.CompletedTask; }"), WorkerIn("Mail")));
        Assert.That(Keys(map), Is.EquivalentTo(new[] { "Bakgrund: Worker (Jobs)", "Bakgrund: Worker (Mail)", "Bakgrund: Cleaner" }), "bara de som krockar får projektet; Cleaner är unik och är som förut");
        Assert.That(map.EntryPoints.Select(e => (e.BaseKey, e.Qualifier)), Is.EquivalentTo(new (string, string?)[]
        {
            ("Bakgrund: Worker", "Jobs"), ("Bakgrund: Worker", "Mail"), ("Bakgrund: Cleaner", null),
        }));
        Assert.That(map.EntryPoints.Select(e => e.Key).Distinct().Count(), Is.EqualTo(map.EntryPoints.Count), "namnen är unika");
    }

    [Test]
    public async Task The_same_route_in_two_web_apps_gets_the_project_in_the_key_and_a_unique_route_does_not()
    {
        var map = await MapAsync(Build(true, HealthIn("Api"), HealthIn("Web")));
        Assert.That(Keys(map), Is.EquivalentTo(new[] { "GET /health (Api)", "GET /health (Web)", "GET /only (Api)", "GET /only (Web)" }),
            "samma rutt i båda: projektet efter. /only finns också i båda, så också den");

        var single = await MapAsync(Build(true, HealthIn("Api"), Web("Web", "[ApiController, Route(\"annat\")] public class AController : ControllerBase { [HttpGet] public int Get() => 1; }")));
        Assert.That(Keys(single), Is.EquivalentTo(new[] { "GET /health", "GET /only", "GET /annat" }), "ingen krock: inget projekt i namnet");
    }

    [Test]
    public async Task Two_entries_with_the_same_name_in_one_project_get_the_place_in_the_code_too()
    {
        var map = await MapAsync(Build(true, new Proj("Jobs", """
            namespace Alfa { public class Worker : Microsoft.Extensions.Hosting.BackgroundService { protected override Task ExecuteAsync(CancellationToken ct) => Task.CompletedTask; } }
            namespace Beta { public class Worker : Microsoft.Extensions.Hosting.BackgroundService { protected override Task ExecuteAsync(CancellationToken ct) => Task.CompletedTask; } }
            """), WorkerIn("Mail")));
        var keys = Keys(map).Order(StringComparer.Ordinal).ToList();
        Assert.That(keys, Has.Count.EqualTo(3));
        Assert.That(keys.Distinct().Count(), Is.EqualTo(3), "alla namn är unika");
        Assert.That(keys, Has.Exactly(2).Matches<string>(k => System.Text.RegularExpressions.Regex.IsMatch(k!, @"^Bakgrund: Worker \(Jobs, Jobs/Fil\d\.cs:\d+\)$")), "projektet och platsen när projektet inte räcker");
        Assert.That(keys, Does.Contain("Bakgrund: Worker (Mail)"), "i projektet där namnet är ensamt räcker projektet");
    }

    [Test]
    public async Task The_overview_gives_each_entry_with_the_same_name_its_own_node_and_path()
    {
        var map = await MapAsync(Build(true, WorkerIn("Jobs"), WorkerIn("Mail")));
        var overview = CodeMapOverview.Build(map);
        var entries = overview.Nodes.Where(n => n.Kind == OverviewKind.Entry).ToList();
        Assert.That(entries.Select(n => n.Id), Is.EqualTo(new[] { "in:Bakgrund: Worker (Jobs)", "in:Bakgrund: Worker (Mail)" }).AsCollection, "en ingång per Worker, inte en som slagits ihop");
        // Varje Worker når sin egen filskrivning: pilarna går genom var sin klass.
        var op = overview.Nodes.Single(n => n.Kind == OverviewKind.Operation && n.Name == "skriva Jobs.txt");
        Assert.That(op.Entries, Is.EqualTo(new[] { "Bakgrund: Worker (Jobs)" }));
        Assert.That(overview.Nodes.Single(n => n.Id == CodeMapOverview.ClassId("Mail", "Worker")).Entries, Is.EqualTo(new[] { "Bakgrund: Worker (Mail)" }));
    }

    [Test]
    public async Task Mermaid_entry_takes_the_key_with_the_project_and_says_which_ones_exist_for_the_name_without_it()
    {
        var map = await MapAsync(Build(true, HealthIn("Api"), HealthIn("Web")));
        var web = CodeMapMermaid.Write(map, MermaidOptions.Parse("methods", null, "get /health (web)"));
        Assert.That(web, Does.Contain("Bara vägen för GET /health (Web).").And.Contain("Web"), "stora och små bokstäver spelar ingen roll");
        Assert.That(web, Does.Not.Contain("HealthController_2"), "bara en av dem");
        var e = Assert.Throws<UsageException>(() => CodeMapMermaid.Write(map, MermaidOptions.Parse("classes", null, "GET /health")))!;
        Assert.That((e.Message, e.Tip), Is.EqualTo(("Anropet 'GET /health' finns i flera projekt.", "Skriv det med projektet: \"GET /health (Api)\", \"GET /health (Web)\".")));

        var unique = await MapAsync(Build(true, HealthIn("Api"), Web("Web", "[ApiController, Route(\"annat\")] public class AController : ControllerBase { [HttpGet] public int Get() => 1; }")));
        Assert.That(CodeMapMermaid.Write(unique, MermaidOptions.Parse("classes", null, "GET /health")), Does.Contain("Bara vägen för GET /health."), "ett unikt namn skrivs som förut");
        Assert.Throws<UsageException>(() => CodeMapMermaid.Write(unique, MermaidOptions.Parse("classes", null, "GET /nej")));
    }

    [Test]
    public async Task Json_has_a_qualifier_only_on_entries_that_share_a_name_and_the_overview_uses_the_full_key()
    {
        var map = await MapAsync(Build(true, HealthIn("Api"), Web("Web", "[ApiController, Route(\"health\")] public class HealthController : ControllerBase { [HttpGet] public int Get() => 1; }")));
        using var doc = JsonDocument.Parse(CodeMapJson.Serialize(map));
        var entries = doc.RootElement.GetProperty("entryPoints").EnumerateArray().ToList();
        Assert.That(entries.Select(e => (e.GetProperty("path").GetString(), e.GetProperty("project").GetString(), e.TryGetProperty("qualifier", out var q) ? q.GetString() : null)),
            Is.EqualTo(new (string?, string?, string?)[] { ("/health", "Api", "Api"), ("/health", "Web", "Web"), ("/only", "Api", null) }).AsCollection);
        Assert.That(doc.RootElement.GetProperty("overview").GetProperty("nodes").EnumerateArray().Where(n => n.GetProperty("kind").GetString() == "entry").Select(n => n.GetProperty("name").GetString()),
            Is.EquivalentTo(new[] { "GET /health (Api)", "GET /health (Web)", "GET /only" }));

        var unique = await MapAsync(Build(true, HealthIn("Api")));
        Assert.That(CodeMapJson.Serialize(unique), Does.Not.Contain("qualifier"), "fältet finns bara när namnet annars skulle krocka");
    }

    [Test]
    public async Task Text_shows_the_name_that_Mermaid_entry_takes()
    {
        var map = await MapAsync(Build(true, WorkerIn("Jobs"), WorkerIn("Mail")));
        Assert.That(string.Join("\n", CodeMapText.Lines(map)), Does.Contain("Bakgrund: Worker (Jobs)  Worker.ExecuteAsync [Jobs]").And.Contain("Bakgrund: Worker (Mail)  Worker.ExecuteAsync [Mail]"));

        var http = await MapAsync(Build(true, HealthIn("Api"), HealthIn("Web")));
        Assert.That(string.Join("\n", CodeMapText.Lines(http)), Does.Contain("GET /health (Api)  HealthController.Get [Api]").And.Contain("GET /health (Web)  HealthController.Get [Web]"));
    }

    [Test]
    public async Task Page_lists_both_entries_by_full_key_and_finds_the_call_in_the_project_that_is_tested()
    {
        var map = await MapAsync(Build(true, HealthIn("Api"), HealthIn("Web")));
        await WithPageAsync(map, async (page, ready) =>
        {
            Assert.That(await OptionTextsAsync(page, "entry"),
                Is.EqualTo(new[] { "GET /health (Api)", "GET /health (Web)", "GET /only (Api)", "GET /only (Web)" }));
            // Samma rutt i två projekt: utan projekt det första, med projekt det som testas.
            Assert.That(await page.EvaluateAsync<string[]>("() => [SystemMap.entryFor(null, 'GET', '/health'), SystemMap.entryFor(null, 'GET', '/health', 'Web'), SystemMap.entryFor(null, 'GET', '/health', 'Api'), SystemMap.entryFor(null, 'GET', '/health', 'Okänt')].map(e => e.project)"),
                Is.EqualTo(new[] { "Api", "Web", "Api", "Api" }));
            await PickAsync(page, "entry", "GET /health (Web)");
            await ready();
            Assert.That(await page.EvaluateAsync<string[]>("() => SystemMap.cy().nodes('.t-entry').filter(n => !n.hasClass('dimmed')).map(n => n.data('label'))"),
                Is.EqualTo(new[] { "GET /health (Web)" }), "valet av det ena anropet dämpar det andra");
        });
    }
}
