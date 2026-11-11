using Microsoft.Playwright;

namespace SystemMap.Tests;

/// <summary>Sidan i en riktig webbläsare: zoomknapparna nere till höger, procenten och tangenterna.</summary>
public partial class CodeMapTests
{
    private const string Zoom = "() => SystemMap.cy().zoom()";
    private const string ZoomPct = "#map-zoom-pct";

    [Test]
    public async Task Page_zoom_buttons_change_the_zoom_and_the_percentage_follows()
    {
        var map = await MapAsync(Build(true, Web("Api", AppStart + FlowApi)));
        await WithPageAsync(map, async (page, ready) =>
        {
            Assert.That(await page.GetAttributeAsync("#map-zoom", "role"), Is.EqualTo("group"));
            Assert.That(await page.GetAttributeAsync("#map-zoom", "aria-label"), Is.EqualTo("Zoom"));
            Assert.That(await page.GetAttributeAsync("#map-zoom-in", "aria-label"), Is.EqualTo("Zooma in (+)"));
            Assert.That(await page.GetAttributeAsync("#map-zoom-out", "aria-label"), Is.EqualTo("Zooma ut (−)"));
            Assert.That(await page.GetAttributeAsync("#map-zoom-fit", "aria-label"), Is.EqualTo("Passa in hela kartan (0)"));

            var start = await page.EvaluateAsync<double>(Zoom);
            Assert.That(await page.InnerTextAsync(ZoomPct), Is.EqualTo($"{Math.Round(start * 100)} %"));
            await page.ClickAsync("#map-zoom-in");
            Assert.That(await page.EvaluateAsync<double>(Zoom), Is.EqualTo(start * 1.25).Within(1e-6));
            Assert.That(await page.InnerTextAsync(ZoomPct), Is.EqualTo($"{Math.Round(start * 1.25 * 100)} %"));
            await page.ClickAsync("#map-zoom-out");
            await page.ClickAsync("#map-zoom-out");
            Assert.That(await page.EvaluateAsync<double>(Zoom), Is.EqualTo(start / 1.25).Within(1e-6));
            Assert.That(await page.InnerTextAsync(ZoomPct), Is.EqualTo($"{Math.Round(start / 1.25 * 100)} %"));

            // Passa in går tillbaka till hela kartan, och procenten följer.
            await page.ClickAsync("#map-zoom-fit");
            Assert.That(await page.EvaluateAsync<double>(Zoom), Is.EqualTo(start).Within(1e-6));
            Assert.That(await page.InnerTextAsync(ZoomPct), Is.EqualTo($"{Math.Round(start * 100)} %"));
            Assert.That(await page.EvaluateAsync<double>(Zoom), Is.LessThanOrEqualTo(1.2), "passa in zoomar aldrig in mer än 120 %");
        }, groupsOpen: false);   // inpassningen gäller kartan som den ritas från början
    }

    [Test]
    public async Task Page_zoom_keys_work_in_the_map_but_not_in_a_text_field_and_the_percentage_follows_fitView_and_setting_the_zoom()
    {
        var map = await MapAsync(Build(true, Web("Api", AppStart + FlowApi)));
        await WithPageAsync(map, async (page, ready) =>
        {
            var start = await page.EvaluateAsync<double>(Zoom);
            await page.FocusAsync("#map-view");
            await page.Keyboard.PressAsync("+");
            Assert.That(await page.EvaluateAsync<double>(Zoom), Is.EqualTo(start * 1.25).Within(1e-6));
            await page.Keyboard.PressAsync("=");
            Assert.That(await page.EvaluateAsync<double>(Zoom), Is.EqualTo(start * 1.25 * 1.25).Within(1e-6));
            await page.Keyboard.PressAsync("-");
            Assert.That(await page.EvaluateAsync<double>(Zoom), Is.EqualTo(start * 1.25).Within(1e-6));
            await page.Keyboard.PressAsync("0");
            Assert.That(await page.EvaluateAsync<double>(Zoom), Is.EqualTo(start).Within(1e-6));

            // Fokus på en zoomknapp: tangenterna gäller också där.
            await page.FocusAsync("#map-zoom-fit");
            await page.Keyboard.PressAsync("+");
            Assert.That(await page.EvaluateAsync<double>(Zoom), Is.EqualTo(start * 1.25).Within(1e-6));

            // I ett textfält skriver man bara.
            await page.FocusAsync("#map-fn");
            await page.Keyboard.PressAsync("-");
            await page.Keyboard.PressAsync("0");
            Assert.That(await page.EvaluateAsync<double>(Zoom), Is.EqualTo(start * 1.25).Within(1e-6), "tangenterna zoomar inte i ett textfält");

            // Procenten följer även zoom som sätts på annat sätt (nyp, Ctrl + rullhjul) och fitView.
            await page.EvaluateAsync("() => SystemMap.cy().zoom({ level: 0.5, renderedPosition: { x: 10, y: 10 } })");
            Assert.That(await page.InnerTextAsync(ZoomPct), Is.EqualTo("50 %"));
        }, groupsOpen: false);   // inpassningen gäller kartan som den ritas från början
    }

