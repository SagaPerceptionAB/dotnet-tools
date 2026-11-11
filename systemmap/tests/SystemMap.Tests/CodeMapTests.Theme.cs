using Microsoft.Playwright;

namespace SystemMap.Tests;

/// <summary>Ljust och mörkt tema (VS Codes färger), temaknappen och den hopfällda klassen som ruta.</summary>
public partial class CodeMapTests
{
    private const string ThemeOf = "() => document.getElementById('map').getAttribute('data-theme')";
    private const string ThemeLabel = "() => document.getElementById('map-theme').getAttribute('aria-label')";
    private const string SavedTheme = "() => sessionStorage.getItem('map-theme')";
    private const string ShutClasses = "SystemMap.cy().nodes('.t-class').not('.group')";
    // Nodernas mitt och färger: kantfärg och fyllning per nod, och pilarnas färg.
    private const string Positions = "() => JSON.stringify(SystemMap.cy().nodes().not('.anchor').map(n => [n.id(), n.position()]))";
    private const string Looks = "() => JSON.stringify(SystemMap.cy().nodes().not('.anchor').map(n => [n.id(), n.style('border-color'), n.style('background-color'), n.style('color')]))";
    private const string ArrowColors = "() => SystemMap.cy().edges().not('.band').map(e => [e.target().id(), e.source().id(), e.style('line-color')])";

    private static string Rgb(string hex) => $"rgb({Convert.ToInt32(hex[1..3], 16)},{Convert.ToInt32(hex[3..5], 16)},{Convert.ToInt32(hex[5..7], 16)})";

    [Test]
    public async Task The_theme_button_toggles_between_light_and_dark_and_saves_the_choice()
    {
        var map = await TwoProjects();
        await WithPageAsync(map, async (page, ready) =>
        {
            Assert.That(await page.EvaluateAsync<string>(ThemeOf), Is.EqualTo("light"), "utan sparat val följer kartan datorn (ljus)");
            Assert.That(await page.EvaluateAsync<string>(ThemeLabel), Is.EqualTo("Byt till mörkt tema"));
            Assert.That(await page.GetAttributeAsync("#map-theme", "title"), Is.EqualTo("Byt till mörkt tema"));
            Assert.That(await page.GetAttributeAsync("#map-theme", "aria-pressed"), Is.Null, "en vanlig knapp, inte en växlad");
            Assert.That(await page.TextContentAsync(".map-field-theme .map-label"), Is.EqualTo("Tema"));
            Assert.That(await page.EvaluateAsync<string>("() => document.querySelector('.map-field-direction').nextElementSibling.id"), Is.EqualTo("map-theme-field"), "direkt efter Riktning");
            var box = (await page.QuerySelectorAsync("#map-theme"))!;
            var size = await box.BoundingBoxAsync();
            Assert.That((size!.Width, size.Height), Is.EqualTo((44d, 44d)));
            var icon = await page.InnerHTMLAsync("#map-theme");

            await page.ClickAsync("#map-theme");
            Assert.That(await page.EvaluateAsync<string>(ThemeOf), Is.EqualTo("dark"));
            Assert.That(await page.EvaluateAsync<string>(ThemeLabel), Is.EqualTo("Byt till ljust tema"));
            Assert.That(await page.InnerHTMLAsync("#map-theme"), Is.Not.EqualTo(icon), "ikonen visar temat man byter till");
            Assert.That(await page.EvaluateAsync<string>(SavedTheme), Is.EqualTo("dark"));
            await page.WaitForFunctionAsync("() => document.getElementById('map-live').textContent === 'Mörkt tema.'");

            await page.ClickAsync("#map-theme");
            Assert.That(await page.EvaluateAsync<string>(ThemeOf), Is.EqualTo("light"));
            Assert.That(await page.EvaluateAsync<string?>(SavedTheme), Is.Null, "ett klick tillbaka till datorns tema tar bort valet");
            await page.WaitForFunctionAsync("() => document.getElementById('map-live').textContent === 'Ljust tema.'");

            await page.ClickAsync("#map-theme");
            await page.ReloadAsync();
            await ready();
            Assert.That(await page.EvaluateAsync<string>(ThemeOf), Is.EqualTo("dark"), "valet sparas som riktningen");
        });
    }

