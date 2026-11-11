using Microsoft.Playwright;

namespace SystemMap.Tests;

/// <summary>Kartan använder skärmen: startvyn är projekten, anropsgrupperna är hopfällda, projekten står i flödets ordning och anropen och
/// integrationerna står nära systemrutan.</summary>
public partial class CodeMapTests
{
    private const string CollapsedCalls = "() => SystemMap.cy().nodes('.t-calls').map(n => n.data('label')).sort()";
    private const string OpenGroups = "() => SystemMap.cy().nodes('.t-callgroup').map(n => n.data('label')).sort()";

    // --- Startvyn -----------------------------------------------------------------------------------

    [Test]
    public async Task Page_opens_at_the_level_projects_when_nothing_is_saved_and_Rensa_goes_back_to_it()
    {
        var map = await MapAsync(Build(true, Web("Api", AppStart + FlowApi)));
        await WithPageAsync(map, async (page, ready) =>
        {
            Assert.That(await StoredLevelAsync(page), Is.Null, "inget sparat val: sidan väljer själv, utan att spara");
            Assert.That(await page.InputValueAsync("#map-cls"), Is.EqualTo("Visas inte"));
            Assert.That(await page.IsDisabledAsync("#map-fn"), Is.True);
            Assert.That(await page.EvaluateAsync<int>("() => SystemMap.cy().nodes('.t-class').length"), Is.Zero, "bara projekten");
            Assert.That(await page.EvaluateAsync<int>("() => SystemMap.cy().nodes('.t-project').length"), Is.EqualTo(1));
            Assert.That(await page.GetAttributeAsync("#map-clear", "aria-disabled"), Is.EqualTo("true"), "startvyn är inget val: Rensa är grå");

            await PickAsync(page, "cls", "Alla");
            await ready();
            Assert.That(await page.EvaluateAsync<int>("() => SystemMap.cy().nodes('.t-class').length"), Is.GreaterThan(0));
            await LevelAsync(page, "methods");
            await ready();
            Assert.That(await page.EvaluateAsync<int>("() => SystemMap.cy().nodes('.t-method').length"), Is.GreaterThan(0));
            await page.ClickAsync("#map-clear");
            await ready();
            Assert.That(await page.InputValueAsync("#map-cls"), Is.EqualTo("Visas inte"));
            Assert.That(await page.EvaluateAsync<int>("() => SystemMap.cy().nodes('.t-class, .t-method').length"), Is.Zero);
            Assert.That(await StoredLevelAsync(page), Is.EqualTo("projects"));
        }, level: null);
    }

    [TestCase("classes")]
    [TestCase("methods")]
    public async Task Page_uses_a_saved_level_before_the_start_view(string saved)
    {
        var map = await MapAsync(Build(true, Web("Api", AppStart + FlowApi)));
        await WithPageAsync(map, async (page, ready) =>
        {
            Assert.That(await page.InputValueAsync("#map-cls"), Is.EqualTo("Alla"));
            Assert.That(await page.EvaluateAsync<int>("() => SystemMap.cy().nodes('.t-class').length"), Is.GreaterThan(0));
            Assert.That(await page.EvaluateAsync<int>("() => SystemMap.cy().nodes('.t-method').length"), saved == "methods" ? Is.GreaterThan(0) : Is.Zero);
            await page.ReloadAsync();
            await ready();
            Assert.That(await page.EvaluateAsync<int>("() => SystemMap.cy().nodes('.t-class').length"), Is.GreaterThan(0), "det sparade valet består");
        }, level: saved);
    }

    // --- Hopfällda anropsgrupper --------------------------------------------------------------------

