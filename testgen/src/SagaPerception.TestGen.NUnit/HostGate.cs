using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using TestGen.Core.Projects;
using TestGen.Core.Settings;
using TestGen.Mock;

namespace SagaPerception.TestGen.NUnit;

/// <summary>Ett utgående anrop från appen som värdpolicyn tog hand om.</summary>
internal sealed record HostCall(string Host, string Client, string Method, string Url, HostAction Action, int Status, string? Note = null)
{
    public string Text => $"{Method} {Url} → {Status} ({Action.ToString().ToLowerInvariant()}{(Client.Length > 0 ? $", klient {Client}" : "")}){(Note is null ? "" : ": " + Note)}";
}

/// <summary>
/// Värdpolicyn ur testgen.yaml för appens utgående anrop: <c>allow</c> släpper igenom, <c>block</c> (standard) svarar 502 med en förklaring
/// och <c>mock</c> skickar anropet till en mock ur en spec (<c>&lt;första delen av värdnamnet&gt;.openapi.yaml</c>). Anropen registreras.
/// </summary>
internal sealed class HostGate(HostPolicy policy, IReadOnlyList<string> specDirectories, int seed = 12345) : IAsyncDisposable
{
    private readonly List<HostCall> _calls = [];
    private readonly ConcurrentDictionary<string, Lazy<Task<MockServer?>>> _mocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _own = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpMessageInvoker _forward = new(new SocketsHttpHandler(), disposeHandler: true);

    /// <summary>Anropen hittills, i ordning.</summary>
    public IReadOnlyList<HostCall> Calls
    {
        get { lock (_calls) return _calls.ToArray(); }
    }

    /// <summary>Anrop till adressen (t.ex. en mock som testet själv startat) släpps alltid igenom och registreras inte. Anropas innan appen startar.</summary>
    public void AllowOwn(string url) => _own.Add(new Uri(url).Authority);

    /// <summary>Lägger en hanterare först i varje HttpClient som appen skapar med IHttpClientFactory.</summary>
    public void Register(IServiceCollection services) =>
        services.ConfigureAll<HttpClientFactoryOptions>(options =>
            options.HttpMessageHandlerBuilderActions.Add(builder => builder.AdditionalHandlers.Insert(0, new HostPolicyHandler(this, builder.Name ?? ""))));

    internal async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request, string client, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> pass, CancellationToken ct)
    {
        var uri = request.RequestUri ?? throw new InvalidOperationException("Anropet saknar adress.");
        if (_own.Contains(uri.Authority)) return await pass(request, ct);
        var action = policy.For(uri.Host);
        if (action == HostAction.Allow) return await pass(request, ct);

        var method = request.Method.Method;
        var shown = $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}";
        if (action == HostAction.Mock)
        {
            if (await MockFor(uri.Host) is { } mock)
            {
                request.RequestUri = new Uri(mock.Url.TrimEnd('/') + uri.PathAndQuery);
                var response = await _forward.SendAsync(request, ct);
                var problems = mock.Calls.Count > 0 ? mock.Calls[^1].Problems : [];
                Add(new HostCall(uri.Host, client, method, shown, action, (int)response.StatusCode, problems.Count == 0 ? null : string.Join("; ", problems)));
                return response;
            }
            return Problem(uri, client, method, shown, action,
                $"testgen kunde inte mocka anropet till {uri.Host}: ingen spec hittades. Gör så här: lägg {SpecName(uri.Host)}.openapi.yaml i samma mapp som testgen.yaml, " +
                $"eller sätt {uri.Host}: allow under hosts.");
        }

        return Problem(uri, client, method, shown, action,
            $"testgen blockerade anropet till {uri.Host} (värdpolicy block){(client.Length > 0 ? $" från klienten {client}" : "")}. " +
            $"Gör så här: sätt {uri.Host}: mock under hosts i testgen.yaml (svar ur en spec) eller {uri.Host}: allow (det riktiga systemet).");
    }

    private HttpResponseMessage Problem(Uri uri, string client, string method, string shown, HostAction action, string detail)
    {
        Add(new HostCall(uri.Host, client, method, shown, action, (int)HttpStatusCode.BadGateway));
        var body = JsonSerializer.Serialize(new { type = "about:blank", title = "Anropet stoppades av testgen", status = 502, detail },
            new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        return new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent(body, Encoding.UTF8, "application/problem+json") };
    }

    private void Add(HostCall call)
    {
        lock (_calls) _calls.Add(call);
    }

    private Task<MockServer?> MockFor(string host) =>
        _mocks.GetOrAdd(host, h => new Lazy<Task<MockServer?>>(async () =>
            FindSpec(h) is { } spec ? await MockServer.StartAsync(spec, new MockOptions { Seed = seed }) : null)).Value;

    private string? FindSpec(string host)
    {
        foreach (var dir in specDirectories)
            foreach (var extension in new[] { "yaml", "yml", "json" })
            {
                var path = Path.Combine(dir, $"{SpecName(host)}.openapi.{extension}");
                if (File.Exists(path)) return path;
            }
        return null;
    }

    /// <summary>"betaltjanst.example" blir "betaltjanst".</summary>
    internal static string SpecName(string host) => host.Split('.')[0].ToLowerInvariant();

    public async ValueTask DisposeAsync()
    {
        foreach (var mock in _mocks.Values)
            if (mock.IsValueCreated && await mock.Value is { } server)
                await server.DisposeAsync();
        _forward.Dispose();
    }
}

/// <summary>Skickar varje utgående anrop genom <see cref="HostGate"/>.</summary>
internal sealed class HostPolicyHandler(HostGate gate, string client) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        gate.HandleAsync(request, client, base.SendAsync, cancellationToken);
}
