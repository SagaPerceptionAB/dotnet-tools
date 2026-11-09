namespace TestGen.Core.Run;

/// <summary>
/// Vid 429 med <c>Retry-After</c> väntar den och skickar anropet en gång till, högst <see cref="MaxWait"/>. Utan huvudet, eller med en
/// längre väntetid, ges 429 vidare (testet hoppas över med orsaken rate-limited).
/// </summary>
public sealed class RateLimitHandler : DelegatingHandler
{
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(10);

    private readonly bool _disposeInner;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <param name="inner">Hanteraren som skickar anropet.</param>
    /// <param name="disposeInner">Om <paramref name="inner"/> ska göras ren när den här hanteraren görs ren.</param>
    /// <param name="delay">Väntan; tester byter ut den.</param>
    public RateLimitHandler(HttpMessageHandler inner, bool disposeInner = true, Func<TimeSpan, CancellationToken, Task>? delay = null) : base(inner)
    {
        _disposeInner = disposeInner;
        _delay = delay ?? Task.Delay;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        if ((int)response.StatusCode != 429 || WaitOf(response) is not { } wait || wait > MaxWait) return response;

        HttpRequestMessage retry;
        try { retry = await CloneAsync(request); }
        catch (ObjectDisposedException) { return response; }
        response.Dispose();
        await _delay(wait, cancellationToken);
        return await base.SendAsync(retry, cancellationToken);
    }

    /// <summary>Väntetiden Retry-After anger (sekunder eller klockslag), eller null om huvudet saknas.</summary>
    private static TimeSpan? WaitOf(HttpResponseMessage response)
    {
        var after = response.Headers.RetryAfter;
        if (after is null) return null;
        var wait = after.Delta ?? (after.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        return wait is null ? null : wait.Value < TimeSpan.Zero ? TimeSpan.Zero : wait;
    }

    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage request)
    {
        var copy = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version };
        foreach (var header in request.Headers) copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        if (request.Content is { } content)
        {
            copy.Content = new ByteArrayContent(await content.ReadAsByteArrayAsync());
            foreach (var header in content.Headers.Where(h => !h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))) copy.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        return copy;
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposeInner) base.Dispose(disposing);
    }
}
