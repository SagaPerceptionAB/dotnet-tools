using Microsoft.Playwright;

namespace SystemMap.Tests;

/// <summary>Projektreferenserna (csproj) i sidan: linjerna och färgerna på nivån Projekt, antalet anrop, detaljkortet för en linje och teckenförklaringen.</summary>
public partial class CodeMapTests
{
    // Namnet på ett projekt (första raden i etiketten) och linjerna mellan projekt som "från>till".
    private const string Pair = "e => e.source().data('label').split('\\n')[0] + '>' + e.target().data('label').split('\\n')[0]";
    private const string RefLines = "() => SystemMap.cy().edges('.ref').map(" + Pair + ").sort()";
    private const string CountedLines = "() => SystemMap.cy().edges('.counted').map(e => (" + Pair + ")(e) + ' ' + e.data('count')).sort()";

    private static Task TapLineAsync(IPage page, string selector, string pair) =>
        page.EvaluateAsync($"pair => {{ SystemMap.cy().edges('{selector}').filter(e => ({Pair})(e) === pair)[0].emit('tap'); }}", pair);

    private static Task<string[]> LineStyleAsync(IPage page, string pair) =>
        page.EvaluateAsync<string[]>($"pair => {{ const e = SystemMap.cy().edges('.ref').filter(e => ({Pair})(e) === pair)[0]; return [e.style('line-color'), e.style('line-style'), e.style('curve-style'), e.style('target-label')]; }}", pair);

    private static async Task<string> CardTextAsync(IPage page)
    {
        await page.WaitForSelectorAsync("#map-card:not([hidden])");
        return await page.InnerTextAsync("#map-card");
    }

    [Test]
    public async Task Page_at_the_level_Projekt_draws_references_without_calls_as_thin_dashed_lines_and_counts_the_calls()
    {
        var map = await MapRefsAsync();
        await WithPageAsync(map, async (page, ready) =>
        {
            Assert.That(await page.EvaluateAsync<string[]>(RefLines), Is.EqualTo(new[] { "Api>Infrastructure", "Api>Legacy", "Api>Shared", "Infrastructure>Application" }),
                "referenser med anrop har sin pil, de andra en linje");
            Assert.That(await page.EvaluateAsync<string[]>(CountedLines), Does.Contain("Api>Application 1 anrop"));

            // Grå och streckade (uppstart, annat i koden), orange med texten "används inte" när inget används; böjda när anropen går åt andra hållet.
            // (Raka, men böjda runt en nod som står i vägen: se Page_a_reference_line_that_would_run_through_another_project_is_bent_around_it.)
            var straight = Is.AnyOf("straight", "unbundled-bezier");
            foreach (var (pair, color, label) in new[] { ("Api>Infrastructure", "rgb(118,118,118)", ""), ("Api>Shared", "rgb(118,118,118)", ""), ("Api>Legacy", "rgb(191,136,3)", "används inte") })
            {
                var style = await LineStyleAsync(page, pair);
                Assert.That((style[0], style[1], style[3]), Is.EqualTo((color, "dashed", label)), pair);
                Assert.That(style[2], straight, pair);
            }
            Assert.That(await LineStyleAsync(page, "Infrastructure>Application"), Is.EqualTo(new[] { "rgb(118,118,118)", "dashed", "unbundled-bezier", "" }), "anropen går åt andra hållet");
            Assert.That(await page.EvaluateAsync<double>("() => SystemMap.cy().edges('.ref')[0].numericStyle('width')"), Is.LessThan(2), "tunna");
            Assert.That(await page.EvaluateAsync<string>("() => SystemMap.cy().edges('.counted')[0].style('line-style')"), Is.EqualTo("solid"));
        }, level: "projects", groupsOpen: false);
    }

    [Test]
    public async Task Page_reference_lines_follow_the_dark_theme()
    {
        var map = await MapRefsAsync();
        await WithPageAsync(map, async (page, ready) =>
        {
            Assert.That((await LineStyleAsync(page, "Api>Legacy"))[0], Is.EqualTo("rgb(204,167,0)"));
            Assert.That((await LineStyleAsync(page, "Api>Shared"))[0], Is.EqualTo("rgb(157,157,157)"));
            Assert.That(await page.EvaluateAsync<string>("() => getComputedStyle(document.querySelector('#map-refs .map-line.warn')).borderTopColor"), Is.EqualTo("rgb(204, 167, 0)"));

            // Byter man tema ritas färgerna om utan ny utläggning.
            await page.ClickAsync("#map-theme");
            Assert.That((await LineStyleAsync(page, "Api>Legacy"))[0], Is.EqualTo("rgb(191,136,3)"));
            Assert.That(await page.EvaluateAsync<string>("() => getComputedStyle(document.querySelector('#map-refs .map-line.warn')).borderTopColor"), Is.EqualTo("rgb(191, 136, 3)"));
        }, level: "projects", groupsOpen: false, colors: ColorScheme.Dark);
    }

