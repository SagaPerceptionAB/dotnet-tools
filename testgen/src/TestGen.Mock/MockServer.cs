using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi;
using TestGen.Core;
using TestGen.Core.Run;

namespace TestGen.Mock;

/// <summary>
/// En mock ur en OpenAPI-spec på Kestrel, bara på 127.0.0.1. Den svarar enligt specen, kontrollerar varje anrop mot specen
/// (400 med förklaring om det inte stämmer) och loggar det. En server per spec.
/// </summary>
public sealed class MockServer : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private const int MaxLoggedBody = 4000;

    private readonly WebApplication _app;
    private readonly OperationRoutes _routes;
    private readonly MockOptions _options;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _gate = new();
    private readonly List<MockCall> _calls = [];
    private readonly ConcurrentDictionary<string, Override> _overrides = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, JsonNode?> _generated = new(StringComparer.Ordinal);

    private sealed record Override(int Status, string? Body);

    private MockServer(string name, ApiSpec spec, MockOptions options)
    {
        Name = name;
        _options = options;
        _routes = new OperationRoutes(spec.Operations);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Listen(IPAddress.Loopback, options.Port);
            kestrel.Limits.MaxRequestBodySize = options.MaxBodyBytes;
        });
        _app = builder.Build();
        _app.Run(HandleAsync);
    }

    /// <summary>Namnet på specen: <c>info.title</c>, annars filnamnet.</summary>
    public string Name { get; }

    /// <summary>Adressen, till exempel <c>http://127.0.0.1:51234</c>.</summary>
    public string Url { get; private set; } = "";

    /// <summary>Anropen hittills, äldsta först.</summary>
    public IReadOnlyList<MockCall> Calls
    {
        get { lock (_gate) return _calls.ToArray(); }
    }

    /// <summary>Anropet har loggats. Körs på den tråd som tog emot anropet.</summary>
    public event Action<MockCall>? CallLogged;

    /// <summary>Läser specen (fil eller adress) och startar mocken.</summary>
    public static async Task<MockServer> StartAsync(string spec, MockOptions? options = null, CancellationToken ct = default)
    {
        var parsed = OpenApiLoader.LoadSpec(spec);
        return await StartAsync(parsed, parsed.Title ?? NameFromSource(spec), options, ct);
    }

    public static async Task<MockServer> StartAsync(ApiSpec spec, string name, MockOptions? options = null, CancellationToken ct = default)
    {
        var server = new MockServer(name, spec, options ?? new MockOptions());
        try
        {
            await server._app.StartAsync(ct);
            server.Url = server._app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            return server;
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }
    }

    /// <summary>Låter en operation svara med <paramref name="status"/> (och <paramref name="jsonBody"/>) tills <see cref="Reset"/>. Anropet kontrolleras fortfarande mot specen.</summary>
    public void Respond(string operationId, int status, string? jsonBody = null)
    {
        if (!_routes.All.Any(r => r.Operation.OperationId == operationId))
            throw new ArgumentException($"Specen har ingen operation som heter {operationId}. Operationer: {string.Join(", ", _routes.All.Select(r => r.Operation.OperationId).Distinct())}.", nameof(operationId));
        _overrides[operationId] = new Override(status, jsonBody);
    }

    /// <summary>Tömmer loggen och tar bort alla <see cref="Respond"/>.</summary>
    public void Reset()
    {
        _overrides.Clear();
        lock (_gate) _calls.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        try { await _app.StopAsync(); }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or InvalidOperationException) { /* redan stoppad */ }
        await _app.DisposeAsync();
    }

    private static string NameFromSource(string spec) =>
        Path.GetFileNameWithoutExtension(Uri.TryCreate(spec, UriKind.Absolute, out var uri) && uri.Scheme.StartsWith("http", StringComparison.Ordinal) ? uri.AbsolutePath : spec);

    // --- Anrop -----------------------------------------------------------------------------------

    private async Task HandleAsync(HttpContext context)
    {
        var request = context.Request;
        var path = request.Path.Value ?? "/";

        var (body, tooLarge) = await ReadBodyAsync(request, context.RequestAborted);
        if (tooLarge)
        {
            const string title = "Kroppen är för stor";
            await Finish(context, null, 413, Problem(413, title, $"Mocken tar emot högst {_options.MaxBodyBytes} byte."), [title], null);
            return;
        }

        if (_routes.Match(request.Method, path) is not { } match)
        {
            var detail = _routes.Explain(request.Method, path);
            await Finish(context, null, 404, Problem(404, "Finns inte i specen", detail), [detail], body);
            return;
        }

        var op = match.Route.Operation;
        var problems = RequestChecker.Check(op, match.Values, request, body);
        if (problems.Count > 0)
        {
            var errors = new JsonArray(problems.Select(p => (JsonNode)new JsonObject { ["field"] = p.Field.Length == 0 ? null : p.Field, ["message"] = p.Message }).ToArray());
            var detail = $"{op.OperationId}: {problems.Count} fel i anropet";
            await Finish(context, op, 400, Problem(400, "Anropet stämmer inte med specen", detail, errors), problems.Select(p => p.ToString()).ToList(), body);
            return;
        }

        var (status, json) = ResponseFor(op, match.Values, body);
        await Finish(context, op, status, json, [], body, "application/json");
    }

    private async Task<(string? Body, bool TooLarge)> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength > _options.MaxBodyBytes) return (null, true);
        try
        {
            using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer, ct);
            if (buffer.Length > _options.MaxBodyBytes) return (null, true);
            return (buffer.Length == 0 ? null : Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length), false);
        }
        catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return (null, true);
        }
    }

    // --- Svar ------------------------------------------------------------------------------------

    private (int Status, string? Json) ResponseFor(OperationInfo op, IReadOnlyDictionary<string, string> pathValues, string? requestBody)
    {
        var overridden = _overrides.TryGetValue(op.OperationId, out var o) ? o : null;
        var status = overridden?.Status ?? (op.SuccessStatuses.Count > 0 ? op.SuccessStatuses[0] : 200);
        if (overridden?.Body is { } raw) return (status, raw);
        if (op.ResponseSchemas is not { } schemas || !schemas.TryGetValue(status, out var schema)) return (status, null);

        var generated = _generated.GetOrAdd($"{op.OperationId}\0{status}", _ => Generate(op, schema));
        if (generated is not JsonObject obj) return (status, generated?.ToJsonString(JsonOptions));

        // Fält som också finns i anropet (kroppen och sökvägen) får anropets värden, så att en sida visar det användaren skrev.
        var copy = (JsonObject)obj.DeepClone();
        var (props, _) = SchemaUtil.Flatten(schema);
        if (!string.IsNullOrWhiteSpace(requestBody) && TryParseObject(requestBody) is { } sent)
            foreach (var (key, value) in sent)
                if (copy.ContainsKey(key) && value is not null) copy[key] = value.DeepClone();
        foreach (var (key, value) in pathValues)
            if (copy.ContainsKey(key) && props.TryGetValue(key, out var ps)) copy[key] = RequestChecker.Coerce([value], ps);
        return (status, copy.ToJsonString(JsonOptions));
    }

    private JsonNode? Generate(OperationInfo op, IOpenApiSchema schema)
    {
        try
        {
            // Frö per operation, så att svaret inte ändras när en annan operation läggs till eller tas bort.
            return new SchemaDataGenerator(Fnv.OperationSeed(op.OperationId, _options.Seed), "sv").GenerateField("", schema);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or FormatException or IndexOutOfRangeException or RegexMatchTimeoutException)
        {
            return null;   // t.ex. ett mönster som generatorn inte klarar: svaret blir tomt
        }
    }

    private static JsonObject? TryParseObject(string json)
    {
        try { return JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static string Problem(int status, string title, string detail, JsonArray? errors = null)
    {
        var problem = new JsonObject { ["type"] = "about:blank", ["title"] = title, ["status"] = status, ["detail"] = detail };
        if (errors is not null) problem["errors"] = errors;
        return problem.ToJsonString(JsonOptions);
    }

    private async Task Finish(HttpContext context, OperationInfo? op, int status, string? json, IReadOnlyList<string> problems, string? requestBody,
        string? contentType = null)
    {
        var request = context.Request;
        Log(new MockCall(_clock.ElapsedMilliseconds, request.Method, request.Path.Value ?? "/", MaskQuery(request.QueryString.Value),
            requestBody is null ? null : MaskBody(requestBody), op?.OperationId, status, problems));

        context.Response.StatusCode = status;
        if (json is null || status is 204 or 304) return;
        context.Response.ContentType = (contentType ?? "application/problem+json") + "; charset=utf-8";
        await context.Response.WriteAsync(json, Encoding.UTF8, context.RequestAborted);
    }

    // --- Logg ------------------------------------------------------------------------------------

    private void Log(MockCall call)
    {
        lock (_gate) _calls.Add(call);
        CallLogged?.Invoke(call);
    }

    private string MaskQuery(string? query)
    {
        if (string.IsNullOrEmpty(query)) return "";
        var pairs = QueryHelpers.ParseQuery(query).SelectMany(kv => kv.Value.Select(v => (kv.Key, Value: v ?? "")));
        return _options.Mask.Scrub(string.Join('&', pairs.Select(p => $"{p.Key}={_options.Mask.Value(p.Key, p.Value)}")));
    }

    private string MaskBody(string body)
    {
        var text = body;
        try
        {
            if (JsonNode.Parse(body) is { } node)
            {
                MaskNode(node);
                text = node.ToJsonString(JsonOptions);
            }
        }
        catch (JsonException) { /* ingen JSON: texten maskas som den är */ }
        return ResponseStore.Shorten(_options.Mask.Scrub(text), MaxLoggedBody);
    }

    private void MaskNode(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    if (_options.Mask.IsSecret(key) && obj[key] is JsonValue) obj[key] = HeaderMask.Hidden;
                    else if (obj[key] is { } child) MaskNode(child);
                }
                break;
            case JsonArray arr:
                foreach (var child in arr) if (child is not null) MaskNode(child);
                break;
        }
    }
}