    [Test]
    public async Task Without_a_saved_choice_the_map_follows_the_computer_and_a_click_back_to_it_removes_the_saved_choice()
    {
        var map = await TwoProjects();
        await WithPageAsync(map, async (page, ready) =>
        {
            Assert.That(await page.EvaluateAsync<string>(ThemeOf), Is.EqualTo("dark"), "prefers-color-scheme: dark utan sparat val");
            Assert.That(await page.EvaluateAsync<string>(ThemeLabel), Is.EqualTo("Byt till ljust tema"));
            Assert.That(await page.EvaluateAsync<string>("() => SystemMap.cy().nodes('.t-class')[0].style('background-color')"), Is.EqualTo(Rgb("#1F1F1F")));

            await page.EmulateMediaAsync(new PageEmulateMediaOptions { ColorScheme = ColorScheme.Light });
            await page.WaitForFunctionAsync("() => document.getElementById('map').getAttribute('data-theme') === 'light'");   // följer datorn medan sidan är öppen

            await page.ClickAsync("#map-theme");                 // datorn är ljus: mörkt är ett eget val
            Assert.That(await page.EvaluateAsync<string>(SavedTheme), Is.EqualTo("dark"));
            await page.EmulateMediaAsync(new PageEmulateMediaOptions { ColorScheme = ColorScheme.Dark });
            await page.EmulateMediaAsync(new PageEmulateMediaOptions { ColorScheme = ColorScheme.Light });
            Assert.That(await page.EvaluateAsync<string>(ThemeOf), Is.EqualTo("dark"), "ett sparat val följs inte av datorn");
            await page.ClickAsync("#map-theme");                 // ljust är datorns: valet tas bort
            Assert.That(await page.EvaluateAsync<string?>(SavedTheme), Is.Null);
            await page.EmulateMediaAsync(new PageEmulateMediaOptions { ColorScheme = ColorScheme.Dark });
            await page.WaitForFunctionAsync("() => document.getElementById('map').getAttribute('data-theme') === 'dark'");
        }, colors: ColorScheme.Dark);
    }

    [TestCase("light", ColorScheme.Dark, "light")]
    [TestCase("dark", ColorScheme.Light, "dark")]
    [TestCase("auto", ColorScheme.Dark, "dark")]
    [TestCase("auto", ColorScheme.Light, "light")]
    public async Task The_theme_setting_locks_the_theme_and_hides_the_button(string setting, ColorScheme computer, string expected)
    {
        var map = await TwoProjects();
        await WithPageAsync(map, async (page, ready) =>
        {
            await page.EvaluateAsync("(s) => { document.getElementById('map').remove(); const r = document.createElement('div'); r.id = 'map'; document.body.appendChild(r); SystemMap.create(r, { theme: s }); }", setting);
            Assert.That(await page.EvaluateAsync<string>(ThemeOf), Is.EqualTo(expected));
            Assert.That(await page.IsVisibleAsync("#map-theme"), Is.False);
            Assert.That(await page.IsVisibleAsync("#map-theme-field"), Is.False);
        }, colors: computer);
    }

