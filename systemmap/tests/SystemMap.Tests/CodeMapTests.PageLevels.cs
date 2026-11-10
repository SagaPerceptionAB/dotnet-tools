using Microsoft.Playwright;

namespace SystemMap.Tests;

/// <summary>Sidan i en riktig webbläsare: detaljnivån som väljs i listorna, och det som är dolt (hidden) med rutan nere till vänster.</summary>
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
            Assert.That(await page.InputValueAsync("#map-fn"), Is.EqualTo("Visas inte"));

            // Nivån väljs i listan Funktion.
            await PickAsync(page, "fn", "Alla");
            await ready();
            Assert.That(await page.InputValueAsync("#map-fn"), Is.EqualTo("Alla"));
            Assert.That(await page.InputValueAsync("#map-cls"), Is.EqualTo("Alla"));
            Assert.That(await page.EvaluateAsync<bool>("() => document.getElementById('map-fn').parentElement.classList.contains('has-value')"), Is.False, "ett nivåval är inget filter: ingen blå kant");
            Assert.That(await page.IsHiddenAsync("#map-status"), Is.True);
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

            // Integration: med läsningar och skrivningar visar också raderna i integrationen.
            await LevelAsync(page, "all");
            await ready();
            Assert.That(await page.InputValueAsync("#map-integ"), Is.EqualTo("Alla, med läsningar och skrivningar"));
            Assert.That(await page.EvaluateAsync<int>("() => SystemMap.cy().nodes('.t-operation').length"), Is.GreaterThan(0));
            await LevelAsync(page, "classes");
            await ready();
            Assert.That(await page.EvaluateAsync<int>("() => SystemMap.cy().nodes('.t-method').length"), Is.Zero);
        });
    }

    [Test]
    public async Task Page_level_projects_is_Klass_Visas_inte_and_turns_off_the_list_of_functions()
    {
        var map = await MapAsync(Build(true, Web("Api", AppStart + FlowApi)));
        await WithPageAsync(map, async (page, ready) =>
        {
            await PickAsync(page, "cls", "Visas inte");
            await ready();
            Assert.That(await page.InputValueAsync("#map-cls"), Is.EqualTo("Visas inte"));
            Assert.That(await page.IsDisabledAsync("#map-fn"), Is.True);
            Assert.That(await page.InputValueAsync("#map-fn"), Is.EqualTo("Visas inte"));
            Assert.That(await page.EvaluateAsync<int>("() => SystemMap.cy().nodes('.t-class').length"), Is.Zero, "bara projekten");
            Assert.That(await page.EvaluateAsync<int>("() => SystemMap.cy().nodes('.t-project').length"), Is.EqualTo(1));
            Assert.That(await page.EvaluateAsync<string>("() => sessionStorage.getItem('map-level')"), Is.EqualTo("projects"));

            // Ett kryss i en klass ger nivån klasser.
            await PickAsync(page, "cls", "AuthService");
            await ready();
            Assert.That(await page.IsDisabledAsync("#map-fn"), Is.False);
            Assert.That(await page.EvaluateAsync<string>("() => sessionStorage.getItem('map-level')"), Is.EqualTo("classes"));
            Assert.That(await page.EvaluateAsync<int>("() => SystemMap.cy().nodes('.t-class').length"), Is.GreaterThan(0));
        });
    }

    [Test]
    public async Task Page_remembers_the_level_methods_when_it_is_reloaded()
    {
        var map = await MapAsync(Build(true, Web("Api", AppStart + FlowApi)));
        await WithPageAsync(map, async (page, ready) =>
        {
            await LevelAsync(page, "methods");
            await ready();
            await page.ReloadAsync();
            await ready();
            Assert.That(await page.InputValueAsync("#map-fn"), Is.EqualTo("Alla"));
            Assert.That(await page.EvaluateAsync<int>("() => SystemMap.cy().nodes('.t-method').length"), Is.GreaterThan(0));
        });
    }

    [Test]
    public async Task Page_written_with_a_hidden_class_keeps_the_path_through_it_and_says_so_in_the_box_with_Visa_dem()
    {
        var map = await MapAsync(Build(true, Web("Api", AppStart + FlowApi)));
        await WithPageAsync(map, async (page, ready) =>
        {
            Assert.That(await page.IsHiddenAsync("#map-legend"), Is.True, "inget är dolt och allt nås: ingen ruta");
        });

        await WithPageAsync(map, async (page, ready) =>
        {
            Assert.That(await page.EvaluateAsync<string[]>(Classes), Is.EqualTo(AllClasses.Where(c => c != "AuthService")));
            Assert.That(await page.EvaluateAsync<string[]>(Arrows), Does.Contain("AuthController>AuthClient").And.Not.Contain("AuthController>AuthService"),
                "vägen genom den dolda klassen står kvar: controllern anropar nu klassen efter den");
            Assert.That(await page.IsVisibleAsync("#map-legend"), Is.True, "den som läser kartan ser att något är dolt");
            Assert.That(await page.InnerTextAsync("#map-hint"), Is.EqualTo("1 dold med --hide"));
            Assert.That(await page.GetAttributeAsync("#map-hint", "title"), Does.Contain("AuthService"));
            Assert.That(await page.IsHiddenAsync("#map-unreached-field"), Is.True, "allt nås: inget val om det");
            Assert.That(await OptionTextsAsync(page, "cls"), Does.Not.Contain("AuthService"), "det som är dolt går inte att välja");

            await page.ClickAsync("#map-show-all");
            await ready();
            Assert.That(await page.EvaluateAsync<string[]>(Classes), Is.EqualTo(AllClasses));
            Assert.That(await page.IsHiddenAsync("#map-legend"), Is.True, "inget är dolt längre");
            Assert.That(await OptionTextsAsync(page, "cls"), Does.Contain("AuthService"));
        }, HiddenIn(map, "AuthService"));

        // Ett dolt projekt döljer klasserna i det.
        await WithPageAsync(map, async (page, ready) =>
        {
            Assert.That(await page.EvaluateAsync<int>("() => SystemMap.cy().nodes('.t-project, .t-class, .t-method').length"), Is.Zero);
            Assert.That(await page.InnerTextAsync("#map-hint"), Is.EqualTo("1 dold med --hide"));
        }, HiddenIn(map, "projekt:Api"));
    }

    [Test]
    public async Task Page_written_with_a_hidden_integration_leaves_out_its_rows_and_Visa_dem_gives_it_back()
    {
        var map = await MapAsync(Build(true, Web("Api", AppStart + FlowApi)));
        var integrations = CodeMapOverview.Build(map).Nodes.Where(n => n.Kind == OverviewKind.Integration).Select(n => "integration:" + n.Name).ToArray();
        await WithPageAsync(map, async (page, ready) =>
        {
            await LevelAsync(page, "all");
            await ready();
            Assert.That(await page.EvaluateAsync<int>("() => SystemMap.cy().nodes('.t-integration, .t-operation').length"), Is.Zero, "integrationen och raderna är borta");
            Assert.That(await page.EvaluateAsync<int>("() => SystemMap.cy().edges().not('.band').filter(e => e.target().hasClass('t-operation') || e.target().hasClass('t-integration')).length"), Is.Zero, "pilarna dit är borta");
            Assert.That(await page.EvaluateAsync<string[]>(Classes), Is.EqualTo(AllClasses), "klasserna är kvar");
            Assert.That(await OptionTextsAsync(page, "integ"), Is.EqualTo(new[] { "Alla", "Alla, med läsningar och skrivningar" }), "inga integrationer att välja");

            await page.ClickAsync("#map-show-all");
            await ready();
            Assert.That(await page.EvaluateAsync<int>("() => SystemMap.cy().nodes('.t-integration').length"), Is.EqualTo(1));
            Assert.That(await page.IsHiddenAsync("#map-legend"), Is.True);
        }, HiddenIn(map, integrations));
    }

    [Test]
    public async Task Page_choice_to_show_all_is_remembered_and_beats_the_page_that_hid_parts()
    {
        var map = await MapAsync(Build(true, Web("Api", AppStart + FlowApi)));
        // Sidan skrevs med --hide: delarna är dolda när den öppnas.
        await WithPageAsync(map, async (page, ready) =>
        {
            Assert.That(await page.EvaluateAsync<string[]>(Classes), Is.EqualTo(AllClasses.Where(c => c != "AuthService" && c != "MeController")), "dolda från början");
            Assert.That(await page.InnerTextAsync("#map-hint"), Is.EqualTo("2 dolda med --hide"));
            await page.ReloadAsync();
            await ready();
            Assert.That(await page.EvaluateAsync<string[]>(Classes), Is.EqualTo(AllClasses.Where(c => c != "AuthService" && c != "MeController")), "inget sparat än: sidans val gäller");

            await page.ClickAsync("#map-show-all");
            await ready();
            await page.ReloadAsync();
            await ready();
            Assert.That(await page.EvaluateAsync<string[]>(Classes), Is.EqualTo(AllClasses), "att visa allt är också ett val som sparas, och det gäller före det sidan skrevs med");
            Assert.That(await page.IsHiddenAsync("#map-legend"), Is.True);
        }, HiddenIn(map, "AuthService", "MeController"));
    }

    [Test]
    public async Task Page_does_not_save_a_Mermaid_file_that_would_ignore_what_is_hidden()
    {
        var map = await MapAsync(Build(true, Web("Api", AppStart + FlowApi)));
        await WithPageAsync(map, async (page, ready) =>
        {
            await page.ClickAsync("#map-mermaid");
            await page.WaitForFunctionAsync("() => document.getElementById('map-mermaid-status').textContent !== ''");
            Assert.That(await page.InnerTextAsync("#map-mermaid-status"), Does.Contain("Mermaid-filen kan inte följa det som är dolt i sidan.").And.Contain("dotnet systemmap --mermaid karta.mmd --hide"));

            await page.ClickAsync("#map-show-all");
            await ready();
            var download = await page.RunAndWaitForDownloadAsync(() => page.ClickAsync("#map-mermaid"));
            Assert.That(download.SuggestedFilename, Is.EqualTo("systemmap.mmd"), "utan dolda delar sparas filen som vanligt");
        }, HiddenIn(map, "AuthService"));
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
