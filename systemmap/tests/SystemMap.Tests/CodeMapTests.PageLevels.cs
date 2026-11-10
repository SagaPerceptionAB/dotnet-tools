using Microsoft.Playwright;

namespace SystemMap.Tests;

/// <summary>Sidan i en riktig webbläsare: nivån Funktioner och "Visa", där man väljer vad som syns.</summary>
public partial class CodeMapTests
{
    private const string Methods = "() => SystemMap.cy().nodes('.t-method').map(n => n.parent().data('label') + '.' + n.data('label')).sort()";
    private const string Classes = "() => SystemMap.cy().nodes('.t-class').map(n => n.data('label')).sort()";
    private static readonly string[] AllClasses = ["AuthClient", "AuthController", "AuthService", "FamilyController", "FamilyService", "MeController"];

    private static IReadOnlySet<string> HiddenIn(CodeMapModel map, params string[] names) =>
        CodeMapOverview.ResolveHidden(CodeMapOverview.Build(map), names);

    [Test]
    public async Task Page_at_the_level_methods_shows_the_methods_in_their_classes_but_the_rows_stay_in_the_integration()
    {
        var map = await MapAsync(Build(true, Web("Api", AppStart + FlowApi)));
        await WithPageAsync(map, async (page, ready) =>
        {
            Assert.That(await page.EvaluateAsync<int>("() => SystemMap.cy().nodes('.t-method').length"), Is.Zero, "från början visas klasserna");

            await page.ClickAsync("[data-level='methods']");
            await ready();
            Assert.That(await page.GetAttributeAsync("[data-level='methods']", "aria-pressed"), Is.EqualTo("true"));
            Assert.That(await page.GetAttributeAsync("[data-level='classes']", "aria-pressed"), Is.EqualTo("false"));
            Assert.That(await page.InnerTextAsync("[data-level='methods']"), Is.EqualTo("Funktioner"));
            Assert.That(await page.EvaluateAsync<string[]>(Methods), Is.EqualTo(new[]
            {
                "AuthClient.Verify()", "AuthController.In()", "AuthController.Out()", "AuthService.Check()", "FamilyController.Get()",
                "FamilyController.Post()", "FamilyService.Load()", "MeController.Get()",
            }));
            Assert.That(await page.EvaluateAsync<bool>("() => SystemMap.cy().nodes('.t-method').every(n => n.parent().hasClass('t-class') && n.parent().parent().hasClass('t-project'))"), Is.True,
                "metoderna ligger i sin klass i sitt projekt");
            Assert.That(await page.EvaluateAsync<int>("() => SystemMap.cy().nodes('.t-operation').length"), Is.Zero, "raderna i integrationen visas inte");
            Assert.That(await page.EvaluateAsync<int>("() => SystemMap.cy().nodes('.t-integration').length"), Is.EqualTo(1));
            Assert.That(await page.EvaluateAsync<bool>("() => SystemMap.cy().nodes('.t-integration').every(n => n.hasClass('badge'))"), Is.True, "integrationen är hopfälld, med antalet rader på");
            Assert.That(await page.EvaluateAsync<string[]>("() => SystemMap.cy().edges().not('.band').filter(e => e.source().hasClass('t-method') && e.target().hasClass('t-method')).map(e => e.source().data('label') + '>' + e.target().data('label')).sort()"),
                Does.Contain("Check()>Verify()").And.Contain("Load()>Verify()").And.Contain("In()>Check()"), "anropen mellan metoderna");

            // Allt visar också raderna i integrationen.
            await page.ClickAsync("[data-level='all']");
            await ready();
            Assert.That(await page.EvaluateAsync<int>("() => SystemMap.cy().nodes('.t-operation').length"), Is.GreaterThan(0));
            await page.ClickAsync("[data-level='classes']");
            await ready();
            Assert.That(await page.EvaluateAsync<int>("() => SystemMap.cy().nodes('.t-method').length"), Is.Zero);
        });
    }