    [Test]
    public async Task A_change_of_theme_repaints_the_colors_without_moving_the_nodes()
    {
        var map = await TwoProjects();
        await WithPageAsync(map, async (page, ready) =>
        {
            var positions = await page.EvaluateAsync<string>(Positions);
            var looks = await page.EvaluateAsync<string>(Looks);
            string Class(string where, string id) => $"() => SystemMap.cy().getElementById('{id}').style('{where}')";
            Assert.That(await page.EvaluateAsync<string>(Class("border-color", "class:Lib/Writer")), Is.EqualTo(Rgb("#D67E00")));
            Assert.That(await page.EvaluateAsync<string>(Class("border-color", "proj:Lib")), Is.EqualTo(Rgb("#388A34")));
            Assert.That(await page.EvaluateAsync<string>(Class("border-color", "system")), Is.EqualTo(Rgb("#652D90")));
            Assert.That(await page.EvaluateAsync<string>(Class("background-color", "class:Lib/Writer")), Is.EqualTo(Rgb("#FFFFFF")));
            var arrows = await page.EvaluateAsync<string[][]>(ArrowColors);
            Assert.That(arrows.Where(a => a[0].StartsWith("int:")).Select(a => a[2]).Distinct(), Is.EqualTo(new[] { Rgb("#0063D3") }), "pilar till integrationer är blå");
            Assert.That(arrows.Where(a => (a[1].StartsWith("in:") || a[1].StartsWith("cg:"))).Select(a => a[2]).Distinct(), Is.EqualTo(new[] { Rgb("#767676") }), "pilar från anrop är gråa som anropen");
            Assert.That(arrows.Where(a => a[0].StartsWith("class:") && !(a[1].StartsWith("in:") || a[1].StartsWith("cg:"))).Select(a => a[2]).Distinct(), Is.EqualTo(new[] { Rgb("#A0A0A0") }), "pilar mellan kod");

            await page.ClickAsync("#map-theme");
            Assert.That(await page.EvaluateAsync<string>(Positions), Is.EqualTo(positions), "ingen ny utläggning");
            Assert.That(await page.EvaluateAsync<string>(Looks), Is.Not.EqualTo(looks));
            Assert.That(await page.EvaluateAsync<bool>("() => document.getElementById('map-view').hasAttribute('data-ready')"), Is.True, "ingen ny utläggning är igång");
            Assert.That(await page.EvaluateAsync<string>(Class("border-color", "class:Lib/Writer")), Is.EqualTo(Rgb("#EE9D28")));
            Assert.That(await page.EvaluateAsync<string>(Class("border-color", "proj:Lib")), Is.EqualTo(Rgb("#89D185")));
            Assert.That(await page.EvaluateAsync<string>(Class("border-color", "system")), Is.EqualTo(Rgb("#B180D7")));
            Assert.That(await page.EvaluateAsync<string>(Class("background-color", "class:Lib/Writer")), Is.EqualTo(Rgb("#1F1F1F")));
            Assert.That(await page.EvaluateAsync<string>(Class("color", "class:Lib/Writer")), Is.EqualTo(Rgb("#CCCCCC")), "texten i noder");
            arrows = await page.EvaluateAsync<string[][]>(ArrowColors);
            Assert.That(arrows.Where(a => a[0].StartsWith("int:")).Select(a => a[2]).Distinct(), Is.EqualTo(new[] { Rgb("#59A4F9") }));
            Assert.That(arrows.Where(a => a[0].StartsWith("class:") && !(a[1].StartsWith("in:") || a[1].StartsWith("cg:"))).Select(a => a[2]).Distinct(), Is.EqualTo(new[] { Rgb("#6E6E6E") }));
            Assert.That(await page.EvaluateAsync<string>("() => getComputedStyle(document.getElementById('map-view')).backgroundColor"), Is.EqualTo(Rgb("#1F1F1F").Replace(",", ", ")));
            Assert.That(await page.EvaluateAsync<string>("() => getComputedStyle(document.getElementById('map-bar')).backgroundColor"), Is.EqualTo(Rgb("#181818").Replace(",", ", ")));
        });
    }