    [Test]
    public async Task A_call_group_is_one_call_with_the_count_until_it_is_opened_and_shut_again_with_a_click()
    {
        var map = await MapAsync(Build(true, Web("Api", AppStart + FlowApi)));
        await WithPageAsync(map, async (page, ready) =>
        {
            Assert.That(await page.EvaluateAsync<string[]>(CollapsedCalls), Is.EqualTo(new[] { "/v1/auth · 2 anrop", "/v1/family · 2 anrop" }));
            Assert.That(await page.EvaluateAsync<string[]>(OpenGroups), Is.Empty);
            Assert.That(await page.EvaluateAsync<string[]>(EntryNodes), Is.EqualTo(new[] { "/v1/auth · 2 anrop", "/v1/family · 2 anrop", "GET /v1/me" }),
                "grupperna är ett anrop var; det ensamma anropet är som förut");
            Assert.That(await page.EvaluateAsync<bool>("() => SystemMap.cy().nodes('.t-calls').every(n => n.hasClass('long') && n.data('icon') !== '' && n.height() === 30 && !n.hasClass('badge') && n.width() > 100)"), Is.True,
                "utseende som ett anrop: ikon, bredd efter texten, ingen +N-bricka");
            Assert.That(await page.EvaluateAsync<string[]>("() => SystemMap.cy().edges().not('.band').filter(e => e.source().hasClass('t-calls')).map(e => e.source().data('label') + '>' + e.target().data('label').split('\\n')[0]).sort()"),
                Is.EqualTo(new[] { "/v1/auth · 2 anrop>AuthController", "/v1/family · 2 anrop>FamilyController" }), "en pil från det hopfällda anropet till det anropen når");
            Assert.That(await OptionTextsAsync(page, "entry"), Is.EqualTo(new[] { "POST /v1/auth/in", "POST /v1/auth/out", "GET /v1/family", "POST /v1/family", "GET /v1/me" }),
                "listan Anrop har de riktiga anropen");

            // Ett klick fäller ut gruppen: rutan med anropen.
            await TapNodeAsync(page, ".t-calls", "/v1/auth · 2 anrop");
            await ready();
            Assert.That(await page.EvaluateAsync<string[]>(OpenGroups), Is.EqualTo(new[] { "/v1/auth" }));
            Assert.That(await page.EvaluateAsync<string[]>(CollapsedCalls), Is.EqualTo(new[] { "/v1/family · 2 anrop" }));
            Assert.That(await page.EvaluateAsync<string[]>("() => SystemMap.cy().nodes('.t-callgroup').children().map(n => n.data('label')).sort()"), Is.EqualTo(new[] { "POST /v1/auth/in", "POST /v1/auth/out" }));

            // Utfälld grupp kommer man ihåg när något annat ändras (som för projekt och klasser).
            await PickAsync(page, "project", "Api");
            await ready();
            Assert.That(await page.EvaluateAsync<string[]>(OpenGroups), Is.EqualTo(new[] { "/v1/auth" }));

            // Ett klick på rutan fäller ihop den igen.
            await TapNodeAsync(page, ".t-callgroup", "/v1/auth");
            await ready();
            Assert.That(await page.EvaluateAsync<string[]>(OpenGroups), Is.Empty);
            Assert.That(await page.EvaluateAsync<string[]>(CollapsedCalls), Is.EqualTo(new[] { "/v1/auth · 2 anrop", "/v1/family · 2 anrop" }));
        }, groupsOpen: false);
    }