    [Test]
    public async Task Page_remembers_the_level_methods_when_it_is_reloaded()
    {
        var map = await MapAsync(Build(true, Web("Api", AppStart + FlowApi)));
        await WithPageAsync(map, async (page, ready) =>
        {
            await page.ClickAsync("[data-level='methods']");
            await ready();
            await page.ReloadAsync();
            await ready();
            Assert.That(await page.GetAttributeAsync("[data-level='methods']", "aria-pressed"), Is.EqualTo("true"));
            Assert.That(await page.EvaluateAsync<int>("() => SystemMap.cy().nodes('.t-method').length"), Is.GreaterThan(0));
        });
    }

    [Test]
    public async Task Page_hides_a_class_with_its_checkbox_and_the_path_through_it_stays_connected()
    {
        var map = await MapAsync(Build(true, Web("Api", AppStart + FlowApi)));
        await WithPageAsync(map, async (page, ready) =>
        {
            Assert.That(await page.EvaluateAsync<string[]>(Classes), Is.EqualTo(AllClasses));
            Assert.That(await page.EvaluateAsync<string[]>(Arrows), Does.Contain("AuthController>AuthService").And.Contain("AuthService>AuthClient"));
            Assert.That(await page.IsHiddenAsync("#map-hint"), Is.True, "inget är dolt: ingen markering");
            Assert.That(await page.IsVisibleAsync("#map-show"), Is.True);

            await page.ClickAsync("#map-show-summary");
            // Delarna: projektet med sina klasser, och integrationen.
            Assert.That(await page.EvaluateAsync<string[]>("() => [...document.querySelectorAll('#map-tree input')].map(i => i.parentElement.textContent.trim())"),
                Is.EqualTo(new[] { "Api", "AuthClient", "AuthController", "AuthService", "FamilyController", "FamilyService", "MeController" }.Concat(await page.EvaluateAsync<string[]>("() => [...document.querySelectorAll('#map-tree fieldset:last-child input')].map(i => i.parentElement.textContent.trim())")).Distinct()));
            Assert.That(await page.IsCheckedAsync("input[data-part='class:Api/AuthService']"), Is.True, "allt är ikryssat från början");
            // Bockarna ligger under varandra i panelen, inte på en hög.
            var ys = await page.EvaluateAsync<double[]>("() => [...document.querySelectorAll('#map-tree input')].map(i => i.getBoundingClientRect().top)");
            Assert.That(ys, Is.Ordered.Ascending.And.Unique, "en bock per rad");
            Assert.That(await page.EvaluateAsync<bool>("() => { const p = document.getElementById('map-show-panel').getBoundingClientRect(); return [...document.querySelectorAll('#map-tree input')].every(i => { const r = i.getBoundingClientRect(); return r.left >= p.left && r.right <= p.right; }); }"), Is.True, "i panelen");

            await page.UncheckAsync("input[data-part='class:Api/AuthService']");
            await ready();
            Assert.That(await page.EvaluateAsync<string[]>(Classes), Is.EqualTo(AllClasses.Where(c => c != "AuthService")));
            Assert.That(await page.EvaluateAsync<string[]>(Arrows), Does.Contain("AuthController>AuthClient").And.Not.Contain("AuthController>AuthService"),
                "vägen genom den dolda klassen står kvar: controllern anropar nu klassen efter den");
            Assert.That(await page.IsVisibleAsync("#map-hint"), Is.True, "den som läser kartan ser att något är dolt");
            Assert.That(await page.InnerTextAsync("#map-hint"), Is.EqualTo("1 dolda"));
            Assert.That(await page.GetAttributeAsync("#map-show-summary", "aria-label"), Is.EqualTo("Visa, 1 dolda"));

            // Ett dolt projekt döljer klasserna i det: deras bockar står avbockade och går inte att ändra.
            await page.UncheckAsync("input[data-part='proj:Api']");
            await ready();
            Assert.That(await page.EvaluateAsync<int>("() => SystemMap.cy().nodes('.t-project, .t-class, .t-method').length"), Is.Zero);
            Assert.That(await page.IsDisabledAsync("input[data-part='class:Api/AuthClient']"), Is.True);
            Assert.That(await page.IsCheckedAsync("input[data-part='class:Api/AuthClient']"), Is.False);
            Assert.That(await page.InnerTextAsync("#map-hint"), Is.EqualTo("2 dolda"), "bara det som själv är avbockat räknas");
            await page.CheckAsync("input[data-part='proj:Api']");
            await ready();
            Assert.That(await page.EvaluateAsync<string[]>(Classes), Is.EqualTo(AllClasses.Where(c => c != "AuthService")), "klassen som var dold för sig är det fortfarande");
        });
    }

