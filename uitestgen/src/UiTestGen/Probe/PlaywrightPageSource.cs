using System.Text.Json;
using Microsoft.Playwright;

namespace UiTestGen.Probe;

/// <summary>Öppnar sidor i Chromium via Playwright och läser DOM:en med en liten JavaScript-snutt.</summary>
internal sealed class PlaywrightPageSource : IPageSource, IAsyncDisposable
{
    public const string BrowserPathVariable = "UITEST_BROWSER_PATH";

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly IPlaywright _playwright;
    private readonly IBrowser _browser;
    private readonly IBrowserContext _context;
    private readonly string _origin;
    private readonly int _timeoutMs;

    private PlaywrightPageSource(IPlaywright playwright, IBrowser browser, IBrowserContext context, string origin, int timeoutMs)
    {
        _playwright = playwright;
        _browser = browser;
        _context = context;
        _origin = origin;
        _timeoutMs = timeoutMs;
    }

    public static async Task<PlaywrightPageSource> LaunchAsync(string origin, ProbeOptions options)
    {
        var path = options.BrowserPath ?? Environment.GetEnvironmentVariable(BrowserPathVariable);
        if (string.IsNullOrWhiteSpace(path)) path = null;
        if (path is not null && !File.Exists(path))
            throw MissingBrowser($"Hittar inte webbläsaren: {Path.GetFullPath(path)}");

        var playwright = await Playwright.CreateAsync();
        try
        {
            // Som root (t.ex. i en container) startar Chromium inte med sandlådan på.
            var launch = new BrowserTypeLaunchOptions { Headless = true, ExecutablePath = path };
            if (OperatingSystem.IsLinux() && Environment.UserName == "root") launch.Args = ["--no-sandbox"];
            var browser = await playwright.Chromium.LaunchAsync(launch);
            var context = await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
            return new PlaywrightPageSource(playwright, browser, context, origin, options.TimeoutMs);
        }
        catch (PlaywrightException e) when (e.Message.Contains("Executable doesn't exist", StringComparison.Ordinal)
                                            || e.Message.Contains("playwright install", StringComparison.Ordinal))
        {
            playwright.Dispose();
            throw MissingBrowser("Hittar ingen webbläsare för sonderingen.", e.Message);
        }
        catch
        {
            playwright.Dispose();
            throw;
        }
    }

    private static UsageException MissingBrowser(string what, string? technical = null) => new(what,
        "Installera Chromium med: pwsh <mapp med uitestgen>/playwright.ps1 install chromium " +
        "(eller: dotnet tool install --global Microsoft.Playwright.CLI och sedan playwright install chromium). " +
        $"Har du redan en webbläsare, ange den med --browser-path <fil> eller miljövariabeln {BrowserPathVariable}.", technical);

    public async Task<PageResult> OpenAsync(string path, CancellationToken cancellationToken)
    {
        var page = await _context.NewPageAsync();
        try
        {
            page.SetDefaultTimeout(_timeoutMs);
            var response = await page.GotoAsync(_origin + path, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = _timeoutMs });
            var status = response?.Status ?? 200;
            var finalUri = new Uri(page.Url);
            var finalPath = string.Equals(finalUri.GetLeftPart(UriPartial.Authority), _origin, StringComparison.OrdinalIgnoreCase)
                ? finalUri.AbsolutePath : page.Url;
            if (status >= 400) return new PageResult(status, finalPath, null);
            var raw = await page.EvaluateAsync<string>(ReadScript);
            var snapshot = JsonSerializer.Deserialize<PageSnapshot>(raw, Json);
            return new PageResult(status, finalPath, snapshot);
        }
        catch (Exception e) when (e is TimeoutException || e is PlaywrightException && e.Message.StartsWith("Timeout ", StringComparison.Ordinal))
        {
            return new PageResult(0, path, null) { TimedOut = true };
        }
        catch (PlaywrightException e)
        {
            return new PageResult(0, path, null, e.Message.Split('\n')[0].Trim());
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _context.CloseAsync();
        await _browser.CloseAsync();
        _playwright.Dispose();
    }

    /// <summary>Läser titel, rubrik, länkar och formulär. Svaret är JSON med samma namn som <see cref="PageSnapshot"/>.</summary>
    internal const string ReadScript = """
        () => {
          const clean = s => { s = (s ?? '').replace(/\s+/g, ' ').trim(); return s || null; };
          const labelOf = el => {
            const l = el.labels && el.labels[0];
            if (l) {
              const c = l.cloneNode(true);
              c.querySelectorAll('input,select,textarea').forEach(x => x.remove());
              const t = clean(c.textContent);
              if (t) return t;
            }
            const aria = clean(el.getAttribute('aria-label'));
            if (aria) return aria;
            const by = el.getAttribute('aria-labelledby');
            if (by) {
              const t = clean(by.split(/\s+/).map(id => document.getElementById(id)?.textContent ?? '').join(' '));
              if (t) return t;
            }
            return null;
          };
          const skipped = ['hidden', 'submit', 'button', 'image', 'reset'];
          const forms = [...document.forms].map(f => {
            const fields = [...f.elements]
              .filter(e => ['INPUT', 'SELECT', 'TEXTAREA'].includes(e.tagName) && !(e.tagName === 'INPUT' && skipped.includes(e.type)))
              .map(e => {
                const input = e.tagName === 'INPUT';
                return {
                  tag: e.tagName.toLowerCase(),
                  type: input ? e.type : null,
                  name: clean(e.getAttribute('name')),
                  id: clean(e.id),
                  label: labelOf(e),
                  value: input ? e.getAttribute('value') : null,
                  testId: clean(e.getAttribute('data-testid')),
                  required: !!e.required,
                  minLength: input || e.tagName === 'TEXTAREA' ? (e.minLength >= 0 ? e.minLength : null) : null,
                  maxLength: input || e.tagName === 'TEXTAREA' ? (e.maxLength >= 0 ? e.maxLength : null) : null,
                  min: input ? e.getAttribute('min') : null,
                  max: input ? e.getAttribute('max') : null,
                  pattern: input ? e.getAttribute('pattern') : null,
                  options: e.tagName === 'SELECT' ? [...e.options].map(o => o.value).filter(v => v) : []
                };
              });
            const b = f.querySelector('button[type=submit], input[type=submit], button:not([type])');
            return {
              fields,
              submitText: b ? clean(b.tagName === 'INPUT' ? b.value : b.textContent) : null,
              submitTestId: b ? clean(b.getAttribute('data-testid')) : null
            };
          });
          const links = [...document.querySelectorAll('a[href]')]
            .filter(a => typeof a.href === 'string')
            .map(a => ({ href: a.href, text: clean(a.textContent) }));
          return JSON.stringify({
            title: clean(document.title),
            heading: clean(document.querySelector('h1')?.textContent),
            links,
            forms
          });
        }
        """;
}