    [Test]
    public async Task Page_click_on_a_line_between_two_projects_opens_the_card_for_the_pair()
    {
        var map = await MapRefsAsync();
        await WithPageAsync(map, async (page, ready) =>
        {
            await TapLineAsync(page, ".ref", "Api>Legacy");
            var card = await CardTextAsync(page);
            Assert.That(card, Does.Contain("Api → Legacy").And.Contain("Används inte: ingen kod i Api använder något i Legacy"));
            Assert.That(await page.EvaluateAsync<bool>("() => !!document.querySelector('#map-card p.warn')"), Is.True, "orange text");
            Assert.That(await page.EvaluateAsync<string>("() => getComputedStyle(document.querySelector('#map-card p.warn')).color"), Is.EqualTo("rgb(191, 136, 3)"));

            await TapLineAsync(page, ".ref", "Api>Infrastructure");
            card = await CardTextAsync(page);
            var infra = Ref(map, "Api", "Infrastructure");
            Assert.That(card, Does.Contain("Api → Infrastructure").And.Contain("Används bara vid uppstart").And.Contain("Exempel: anropar InfraRegistration.AddInfra")
                .And.Contain($"Finns i: Api/Fil1.cs, rad {infra.Line}"));
            Assert.That(card, Does.Not.Contain("Legacy"), "ett kort i taget");

            // En pil med anrop öppnar också kortet, med anropen och vad referensen används till.
            await TapLineAsync(page, ".counted", "Api>Application");
            card = await CardTextAsync(page);
            Assert.That(card, Does.Contain("Api → Application").And.Contain("Används av 1 anrop").And.Contain("Används av anropen: POST /api/Orders").And.Contain("Exempel: använder IOrderService"));

            // Anropen går åt andra hållet: kortet säger det.
            await TapLineAsync(page, ".ref", "Infrastructure>Application");
            Assert.That(await CardTextAsync(page), Does.Contain("Infrastructure → Application").And.Contain("Exempel: implementerar IOrderRepo").And.Contain("Anrop går också från Application till Infrastructure"));

            // Kortet stängs med ×, och ett klick på ett projekt fäller ut det som förut (inget kort).
            await page.ClickAsync("#map-card .close");
            Assert.That(await page.IsHiddenAsync("#map-card"), Is.True);
        }, level: "projects", groupsOpen: false);
    }

    [Test]
    public async Task Page_a_click_on_a_project_still_opens_it_and_a_project_that_is_open_has_no_reference_lines()
    {
        var map = await MapRefsAsync();
        await WithPageAsync(map, async (page, ready) =>
        {
            await page.EvaluateAsync("() => { SystemMap.cy().nodes('.t-project').filter(n => n.data('label').split('\\n')[0] === 'Api')[0].emit('tap'); }");
            await ready();
            Assert.That(await page.IsHiddenAsync("#map-card"), Is.True, "projektet fälls ut, inget kort");
            Assert.That(await page.EvaluateAsync<int>("() => SystemMap.cy().nodes('.t-class').length"), Is.GreaterThan(0));
            Assert.That(await page.EvaluateAsync<string[]>(RefLines), Is.EqualTo(new[] { "Infrastructure>Application" }), "bara mellan projekt som ritas hopfällda");
        }, level: "projects", groupsOpen: false);
    }

    [Test]
    public async Task Page_draws_no_reference_lines_at_the_levels_Klasser_and_below_and_the_legend_goes_away()
    {
        var map = await MapRefsAsync();
        await WithPageAsync(map, async (page, ready) =>
        {
            Assert.That(await page.IsVisibleAsync("#map-refs"), Is.True);
            await PickAsync(page, "cls", "Alla");
            await ready();
            Assert.That(await page.EvaluateAsync<int>("() => SystemMap.cy().edges('.ref').length"), Is.Zero);
            Assert.That(await page.EvaluateAsync<int>("() => SystemMap.cy().edges('.counted').length"), Is.Zero, "projekten är utfällda: inga pilar mellan projekt");
            Assert.That(await page.IsHiddenAsync("#map-refs"), Is.True);

            await PickAsync(page, "cls", "Visas inte");
            await ready();
            Assert.That(await page.EvaluateAsync<string[]>(RefLines), Has.Length.EqualTo(4));
            Assert.That(await page.IsVisibleAsync("#map-refs"), Is.True);
        }, level: "projects", groupsOpen: false);
    }

