using Microsoft.Playwright;

namespace TestGen.Browser;

/// <summary>En sida i en egen kontext, med felinsamling. Bilagorna (skärmdump, HTML, konsollogg, spår) hämtas på begäran.</summary>
public sealed class BrowserSession : IAsyncDisposable
{
    private readonly IBrowserContext _context;
    private readonly bool _tracing;
    private readonly object _gate = new();
    private readonly List<string> _console = [];

    internal BrowserSession(IBrowserContext context, IPage page, SessionOptions options)
    {
        _context = context;
        _tracing = options.Trace;
        Page = page;
        Errors = PageErrors.Attach(page, options.IsIgnored);
        page.Console += (_, message) =>
        {
            lock (_gate) _console.Add($"[{message.Type}] {message.Text}");
        };
    }

    public IPage Page { get; }

    public PageErrors Errors { get; }

    /// <summary>Alla konsolrader sidan skrivit, inte bara felen.</summary>
    public IReadOnlyList<string> ConsoleLog
    {
        get { lock (_gate) return [.. _console]; }
    }

    /// <summary>Går till adressen (en sökväg om <see cref="SessionOptions.BaseUrl"/> är satt) och ger svaret.</summary>
    public async Task<IResponse> OpenAsync(string url)
    {
        var response = await Page.GotoAsync(url);
        return response ?? throw new InvalidOperationException($"Inget svar från {url}.");
    }

    /// <summary>Skärmdump av hela sidan som PNG.</summary>
    public Task<byte[]> ScreenshotAsync() => Page.ScreenshotAsync(new() { Type = ScreenshotType.Png, FullPage = true });

    /// <summary>Sidans HTML som den ser ut nu.</summary>
    public Task<string> HtmlAsync() => Page.ContentAsync();

    /// <summary>Sparar Playwright-spåret i en zip-fil. Kräver <see cref="SessionOptions.Trace"/>; annars returneras false och inget skrivs.</summary>
    public async Task<bool> SaveTraceAsync(string path)
    {
        if (!_tracing) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await _context.Tracing.StopAsync(new() { Path = path });
        return true;
    }

    public async ValueTask DisposeAsync() => await _context.CloseAsync();
}
