using System.Text.Json.Nodes;
using TestGen.Core.Settings;

namespace TestGen.Core.Run;

/// <summary>Inloggningssteget (<c>login</c> i testgen.yaml) lyckades inte: vad som hände, vad man gör i testgen.yaml och det tekniska.</summary>
public sealed class LoginException(string what, string fix, string? technical = null) : Exception(what)
{
    public string Fix { get; } = fix;

    public string? Technical { get; } = technical;

    /// <summary>Tre delar, var och en på egen rad.</summary>
    public string Text => string.Join(Environment.NewLine, new[] { Message, "Gör så här: " + Fix, Technical is null ? null : "Tekniskt: " + Technical }.OfType<string>());
}

public sealed partial class TestRunner
{
    /// <summary>
    /// Anropar inloggningsoperationen en gång och ger token ur svaret. Anropet byggs som ett giltigt anrop ur specen, med värdena från
    /// <c>login.body</c> över de genererade. Kastar <see cref="LoginException"/> om operationen saknas, anropet nekas eller token inte finns i svaret.
    /// </summary>
    public async Task<string> LoginAsync(LoginSettings login, CancellationToken cancellationToken = default)
    {
        _ct = cancellationToken;
        var project = _opt.Project ?? "<api>";
        var where = $"apis.{project}.login";
        var spec = _opt.Prepared?.Spec ?? OpenApiLoader.LoadSpec(_opt.Spec);
        var ops = spec.Operations.ToList();
        _ops = ops.ToDictionary(o => o.OperationId);
        if (login.Operation is not { Length: > 0 } operation || !_ops.TryGetValue(operation, out var op))
        {
            var wanted = login.Operation ?? "";
            throw new LoginException($"Inloggningen använder operationen '{wanted}', men specen har ingen sådan.",
                $"Skriv operationId för inloggningen under {where}.operation i testgen.yaml.{Suggest.DidYouMean(wanted, _ops.Keys)}",
                "operationer i specen: " + Suggest.List(_ops.Keys.Order(StringComparer.Ordinal)));
        }
        if (login.Token is not { Length: > 0 } tokenPath)
            throw new LoginException("Inloggningen saknar var token finns i svaret.", $"Ange sökvägen under {where}.token i testgen.yaml, t.ex. accessToken.");
        if (op.UnsupportedBody is not null)
            throw new LoginException($"Inloggningsoperationen {operation} har en body som testgen inte kan bygga ({op.UnsupportedBody}).", $"Välj en annan operation under {where}.operation i testgen.yaml.");

        var cfg = Copy(_opt.Generator ?? new GeneratorOptions());
        if (login.Body is { Count: > 0 })
        {
            cfg.Overrides.TryGetValue(operation, out var existing);
            cfg.Overrides[operation] = new Dictionary<string, object>(existing ?? [], StringComparer.Ordinal);
            foreach (var (field, value) in login.Body) cfg.Overrides[operation][field] = value;
        }
        _gen = new ScenarioGenerator(ops, cfg);
        SetUpHeaders(spec);
        _http = CreateHttpClient();
        try
        {
            var ctx = new CaseContext { TestId = "login" };
            _saltBase = 0;
            _saltN = 0;
            int status;
            JsonNode? body;
            string raw;
            try { (status, body, raw) = await ExecuteAsync(ctx, GenerateFresh(op)); }
            catch (SetupFailedException e)
            {
                throw new LoginException($"Ett förberedande anrop till inloggningen ({e.Failure.Operation}) gav {e.Status}.",
                    $"Ge anropet värden som appen godtar under {where}.body i testgen.yaml.", $"{e.Message}");
            }
            catch (ReadOnlyException e) { throw new LoginException(e.Message, "Inloggningssteget kan inte köras i en skrivskyddad miljö."); }
            catch (TaskCanceledException) when (!_ct.IsCancellationRequested)
            {
                throw new LoginException($"Inloggningen ({operation}) fick inget svar inom {_opt.TimeoutSeconds} sekunder.", "Kontrollera att appen startar och svarar.");
            }
            catch (HttpRequestException e)
            {
                throw new LoginException($"Inloggningen ({operation}) kunde inte skickas.", "Kontrollera att appen startar och svarar.", _mask.Scrub(e.Message));
            }

            var sent = ctx.Exchanges.LastOrDefault();
            var call = sent is null ? $"{op.Method} {op.Path}" : $"{sent.Method} {new Uri(sent.Url).AbsolutePath}";
            if (status is < 200 or > 299)
                throw new LoginException($"Inloggningen ({operation}) gav {status}.",
                    $"Ge anropet värden som appen godtar under {where}.body i testgen.yaml. Appen måste också godta inloggningen i testprocessen, t.ex. med en inställning under apis.{project}.settings.",
                    _mask.Scrub($"{call}" + (sent?.RequestBody is { Length: > 0 } sentBody ? " " + Shorten(sentBody) : "") + $" -> {status}" + (raw.Length == 0 ? "" : ": " + Shorten(raw))));
            var token = TokenAt(body, tokenPath);
            if (token is null)
                throw new LoginException($"Svaret på inloggningen ({operation}) saknar token på sökvägen '{tokenPath}'.",
                    $"Ange var token finns i svaret under {where}.token i testgen.yaml, med punkt för nästlade fält (t.ex. data.accessToken).",
                    $"{call} -> {status}; fält i svaret: " + (body is JsonObject o ? Suggest.List(o.Select(kv => kv.Key)) : "(inget objekt)"));
            return token;
        }
        finally { _http.Dispose(); }
    }

    private static GeneratorOptions Copy(GeneratorOptions o) => new()
    {
        Seed = o.Seed,
        Locale = o.Locale,
        Bindings = o.Bindings,
        Overrides = o.Overrides.ToDictionary(kv => kv.Key, kv => kv.Value),
        SkipOperations = o.SkipOperations,
    };

    private static string Shorten(string text) => text.Length <= 300 ? text : text[..300] + "...";

    /// <summary>Värdet på sökvägen (fält åtskilda av punkt, heltal för listor) om det är en icke-tom text eller ett tal, annars null.</summary>
    internal static string? TokenAt(JsonNode? node, string path)
    {
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            node = node switch
            {
                JsonObject o => o.FirstOrDefault(kv => kv.Key.Equals(part, StringComparison.OrdinalIgnoreCase)).Value,
                JsonArray a when int.TryParse(part, out var i) && i >= 0 && i < a.Count => a[i],
                _ => null,
            };
            if (node is null) return null;
        }
        return node is JsonValue v && v.ToString() is { Length: > 0 } text ? text : null;
    }
}