    [Test]
    public async Task Page_legend_has_three_rows_about_the_lines_only_when_there_are_references_to_show()
    {
        await WithPageAsync(await MapRefsAsync(), async (page, ready) =>
        {
            Assert.That(await page.IsVisibleAsync("#map-legend"), Is.True);
            Assert.That(await page.InnerTextAsync("#map-refs"), Is.EqualTo(
                "Mellan projekten\nanrop som går genom referensen (antal anrop in)\nreferens i csproj som används på annat sätt (uppstart, anrop åt andra hållet)\nreferens i csproj som inte används"));
            Assert.That(await page.Locator("#map-refs .map-ref-row").CountAsync(), Is.EqualTo(3));
            Assert.That(await page.GetAttributeAsync("#map-refs", "aria-label"), Is.EqualTo("Linjerna mellan projekten"));
        }, level: "projects", groupsOpen: false);

        await WithPageAsync(await MapAsync(Controller("", "")), async (page, ready) =>
        {
            Assert.That(await page.IsHiddenAsync("#map-refs"), Is.True, "inga referenser");
            Assert.That(await page.IsHiddenAsync("#map-legend"), Is.True);
        }, level: "projects", groupsOpen: false, showUnreached: false);
    }

    [Test]
    public async Task Page_a_project_that_no_call_reaches_but_another_references_stands_after_the_one_that_references_it()
    {
        await WithPageAsync(await MapRefsAsync(), async (page, ready) =>
        {
            var x = await page.EvaluateAsync<Dictionary<string, double>>("() => Object.fromEntries(SystemMap.cy().nodes('.t-project').map(n => [n.data('label').split('\\n')[0], n.position('x')]))");
            var integrations = await page.EvaluateAsync<double[]>("() => SystemMap.cy().nodes('.t-integration').map(n => n.position('x'))");
            Assert.That(x["Legacy"], Is.GreaterThan(x["Api"]));
            Assert.That(x["Legacy"], Is.LessThan(integrations.Min()), "före integrationerna, inte sist");
        }, level: "projects", groupsOpen: false);
    }

    [Test]
    public async Task Page_picking_a_call_hides_the_reference_lines_and_the_pair_cards_still_work_without_them()
    {
        var map = await MapRefsAsync();
        await WithPageAsync(map, async (page, ready) =>
        {
            await TapEntryAsync(page, "POST /api/Orders");
            await ready();
            Assert.That(await page.EvaluateAsync<int>("() => SystemMap.cy().edges('.ref').length"), Is.Zero);
            Assert.That(await page.IsHiddenAsync("#map-refs"), Is.True);
        }, level: "projects", groupsOpen: false);
    }

    // Varje referenslinje som ritas: böjd eller inte, vilka andra noder kurvan (sampled längs de ritade ändpunkterna och kontrollpunkten) skär och vilka den
    // rakt mellan nodernas mitt skulle ha skurit.
    private const string LineHits = """
        () => {
          const cy = SystemMap.cy(), name = n => n.data('label').split('\n')[0];
          const leaves = cy.nodes().filter(n => !n.isParent() && !n.hasClass('anchor'));
          const into = (p, n) => { const b = n.boundingBox({ includeLabels: false, includeOverlays: false }); return p.x > b.x1 && p.x < b.x2 && p.y > b.y1 && p.y < b.y2; };
          return cy.edges('.ref').map(e => {
            const s = e.sourceEndpoint(), t = e.targetEndpoint(), curved = e.style('curve-style') === 'unbundled-bezier', c = curved ? e.controlPoints()[0] : null;
            const along = (u) => c ? { x: (1 - u) * (1 - u) * s.x + 2 * (1 - u) * u * c.x + u * u * t.x, y: (1 - u) * (1 - u) * s.y + 2 * (1 - u) * u * c.y + u * u * t.y }
              : { x: s.x + (t.x - s.x) * u, y: s.y + (t.y - s.y) * u };
            const a = e.source().position(), z = e.target().position();
            const straight = (u) => ({ x: a.x + (z.x - a.x) * u, y: a.y + (z.y - a.y) * u });
            const others = leaves.filter(n => n.id() !== e.source().id() && n.id() !== e.target().id());
            const crossing = (at) => others.filter(n => { for (let k = 0; k <= 100; k++) if (into(at(k / 100), n)) return true; return false; }).map(name);
            return { pair: name(e.source()) + '>' + name(e.target()), curved, hits: crossing(along), straightHits: crossing(straight) };
          });
        }
        """;