    [Test]
    public async Task A_shut_class_is_a_small_box_with_its_functions_on_a_second_line_and_no_badge()
    {
        var map = await TwoProjects();
        await WithPageAsync(map, async (page, ready) =>
        {
            Assert.That(await page.EvaluateAsync<bool>("() => " + ShutClasses + ".length > 0 && " + ShutClasses + ".every(n => n.hasClass('cbox') && !n.hasClass('badge') && !n.hasClass('long') && n.style('corner-radius') === '12px' && n.height() === 50 && n.style('border-width') === '1.5px' && n.style('font-weight') === '600' && n.style('background-opacity') === '1')"), Is.True);
            var texts = await page.EvaluateAsync<string[]>("() => " + ShutClasses + ".map(n => n.data('label').replace('\\n', ' | ')).sort()");
            Assert.That(texts, Has.Member("Writer | 1 funktion").And.Member("Single | 1 funktion"));
            Assert.That(texts, Has.No.Member("Writer"), "raden med funktioner finns när de finns");
            Assert.That(await page.EvaluateAsync<bool>("() => " + ShutClasses + ".filter(n => n.data('label').indexOf('\\n') < 0).every(n => !/funktion/.test(n.data('label')))"), Is.True, "ingen rad om 0");
            Assert.That(await page.EvaluateAsync<bool>("() => " + ShutClasses + ".every(n => n.data('icon').length > 0 && n.data('bn') === 0 && n.data('badge') === '')"), Is.True, "klassikonen men ingen bricka");
            Assert.That(await page.EvaluateAsync<bool>("() => " + ShutClasses + ".every(n => n.width() >= 60)"), Is.True);

            // Ett klick fäller ut klassen (en yta med namnet) och ett klick till fäller ihop den.
            await page.EvaluateAsync("() => { SystemMap.cy().getElementById('class:Lib/Writer').emit('tap'); }");
            await ready();
            Assert.That(await page.EvaluateAsync<bool>("() => { const n = SystemMap.cy().getElementById('class:Lib/Writer'); return n.hasClass('group') && n.data('label') === 'Writer'; }"), Is.True);
            await page.EvaluateAsync("() => { SystemMap.cy().getElementById('class:Lib/Writer').emit('tap'); }");
            await ready();
            Assert.That(await page.EvaluateAsync<string>("() => SystemMap.cy().getElementById('class:Lib/Writer').data('label')"), Is.EqualTo("Writer\n1 funktion"));
        });
    }

    [Test]
    public async Task Shut_classes_do_not_overlap_other_nodes_in_both_directions_and_at_every_level()
    {
        var map = await TwoProjects();
        await WithPageAsync(map, async (page, ready) =>
        {
            foreach (var level in new[] { "classes", "methods" })
                foreach (var dir in new[] { "LR", "TB" })
                {
                    await LevelAsync(page, level);
                    await page.ClickAsync($"[data-direction='{dir}']");
                    await ready();
                    await AssertNoClassOverlapAsync(page, $"{level} {dir}");
                }
            await page.EvaluateAsync("() => { SystemMap.cy().getElementById('proj:Lib').emit('tap'); }");   // hopfälld projekt, klasserna dolda
            await ready();
            await AssertNoClassOverlapAsync(page, "projekt hopfällt");
        });
    }

    private static async Task AssertNoClassOverlapAsync(IPage page, string when)
    {
        var boxes = (await page.EvaluateAsync<System.Text.Json.JsonElement>(Leaves)).EnumerateArray().Select(e => e.EnumerateArray().ToArray()).ToArray();
        Assert.That(boxes, Is.Not.Empty);
        for (var i = 0; i < boxes.Length; i++)
            for (var j = i + 1; j < boxes.Length; j++)
            {
                var a = boxes[i][0].GetString()!; var b = boxes[j][0].GetString()!;
                if (!a.StartsWith("class:") && !b.StartsWith("class:")) continue;
                double V(int k, string p) => boxes[k][1].GetProperty(p).GetDouble();
                var overlap = V(i, "x1") < V(j, "x2") - 1 && V(j, "x1") < V(i, "x2") - 1 && V(i, "y1") < V(j, "y2") - 1 && V(j, "y1") < V(i, "y2") - 1;
                Assert.That(overlap, Is.False, $"{a} och {b} överlappar ({when})");
            }
    }
}
