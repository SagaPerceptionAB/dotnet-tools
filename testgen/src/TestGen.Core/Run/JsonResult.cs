using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TestGen.Core.Run;

/// <summary>
/// Maskinläsbart resultat (--report resultat.json) för CI och för jämförelse mellan körningar (--baseline).
/// Formatet är fast och engelskt: nyckelnamn, orsakskoder och regelkoder är alltid samma ord, tal skrivs med punkt,
/// tider är ISO 8601 med förskjutning och varaktigheter är sekunder respektive millisekunder. Texter för människor
/// (verdict, title, explanation, hint) är på svenska och ska inte matchas mot. Schema: docs/ci/testgen-result.schema.json.
/// Alla texter går genom <see cref="HeaderMask.Scrub"/>.
/// </summary>
public static class JsonResult
{
    /// <summary>Ändras bara när formatet ändras så att äldre läsare går sönder.</summary>
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Serialize(JsonObject root) => root.ToJsonString(Pretty) + "\n";

    /// <summary>null utelämnas överallt i filen.</summary>
    private static void Prune(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var name in o.Where(kv => kv.Value is null).Select(kv => kv.Key).ToList()) o.Remove(name);
                foreach (var (_, child) in o) Prune(child);
                break;
            case JsonArray a:
                foreach (var child in a) Prune(child);
                break;
        }
    }

    /// <summary>Resultatet av ett test som ord för maskiner: passed, failed eller skipped (i texten OK, FEL, HOPPAD).</summary>
    public static string OutcomeCode(string outcome) => outcome switch { "OK" => "passed", "FEL" => "failed", _ => "skipped" };

    /// <summary>ISO 8601 med förskjutning, t.ex. 2026-11-01T21:51:43.120+01:00.</summary>
    public static string Iso(DateTimeOffset t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture);

    public static string Iso(DateTime local) => Iso(new DateTimeOffset(local));

    /// <summary>Sekunder med tre decimaler och punkt.</summary>
    public static double Seconds(TimeSpan t) => Math.Round(t.TotalSeconds, 3);

    public static JsonObject Build(ReportInfo info, IReadOnlyList<ReportEntry> entries, HeaderMask mask)
    {
        string? S(string? s) => s is null ? null : mask.Scrub(s);
        var outcomes = ReportWriter.Outcomes(entries, mask);
        var keys = TestKeys.For(entries.Select(e => e.Info ?? TestInfo.Basic(e.Id, e.Group, e.Call, e.Test)));
        var root = Header(info, mask);
        var exit = info.ExitCode ?? ExitPolicy.Decide(outcomes);
        root["totals"] = new JsonObject
        {
            ["tests"] = entries.Count,
            ["passed"] = entries.Count(e => e.Outcome == "OK"),
            ["failed"] = entries.Count(e => e.Outcome == "FEL"),
            ["skipped"] = entries.Count(e => e.Outcome == "HOPPAD"),
            ["warnings"] = entries.Sum(e => e.Result.Warnings.Count),
        };
        root["verdict"] = S(PlainLanguage.Verdict(outcomes, info.PossibleTotal, info.Partial));
        root["verdictKind"] = VerdictKinds.Of(outcomes);
        var causes = new JsonArray();
        foreach (var c in PlainLanguage.CauseSummary(outcomes))
        {
            var ids = new JsonArray();
            foreach (var id in outcomes.Where(o => (o.Cause ?? PlainLanguage.Cause(o.Test, o.Result)) == c.Cause).Select(o => o.Id)) ids.Add(id);
            causes.Add(new JsonObject { ["code"] = CauseCodes.Code(c.Cause), ["name"] = c.Name, ["count"] = c.Count, ["testIds"] = ids });
        }
        root["causes"] = causes;
        root["exitCode"] = exit;
        root["rerun"] = S(ReportWriter.RerunCommand(info));
        root["rerunFailed"] = S(ReportWriter.RerunFailedCommand(info, entries.Where(e => e.Outcome == "FEL")));
        root["comparison"] = info.Comparison is { } cmp ? Comparison(cmp) : null;

        var tests = new JsonArray();
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var t = e.Info ?? TestInfo.Basic(e.Id, e.Group, e.Call, e.Test);
            var plain = e.Plain(mask);
            var cause = plain.Cause;
            var failed = e.Outcome != "OK";
            var space = e.Call.IndexOf(' ');
            var expected = new JsonArray();
            foreach (var x in t.ExpectedStatus)
                expected.Add(x.From == x.To ? JsonValue.Create(x.From) : JsonValue.Create(x.ToString()));
            var curl = failed && ExchangeText.Curl(e.Result.Exchanges) is { } cu ? S(cu) : null;
            tests.Add(new JsonObject
            {
                ["id"] = e.Id,
                ["key"] = keys[i],
                ["group"] = S(e.Group),
                ["operationId"] = S(t.OperationId.Length == 0 ? null : t.OperationId),
                ["method"] = S(t.Method.Length > 0 ? t.Method : space > 0 ? e.Call[..space] : null),
                ["path"] = S(space > 0 ? e.Call[(space + 1)..] : e.Call),
                ["rule"] = TestKeys.RuleCode(t),
                ["field"] = S(t.Case?.Field),
                ["scenario"] = S(t.Step?.Scenario),
                ["step"] = t.Step?.Number,
                ["name"] = S(t.Description),
                ["title"] = S(plain.Title),
                ["outcome"] = OutcomeCode(e.Outcome),
                ["cause"] = cause is { } c ? CauseCodes.Code(c) : null,
                ["causeName"] = cause is { } c2 ? PlainLanguage.CauseName(c2) : null,
                ["expectedStatus"] = expected,
                ["actualStatus"] = e.Result.Status,
                ["setupStatus"] = e.Result.Setup?.Status,
                ["timedOut"] = e.Result.TimedOut,
                ["durationMs"] = e.Result.Exchanges.Sum(x => x.ElapsedMs),
                ["rootCauseTestId"] = e.Result.RootCauseTestId,
                ["explanation"] = failed ? S(plain.Explanation) : null,
                ["hint"] = failed ? S(plain.Hint) : null,
                ["warnings"] = Warnings(e.Result, mask),
                ["curl"] = curl,
                ["rerun"] = failed ? S(ReportWriter.RerunCommand(info, e.Id)) : null,
            });
        }
        root["tests"] = tests;
        Prune(root);
        return root;
    }

    private static JsonArray Warnings(CaseResult r, HeaderMask mask)
    {
        var a = new JsonArray();
        foreach (var w in r.Warnings) a.Add(mask.Scrub(PlainLanguage.WarningText(w)));
        return a;
    }

    /// <summary>Resultat för en körning som inte kunde genomföras (API:et svarade aldrig): inga tester, exitkod 3 och felet.</summary>
    public static JsonObject BuildFailure(ReportInfo info, string what, string action, int finishedTests, HeaderMask mask)
    {
        var root = Header(info, mask);
        root["totals"] = new JsonObject { ["tests"] = 0, ["passed"] = 0, ["failed"] = 0, ["skipped"] = 0, ["warnings"] = 0 };
        root["verdict"] = mask.Scrub("Körningen kunde inte genomföras. " + what);
        root["verdictKind"] = "unreachable";
        root["causes"] = new JsonArray();
        root["exitCode"] = ExitCodes.Unreachable;
        root["rerun"] = ReportWriter.RerunCommand(info) is { } rr ? mask.Scrub(rr) : null;
        root["error"] = new JsonObject { ["message"] = mask.Scrub(what), ["action"] = mask.Scrub(action), ["finishedTestsNotIncluded"] = finishedTests };
        root["tests"] = new JsonArray();
        Prune(root);
        return root;
    }

    private static JsonObject Header(ReportInfo info, HeaderMask mask)
    {
        string? S(string? s) => s is null ? null : mask.Scrub(s);
        var duration = info.Duration ?? TimeSpan.Zero;
        return new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["toolVersion"] = info.Version,
            ["layer"] = info.Layer,
            ["project"] = S(info.Project),
            ["startedAt"] = Iso(info.When),
            ["finishedAt"] = Iso(new DateTimeOffset(info.When) + duration),
            ["durationSeconds"] = Seconds(duration),
            ["api"] = S(info.Api),
            ["environment"] = S(info.Env),
            ["seed"] = info.Seed,
            ["seedRandom"] = info.SeedRandom,
            ["seedNote"] = S(info.SeedNote),
            ["spec"] = S(info.Spec),
            ["excel"] = S(info.Excel),
            ["timeoutSeconds"] = info.TimeoutSeconds,
            ["partial"] = info.Partial,
            ["possibleTotal"] = info.PossibleTotal,
            ["filterDescription"] = S(info.FilterDescription),
            ["aborted"] = info.AbortKind is not null,
            ["abortKind"] = info.AbortKind,
            ["notice"] = S(info.Notice),
        };
    }

    private static JsonObject Comparison(BaselineComparison c)
    {
        JsonArray Items(IReadOnlyList<CompareItem> items)
        {
            var a = new JsonArray();
            foreach (var i in items)
                a.Add(new JsonObject
                {
                    ["id"] = i.Id,
                    ["key"] = i.Key,
                    ["title"] = i.Title,
                    ["cause"] = i.Cause is { } cause ? CauseCodes.Code(cause) : null,
                    ["previousId"] = i.PreviousId,
                });
            return a;
        }
        return new JsonObject
        {
            ["baselineFile"] = c.File,
            ["baselineStartedAt"] = c.StartedAt,
            ["baselineSeed"] = c.Seed,
            ["newFailures"] = Items(c.New),
            ["fixed"] = Items(c.Fixed),
            ["persisting"] = Items(c.Persisting),
            ["notRerun"] = c.Unverified,
        };
    }

    public static string Text(ReportInfo info, IReadOnlyList<ReportEntry> entries, HeaderMask mask) => Serialize(Build(info, entries, mask));

    public static void WriteFile(string fullPath, string json) => File.WriteAllText(fullPath, json, new UTF8Encoding(false));
}