    [Test]
    public async Task Page_a_reference_line_that_would_run_through_another_project_is_bent_around_it()
    {
        // Api → Infrastructure (bara uppstart) skulle gå rakt genom Application, som står mellan dem: de ställs på en rad.
        await WithPageAsync(await MapRefsAsync(), async (page, ready) =>
        {
            await page.EvaluateAsync(Row);
            var lines = await page.EvaluateAsync<System.Text.Json.JsonElement>(LineHits);
            var all = lines.EnumerateArray().ToList();
            Assert.That(all, Is.Not.Empty);
            Assert.That(all.Where(l => l.GetProperty("hits").GetArrayLength() > 0).Select(l => l.GetProperty("pair").GetString()), Is.Empty, "ingen linje skär en annan nod");
            var api = all.Single(l => l.GetProperty("pair").GetString() == "Api>Infrastructure");
            Assert.That(api.GetProperty("straightHits").EnumerateArray().Select(h => h.GetString()), Does.Contain("Application"), "den raka linjen hade gått genom Application");
            Assert.That(api.GetProperty("curved").GetBoolean(), Is.True);
            // Och den går att klicka på: mitt på kurvan.
            await page.EvaluateAsync("() => { SystemMap.cy().edges('.ref').filter(e => e.source().data('label').startsWith('Api') && e.target().data('label').startsWith('Infrastructure'))[0].emit('tap'); }");
            Assert.That(await CardTextAsync(page), Does.Contain("Api → Infrastructure"));

            // Samma kontroll för linjen som redan är böjd för att anropen går åt andra hållet.
            await page.EvaluateAsync(RowBack);
            all = (await page.EvaluateAsync<System.Text.Json.JsonElement>(LineHits)).EnumerateArray().ToList();
            Assert.That(all.Where(l => l.GetProperty("hits").GetArrayLength() > 0).Select(l => l.GetProperty("pair").GetString()), Is.Empty);
            var back = all.Single(l => l.GetProperty("pair").GetString() == "Infrastructure>Application");
            Assert.That(back.GetProperty("curved").GetBoolean(), Is.True);
        }, level: "projects", groupsOpen: false);
        // Likadant uppifrån och ned.
        await WithPageAsync(await MapRefsAsync(), async (page, ready) =>
        {
            await page.ClickAsync("[data-direction=TB]");
            await ready();
            await page.EvaluateAsync(Row);
            var hits = (await page.EvaluateAsync<System.Text.Json.JsonElement>(LineHits)).EnumerateArray().Where(l => l.GetProperty("hits").GetArrayLength() > 0).ToList();
            Assert.That(hits, Is.Empty);
        }, level: "projects", groupsOpen: false);
    }

    // Api, Application och Infrastructure på en rad (i den ordningen) med de andra projekten långt ifrån, och linjerna dras om.
    private const string Row = """
        () => {
          const cy = SystemMap.cy(), at = { Api: [-400, 0], Application: [0, 0], Infrastructure: [400, 0], Shared: [0, 400], Legacy: [-400, 400] };
          // Anropen och integrationerna flyttas långt bort, så att bara projekten står där linjerna går (annars beror det på typsnittet,
          // som ger noderna deras bredd, om de råkar stå i vägen).
          cy.nodes().filter(n => !n.isParent() && !n.hasClass('t-project') && !n.hasClass('anchor')).forEach(n => n.position({ x: n.position().x + 8000, y: n.position().y + 8000 }));
          cy.nodes('.t-project').forEach(n => { const p = at[n.data('label').split('\n')[0]]; n.position({ x: p[0], y: p[1] }); });
          SystemMap.routeLines();
        }
        """;

    // Som Row, men Api mellan Application och Infrastructure: linjen som böjs för att anropen går åt andra hållet (Infrastructure → Application) går förbi Api.
    private static readonly string RowBack = Row.Replace("Api: [-400, 0], Application: [0, 0]", "Api: [0, 0], Application: [-400, 0]");

