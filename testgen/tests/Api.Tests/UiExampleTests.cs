using Microsoft.Playwright;

namespace Api.Tests;

/// <summary>
/// Exempel på UI-test med mockade backend-anrop via page.RouteAsync. Kräver webbläsare:
/// pwsh bin/Debug/net10.0/playwright.ps1 install chromium. Körs bara när det väljs uttryckligen.
/// </summary>
[TestFixture, Explicit("Exempel; kräver installerad Playwright-webbläsare.")]
public class UiExampleTests
{
    [Test]
    public async Task Page_shows_mocked_customer()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await browser.NewPageAsync();

        await page.RouteAsync("**/api/customers/*", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "application/json",
            Body = "{\"id\":1,\"name\":\"Stina Stamkund\"}",
        }));
        await page.RouteAsync("https://example.test/", route => route.FulfillAsync(new RouteFulfillOptions
        {
            ContentType = "text/html",
            Body = "<div id=n></div><script>fetch('/api/customers/1').then(r=>r.json()).then(c=>n.textContent=c.name)</script>",
        }));

        await page.GotoAsync("https://example.test/");
        await Assertions.Expect(page.Locator("#n")).ToHaveTextAsync("Stina Stamkund");
    }
}