    [Test]
    public async Task Choosing_a_call_in_a_collapsed_group_opens_the_group_and_marks_the_path_and_a_shut_group_is_marked_as_the_path()
    {
        var map = await MapAsync(Build(true, Web("Api", AppStart + FlowApi)));
        await WithPageAsync(map, async (page, ready) =>
        {
            await PickAsync(page, "entry", "POST /v1/auth/in");
            await ready();
            Assert.That(await page.EvaluateAsync<string[]>(OpenGroups), Is.EqualTo(new[] { "/v1/auth" }), "gruppen med det valda anropet fälls ut");
            Assert.That(await page.EvaluateAsync<string[]>(PathEntries), Is.EqualTo(new[] { "POST /v1/auth/in" }), "anropet syns och vägen markeras");
            Assert.That(await page.EvaluateAsync<string[]>(CollapsedCalls), Is.EqualTo(new[] { "/v1/family · 2 anrop" }));
            Assert.That(await page.EvaluateAsync<bool>("() => SystemMap.cy().nodes('.t-calls').every(n => n.hasClass('dimmed'))"), Is.True, "gruppen utan det valda anropet är dämpad");

            // Fälls gruppen ihop ändå markeras den som vägen.
            await TapNodeAsync(page, ".t-callgroup", "/v1/auth");
            await ready();
            Assert.That(await page.EvaluateAsync<string[]>(OpenGroups), Is.Empty);
            Assert.That(await page.EvaluateAsync<string[]>("() => SystemMap.cy().nodes('.t-calls').filter(n => n.hasClass('path')).map(n => n.data('label'))"), Is.EqualTo(new[] { "/v1/auth · 2 anrop" }));
            Assert.That(await page.InputValueAsync("#map-entry"), Is.EqualTo("POST /v1/auth/in"), "valet ligger kvar");

            // Ett anrop i en annan grupp: den fälls ut, den första (ihopfällda igen) är inte längre vald.
            await PickAsync(page, "entry", "POST /v1/auth/in", "GET /v1/family");
            await ready();
            Assert.That(await page.EvaluateAsync<string[]>(OpenGroups), Is.EqualTo(new[] { "/v1/family" }));
        }, groupsOpen: false);
    }

    [Test]
    public async Task Collapsed_call_groups_work_in_the_vertical_direction_and_a_group_of_a_kind_is_collapsed_too()
    {
        var map = await MapAsync(Build(true, Web("Api", AppStart + FlowApi + Writer + MixedCode + """

            public class OtherConsumer : MassTransit.IConsumer<OrderPlaced> { public Task Consume(MassTransit.ConsumeContext<OrderPlaced> c) => Task.CompletedTask; }
            """)));
        await WithPageAsync(map, async (page, ready) =>
        {
            await page.ClickAsync("[data-direction='TB']");
            await ready();
            var collapsed = await page.EvaluateAsync<string[]>(CollapsedCalls);
            Assert.That(collapsed, Does.Contain("/v1 · 5 anrop"), "området är det första segmentet efter den gemensamma början (här ingen)");
            Assert.That(collapsed, Does.Contain("Meddelanden · 2 anrop"), "en grupp av en sort är hopfälld på samma sätt");
            await TapNodeAsync(page, ".t-calls", "Meddelanden · 2 anrop");
            await ready();
            Assert.That(await page.EvaluateAsync<string[]>(OpenGroups), Is.EqualTo(new[] { "Meddelanden" }));
            Assert.That(await page.EvaluateAsync<string[]>("() => SystemMap.cy().nodes('.t-callgroup').children().map(n => n.data('label')).sort()"),
                Is.EqualTo(new[] { "Meddelande: OrderPlaced → OrderPlacedConsumer", "Meddelande: OrderPlaced → OtherConsumer" }));
        }, groupsOpen: false);
    }

    private static Task TapNodeAsync(IPage page, string selector, string label) =>
        page.EvaluateAsync("([selector, label]) => { SystemMap.cy().nodes(selector).filter(n => n.data('label') === label)[0].emit('tap'); }", new[] { selector, label });

    // --- Projekten i flödets ordning ----------------------------------------------------------------

    /// <summary>Zeta och Mid tar emot anropen och anropar Alpha, som kommer först i bokstavsordning: Alpha står ändå efter dem.</summary>
    private static Task<CodeMapModel> FlowOrdered() => MapAsync(Build(true,
        new Proj("Alpha", "public class Worker { public int Go() => 1; }\npublic class Helper { public int Help() => 2; }"),
        Web("Mid", AppStart + "\n[ApiController, Route(\"m\")] public class MidController : ControllerBase { [HttpGet] public int Get() => new Worker().Go(); }", "Alpha"),
        Web("Zeta", AppStart + "\n[ApiController, Route(\"z\")] public class ZetaController : ControllerBase { [HttpGet] public int Get() => new Helper().Help(); }", "Alpha")));