    private const string CountLabels = """
        () => {
          const cy = SystemMap.cy(), ctx = document.createElement('canvas').getContext('2d'), name = n => n.data('label').split('\n')[0];
          ctx.font = '12px system-ui';
          const leaves = cy.nodes().filter(n => !n.isParent() && !n.hasClass('anchor'));
          return cy.edges('.counted').map(e => {
            const text = e.data('text'), pair = name(e.source()) + '>' + name(e.target());
            if (!text) return { pair, text, overlaps: [], background: e.style('text-background-opacity') };
            const m = e.midpoint(), x = m.x + e.numericStyle('text-margin-x'), y = m.y + e.numericStyle('text-margin-y'), w = ctx.measureText(text).width;
            const overlaps = leaves.filter(n => { const b = n.boundingBox({ includeLabels: false, includeOverlays: false });
              return b.x1 < x + w / 2 && b.x2 > x - w / 2 && b.y1 < y + 7 && b.y2 > y - 7; }).map(name);
            return { pair, text, overlaps, background: e.style('text-background-opacity'), color: e.style('text-background-color') };
          });
        }
        """;

    [Test]
    public async Task Page_the_text_with_the_number_of_calls_never_overlaps_a_node_and_is_left_out_on_a_short_arrow()
    {
        using var loaded = await SolutionLoader.LoadAsync(TestPaths.Solution, restore: true);
        var map = await CodeMapper.AnalyzeAsync(loaded.Solution, TestPaths.Solution, SpecReader.Load(TestPaths.Spec), loaded.Warnings);
        await WithPageAsync(map, async (page, ready) =>
        {
            var labels = (await page.EvaluateAsync<System.Text.Json.JsonElement>(CountLabels)).EnumerateArray().ToList();
            Assert.That(labels.Select(l => l.GetProperty("pair").GetString()), Does.Contain("ShopApi>ShopApplication").And.Contain("ShopApplication>ShopInfrastructure"));
            Assert.That(labels.Where(l => l.GetProperty("overlaps").GetArrayLength() > 0).Select(l => l.GetProperty("pair").GetString()), Is.Empty);
            Assert.That(labels.Single(l => l.GetProperty("pair").GetString() == "ShopApi>ShopApplication").GetProperty("text").GetString(), Is.Empty, "också den är kortare än texten plus 20 px");
            Assert.That(labels.Single(l => l.GetProperty("pair").GetString() == "ShopApplication>ShopInfrastructure").GetProperty("text").GetString(), Is.Empty,
                "pilen är kortare än texten plus 20 px");
            Assert.That(labels.All(l => l.GetProperty("background").GetString() == "1"), Is.True);
            Assert.That(await page.EvaluateAsync<string>("() => SystemMap.cy().edges('.counted')[0].style('text-background-color')"), Is.EqualTo("rgb(255,255,255)"), "kartans bakgrund");

            // Antalet står ändå i kortet.
            await TapLineAsync(page, ".counted", "ShopApplication>ShopInfrastructure");
            Assert.That(await CardTextAsync(page), Does.Contain("ShopApplication → ShopInfrastructure").And.Contain("Används av anropen"));
        }, level: "projects", groupsOpen: false);
    }

    [Test]
    public async Task Page_a_text_with_the_number_of_calls_stands_beside_the_nodes_when_the_arrow_is_long_enough()
    {
        await WithPageAsync(await MapRefsAsync(), async (page, ready) =>
        {
            // Pilarna är korta i den här utläggningen; med projekten längre isär har texten plats, och den ligger då inte över något projekt.
            await page.EvaluateAsync("() => { const cy = SystemMap.cy(); cy.nodes('.t-project').forEach(n => { const p = n.position(); n.position({ x: p.x * 3, y: p.y * 2 }); }); SystemMap.routeLines(); }");
            var labels = (await page.EvaluateAsync<System.Text.Json.JsonElement>(CountLabels)).EnumerateArray().ToList();
            Assert.That(labels.Select(l => l.GetProperty("text").GetString()), Does.Contain("1 anrop"));
            Assert.That(labels.Where(l => l.GetProperty("overlaps").GetArrayLength() > 0), Is.Empty);

            // Ligger projekten tätt är texten borta igen.
            await page.EvaluateAsync("() => { const cy = SystemMap.cy(); cy.nodes('.t-project').forEach(n => { const p = n.position(); n.position({ x: p.x / 3, y: p.y / 2 }); }); SystemMap.routeLines(); }");
            labels = (await page.EvaluateAsync<System.Text.Json.JsonElement>(CountLabels)).EnumerateArray().ToList();
            Assert.That(labels.Where(l => l.GetProperty("overlaps").GetArrayLength() > 0), Is.Empty);
        }, level: "projects", groupsOpen: false);
    }
}
