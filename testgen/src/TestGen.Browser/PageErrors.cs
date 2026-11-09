using Microsoft.Playwright;

namespace TestGen.Browser;

public enum PageErrorKind { Console, Page, Response, RequestFailed }

/// <summary>Ett fel sidan gav upphov till. <see cref="Message"/> är en svensk förklaring.</summary>
public sealed record PageError(PageErrorKind Kind, string Message);

/// <summary>
/// Samlar konsolfel, sidfel (oupptäckta undantag), svar med status 400 eller högre och avbrutna nätverksanrop för en sida.
/// Ett fel räknas en gång: webbläsarens egen konsolrad om en resurs som inte kunde laddas ("Failed to load resource") hoppas över,
/// eftersom svaret eller nätverksfelet redan räknats.
/// </summary>
public sealed class PageErrors
{
    /// <summary>Resurser som inte styr hur sidan fungerar: en saknad bild, ikon, typsnitt eller mediefil (404) fäller inte testet.</summary>
    private static readonly HashSet<string> OptionalResourceTypes = ["image", "font", "media", "manifest"];

    private readonly object _gate = new();
    private readonly List<PageError> _errors = [];
    private readonly Dictionary<IRequest, int> _statuses = [];
    private readonly Func<IRequest, bool>? _extraIgnored;

    private PageErrors(Func<IRequest, bool>? isIgnored) => _extraIgnored = isIgnored;

    /// <summary>Börjar samla på sidan. <paramref name="isIgnored"/> pekar ut fler anrop vars fel inte ska räknas.</summary>
    public static PageErrors Attach(IPage page, Func<IRequest, bool>? isIgnored = null)
    {
        var errors = new PageErrors(isIgnored);
        page.Console += (_, message) =>
        {
            if (message.Type == "error" && !message.Text.StartsWith("Failed to load resource", StringComparison.Ordinal))
                errors.Add(PageErrorKind.Console, $"Konsolfel: {message.Text} ({message.Location})");
        };
        page.PageError += (_, error) => errors.Add(PageErrorKind.Page, $"Sidfel (oupptäckt undantag): {error}");
        page.Response += (_, response) =>
        {
            lock (errors._gate) errors._statuses[response.Request] = response.Status;
            if (response.Status >= 400 && !errors.IsIgnored(response.Request))
                errors.Add(PageErrorKind.Response, $"Svar {response.Status} på {response.Request.Method} {response.Url}");
        };
        // net::ERR_ABORTED är att webbläsaren själv avbröt anropet (t.ex. vid navigering): inget fel i appen.
        page.RequestFailed += (_, request) =>
        {
            if (request.Failure != "net::ERR_ABORTED" && !errors.IsIgnored(request))
                errors.Add(PageErrorKind.RequestFailed, $"Anropet misslyckades: {request.Method} {request.Url} ({request.Failure})");
        };
        return errors;
    }

    /// <summary>Felen hittills, i den ordning de kom.</summary>
    public IReadOnlyList<PageError> All
    {
        get { lock (_gate) return [.. _errors]; }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _errors.Clear();
            _statuses.Clear();
        }
    }

    /// <summary>Felen som text, en rad per fel. Tom om inga fel samlats.</summary>
    public string Describe() => string.Join(Environment.NewLine, All.Select(e => e.Message));

    private void Add(PageErrorKind kind, string message)
    {
        lock (_gate) _errors.Add(new PageError(kind, message));
    }

    /// <summary>/favicon.ico, som webbläsaren hämtar av sig själv, och bilder, typsnitt, mediefiler och manifest som saknas (404).</summary>
    private bool IsIgnored(IRequest request)
    {
        if (request.Url.Contains("/favicon.ico", StringComparison.OrdinalIgnoreCase)) return true;
        if (_extraIgnored?.Invoke(request) == true) return true;
        lock (_gate) return OptionalResourceTypes.Contains(request.ResourceType) && _statuses.TryGetValue(request, out var status) && status == 404;
    }
}
