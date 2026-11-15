using Microsoft.Playwright;

namespace SystemMap.Tests;

/// <summary>Knappen Visa i raden (efter Tema) och panelen under den: förklaringen av linjerna, delar som inte nås och det som är dolt.</summary>
public partial class CodeMapTests
{
    private static Task<string?> ShowExpandedAsync(IPage page) => page.GetAttributeAsync("#map-show", "aria-expanded");

    private static Task<string> FocusedIdAsync(IPage page) => page.EvaluateAsync<string>("() => document.activeElement ? document.activeElement.id : ''");

    private static async Task<CodeMapModel> MapAllReachedAsync() =>
        await MapAsync(Build(true, Web("Api", AppStart + "[ApiController, Route(\"x\")] public class XController : ControllerBase { [HttpGet] public int Get() => 1; }")));

    [Test]
    public async Task The_Visa_button_stands_right_after_Tema_as_a_44_px_info_button_with_a_label_above()
    {
        await WithPageAsync(await WithUnreached(), async (page, ready) =>
        {
            Assert.That(await page.EvaluateAsync<string>("() => document.getElementById('map-theme-field').nextElementSibling.id"), Is.EqualTo("map-show-field"), "direkt efter Tema");
            Assert.That(await page.TextContentAsync("#map-show-field .map-label"), Is.EqualTo("Visa"));
            var size = await page.Locator("#map-show").BoundingBoxAsync();
            Assert.That((size!.Width, size.Height), Is.EqualTo((44d, 44d)));
            Assert.That(await page.GetAttributeAsync("#map-show", "aria-label"), Is.EqualTo("Visa och förklaring"));
            Assert.That(await page.GetAttributeAsync("#map-show", "title"), Is.EqualTo("Visa och förklaring"));
            Assert.That(await page.GetAttributeAsync("#map-show", "aria-controls"), Is.EqualTo("map-show-panel"));
            Assert.That(await page.Locator("#map-show svg circle").CountAsync(), Is.EqualTo(1), "cirkeln med ett i");
            Assert.That(await page.Locator("#map-show .map-count, #map-show span").CountAsync(), Is.Zero, "ingen siffra på knappen");
            Assert.That(await ShowExpandedAsync(page), Is.EqualTo("false"));
            Assert.That(await page.IsHiddenAsync("#map-show-panel"), Is.True);
            var button = await page.Locator("#map-show").BoundingBoxAsync();
            var label = await page.Locator("#map-show-field .map-label").BoundingBoxAsync();
            Assert.That(label!.Y + label.Height, Is.LessThanOrEqualTo(button!.Y + 1), "etiketten står ovanför knappen");
        });
    }

    [Test]
    public async Task The_Visa_button_is_only_there_when_the_panel_has_something_to_show()
    {
        await WithPageAsync(await MapAllReachedAsync(), async (page, ready) =>
        {
            Assert.That(await page.IsHiddenAsync("#map-show-field"), Is.True, "allt nås, inget är dolt och inga referenslinjer ritas");
        });
        await WithPageAsync(await WithUnreached(), async (page, ready) =>
        {
            Assert.That(await page.IsVisibleAsync("#map-show"), Is.True, "delar som inte nås");
        }, level: "classes");
        var all = await MapAllReachedAsync();
        await WithPageAsync(all, async (page, ready) =>
        {
            Assert.That(await page.IsVisibleAsync("#map-show"), Is.True, "något är dolt");
        }, HiddenIn(all, "XController"));
        await WithPageAsync(await MapRefsAsync(), async (page, ready) =>
        {
            Assert.That(await page.IsVisibleAsync("#map-show"), Is.True, "referenslinjerna förklaras");
        }, level: "projects", groupsOpen: false);
    }

    [Test]
    public async Task The_panel_opens_and_closes_with_the_button_Esc_and_a_click_outside()
    {
        await WithPageAsync(await WithUnreached(), async (page, ready) =>
        {
            await page.ClickAsync("#map-show");
            Assert.That(await ShowExpandedAsync(page), Is.EqualTo("true"));
            Assert.That(await page.IsVisibleAsync("#map-show-panel"), Is.True);

            // Ett nytt klick på knappen stänger.
            await page.ClickAsync("#map-show");
            Assert.That(await ShowExpandedAsync(page), Is.EqualTo("false"));
            Assert.That(await page.IsHiddenAsync("#map-show-panel"), Is.True);

            // Esc stänger och fokus går tillbaka till knappen, också när fokus är inne i panelen.
            await page.ClickAsync("#map-show");
            await page.FocusAsync("#map-unreached");
            await page.Keyboard.PressAsync("Escape");
            Assert.That(await ShowExpandedAsync(page), Is.EqualTo("false"));
            Assert.That(await page.IsHiddenAsync("#map-show-panel"), Is.True);
            Assert.That(await FocusedIdAsync(page), Is.EqualTo("map-show"));

            // Ett klick utanför, i kartan och i raden.
            await page.ClickAsync("#map-show");
            await page.Mouse.ClickAsync(60, 700);
            Assert.That(await ShowExpandedAsync(page), Is.EqualTo("false"));
            await page.ClickAsync("#map-show");
            await page.ClickAsync("#map-theme-field .map-label");
            Assert.That(await ShowExpandedAsync(page), Is.EqualTo("false"));

            // Ett klick inne i panelen stänger den inte.
            await page.ClickAsync("#map-show");
            await page.ClickAsync("#map-show-panel", new PageClickOptions { Position = new Position { X = 4, Y = 4 } });
            Assert.That(await ShowExpandedAsync(page), Is.EqualTo("true"));
        });
    }

