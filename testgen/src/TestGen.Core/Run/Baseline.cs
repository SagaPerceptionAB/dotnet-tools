using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TestGen.Core.Run;

/// <summary>Ett test i jämförelsen med förra körningen.</summary>
/// <param name="Id">Testets id i den här körningen (T-018).</param>
/// <param name="Key">Den stabila nyckeln (se <see cref="TestKeys"/>).</param>
/// <param name="Title">Rubrik i klartext.</param>
/// <param name="Cause">Orsaken till FEL/HOPPAD nu (null om testet nu är OK).</param>
/// <param name="PreviousId">Id i förra körningen om det skiljer sig från <paramref name="Id"/>, annars null.</param>
public sealed record CompareItem(string Id, string Key, string Title, FailureCause? Cause, string? PreviousId = null)
{
    /// <summary>
    /// "T-018 Skapa order – quantity för lågt", "T-018 (var T-016) ..." om numret ändrats, och "[Testdata fanns redan]" sist när orsaken nu är osäker
    /// (testet kunde inte avgöra något den här gången, trots FEL).
    /// </summary>
    public string Text => (PreviousId is null ? $"{Id} {Title}" : $"{Id} (var {PreviousId}) {Title}")
                          + (Cause is { } c and not (FailureCause.System or FailureCause.Expectation) ? $" [{PlainLanguage.CauseName(c)}]" : "");
}

/// <summary>
/// Jämförelsen mellan den här körningen och en tidigare JSON-rapport (--baseline). Tester matchas på stabil nyckel, inte på T-id.
/// </summary>
/// <param name="File">Filen som jämförelsen gjordes mot (som angiven).</param>
/// <param name="StartedAt">När förra körningen startade (ISO 8601 ur filen), om den anges.</param>
/// <param name="Seed">Förra körningens frö, om det anges.</param>
/// <param name="New">FEL nu som inte var FEL förra gången (saknades, var OK eller HOPPAD).</param>
/// <param name="Fixed">FEL förra gången som nu är OK.</param>
/// <param name="Persisting">FEL både förra gången och nu.</param>
/// <param name="Unverified">Antal tester som var FEL förra gången men inte kördes eller hoppades över nu (säger inget om att de är lagade).</param>
public sealed record BaselineComparison(string File, string? StartedAt, int? Seed, IReadOnlyList<CompareItem> New,
    IReadOnlyList<CompareItem> Fixed, IReadOnlyList<CompareItem> Persisting, int Unverified)
{
    /// <summary>Raderna som visas i terminalen, HTML och Excel: etikett och innehåll.</summary>
    public IReadOnlyList<(string Label, string Text)> Rows => new[]
    {
        ("Nya FEL sedan förra körningen", Join(New)),
        ("Lagade sedan sist", Join(Fixed)),
        ("Kvar sedan tidigare", Join(Persisting)),
    };

    private static string Join(IReadOnlyList<CompareItem> items)
    {
        if (items.Count == 0) return "inga";
        var shown = items.Take(10).Select(i => i.Text);
        return string.Join("; ", shown) + (items.Count > 10 ? $" och {items.Count - 10} till" : "");
    }

    /// <summary>Rubriken ovanför raderna, t.ex. "Jämförelse med forra.json (körd 2026-11-01T21:51:43+01:00)".</summary>
    public string Heading => $"Jämförelse med {File}" + (StartedAt is null ? "" : $" (körd {StartedAt})");
}

/// <summary>
/// De stabila nycklarna som tester matchas på mellan körningar: <c>api:&lt;operationId&gt;:&lt;regel&gt;:&lt;fält&gt;</c> (t.ex. <c>api:createProduct:missing:name</c>)
/// och <c>api:&lt;operationId&gt;:excel:&lt;scenario&gt;:&lt;steg&gt;</c> för Excel. Saknas operationId används metod och normaliserad sökväg (<c>api:get/products/{id}:valid</c>).
/// </summary>
public static class TestKeys
{
    /// <summary>Nyckeln utan löpnummer. Hör samma test ihop får det samma nyckel även om T-id:t ändrats.</summary>
    public static string Base(string operation, string rule, string? field, string? scenario, int? step) =>
        rule == "excel"
            ? $"api:{operation}:excel:{scenario}:{step}"
            : field is null ? $"api:{operation}:{rule}" : $"api:{operation}:{rule}:{field}";

    /// <summary>operationId, annars metod och sökväg i gemener utan avslutande snedstreck (<c>post/orders</c>).</summary>
    public static string Operation(TestInfo info)
    {
        if (info.OperationId.Length > 0) return info.OperationId;
        var space = info.Call.IndexOf(' ');
        var path = space > 0 ? info.Call[(space + 1)..] : "";
        return (info.Method + "/" + path.Trim('/')).ToLowerInvariant().TrimEnd('/');
    }

    /// <summary>Gör nycklarna unika: ett andra test med samma nyckel får suffixet #2 (i körordning).</summary>
    public static List<string> Unique(IEnumerable<string> baseKeys)
    {
        var seen = new Dictionary<string, int>();
        var result = new List<string>();
        foreach (var k in baseKeys)
        {
            seen[k] = seen.GetValueOrDefault(k) + 1;
            result.Add(seen[k] == 1 ? k : $"{k}#{seen[k]}");
        }
        return result;
    }