    /// <summary>Klickar knappen tills den blir avstängd (aria-disabled): en avstängd knapp går inte att klicka.</summary>
    private static async Task ClickWhileEnabledAsync(IPage page, string button)
    {
        for (var i = 0; i < 40 && !await page.IsDisabledAsync(button); i++) await page.ClickAsync(button);
        Assert.That(await page.IsDisabledAsync(button), Is.True, "knappen har nått gränsen");
    }

    [Test]
    public async Task Page_zoom_button_is_aria_disabled_at_the_limits_and_max_zoom_is_4()
    {
        var map = await MapAsync(Build(true, Web("Api", AppStart + FlowApi)));
        await WithPageAsync(map, async (page, ready) =>
        {
            Assert.That(await page.EvaluateAsync<double>("() => SystemMap.cy().maxZoom()"), Is.EqualTo(4));
            Assert.That(await page.GetAttributeAsync("#map-zoom-in", "aria-disabled"), Is.EqualTo("false"));
            await ClickWhileEnabledAsync(page, "#map-zoom-in");
            Assert.That(await page.EvaluateAsync<double>(Zoom), Is.EqualTo(4).Within(1e-6));
            Assert.That(await page.InnerTextAsync(ZoomPct), Is.EqualTo("400 %"));
            Assert.That(await page.GetAttributeAsync("#map-zoom-in", "aria-disabled"), Is.EqualTo("true"));
            Assert.That(await page.GetAttributeAsync("#map-zoom-out", "aria-disabled"), Is.EqualTo("false"));
            await ClickWhileEnabledAsync(page, "#map-zoom-out");
            Assert.That(await page.EvaluateAsync<double>(Zoom), Is.EqualTo(0.2).Within(1e-6));
            Assert.That(await page.GetAttributeAsync("#map-zoom-out", "aria-disabled"), Is.EqualTo("true"));
            Assert.That(await page.GetAttributeAsync("#map-zoom-in", "aria-disabled"), Is.EqualTo("false"));
        });
    }

    [Test]
    public async Task Page_zoom_box_does_not_cover_the_legend_and_the_hint_names_the_modifier_key()
    {
        var map = await MapAsync(Build(true, Web("Api", AppStart + FlowApi)));
        await WithPageAsync(map, async (page, ready) =>
        {
            Assert.That(await page.InnerTextAsync("#map-zoom-hint"), Does.Contain("rullhjul zoomar"));
            var zoom = await page.Locator("#map-zoom").BoundingBoxAsync();
            Assert.That(zoom, Is.Not.Null);
            var legend = await page.Locator("#map-legend").BoundingBoxAsync();
            if (legend != null) Assert.That(legend.X + legend.Width, Is.LessThan(zoom!.X), "förklaringen ligger till vänster om zoomrutan");
            Assert.That(await page.GetAttributeAsync("#map-view", "tabindex"), Is.EqualTo("0"));
        });
    }
}