    [Test]
    public async Task Page_hides_an_integration_with_its_rows_and_resets_with_show_all()
    {
        var map = await MapAsync(Build(true, Web("Api", AppStart + FlowApi)));
        await WithPageAsync(map, async (page, ready) =>
        {
            await page.ClickAsync("[data-level='all']");
            await ready();
            Assert.That(await page.EvaluateAsync<int>("() => SystemMap.cy().nodes('.t-integration, .t-operation').length"), Is.GreaterThan(1));

            await page.ClickAsync("#map-show-summary");
            await page.UncheckAsync("#map-tree fieldset:last-child input");
            await ready();
            Assert.That(await page.EvaluateAsync<int>("() => SystemMap.cy().nodes('.t-integration, .t-operation').length"), Is.Zero, "integrationen och raderna är borta");
            Assert.That(await page.EvaluateAsync<int>("() => SystemMap.cy().edges().not('.band').filter(e => e.target().hasClass('t-operation') || e.target().hasClass('t-integration')).length"), Is.Zero, "pilarna dit är borta");
            Assert.That(await page.EvaluateAsync<string[]>(Classes), Is.EqualTo(AllClasses), "klasserna är kvar");

            // Visa allt ger tillbaka allt.
            Assert.That(await page.GetAttributeAsync("#map-show-all", "aria-disabled"), Is.EqualTo("false"));
            await page.ClickAsync("#map-show-all");
            await ready();
            Assert.That(await page.EvaluateAsync<int>("() => SystemMap.cy().nodes('.t-integration').length"), Is.EqualTo(1));
            Assert.That(await page.IsHiddenAsync("#map-hint"), Is.True);
            Assert.That(await page.EvaluateAsync<int>("() => document.querySelectorAll('#map-tree input:not(:checked)').length"), Is.Zero);
            Assert.That(await page.GetAttributeAsync("#map-show-all", "aria-disabled"), Is.EqualTo("true"), "inget att visa");
        });
    }

    [Test]
    public async Task Page_remembers_what_is_hidden_when_reloaded_and_a_choice_to_show_all_beats_the_page_that_hid_parts()
    {
        var map = await MapAsync(Build(true, Web("Api", AppStart + FlowApi)));
        // Sidan skrevs med --hide: delarna är avbockade när den öppnas.
        await WithPageAsync(map, async (page, ready) =>
        {
            Assert.That(await page.EvaluateAsync<string[]>(Classes), Is.EqualTo(AllClasses.Where(c => c != "AuthService" && c != "MeController")), "avbockade från början");
            Assert.That(await page.InnerTextAsync("#map-hint"), Is.EqualTo("2 dolda"));

            await page.ClickAsync("#map-show-summary");
            await page.CheckAsync("input[data-part='class:Api/MeController']");
            await ready();
            await page.ReloadAsync();
            await ready();
            Assert.That(await page.EvaluateAsync<string[]>(Classes), Is.EqualTo(AllClasses.Where(c => c != "AuthService")), "det man valt sparas, och gäller före det sidan skrevs med");
            Assert.That(await page.InnerTextAsync("#map-hint"), Is.EqualTo("1 dolda"));

            await page.ClickAsync("#map-show-summary");
            await page.ClickAsync("#map-show-all");
            await ready();
            await page.ReloadAsync();
            await ready();
            Assert.That(await page.EvaluateAsync<string[]>(Classes), Is.EqualTo(AllClasses), "att visa allt är också ett val som sparas");
            Assert.That(await page.IsHiddenAsync("#map-hint"), Is.True);
        }, HiddenIn(map, "AuthService", "MeController"));
    }

