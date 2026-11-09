using Microsoft.Playwright;

namespace TestGen.Browser;

/// <summary>Hur en session (en webbläsarkontext) ska se ut.</summary>
/// <param name="BaseUrl">Appens adress. Med den kan <see cref="BrowserSession.OpenAsync"/> ta en sökväg.</param>
/// <param name="Trace">Spela in Playwright-spår (skärmdumpar och nätverk). Avstängt som standard, eftersom spåret kan innehålla känsliga uppgifter.</param>
/// <param name="IsIgnored">Anrop vars fel inte ska räknas (utöver favicon och saknade bilder).</param>
/// <param name="Headers">Headers som läggs på alla anrop till <paramref name="BaseUrl"/> (och bara dem), t.ex. testanvändarens token.</param>
public sealed record SessionOptions(string? BaseUrl = null, bool Trace = false, Func<IRequest, bool>? IsIgnored = null, IReadOnlyDictionary<string, string>? Headers = null);

/// <summary>En Chromium (headless). Starta en per testklass och öppna en <see cref="BrowserSession"/> per test.</summary>
public sealed class BrowserHost : IAsyncDisposable
{
    private readonly IPlaywright _playwright;
    private readonly IBrowser _browser;

    private BrowserHost(IPlaywright playwright, IBrowser browser)
    {
        _playwright = playwright;
        _browser = browser;
    }

    /// <summary>Startar Chromium. Playwright letar efter webbläsaren på sitt vanliga sätt (<c>PLAYWRIGHT_BROWSERS_PATH</c> gäller).</summary>
    /// <exception cref="ChromiumMissingException">Chromium är inte installerat.</exception>
    public static async Task<BrowserHost> StartAsync()
    {
        var playwright = await Playwright.CreateAsync();
        try
        {
            var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            return new BrowserHost(playwright, browser);
        }
        catch (PlaywrightException e) when (e.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase))
        {
            playwright.Dispose();
            throw ChromiumMissingException.Create(e);
        }
        catch
        {
            playwright.Dispose();
            throw;
        }
    }

    /// <summary>En ny kontext med en sida. Fast fönsterstorlek, språk och tidszon, så att skärmdumpar och datum blir desamma överallt.</summary>
    public async Task<BrowserSession> NewSessionAsync(SessionOptions? options = null)
    {
        options ??= new SessionOptions();
        var context = await _browser.NewContextAsync(new()
        {
            BaseURL = options.BaseUrl,
            ViewportSize = new ViewportSize { Width = 1280, Height = 800 },
            Locale = "sv-SE",
            TimezoneId = "Europe/Stockholm",
            ColorScheme = ColorScheme.Light,
        });
        if (options.Headers is { Count: > 0 } headers && options.BaseUrl is { } baseUrl)
        {
            var origin = new Uri(baseUrl).GetLeftPart(UriPartial.Authority);
            await context.RouteAsync(url => url.StartsWith(origin, StringComparison.OrdinalIgnoreCase), async route =>
            {
                var merged = new Dictionary<string, string>(route.Request.Headers, StringComparer.OrdinalIgnoreCase);
                foreach (var (name, value) in headers) merged[name] = value;
                await route.ContinueAsync(new() { Headers = merged });
            });
        }
        if (options.Trace) await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = false });
        var page = await context.NewPageAsync();
        return new BrowserSession(context, page, options);
    }

    public async ValueTask DisposeAsync()
    {
        await _browser.DisposeAsync();
        _playwright.Dispose();
    }
}
