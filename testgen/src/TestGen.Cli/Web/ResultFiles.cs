using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TestGen.Core.Projects;
using TestGen.Core.Run;

namespace TestGen.Cli.Web;

/// <summary>En rad i listan över resultat: en körning i sidan eller en resultatfil från <c>dotnet test</c>.</summary>
internal sealed record ResultRow(string Id, string Source, string Layer, string Project, string? Started, int Passed, int Failed, int Skipped, bool Running);

/// <summary>
/// Resultatfilerna som <c>dotnet test</c> skriver (<c>TestResults/testgen/*.json</c> i testprojekten för testgen) och det sidan gör av dem.
/// Klienten får bara ogenomskinliga id: sökvägar kommer aldrig utifrån, och bara filer i testprojektens kataloger läses.
/// </summary>
internal static class ResultFiles
{
    public const long MaxBytes = 20 * 1024 * 1024;
    private const int MaxFiles = 200;


    /// <summary>Resultatfilerna för solutionen, med id. Ingen solution eller inga testprojekt ger en tom lista.</summary>
    public static IReadOnlyList<(string Id, string Path)> Discover(RepositoryFacts? facts)
    {
        var found = new List<(string, string)>();
        if (facts is null) return found;
        foreach (var project in facts.TestGenProjects)
        {
            var dir = Path.GetFullPath(Path.Combine(project.Directory, ReportWriter.ResultsDirectory));
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.json").Order(StringComparer.Ordinal).Take(MaxFiles))
            {
                var info = new FileInfo(file);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length > MaxBytes) continue;
                var relative = Terminal.Relative(file, facts.SolutionDirectory);
                found.Add((IdOf(relative), file));
            }
        }
        return found;
    }

    private static string IdOf(string relativePath) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(relativePath)))[..16].ToLowerInvariant();

    /// <summary>Filen som JSON, eller null om den inte är ett resultat från testgen (fel format, annan schemaVersion eller för stor).</summary>
    public static JsonObject? Read(string path)
    {
        try
        {
            if (new FileInfo(path).Length > MaxBytes) return null;
            if (JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) is not JsonObject root) return null;
            return root["schemaVersion"]?.GetValue<int>() == JsonResult.SchemaVersion && root["tests"] is JsonArray ? root : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    public static ResultRow Row(string id, string source, JsonObject json, string fallbackProject)
    {
        var totals = json["totals"] as JsonObject;
        return new ResultRow(id, source, Text(json, "layer") ?? "api", Text(json, "project") ?? fallbackProject, Text(json, "startedAt"),
            Count(totals, "passed"), Count(totals, "failed"), Count(totals, "skipped"), false);
    }

    private static int Count(JsonObject? totals, string name) => totals?[name] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0;

    private static string? Text(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static IEnumerable<JsonObject> Tests(JsonObject json) => json["tests"] is JsonArray a ? a.OfType<JsonObject>() : [];

    /// <summary>Det sidan behöver för att visa en resultatfil: korten och sammanfattningen, i samma form som under en körning.</summary>
    public static object Open(JsonObject json)
    {
        var tests = Tests(json).ToList();
        var results = tests.Select((t, i) =>
        {
            var cause = Text(t, "cause");
            var method = Text(t, "method") is { Length: > 0 } m ? m + " " : "";
            return new
            {
                index = i + 1,
                total = tests.Count,
                id = Text(t, "id") ?? "",
                group = Text(t, "group") ?? "",
                outcome = Text(t, "outcome") ?? "skipped",
                call = method + (Text(t, "path") ?? ""),
                title = Text(t, "title") ?? Text(t, "name") ?? "",
                explanation = Text(t, "explanation") ?? "",
                hint = Text(t, "hint"),
                technical = Array.Empty<string>(),
                curl = Text(t, "curl"),
                cause = Text(t, "causeName"),
                causeCode = cause,
                followsFrom = Text(t, "rootCauseTestId"),
                warnings = (t["warnings"] as JsonArray)?.Select(w => w?.GetValue<string>() ?? "").ToList() ?? [],
                conflict = cause == "test-data",
                chain = Array.Empty<object>(),
            };
        }).ToList();
        var totals = json["totals"] as JsonObject;
        var causes = (json["causes"] as JsonArray)?.OfType<JsonObject>().Select(c =>
        {
            var parsed = CauseCodes.TryParse(Text(c, "code") ?? "", out var known) ? known : (FailureCause?)null;
            return new
            {
                code = Text(c, "code"),
                name = Text(c, "name"),
                head = parsed is { } p ? PlainLanguage.CauseHead(p) : Text(c, "name"),
                tail = parsed is { } q ? PlainLanguage.CauseTail(q) : "",
                count = Count(c, "count"),
                ids = (c["testIds"] as JsonArray)?.Select(x => x?.GetValue<string>() ?? "").Take(10).ToList() ?? [],
            };
        }).ToList();
        return new
        {
            results,
            done = new
            {
                verdict = Text(json, "verdict"),
                summary = Text(json, "verdict") ?? "",
                causes,
                total = tests.Count,
                passed = Count(totals, "passed"),
                failed = Count(totals, "failed"),
                skipped = Count(totals, "skipped"),
                warnings = Count(totals, "warnings"),
                cancelled = json["aborted"]?.GetValue<bool>() ?? false,
                hasReport = false,
                partial = json["partial"]?.GetValue<bool>() ?? false,
                api = Text(json, "api") ?? "",
                env = Text(json, "environment"),
                seed = json["seed"] is JsonValue s && s.TryGetValue<int>(out var seed) ? seed : (int?)null,
                started = Text(json, "startedAt"),
                layer = Text(json, "layer") ?? "api",
                project = Text(json, "project"),
            },
        };
    }

    /// <summary>
    /// Vad som hänt mellan två körningar av samma lager, matchat på <c>key</c>: nya FEL, lagade och kvarstående.
    /// <c>notRerun</c> är FEL förut som inte kördes eller hoppades över nu.
    /// </summary>
    public static object Compare(JsonObject before, JsonObject after)
    {
        var old = Tests(before).Where(t => Text(t, "key") is not null).GroupBy(t => Text(t, "key")!).ToDictionary(g => g.Key, g => g.First());
        var now = Tests(after).Where(t => Text(t, "key") is not null).GroupBy(t => Text(t, "key")!).ToDictionary(g => g.Key, g => g.First());
        object Item(JsonObject t) => new { id = Text(t, "id"), key = Text(t, "key"), title = Text(t, "title") ?? Text(t, "name"), cause = Text(t, "causeName") };
        bool Failed(JsonObject? t) => t is not null && Text(t, "outcome") == "failed";
        bool Passed(JsonObject? t) => t is not null && Text(t, "outcome") == "passed";
        var ordered = now.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        var newFailures = ordered.Where(kv => Failed(kv.Value) && !Failed(old.GetValueOrDefault(kv.Key))).Select(kv => Item(kv.Value)).ToList();
        var persisting = ordered.Where(kv => Failed(kv.Value) && Failed(old.GetValueOrDefault(kv.Key))).Select(kv => Item(kv.Value)).ToList();
        var fixedNow = ordered.Where(kv => Passed(kv.Value) && Failed(old.GetValueOrDefault(kv.Key))).Select(kv => Item(kv.Value)).ToList();
        var notRerun = old.Count(kv => Failed(kv.Value) && !(now.TryGetValue(kv.Key, out var t) && (Passed(t) || Failed(t))));
        return new { newFailures, @fixed = fixedNow, persisting, notRerun };
    }
}
