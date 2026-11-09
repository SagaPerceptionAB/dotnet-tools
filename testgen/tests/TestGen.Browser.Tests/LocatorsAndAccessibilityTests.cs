using Microsoft.Playwright;
using TestGen.Core.Views;

namespace TestGen.Browser.Tests;

public class LocatorsAndAccessibilityTests
{
    private const string Page = """
        <!doctype html><html lang="sv"><head><title>Sida</title></head><body><main>
        <h1>Sida</h1>
        <label for="a">Etikett</label><input id="a">
        <button>Spara</button>
        <span data-testid="x">test-id</span>
        <p class="c">css</p>
        </main></body></html>
        """;

    private const string BadPage = """
        <!doctype html><html lang="sv"><head><title>Dålig</title></head><body><main>
        <h1>Dålig</h1><img src="data:image/gif;base64,R0lGODlhAQABAAAAACw=">
        </main></body></html>
        """;

    private static Task<TestApp> StartAsync() => TestApp.StartAsync(app =>
    {
        TestApp.Page(app, "/page", Page);
        TestApp.Page(app, "/bad", BadPage);
    });

    [TestCase(LocatorKind.Label, "Etikett", null)]
    [TestCase(LocatorKind.Role, "button", "Spara")]
    [TestCase(LocatorKind.TestId, "x", null)]
    [TestCase(LocatorKind.Css, "p.c", null)]
    public async Task Each_kind_of_locator_finds_exactly_one_element(LocatorKind kind, string value, string? name)
    {
        await using var site = await StartAsync();
        await using var session = await BrowserFixture.Host.NewSessionAsync(new SessionOptions(site.Url));
        await session.OpenAsync("/page");

        Assert.That(await session.Page.Resolve(new Locator(kind, value, name)).CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task Role_name_must_match_exactly()
    {
        await using var site = await StartAsync();
        await using var session = await BrowserFixture.Host.NewSessionAsync(new SessionOptions(site.Url));
        await session.OpenAsync("/page");

        Assert.That(await session.Page.Resolve(new Locator(LocatorKind.Role, "button", "Spa")).CountAsync(), Is.Zero);
    }

    [Test]
    public void Unknown_role_is_reported()
    {
        var error = Assert.Throws<ArgumentException>(() => Locators.Resolve(null!, new Locator(LocatorKind.Role, "knapp")));
        Assert.That(error!.Message, Does.Contain("Okänd roll"));
    }

    [Test]
    public async Task Axe_finds_a_known_violation()
    {
        await using var site = await StartAsync();
        await using var session = await BrowserFixture.Host.NewSessionAsync(new SessionOptions(site.Url));
        await session.OpenAsync("/bad");

        var violations = await Accessibility.CheckAsync(session.Page);

        var imageAlt = violations.Single(v => v.Rule == "image-alt");
        Assert.That(imageAlt.Impact, Is.EqualTo("critical"));
        Assert.That(imageAlt.Elements.Single().Selector, Is.EqualTo("img"));
        Assert.That(imageAlt.Describe(), Does.StartWith("image-alt (critical)"));
    }

    [Test]
    public async Task Axe_finds_nothing_on_a_good_page()
    {
        await using var site = await StartAsync();
        await using var session = await BrowserFixture.Host.NewSessionAsync(new SessionOptions(site.Url));
        await session.OpenAsync("/page");

        Assert.That(await Accessibility.CheckAsync(session.Page), Is.Empty);
    }
}
