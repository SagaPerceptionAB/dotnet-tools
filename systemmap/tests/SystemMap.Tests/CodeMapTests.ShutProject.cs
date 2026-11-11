using Microsoft.Playwright;

namespace SystemMap.Tests;

/// <summary>Ett hopfällt projekt (grönt): en rundad ruta med innehållet i texten, utan bricka, som får plats i utläggningen.</summary>
public partial class CodeMapTests
{
    private const string ShutBoxes = "SystemMap.cy().nodes('.t-project').not('.group')";
    private const string ShutTexts = "() => " + ShutBoxes + ".map(n => n.data('label').replace('\\n', ' | ')).sort()";
    private const string Leaves = "() => SystemMap.cy().nodes().not('.system, .group, .anchor').filter(n => !n.isParent()).map(n => [n.id(), n.boundingBox({ includeLabels: false, includeOverlays: false })])";

    // Lib: två klasser med en funktion var, Solo: en klass med en funktion, Data: en klass utan funktioner.
    private static Task<CodeMapModel> TwoProjects() => MapAsync(Build(true,
        new Proj("Lib", "public class Writer { public void Write() => System.IO.File.WriteAllText(\"ut.txt\", \"x\"); }\npublic class Marker { public void Other() { } }\npublic class Dto { }"),
        new Proj("Data", "public class Plain { }"),
        new Proj("Solo", "public class Single { public void Go() { } }"),
        Web("Api", AppStart + FlowApi + "\n[ApiController, Route(\"w\")] public class WController : ControllerBase { [HttpGet] public int Get() { new Writer().Write(); new Single().Go(); new Plain(); return 1; } }\n[ApiController, Route(\"m\")] public class MController : ControllerBase { [HttpGet] public int Get() { new Marker().Other(); return 1; } }", "Lib", "Solo", "Data")));

    /// <summary>Ingen hopfälld projektruta överlappar någon annan nod.</summary>
    private static async Task AssertNoOverlapAsync(IPage page, string when)
    {
        var boxes = (await page.EvaluateAsync<System.Text.Json.JsonElement>(Leaves)).EnumerateArray().Select(e => e.EnumerateArray().ToArray()).ToArray();
        Assert.That(boxes, Is.Not.Empty);
        for (var i = 0; i < boxes.Length; i++)
            for (var j = i + 1; j < boxes.Length; j++)
            {
                if (!boxes[i][0].GetString()!.StartsWith("project:") && !boxes[j][0].GetString()!.StartsWith("project:")) continue;
                double V(int k, string p) => boxes[k][1].GetProperty(p).GetDouble();
                var overlap = V(i, "x1") < V(j, "x2") - 1 && V(j, "x1") < V(i, "x2") - 1 && V(i, "y1") < V(j, "y2") - 1 && V(j, "y1") < V(i, "y2") - 1;
                Assert.That(overlap, Is.False, $"{boxes[i][0].GetString()} och {boxes[j][0].GetString()} överlappar ({when})");
            }
    }

    [Test]
    public async Task A_shut_project_is_a_rounded_box_with_what_it_holds_and_no_badge()
    {
        var map = await TwoProjects();
        await WithPageAsync(map, async (page, ready) =>
        {
            await LevelAsync(page, "projects");
            await ready();
            Assert.That(await page.EvaluateAsync<bool>("() => " + ShutBoxes + ".length === 4 && " + ShutBoxes + ".every(n => n.hasClass('box') && !n.hasClass('badge') && n.style('corner-radius') === '24px' && n.height() === 84 && n.width() >= 230 && n.style('background-opacity') === '1' && n.style('background-color') === 'rgb(238,245,238)')"), Is.True);
            Assert.That(await page.EvaluateAsync<string[]>(ShutTexts), Has.Member("Lib | 2 klasser · 2 funktioner").And.Member("Solo | 1 klass · 1 funktion").And.Member("Data"),
                "singular, plural och bara namnet när allt är 0");
            Assert.That(await page.EvaluateAsync<bool>("() => SystemMap.cy().nodes('.t-integration').every(n => !n.hasClass('box'))"), Is.True, "integrationer behåller sin form");
        });
    }

    [Test]
    public async Task A_shut_project_leaves_out_a_part_that_is_zero_and_only_counts_what_passes_the_filter()
    {
        var map = await TwoProjects();
        await WithPageAsync(map, async (page, ready) =>
        {
            await PickAsync(page, "cls", "Writer");
            await ready();
            await page.EvaluateAsync("() => { SystemMap.cy().nodes('.t-project').filter(n => n.data('label') === 'Lib')[0].emit('tap'); }");
            await ready();
            Assert.That(await page.EvaluateAsync<string[]>(ShutTexts), Is.EqualTo(new[] { "Lib | 1 klass · 1 funktion" }), "bara det som går igenom filtret räknas");
        });
    }

    [Test]
    public async Task A_shut_project_does_not_overlap_other_nodes_at_the_level_projects_in_both_directions()
    {
        var map = await TwoProjects();
        await WithPageAsync(map, async (page, ready) =>
        {
            await LevelAsync(page, "projects");
            await ready();
            await AssertNoOverlapAsync(page, "→");
            await page.ClickAsync("[data-direction='TB']");
            await ready();
            await AssertNoOverlapAsync(page, "↓");
        });
    }

    [Test]
    public async Task A_project_shut_at_the_level_classes_gets_its_box_without_overlap_and_opens_again_on_a_click()
    {
        var map = await TwoProjects();
        await WithPageAsync(map, async (page, ready) =>
        {
            Assert.That(await page.EvaluateAsync<string[]>(ShutTexts), Is.EqualTo(new[] { "Data" }), "från början är projekten utfällda (utom ett utan innehåll)");
            foreach (var dir in new[] { "LR", "TB" })
            {
                await page.ClickAsync($"[data-direction='{dir}']");
                await ready();
                await AssertNoOverlapAsync(page, dir + " före");
                await page.EvaluateAsync("() => { SystemMap.cy().nodes('.t-project').filter(n => n.data('label') === 'Lib')[0].emit('tap'); }");
                await ready();
                Assert.That(await page.EvaluateAsync<string[]>(ShutTexts), Is.EqualTo(new[] { "Data", "Lib | 2 klasser · 2 funktioner" }));
                Assert.That(await page.EvaluateAsync<bool>("() => " + ShutBoxes + ".every(n => !n.hasClass('badge'))"), Is.True);
                await AssertNoOverlapAsync(page, dir);
                await page.EvaluateAsync("() => { " + ShutBoxes + ".filter(n => n.data('label') !== 'Data')[0].emit('tap'); }");
                await ready();
                Assert.That(await page.EvaluateAsync<int>("() => " + ShutBoxes + ".length"), Is.EqualTo(1), "ett klick fäller ut igen");
            }
        });
    }
}
