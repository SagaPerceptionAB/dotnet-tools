using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using TestGen.Scenarios;

namespace Api.Tests;

/// <summary>Kör scenarier mot API:et. En ny instans per test (egna sparade svar).</summary>
public sealed partial class ScenarioRunner
{
    private readonly IAPIRequestContext _api;
    private readonly ResponseStore _store = new();
    private readonly HashSet<string> _inProgress = new();

    public ScenarioRunner(IAPIRequestContext api) => _api = api;

    /// <summary>Genererat scenario: setup, huvudanrop, jämför statuskod.</summary>
    public async Task RunAsync(Scenario scenario)
    {
        foreach (var op in scenario.Setup) await EnsureAsync(op);
        await EnsureDependenciesAsync(scenario);
        var (status, _) = await SendAsync(scenario, scenario.Body);
        if (scenario.ExpectedStatus.Count > 0)
            Assert.That(scenario.ExpectedStatus, Does.Contain(status),
                $"{scenario.Name}: {scenario.Method} {scenario.Path} gav status {status}, förväntade {string.Join("/", scenario.ExpectedStatus)}.");
    }

    /// <summary>Kör ett manuellt scenario steg för steg.</summary>
    public async Task RunAsync(ManualScenario manual)
    {
        for (var i = 0; i < manual.Steps.Count; i++)
        {
            var step = manual.Steps[i];
            var label = $"{manual.File} steg {i + 1} ({step.Call})";
            var baseScenario = ScenarioCatalog.FindValid(step.Call)
                ?? throw new InvalidOperationException(
                    $"{label}: inget genererat giltigt scenario för '{step.Call}' i scenarios/generated. Kör generatorn först.");
            var body = Merge(baseScenario.Body, step.Body);
            var scenario = baseScenario with { Body = body };
            await EnsureDependenciesAsync(scenario);
            var (status, response) = await SendAsync(scenario, body);

            if (step.ExpectStatus is { } exp)
                Assert.That(status, Is.EqualTo(exp), $"{label}: fel statuskod. Svar: {response?.ToJsonString()}");
            else
                Assert.That(status, Is.InRange(200, 299), $"{label}: förväntade 2xx. Svar: {response?.ToJsonString()}");
            if (step.ExpectBody is JsonObject expectBody)
            {
                var expected = expectBody.Select(kv => KeyValuePair.Create(kv.Key, _store.Substitute(kv.Value?.DeepClone()))).ToList();
                Assert.That(BodyMatcher.Mismatches(expected, response), Is.Empty, $"{label}: svaret stämmer inte.");
            }

            _store.Save(step.Save ?? step.Call, response);
        }
    }

    // --- internals -----------------------------------------------------------------------

    private async Task EnsureAsync(string operationId)
    {
        if (_store.Has(operationId)) return;
        if (!_inProgress.Add(operationId))
            throw new InvalidOperationException($"Cirkulärt beroende via '{operationId}'.");
        try
        {
            var s = ScenarioCatalog.FindValid(operationId)
                ?? throw new InvalidOperationException($"Setup '{operationId}' saknar genererat giltigt scenario.");
            foreach (var op in s.Setup) await EnsureAsync(op);
            await EnsureDependenciesAsync(s);
            var (status, response) = await SendAsync(s, s.Body);
            Assert.That(status, Is.InRange(200, 299),
                $"Setup '{operationId}' misslyckades med status {status}: {response?.ToJsonString()}");
            _store.Save(operationId, response);
        }
        finally { _inProgress.Remove(operationId); }
    }

    /// <summary>Kör operationer som scenariot refererar via ${op.fält} men som inte körts ännu.</summary>
    private async Task EnsureDependenciesAsync(Scenario s)
    {
        var text = string.Join("\n", new[] { s.Path, s.Body?.ToJsonString() ?? "" }
            .Concat(s.Query.Values).Concat(s.Headers.Values));
        foreach (var root in ResponseStore.ReferencedAliases(text))
            if (!_store.Has(root) && ScenarioCatalog.FindValid(root) is not null)
                await EnsureAsync(root);
    }

    private async Task<(int Status, JsonNode? Body)> SendAsync(Scenario s, JsonNode? rawBody)
    {
        var body = rawBody is null ? null : _store.Substitute(rawBody.DeepClone());
        var path = _store.SubstituteString(s.Path);
        var query = s.Query.ToDictionary(kv => kv.Key, kv => _store.SubstituteString(kv.Value));
        var headers = s.Headers.ToDictionary(kv => kv.Key, kv => _store.SubstituteString(kv.Value));

        // Sökvägsparametrar {x} hämtas från query eller body (och tas bort därifrån).
        foreach (Match m in PathParam().Matches(path).ToList())
        {
            var name = m.Groups[1].Value;
            string? val = null;
            if (query.Remove(name, out var q)) val = q;
            else if (body is JsonObject bo && bo.Remove(name, out var bv)) val = bv is JsonValue jv && jv.TryGetValue<string>(out var sv) ? sv : bv?.ToJsonString();
            if (val is not null) path = path.Replace(m.Value, Uri.EscapeDataString(val));
        }

        var opts = new APIRequestContextOptions { Method = s.Method.ToUpperInvariant(), Headers = headers, IgnoreHTTPSErrors = true };
        if (query.Count > 0) opts.Params = query.ToDictionary(kv => kv.Key, kv => (object)kv.Value);
        if (body is not null && opts.Method is not ("GET" or "HEAD"))
        {
            headers["Content-Type"] = "application/json";
            opts.Data = body.ToJsonString();
        }
        var resp = await _api.FetchAsync(path, opts);
        return (resp.Status, ResponseStore.ParseBody(await resp.TextAsync()));
    }

    [GeneratedRegex(@"\{([^{}$]+)\}")]
    private static partial Regex PathParam();

    /// <summary>Lägger override ovanpå bas: objekt slås ihop rekursivt, övriga värden ersätts.</summary>
    internal static JsonNode? Merge(JsonNode? baseNode, JsonNode? over)
    {
        if (over is null) return baseNode?.DeepClone();
        if (baseNode is JsonObject b && over is JsonObject o)
        {
            var result = (JsonObject)b.DeepClone();
            foreach (var kv in o) result[kv.Key] = Merge(result[kv.Key], kv.Value);
            return result;
        }
        return over.DeepClone();
    }
}