    [Test]
    public async Task Only_one_of_the_lists_and_the_panel_is_open_at_a_time()
    {
        await WithPageAsync(await WithUnreached(), async (page, ready) =>
        {
            await page.ClickAsync("#map-show");
            await page.ClickAsync("#map-project");
            Assert.That(await ShowExpandedAsync(page), Is.EqualTo("false"), "en lista öppnas: panelen stängs");
            Assert.That(await page.GetAttributeAsync("#map-project", "aria-expanded"), Is.EqualTo("true"));

            await page.ClickAsync("#map-show");
            Assert.That(await ShowExpandedAsync(page), Is.EqualTo("true"));
            Assert.That(await page.GetAttributeAsync("#map-project", "aria-expanded"), Is.EqualTo("false"), "panelen öppnas: listan stängs");
        });
    }

    [Test]
    public async Task The_panel_is_right_aligned_under_the_button_and_looks_like_the_lists_in_both_themes()
    {
        foreach (var colors in new[] { ColorScheme.Light, ColorScheme.Dark })
        {
            await WithPageAsync(await WithUnreached(), async (page, ready) =>
            {
                await page.ClickAsync("#map-show");
                var button = (await page.Locator("#map-show").BoundingBoxAsync())!;
                var panel = (await page.Locator("#map-show-panel").BoundingBoxAsync())!;
                Assert.That(panel.Y, Is.GreaterThanOrEqualTo(button.Y + button.Height), "under knappen");
                Assert.That(panel.X + panel.Width, Is.EqualTo(button.X + button.Width).Within(1), "högerjusterad mot knappen");
                const string looks = "(sel) => { const s = getComputedStyle(document.querySelector(sel)); return [s.backgroundColor, s.borderTopColor, s.borderTopLeftRadius, s.boxShadow]; }";
                await page.ClickAsync("#map-project");
                await page.EvaluateAsync("() => { document.getElementById('map-project-list').hidden = false; }");
                Assert.That(await page.EvaluateAsync<string[]>(looks, "#map-show-panel"), Is.EqualTo(await page.EvaluateAsync<string[]>(looks, "#map-project-list")), "samma bakgrund, kant, radie och skugga som listorna");
            }, colors: colors);
        }
    }

    [Test]
    public async Task The_panel_content_follows_the_level_the_unreached_parts_and_what_is_hidden()
    {
        // Förklaringen av linjerna bara på nivån Projekt, med avdelare när mer följer.
        var refs = await MapRefsAsync();
        await WithPageAsync(refs, async (page, ready) =>
        {
            await OpenShowAsync(page);
            Assert.That(await page.IsVisibleAsync("#map-refs"), Is.True);
            Assert.That(await page.InnerTextAsync("#map-refs strong"), Is.EqualTo("Förklaring"));
            Assert.That(await page.IsHiddenAsync("#map-hidden-row"), Is.True, "inget är dolt");
            await PickAsync(page, "cls", "Alla");
            await ready();
            Assert.That(await page.IsHiddenAsync("#map-refs"), Is.True, "på nivån Klass ritas inga referenslinjer");
        }, level: "projects", groupsOpen: false);

        // Delar som inte nås: kryssrutan, och ingen förklaring av linjer på nivån Klass.
        var unreached = await WithUnreached();
        await WithPageAsync(unreached, async (page, ready) =>
        {
            await OpenShowAsync(page);
            Assert.That(await page.IsHiddenAsync("#map-refs"), Is.True);
            Assert.That(await page.IsHiddenAsync("#map-show-sep"), Is.True, "ingen avdelare utan förklaringen");
            Assert.That(await page.IsVisibleAsync("#map-unreached"), Is.True);
            Assert.That(await page.IsVisibleAsync("#map-unreached-field .map-swatch"), Is.True, "den streckade rutan");
            Assert.That(await page.IsHiddenAsync("#map-hidden-row"), Is.True);
            Assert.That(await page.IsVisibleAsync("#map-show"), Is.True);
        });

        // Det dolda: antalet, namnen och Visa dem; panelen utan delar som inte nås.
        var reached = await MapAllReachedAsync();
        await WithPageAsync(reached, async (page, ready) =>
        {
            await OpenShowAsync(page);
            Assert.That(await page.IsHiddenAsync("#map-unreached-field"), Is.True);
            Assert.That(await page.IsHiddenAsync("#map-refs"), Is.True);
            Assert.That(await page.InnerTextAsync("#map-hint"), Is.EqualTo("1 dold med --hide"));
            Assert.That(await page.GetAttributeAsync("#map-show-all", "title"), Does.Contain("XController"));
            Assert.That(await page.InnerTextAsync("#map-show-all"), Is.EqualTo("Visa dem"));
            await page.ClickAsync("#map-show-all");
            await ready();
            Assert.That(await page.IsHiddenAsync("#map-show-field"), Is.True, "inget kvar att visa: knappen försvinner");
            Assert.That(await page.IsHiddenAsync("#map-show-panel"), Is.True);
        }, HiddenIn(reached, "XController"));

        // Allt tre: förklaringen, avdelare, kryssrutan och det dolda, i den ordningen.
        var all = await MapRefsAsync();
        await WithPageAsync(all, async (page, ready) =>
        {
            await OpenShowAsync(page);
            var ids = await page.EvaluateAsync<string[]>("() => Array.from(document.getElementById('map-show-panel').children).filter(e => !e.hidden).map(e => e.id)");
            Assert.That(ids.Where(i => i is "map-refs" or "map-unreached-field" or "map-hidden-row"), Is.EqualTo(new[] { "map-refs", "map-unreached-field" }.Concat(ids.Contains("map-hidden-row") ? new[] { "map-hidden-row" } : [])));
            if (ids.Contains("map-unreached-field")) Assert.That(ids, Does.Contain("map-show-sep"));
        }, level: "projects", groupsOpen: false);
    }