    [Test]
    public async Task Page_panel_is_used_with_the_keyboard_and_closes_with_Escape()
    {
        var map = await MapAsync(Build(true, Web("Api", AppStart + FlowApi)));
        await WithPageAsync(map, async (page, ready) =>
        {
            await page.FocusAsync("#map-show-summary");
            await page.Keyboard.PressAsync("Enter");
            Assert.That(await page.EvaluateAsync<bool>("() => document.getElementById('map-show').open"), Is.True, "Enter öppnar panelen");
            await page.FocusAsync("input[data-part='class:Api/FamilyService']");
            await page.Keyboard.PressAsync("Space");
            await ready();
            Assert.That(await page.EvaluateAsync<string[]>(Classes), Is.EqualTo(AllClasses.Where(c => c != "FamilyService")), "mellanslag bockar av");
            Assert.That(await page.EvaluateAsync<string>("() => document.activeElement.getAttribute('data-part')"), Is.EqualTo("class:Api/FamilyService"), "fokus står kvar");
            Assert.That(await page.GetAttributeAsync("#map-show-panel", "aria-labelledby"), Is.EqualTo("map-show-title"));
            Assert.That(await page.InnerTextAsync("#map-show-title"), Is.EqualTo("Välj vad som syns"));
            Assert.That(await page.EvaluateAsync<int>("() => document.querySelectorAll('#map-tree input').length"), Is.EqualTo(await page.EvaluateAsync<int>("() => document.querySelectorAll('#map-tree label').length")), "varje bock har en etikett");

            await page.Keyboard.PressAsync("Escape");
            Assert.That(await page.EvaluateAsync<bool>("() => document.getElementById('map-show').open"), Is.False);
            Assert.That(await page.EvaluateAsync<string>("() => document.activeElement.id"), Is.EqualTo("map-show-summary"), "fokus går tillbaka till Visa");
        });
    }

    [Test]
    public async Task Page_does_not_save_a_Mermaid_file_that_would_ignore_what_is_hidden()
    {
        var map = await MapAsync(Build(true, Web("Api", AppStart + FlowApi)));
        await WithPageAsync(map, async (page, ready) =>
        {
            await page.ClickAsync("#map-show-summary");
            await page.UncheckAsync("input[data-part='class:Api/AuthService']");
            await ready();
            await page.ClickAsync("#map-mermaid");
            await page.WaitForFunctionAsync("() => document.getElementById('map-mermaid-status').textContent !== ''");
            Assert.That(await page.InnerTextAsync("#map-mermaid-status"), Does.Contain("Mermaid-filen kan inte följa det som är dolt i sidan.").And.Contain("dotnet systemmap --mermaid karta.mmd --hide"));

            await page.ClickAsync("#map-show-all");
            await ready();
            var download = await page.RunAndWaitForDownloadAsync(() => page.ClickAsync("#map-mermaid"));
            Assert.That(download.SuggestedFilename, Is.EqualTo("systemmap.mmd"), "utan dolda delar sparas filen som vanligt");
        });
    }

    [Test]
    public async Task Page_hides_parts_exactly_as_the_C_sharp_rule_does()
    {
        var map = await MapAsync(Build(true, Web("Api", AppStart + FlowApi)));
        var overview = CodeMapOverview.Build(map);
        // Sidans översikt är sorterad på id (som JSON), så ordningen på anropen i pilarna jämförs inte; vilka som är med gör det.
        string[] Describe(Overview o) => o.Nodes.Select(n => n.Id).Concat(o.Links.Select(l => $"{l.From}>{l.To}[{string.Join(",", l.Entries.Order(StringComparer.Ordinal))}]")).Order(StringComparer.Ordinal).ToArray();
        await WithPageAsync(map, async (page, _) =>
        {
            foreach (var names in new[] { new[] { "AuthService" }, ["AuthService", "FamilyService"], ["klass:AuthClient", "MeController"], ["projekt:Api"], ["AuthController", "AuthService", "klass:AuthClient", "FamilyService"] }
                .Concat([overview.Nodes.Where(n => n.Kind == OverviewKind.Integration).Select(n => "integration:" + n.Name).ToArray()]))
            {
                var ids = HiddenIn(map, names).ToArray();
                var expected = Describe(CodeMapOverview.Hide(overview, new HashSet<string>(ids)));
                var actual = await page.EvaluateAsync<string[]>(
                    "ids => { const o = SystemMap.hide(JSON.parse(document.getElementById('map-data').textContent).overview, ids); return o.nodes.map(n => n.id).concat(o.links.map(l => l.from + '>' + l.to + '[' + l.entries.slice().sort().join(',') + ']')).sort(); }", ids);
                Assert.That(actual, Is.EqualTo(expected), $"dolda: {string.Join(", ", names)}");
            }
        });
    }
}