    [TestCase("LR")]
    [TestCase("TB")]
    public async Task Projects_stand_in_the_order_of_the_flow_and_projects_at_the_same_depth_share_a_column(string direction)
    {
        var map = await FlowOrdered();
        await WithPageAsync(map, async (page, ready) =>
        {
            if (direction == "TB") { await page.ClickAsync("[data-direction='TB']"); await ready(); }
            var axis = direction == "LR" ? "x" : "y";
            async Task<Dictionary<string, double[]>> Spans()
            {
                var boxes = await page.EvaluateAsync<Dictionary<string, double[]>>($"() => Object.fromEntries(SystemMap.cy().nodes('.t-project').map(n => [n.data('label').split('\\n')[0], [n.boundingBox()['{axis}1'], n.boundingBox()['{axis}2']]]))");
                Assert.That(boxes.Keys, Is.EquivalentTo(new[] { "Alpha", "Mid", "Zeta" }));
                return boxes;
            }
            async Task Check(string when)
            {
                var b = await Spans();
                Assert.That(b["Alpha"][0], Is.GreaterThan(b["Zeta"][1]), $"Alpha anropas av Zeta, så den står efter den ({when})");
                Assert.That(b["Alpha"][0], Is.GreaterThan(b["Mid"][1]), $"Alpha står efter Mid ({when})");
                Assert.That(Math.Min(b["Mid"][1], b["Zeta"][1]), Is.GreaterThan(Math.Max(b["Mid"][0], b["Zeta"][0])), $"Mid och Zeta är på samma djup och delar kolumn ({when})");
            }
            await Check("projekt");

            await PickAsync(page, "cls", "Alla");
            await ready();
            await Check("klasser");
        }, level: "projects", groupsOpen: false);
    }

    // --- Kortare pilar ------------------------------------------------------------------------------

    private static async Task<double[]> GapsAsync(IPage page, bool lr) => await page.EvaluateAsync<double[]>("""
        lr => {
          const cy = SystemMap.cy(), b = c => c.boundingBox({ includeLabels: false, includeOverlays: false }), sys = b(cy.getElementById('system'));
          const calls = b(cy.nodes('.t-entry, .t-callgroup')), ints = b(cy.nodes('.t-integration'));
          return lr ? [sys.x1 - calls.x2, ints.x1 - sys.x2] : [sys.y1 - calls.y2, ints.y1 - sys.y2];
        }
        """, lr);

    /// <summary>Anropen, anropsgrupperna och integrationerna överlappar inte systemrutan eller varandra.</summary>
    private static async Task AssertNothingOverlapsAsync(IPage page, string when)
    {
        var bad = await page.EvaluateAsync<string[]>("""
            () => {
              const cy = SystemMap.cy(), b = n => n.boundingBox({ includeLabels: false, includeOverlays: false }), sys = b(cy.getElementById('system')), bad = [];
              const hit = (p, q) => p.x1 < q.x2 - 1 && q.x1 < p.x2 - 1 && p.y1 < q.y2 - 1 && q.y1 < p.y2 - 1;
              const things = cy.nodes('.t-entry, .t-callgroup, .t-integration').toArray();
              for (const n of things) if (hit(b(n), sys)) bad.push(n.id() + ' och systemrutan');
              for (let i = 0; i < things.length; i++) for (let j = i + 1; j < things.length; j++) {
                const p = things[i], q = things[j];
                if (p.ancestors().contains(q) || q.ancestors().contains(p)) continue;
                if (hit(b(p), b(q))) bad.push(p.id() + ' och ' + q.id());
              }
              return bad;
            }
            """);
        Assert.That(bad, Is.Empty, $"överlappar ({when})");
    }