    /// <summary>Regelkoden i JSON och i nyckeln: valid, missing, below-minimum, above-maximum, too-short, too-long, not-in-enum, wrong-type, pattern-mismatch, bad-format, empty, too-few-items, too-many-items, excel eller unknown.</summary>
    public static string RuleCode(TestInfo info) => info.Step is not null ? "excel" : info.Case is null ? "unknown" : info.Case.Rule switch
    {
        TestRule.Valid => "valid",
        TestRule.Missing => "missing",
        TestRule.BelowMinimum => "below-minimum",
        TestRule.AboveMaximum => "above-maximum",
        TestRule.TooLong => "too-long",
        TestRule.TooShort => "too-short",
        TestRule.NotInEnum => "not-in-enum",
        TestRule.WrongType => "wrong-type",
        TestRule.PatternMismatch => "pattern-mismatch",
        TestRule.BadFormat => "bad-format",
        TestRule.Empty => "empty",
        TestRule.TooFewItems => "too-few-items",
        TestRule.TooManyItems => "too-many-items",
        _ => "unknown",
    };

    public static string BaseOf(TestInfo info) =>
        info.Key ?? Base(Operation(info), RuleCode(info), info.Case?.Field, info.Step?.Scenario, info.Step?.Number);

    public static List<string> For(IEnumerable<TestInfo> infos) => Unique(infos.Select(BaseOf));
}

/// <summary>En tidigare körning inläst ur en JSON-rapport (--baseline).</summary>
public sealed class Baseline
{
    private sealed record Old(string Id, string Key, string Outcome);

    public string File { get; }
    public string? StartedAt { get; }
    public int? Seed { get; }
    private readonly Dictionary<string, Old> _byKey;

    private Baseline(string file, string? startedAt, int? seed, Dictionary<string, Old> byKey)
    {
        File = file; StartedAt = startedAt; Seed = seed; _byKey = byKey;
    }

    /// <summary>Läser rapporten. Kastar <see cref="UsageException"/> (exitkod 2) om filen saknas, inte är en JSON-rapport från testgen eller har en annan schemaVersion.</summary>
    public static Baseline Load(string path)
    {
        var full = Path.GetFullPath(path);
        if (!System.IO.File.Exists(full))
            throw new UsageException($"Hittar inte filen att jämföra med (--baseline): {full}",
                "Ange en JSON-rapport från en tidigare körning. Skapa en med --report resultat.json och spara den (t.ex. som artefakt i CI).");
        string text;
        try { text = System.IO.File.ReadAllText(full, Encoding.UTF8); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new UsageException($"Kan inte läsa filen att jämföra med (--baseline): {full}", "Kontrollera att filen finns och att du får läsa den.", e.Message);
        }
        JsonNode? root;
        try { root = JsonNode.Parse(text); }
        catch (JsonException e)
        {
            throw new UsageException($"{full} är ingen JSON-rapport från testgen (filen är inte giltig JSON).",
                "--baseline vill ha en rapport som skrevs med --report resultat.json. Ange rätt fil.", e.Message);
        }
        if (root is not JsonObject obj || obj["schemaVersion"] is null || obj["tests"] is not JsonArray tests)
            throw new UsageException($"{full} ser inte ut som en JSON-rapport från testgen (fälten schemaVersion och tests saknas).",
                "--baseline vill ha en rapport som skrevs med --report resultat.json (inte .html, .xlsx eller .xml). Ange rätt fil.");
        var version = obj["schemaVersion"]!.GetValueKind() == JsonValueKind.Number ? (int?)obj["schemaVersion"]!.GetValue<double>() : null;
        if (version != JsonResult.SchemaVersion)
            throw new UsageException($"{full} har schemaVersion {obj["schemaVersion"]}, men den här versionen av testgen läser schemaVersion {JsonResult.SchemaVersion}.",
                "Kör om den gamla körningen med den här versionen av testgen och jämför mot den nya rapporten, eller använd samma version av testgen i båda körningarna.");

        var byKey = new Dictionary<string, Old>();
        foreach (var t in tests.OfType<JsonObject>())
        {
            var key = Str(t, "key") ?? throw new UsageException($"{full} saknar nyckeln key på ett test.",
                "--baseline vill ha en rapport som skrevs med den här versionen av testgen (--report resultat.json).");
            byKey[key] = new Old(Str(t, "id") ?? "", key, Str(t, "outcome") ?? "");
        }
        return new Baseline(path, Str(obj, "startedAt"), obj["seed"] is JsonValue s && s.TryGetValue<int>(out var seed) ? seed : null, byKey);
    }

    private static string? Str(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>Jämför de färdiga testerna (i körordning) med förra körningen.</summary>
    public BaselineComparison Compare(IReadOnlyList<TestOutcome> current)
    {
        var keys = TestKeys.For(current.Select(o => o.Test));
        var newFailures = new List<CompareItem>();
        var fixedNow = new List<CompareItem>();
        var persisting = new List<CompareItem>();
        for (var i = 0; i < current.Count; i++)
        {
            var o = current[i];
            var key = keys[i];
            _byKey.TryGetValue(key, out var old);
            var prevId = old is not null && old.Id != o.Id && old.Id.Length > 0 ? old.Id : null;
            var item = new CompareItem(o.Id, key, o.Text.Title, o.Cause, prevId);
            if (o.Outcome == "FEL") (old?.Outcome == "failed" ? persisting : newFailures).Add(item);
            else if (o.Outcome == "OK" && old?.Outcome == "failed") fixedNow.Add(item);
        }
        var unverified = _byKey.Values.Count(v => v.Outcome == "failed" && !current.Where((o, i) => keys[i] == v.Key && o.Outcome == "OK").Any()
                                                  && !current.Where((o, i) => keys[i] == v.Key && o.Outcome == "FEL").Any());
        return new BaselineComparison(File, StartedAt, Seed, newFailures, fixedNow, persisting, unverified);
    }
}