    [Test]
    public async Task The_keyboard_opens_the_panel_and_Tab_goes_from_the_button_into_it()
    {
        await WithPageAsync(await WithUnreached(), async (page, ready) =>
        {
            await page.FocusAsync("#map-show");
            await page.Keyboard.PressAsync("Enter");
            Assert.That(await ShowExpandedAsync(page), Is.EqualTo("true"));
            Assert.That(await FocusedIdAsync(page), Is.EqualTo("map-show"), "fokus stannar på knappen");
            await page.Keyboard.PressAsync("Tab");
            Assert.That(await FocusedIdAsync(page), Is.EqualTo("map-unreached"), "knapp, sedan innehållet");
            Assert.That(await ShowExpandedAsync(page), Is.EqualTo("true"), "panelen är öppen medan fokus är i den");
            await page.Keyboard.PressAsync("Space");
            await ready();
            Assert.That(await page.IsCheckedAsync("#map-unreached"), Is.False, "kryssrutan går att växla med tangentbordet");
            await page.Keyboard.PressAsync("Tab");
            Assert.That(await FocusedIdAsync(page), Is.EqualTo("map-show-all"), "sedan Visa dem");
            await page.Keyboard.PressAsync("Escape");
            Assert.That(await FocusedIdAsync(page), Is.EqualTo("map-show"));
            Assert.That(await ShowExpandedAsync(page), Is.EqualTo("false"));
        }, HiddenIn(await WithUnreached(), "LegacyExporter"));
    }

    [Test]
    public async Task There_is_no_box_left_at_the_bottom_left_of_the_map()
    {
        await WithPageAsync(await WithUnreached(), async (page, ready) =>
        {
            Assert.That(await page.Locator("#map-legend, .map-legend").CountAsync(), Is.Zero);
            Assert.That(await page.Locator("#map-wrap #map-view ~ #map-unreached-field, #map-wrap > #map-unreached-field, #map-wrap > #map-refs").CountAsync(), Is.Zero);
            Assert.That(await page.Locator("#map-bar #map-unreached").CountAsync(), Is.EqualTo(1), "valet bor i panelen i raden");
        });
    }

    [Test]
    public async Task The_Visa_button_is_there_with_a_theme_that_is_locked_to_auto_where_the_page_has_no_theme_button()
    {
        await WithPageAsync(await WithUnreached(), async (page, ready) =>
        {
            await page.EvaluateAsync("() => { document.getElementById('map').remove(); const r = document.createElement('div'); r.id = 'map'; document.body.appendChild(r); SystemMap.create(r, { theme: 'auto' }).show(JSON.parse(document.getElementById('map-data').textContent)); }");
            await ready();
            Assert.That(await page.IsVisibleAsync("#map-theme-field"), Is.False);
            Assert.That(await page.IsVisibleAsync("#map-show"), Is.True);
            await page.ClickAsync("#map-show");
            Assert.That(await page.IsVisibleAsync("#map-unreached"), Is.True);
        });
    }

    [Test]
    public async Task The_row_breaks_as_before_on_a_narrow_screen_and_the_panel_stays_inside_the_window()
    {
        await WithPageAsync(await WithUnreached(), async (page, ready) =>
        {
            await page.SetViewportSizeAsync(420, 800);
            await page.ClickAsync("#map-show");
            var panel = (await page.Locator("#map-show-panel").BoundingBoxAsync())!;
            Assert.That(panel.X, Is.GreaterThanOrEqualTo(0), "panelen går inte ut över fönstrets vänsterkant");
            Assert.That(panel.X + panel.Width, Is.LessThanOrEqualTo(420 + 1));
        });
    }
}