    [TestCase("projects", "LR")]
    [TestCase("projects", "TB")]
    [TestCase("classes", "LR")]
    [TestCase("classes", "TB")]
    public async Task Calls_and_integrations_stand_close_to_the_system_box_and_do_not_overlap(string level, string direction)
    {
        var map = await MapAsync(Build(true, Web("Api", AppStart + FlowApi)));
        await WithPageAsync(map, async (page, ready) =>
        {
            var lr = direction == "LR";
            if (!lr) { await page.ClickAsync("[data-direction='TB']"); await ready(); }
            foreach (var step in new[] { "grupperna hopfällda", "grupperna utfällda", "en grupp hopfälld igen", "ett anrop valt" })
            {
                if (step == "grupperna utfällda") { await OpenGroupsAsync(page, ready); }
                else if (step == "en grupp hopfälld igen") { await TapNodeAsync(page, ".t-callgroup", "/v1/auth"); await ready(); }
                else if (step == "ett anrop valt") { await PickAsync(page, "entry", "GET /v1/me"); await ready(); }
                var gaps = await GapsAsync(page, lr);
                var when = $"{level} {direction}, {step}";
                Assert.That(gaps[0], Is.InRange(60, 150), $"luften mellan anropen och systemrutan ({when})");
                Assert.That(gaps[1], Is.InRange(60, 150), $"luften mellan systemrutan och integrationerna ({when})");
                await AssertNothingOverlapsAsync(page, when);
            }
            // Pilarna är korta: ingen pil från ett anrop in är längre än till närmaste projekt plus lite.
            Assert.That(await page.EvaluateAsync<bool>("() => SystemMap.cy().edges().not('.band').filter(e => e.source().hasClass('t-entry') || e.source().hasClass('t-callgroup')).every(e => Math.hypot(e.source().position().x - e.target().position().x, e.source().position().y - e.target().position().y) < 500)"), Is.True);
        }, level: level, groupsOpen: false);
    }

    [TestCase("LR")]
    [TestCase("TB")]
    public async Task Many_integrations_stand_in_several_columns_next_to_the_system_box_without_overlap(string direction)
    {
        var calls = string.Join("\n", Enumerable.Range(1, 14).Select(i => $"await _f.CreateClient(\"svc{i:00}\").GetAsync(\"x\");"));
        var map = await MapAsync(Build(true, Web("Api", AppStart + "\n[ApiController, Route(\"many\")] public class ManyController : ControllerBase { System.Net.Http.IHttpClientFactory _f = null!; [HttpGet] public async Task<int> Get() { " + calls + " return 1; } }")));
        await WithPageAsync(map, async (page, ready) =>
        {
            var lr = direction == "LR";
            if (!lr) { await page.ClickAsync("[data-direction='TB']"); await ready(); }
            var n = await page.EvaluateAsync<int>("() => SystemMap.cy().nodes('.t-integration').length");
            Assert.That(n, Is.EqualTo(14));
            var gaps = await GapsAsync(page, lr);
            Assert.That(gaps[1], Is.InRange(60, 150), "första kolumnen står nära systemrutan");

            // Kolumnerna: platserna längs flödet. Fler än en, med ca 130 (LR) eller 120 (TB) mellan, och inom en kolumn står de ca 112 (LR) eller 130 (TB) isär tvärs flödet.
            var at = await page.EvaluateAsync<double[][]>($"() => SystemMap.cy().nodes('.t-integration').map(n => [n.position('{(lr ? "x" : "y")}'), n.position('{(lr ? "y" : "x")}')])");
            var columns = at.Select(p => Math.Round(p[0])).Distinct().OrderBy(v => v).ToArray();
            Assert.That(columns, Has.Length.GreaterThan(1), "längre än systemrutan: flera kolumner");
            for (var i = 1; i < columns.Length; i++) Assert.That(columns[i] - columns[i - 1], Is.InRange(lr ? 125 : 115, lr ? 135 : 125), "avståndet mellan kolumnerna");
            foreach (var column in at.GroupBy(p => Math.Round(p[0])))
            {
                var across = column.Select(p => p[1]).OrderBy(v => v).ToArray();
                for (var i = 1; i < across.Length; i++) Assert.That(across[i] - across[i - 1], Is.GreaterThanOrEqualTo(lr ? 110 : 128), "avståndet inom kolumnen");
            }
            await AssertNothingOverlapsAsync(page, direction);
        }, groupsOpen: false);
    }
}
