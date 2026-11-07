using System.Net;
using System.Text;
using ClosedXML.Excel;

namespace TestGen.Core.Run;

/// <summary>Ett test i rapporten.</summary>
public sealed record ReportEntry(string Id, string Group, string Call, string Test, CaseResult Result)
{
    public string Outcome => Result.Skipped ? "HOPPAD" : Result.Ok ? "OK" : "FEL";

    /// <summary>Strukturerad info om testet (för klartexten). Saknas den förklaras testet utifrån den tekniska raden.</summary>
    public TestInfo? Info { get; init; }

    /// <summary>Rubrik, förklaring och tolkning i klartext.</summary>
    public PlainText Plain(HeaderMask mask) =>
        PlainLanguage.Explain(Info ?? TestInfo.Basic(Id, Group, Call, Test), Result, mask);
}

/// <summary>Allmänt om körningen: visas överst i rapporten.</summary>
public sealed record ReportInfo(DateTime When, string Api, string? Env, int Seed)
{
    /// <summary>Sökvägarna som användes (för "kör om"-kommandot). Saknas spec visas inget kommando.</summary>
    public string? Spec { get; init; }
    public string? Excel { get; init; }
    public string? Config { get; init; }
    /// <summary>Sant om en token användes; kommandot får då <c>--token '${env:TOKEN}'</c> (aldrig själva värdet).</summary>
    public bool UsesToken { get; init; }
    /// <summary>Sant om egna --header användes. Namnen finns nu i <see cref="RerunHeaders"/>; egenskapen finns kvar för äldre kod.</summary>
    public bool ExtraHeaders { get; init; }
    /// <summary>Alla tester som en full körning har (efter att config hoppat över operationer), även de som filtret valde bort.</summary>
    public int? PossibleTotal { get; init; }
    /// <summary>Sant om färre tester kördes än de möjliga (ett filter var angivet): en delkörning.</summary>
    public bool Partial { get; init; }
    /// <summary>Filtret i ord: "bara createProduct", "scenario Stamkund", "bara Excel-testerna", "T-020". Null utan filter.</summary>
    public string? FilterDescription { get; init; }
    /// <summary>Hur länge körningen tog (satt när körningen är slut).</summary>
    public TimeSpan? Duration { get; init; }
    public int? TimeoutSeconds { get; init; }
    /// <summary>testgens version (assemblyns version).</summary>
    public string? Version { get; init; }
    /// <summary>Sant om slumpfröet valdes av verktyget (--seed slumpa).</summary>
    public bool SeedRandom { get; init; }
    /// <summary>Filtren som gäller körningen, för "kör om"-kommandot.</summary>
    public string? Only { get; init; }
    public IReadOnlyList<string> Scenarios { get; init; } = Array.Empty<string>();
    public bool ExcelOnly { get; init; }
    public IReadOnlyList<string> TestIds { get; init; } = Array.Empty<string>();
    /// <summary>Egna --header som "Namn: värde". Hemliga värden står som ${env:NAMN}, aldrig som själva värdet.</summary>
    public IReadOnlyList<string> RerunHeaders { get; init; } = Array.Empty<string>();
    /// <summary>Notis om att körningen avbröts i förtid (t.ex. flera tidsgränser i rad). Null annars.</summary>
    public string? Notice { get; init; }
    /// <summary>Exitkoden körningen ger (0, 1, 3, 4, 130 eller 143); skrivs i JSON och JUnit. Null om den inte är känd.</summary>
    public int? ExitCode { get; init; }
    /// <summary>Jämförelse med en tidigare JSON-rapport (--jamfor), annars null.</summary>
    public BaselineComparison? Comparison { get; init; }
    /// <summary>Vad som hände med slumpfröet när det inte var ett vanligt tal (stort tal omgjort, --seed ci). Null annars.</summary>
    public string? SeedNote { get; init; }
    /// <summary>Varför körningen avbröts i förtid: "signal", "max-time" eller "timeouts". Null om den gick klart.</summary>
    public string? AbortKind { get; init; }
}

/// <summary>
/// Skriver rapport efter en körning som fristående HTML eller som Excel (typ efter filändelse).
/// All fri text går genom <see cref="HeaderMask.Scrub"/> så inget hemligt värde hamnar i rapporten.
/// </summary>
public static class ReportWriter
{
    /// <summary>Kastar ArgumentException om filändelsen inte är .html, .xlsx, .json, .xml eller .junit.xml. Anropas innan körningen startar.</summary>
    public static void Validate(string path)
    {
        if (Kind(path) is null)
            throw new ArgumentException($"--report: okänd rapporttyp för '{path}'. Använd filändelsen .html, .xlsx, .json eller .xml (JUnit; även .junit.xml), t.ex. --report resultat.html.");
    }

    /// <summary>
    /// Kontrollerar före körningen att rapportens katalog finns (eller går att skapa) och går att skriva till.
    /// Kastar <see cref="UsageException"/> på svenska annars. En befintlig rapportfil rörs inte.
    /// </summary>
    public static void EnsureWritable(string path)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(dir)) return;
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, $".testgen-skrivtest-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            if (Directory.Exists(full)) throw new IOException($"'{full}' är en katalog.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            throw new UsageException($"Rapporten kan inte skrivas till {full}: katalogen {dir} finns inte och går inte att skapa, eller saknar skrivrättigheter.",
                "Välj en annan plats för --report, t.ex. en katalog som du har skrivrättigheter i. Inget har körts än.", e.Message);
        }
    }

    /// <summary>Samma fil? Skiftlägesokänsligt på Windows, annars exakt. Båda sökvägarna normaliseras.</summary>
    public static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>
    /// Skyddar mot att rapporten skriver över något viktigt, innan körningen startar. Kastar <see cref="UsageException"/> (exit 2) om
    /// rapportens plats är samma som en indatafil (Excel, spec, config) eller om en annan rapport pekar på samma fil,
    /// och om en befintlig fil inte är en testgen-rapport (utom med <paramref name="force"/>).
    /// </summary>
    public static void CheckTargets(IReadOnlyList<string> reports, IEnumerable<(string Flag, string? Path)> inputs, bool force)
    {
        var ins = inputs.Where(i => i.Path is { Length: > 0 } p && !Sources.IsUrl(p)).ToList();
        for (var i = 0; i < reports.Count; i++)
        {
            var path = reports[i];
            var full = Path.GetFullPath(path);
            foreach (var (flag, p) in ins)
                if (SamePath(path, p!))
                    throw new UsageException($"Rapporten skulle skrivas över din egen fil: --report {path} är samma fil som {flag} ({full}).",
                        $"Välj ett annat namn på rapporten, t.ex. --report resultat{Path.GetExtension(path)}. Inget har körts och ingen fil har ändrats.");
            for (var j = 0; j < i; j++)
                if (SamePath(path, reports[j]))
                    throw new UsageException($"--report anges två gånger för samma fil: {full}.", "Välj olika filnamn för varje --report, t.ex. --report resultat.html --report resultat.xlsx.");
            if (!force && File.Exists(full) && !LooksLikeReport(full))
                throw new UsageException($"Filen {full} finns redan och ser inte ut som en rapport från testgen. Den skulle skrivas över.",
                    "Välj ett annat namn på --report, eller lägg till --force om du vill skriva över filen.");
        }
    }

    /// <summary>En testgen-rapport: Excel med bladet Sammanfattning, HTML med rubriken Testrapport, JSON med schemaVersion eller JUnit-XML från testgen. Filer som inte går att läsa räknas inte som rapporter.</summary>
    private static bool LooksLikeReport(string full)
    {
        try
        {
            if (Kind(full) == "xlsx")
            {
                using var wb = new XLWorkbook(full);
                return wb.Worksheets.Contains("Sammanfattning");
            }
            if (Kind(full) is "html" or "json" or "junit")
            {
                using var rd = new StreamReader(full, Encoding.UTF8);
                var buf = new char[4096];
                var n = rd.Read(buf, 0, buf.Length);
                var head = new string(buf, 0, n);
                return Kind(full) switch
                {
                    "html" => head.Contains("<title>Testrapport", StringComparison.Ordinal),
                    "json" => head.Contains("\"schemaVersion\"", StringComparison.Ordinal) && head.Contains("\"testgenVersion\"", StringComparison.Ordinal),
                    _ => head.Contains("<testsuites name=\"testgen\"", StringComparison.Ordinal),
                };
            }
            return false;
        }
        catch (Exception) { return false; }
    }

    /// <summary>Skriver en rapport (html/xlsx efter ändelsen) som säger att körningen inte kunde genomföras, och varför.</summary>
    public static string WriteFailure(string path, ReportInfo info, string what, string action, int finishedTests, HeaderMask mask)
    {
        Validate(path);
        var full = Path.GetFullPath(path);
        if (Path.GetDirectoryName(full) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
        if (Kind(path) == "json") { JsonResult.WriteFile(full, JsonResult.Serialize(JsonResult.BuildFailure(info, what, action, finishedTests, mask))); return full; }
        if (Kind(path) == "junit") { File.WriteAllText(full, JUnitResult.TextFailure(info, what, action, mask), new UTF8Encoding(false)); return full; }
        const string headline = "Körningen kunde inte genomföras";
        var extra = finishedTests > 0 ? $"{finishedTests} test hann bli klara innan körningen avbröts; de ingår inte i den här rapporten." : "Inget test hann köras.";
        if (Kind(path) == "html")
        {
            string H(string s) => WebUtility.HtmlEncode(mask.Scrub(s));
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html lang=\"sv\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
            sb.AppendLine("<title>Testrapport</title><style>" + Css + "</style></head><body><main>");
            sb.AppendLine("<h1>Testrapport</h1>");
            sb.AppendLine($"<p class=\"meta\">Försökte köra {H(HtmlStamp(info.When))} mot <strong>{H(info.Api)}</strong>" +
                          (info.Env is null ? "" : $" (miljö: {H(info.Env)})") + $", seed {info.Seed}.</p>");
            sb.AppendLine($"<div class=\"ruta fel\">{headline}</div>");
            sb.AppendLine($"<p class=\"klartext\">{H(what)}</p>");
            sb.AppendLine($"<p class=\"tolkning\"><strong>Gör så här:</strong> {H(action)}</p>");
            sb.AppendLine($"<p>{H(extra)}</p>");
            sb.AppendLine("</main></body></html>");
            File.WriteAllText(full, sb.ToString(), new UTF8Encoding(false));
        }
        else
        {
            using var wb = new XLWorkbook();
            wb.Properties.Title = $"Testresultat {info.When:yyyy-MM-dd}";
            var sum = wb.AddWorksheet("Sammanfattning");
            var rows = new (string, string)[]
            {
                ("Resultat", headline),
                ("Vad hände", what),
                ("Gör så här", action),
                ("Antal tester som hann bli klara", finishedTests.ToString()),
                ("Datum", Stamp(info.When)),
                ("Adress", info.Api),
                ("Miljö", info.Env ?? ""),
                ("Seed", info.Seed.ToString()),
            };
            for (var i = 0; i < rows.Length; i++) { Put(sum, i + 1, 1, rows[i].Item1, mask); Put(sum, i + 1, 2, rows[i].Item2, mask); }
            sum.Column(1).Style.Font.Bold = true;
            sum.Columns(1, 2).AdjustToContents();
            wb.SaveAs(full);
        }
        return full;
    }

    private static string? Kind(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "html",
        ".xlsx" => "xlsx",
        ".json" => "json",
        ".xml" => "junit",   // även .junit.xml
        _ => null,
    };

    /// <summary>Skriver rapporten (skapar katalogen vid behov) och returnerar full sökväg.</summary>
    public static string Write(string path, ReportInfo info, IReadOnlyList<ReportEntry> entries, HeaderMask mask)
    {
        Validate(path);
        var full = Path.GetFullPath(path);
        if (Path.GetDirectoryName(full) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
        switch (Kind(path))
        {
            case "html": File.WriteAllText(full, Html(info, entries, mask), new UTF8Encoding(false)); break;
            case "json": JsonResult.WriteFile(full, JsonResult.Text(info, entries, mask)); break;
            case "junit": File.WriteAllText(full, JUnitResult.Text(info, entries, mask), new UTF8Encoding(false)); break;
            default: Excel(full, info, entries, mask); break;
        }
        return full;
    }

    private static string Resultat(ReportEntry e) => e.Outcome;

    /// <summary>Rapportposterna som <see cref="TestOutcome"/>, så att orsakssammanfattningen och beslutsmeningen kan byggas ur samma källa som terminalen och webbgränssnittet.</summary>
    public static List<TestOutcome> Outcomes(IReadOnlyList<ReportEntry> entries, HeaderMask mask) =>
        entries.Select((e, i) => new TestOutcome(i + 1, entries.Count, e.Info ?? TestInfo.Basic(e.Id, e.Group, e.Call, e.Test), e.Result,
            e.Plain(mask), e.Outcome, Array.Empty<string>(), null)).ToList();

    /// <summary>Anropsdetaljerna för ett test som rader: verdict, detaljrader och anrop/svar (utan curl).</summary>
    public static List<string> DetailLines(ReportEntry e, HeaderMask mask, bool withTiming = true)
    {
        var lines = new List<string>();
        if (e.Outcome != "OK" && e.Result.Verdict != e.Outcome) lines.Add(e.Result.Verdict);
        lines.AddRange(e.Result.Details);
        lines.AddRange(ExchangeText.Describe(e.Result.Exchanges, withTiming, full: e.Outcome == "FEL"));
        return lines.Select(mask.Scrub).ToList();
    }

    /// <summary>Etiketten står på en egen kommentarsrad, så att curl-raderna kan kopieras och köras som de är.</summary>
    private static string Labelled(string curl) => "# curl för anropet (kopiera raderna nedan):\n" + curl;

    /// <summary>Bash-skriptet som återskapar ett FEL-test mot ett tomt system, eller tom text.</summary>
    private static string ReproOf(ReportEntry e, HeaderMask mask) =>
        e.Outcome == "FEL" && ExchangeText.ReproScript(e.Result.Exchanges, e.Plain(mask).Title) is { } s ? mask.Scrub(s) : "";

    /// <summary>Tidpunkt med tidszon, t.ex. "2026-11-01 16:41 (UTC+1)".</summary>
    public static string Stamp(DateTime when)
    {
        var off = TimeZoneInfo.Local.GetUtcOffset(when);
        var zone = off == TimeSpan.Zero ? "UTC"
            : $"UTC{(off < TimeSpan.Zero ? "-" : "+")}{Math.Abs(off.Hours)}{(off.Minutes != 0 ? ":" + Math.Abs(off.Minutes).ToString("00") : "")}";
        return $"{when:yyyy-MM-dd HH:mm} ({zone})";
    }

    /// <summary>Tidpunkt för HTML-rapporten i lokal tid med tydlig förskjutning, t.ex. "2026-11-01 18:38 (UTC+01:00)".</summary>
    public static string HtmlStamp(DateTime when)
    {
        var off = TimeZoneInfo.Local.GetUtcOffset(when);
        return $"{when:yyyy-MM-dd HH:mm} (UTC{(off < TimeSpan.Zero ? "-" : "+")}{Math.Abs(off.Hours):00}:{Math.Abs(off.Minutes):00})";
    }

    private static string Sh(string s) => s.Length > 0 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '/' or '_' or '-' or ':' or '=' or '+' or ',' or '@' or '%')
        ? s : CurlCommand.Quote(s);

    /// <summary>
    /// Det kommando kör-om-raderna börjar med: <c>testgen</c> när processen är det installerade verktyget eller en publicerad binär
    /// (programfilen heter testgen eller testgen.exe), annars <c>./testgen.sh</c>. På Windows utan installerat verktyg: testgen.cmd (se <see cref="RerunNotes"/>).
    /// </summary>
    public static string RerunProgram => RerunProgramFor(Environment.ProcessPath);

    /// <summary>Se <see cref="RerunProgram"/>; <paramref name="processPath"/> är sökvägen till den körande programfilen.</summary>
    public static string RerunProgramFor(string? processPath) =>
        processPath is not null && Path.GetFileName(processPath) is "testgen" or "testgen.exe" ? "testgen" : "./testgen.sh";

    /// <summary>
    /// Färdigt kommando för att köra om samma körning (samma urval, seed, timeout och headers), eller bara ett test med
    /// <paramref name="testId"/> (då med --test, som behåller testets id och testdata). Null om specens sökväg inte är känd.
    /// Innehåller aldrig ett tokenvärde eller ett hemligt header-värde.
    /// </summary>
    public static string? RerunCommand(ReportInfo info, string? testId = null) =>
        BuildRerun(info, testId is null ? null : new[] { testId });

    /// <summary>Ett kommando som kör om bara de misslyckade testerna (--test för varje id). Null om inget kan köras om.</summary>
    public static string? RerunFailedCommand(ReportInfo info, IEnumerable<ReportEntry> failed)
    {
        var ids = failed.Select(e => e.Id).Where(i => i.Length > 0).Distinct().ToList();
        return ids.Count == 0 ? null : BuildRerun(info, ids);
    }

    private static string? BuildRerun(ReportInfo info, IReadOnlyList<string>? tests)
    {
        if (info.Spec is null) return null;
        var sb = new StringBuilder($"{RerunProgram} run --spec {Sh(info.Spec)} --api {Sh(info.Api)}");
        if (info.Excel is not null) sb.Append($" --excel {Sh(info.Excel)}");
        if (info.Env is not null) sb.Append($" --env {Sh(info.Env)}");
        if (info.Config is not null) sb.Append($" --config {Sh(info.Config)}");
        sb.Append($" --seed {info.Seed}");
        if (info.UsesToken && info.Env is null) sb.Append(" --token '${env:TOKEN}'");
        foreach (var h in info.RerunHeaders) sb.Append($" --header {Sh(h)}");
        if (info.TimeoutSeconds is { } t && t != RunSettings.DefaultTimeoutSeconds) sb.Append($" --timeout {t}");
        if (tests is not null)
            foreach (var id in tests) sb.Append($" --test {Sh(id)}");   // --test behåller id och testdata, så inga andra filter behövs
        else
        {
            if (info.ExcelOnly) sb.Append(" --bara-excel");
            foreach (var sc in info.Scenarios) sb.Append($" --scenario {Sh(sc)}");
            if (info.Only is not null) sb.Append($" --only {Sh(info.Only)}");
            foreach (var id in info.TestIds) sb.Append($" --test {Sh(id)}");
        }
        return sb.ToString();
    }

    /// <summary>Anmärkningar som hör till "kör om"-kommandona.</summary>
    private static List<string> RerunNotes(ReportInfo info)
    {
        var notes = new List<string>();
        if (RerunProgram != "testgen") notes.Add("(Windows: testgen.cmd, eller dotnet run --project testgen/src/TestGen.Cli -- …)");
        var vars = new List<string>();
        if (info.UsesToken && info.Env is null) vars.Add("TOKEN");
        foreach (var h in info.RerunHeaders) vars.AddRange(EnvVars.Names(h));
        vars = vars.Distinct().ToList();
        if (vars.Count == 1 && vars[0] == "TOKEN") notes.Add("Kommandot innehåller ingen token. Sätt den först: export TOKEN=<din token>");
        else if (vars.Count > 0) notes.Add("Kommandot innehåller inga hemliga värden. Sätt först: " + string.Join(" ", vars.Select(v => $"export {v}=<värde>")));
        notes.Add("Med --test T-020 körs bara det testet, med samma id och samma testdata som i den här körningen.");
        return notes;
    }

    /// <summary>Tid som "2,1 s" (alltid ett decimaltecken, komma).</summary>
    public static string FormatDuration(TimeSpan t) => t.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',') + " s";

    /// <summary>"Delkörning: bara createProduct (11 av 34 möjliga tester)" eller null om körningen inte var en delkörning.</summary>
    public static string? PartialLine(ReportInfo info, int ran) =>
        info.Partial && info.PossibleTotal is { } all
            ? $"Delkörning: {info.FilterDescription ?? "urval"} ({ran} av {all} möjliga tester)"
            : null;

    private static void AppendRerunHtml(StringBuilder sb, ReportInfo info, List<ReportEntry> failed, Func<string, string> H)
    {
        sb.AppendLine("<section class=\"koromsektion\">");
        sb.AppendLine("<h2>Kör om</h2>");
        sb.AppendLine($"<p>Vill du köra samma tester igen i webbgränssnittet: använd slumpfrö {info.Seed}. Då får testerna samma testdata.</p>");
        if (RerunCommand(info) is { } all)
        {
            sb.AppendLine("<h3>För dig som kör testgen i terminalen</h3>");
            sb.AppendLine(info.Partial
                ? "<p>Samma körning igen (samma urval), med samma slumpfrö (seed):</p>"
                : "<p>Hela körningen igen, med samma slumpfrö (seed):</p>");
            sb.AppendLine($"<pre tabindex=\"0\">{H(all)}</pre>");
            if (RerunFailedCommand(info, failed) is { } each)
            {
                sb.AppendLine("<p>Bara testerna som har FEL (samma id och samma testdata som nu):</p>");
                sb.AppendLine($"<pre tabindex=\"0\">{H(each)}</pre>");
            }
            foreach (var n in RerunNotes(info)) sb.AppendLine($"<p>{H(n)}</p>");
        }
        sb.AppendLine("</section>");
    }

    private static string CurlOf(ReportEntry e, HeaderMask mask) =>
        ExchangeText.Curl(e.Result.Exchanges) is { } c ? mask.Scrub(c) : "";

    private static (int Ok, int Fail, int Skipped) Count(IReadOnlyList<ReportEntry> entries) =>
        (entries.Count(e => e.Outcome == "OK"), entries.Count(e => e.Outcome == "FEL"), entries.Count(e => e.Outcome == "HOPPAD"));

    // --- HTML ---------------------------------------------------------------------------

    public static string Html(ReportInfo info, IReadOnlyList<ReportEntry> entries, HeaderMask mask)
    {
        string H(string s) => WebUtility.HtmlEncode(mask.Scrub(s));
        var (ok, fail, skipped) = Count(entries);
        var sb = new StringBuilder();
        var outcomes = Outcomes(entries, mask);
        var title = $"Testrapport – {PlainLanguage.VerdictShort(outcomes)} – {info.When:yyyy-MM-dd}";
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"sv\"><head><meta charset=\"utf-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.AppendLine($"<title>{H(title)}</title>");
        sb.AppendLine("<style>" + Css + "</style></head><body>");
        sb.AppendLine("<header>");
        sb.AppendLine("<h1>Testrapport</h1>");
        sb.AppendLine("<p class=\"meta\">" +
                      $"Körd {H(HtmlStamp(info.When))} mot <strong>{H(info.Api)}</strong>" +
                      (info.Env is null ? "" : $" (miljö: {H(info.Env)})") +
                      $", seed {info.Seed}.</p>");
        var facts = new List<string>();
        if (info.Duration is { } dur) facts.Add($"tid {FormatDuration(dur)}");
        if (info.TimeoutSeconds is { } to) facts.Add($"timeout {to} s per anrop");
        if (info.Version is { } ver) facts.Add($"testgen {ver}");
        if (info.SeedRandom) facts.Add("slumpfröet valdes av verktyget");
        if (facts.Count > 0) sb.AppendLine($"<p class=\"meta\">{H(string.Join(", ", facts))}.</p>");
        sb.AppendLine("</header>");
        sb.AppendLine("<main>");
        // Beslutsmeningen först, sedan orsakerna (med länkar till testerna), sedan noteringar.
        sb.AppendLine($"<p class=\"klartext\">{H(PlainLanguage.Verdict(outcomes))}</p>");
        var causes = PlainLanguage.CauseSummary(outcomes);
        if (causes.Count > 0)
        {
            sb.AppendLine("<ul class=\"orsaker\" aria-label=\"Orsaker\">");
            foreach (var c in causes)
            {
                var links = string.Join(", ", c.TestIds.Select(id => $"<a href=\"#{H(id)}\">{H(id)}</a>")) + (c.More > 0 ? $" och {c.More} till" : "");
                sb.AppendLine($"<li class=\"cause-{CauseCodes.Code(c.Cause)}\"><strong>{H(PlainLanguage.CauseHead(c.Cause))}:</strong> {links}{H(PlainLanguage.CauseTail(c.Cause))}</li>");
            }
            sb.AppendLine("</ul>");
        }
        if (PartialLine(info, entries.Count) is { } partialLine)
            sb.AppendLine($"<p class=\"tolkning\"><strong>{H(partialLine)}.</strong> De andra testerna kördes inte.</p>");
        if (info.Notice is { } notice) sb.AppendLine($"<p class=\"tolkning\"><strong>Obs:</strong> {H(notice)}</p>");
        if (info.Comparison is { } cmp)
        {
            sb.AppendLine($"<h2>{H(cmp.Heading)}</h2><ul class=\"jamforelse\">");
            foreach (var (label, text) in cmp.Rows) sb.AppendLine($"<li><strong>{H(label)}:</strong> {H(text)}</li>");
            sb.AppendLine("</ul>");
        }
        var results = entries.Select(e => e.Result).ToList();
        if (PlainLanguage.WarningSummary(PlainLanguage.WarningCount(results)) is { } warnSum)
            sb.AppendLine($"<p class=\"varning\"><strong>Varningar:</strong> {H(warnSum)}.</p>");
        if (PlainLanguage.EnvironmentNote(results) is { } envNote)
            sb.AppendLine($"<p class=\"tolkning\"><strong>Miljö:</strong> {H(envNote)}</p>");
        if (PlainLanguage.AuthNote(results) is { } authNote)
            sb.AppendLine($"<p class=\"tolkning\"><strong>Inloggning:</strong> {H(authNote)}</p>");
        sb.AppendLine("<div class=\"sammanfattning\" role=\"group\" aria-label=\"Antal tester per resultat\">");
        sb.AppendLine($"<div class=\"ruta ok\"><span class=\"tal\">{ok}</span> OK</div>");
        sb.AppendLine($"<div class=\"ruta fel\"><span class=\"tal\">{fail}</span> FEL</div>");
        sb.AppendLine($"<div class=\"ruta hoppad\"><span class=\"tal\">{skipped}</span> HOPPAD</div>");
        sb.AppendLine("</div>");

        var plain = entries.ToDictionary(e => e.Id, e => e.Plain(mask));
        if (fail > 0)
        {
            sb.AppendLine("<h2>Tester som visar ett problem</h2><ul class=\"felista\">");
            foreach (var e in entries.Where(e => e.Outcome == "FEL"))
                sb.AppendLine($"<li><a href=\"#{H(e.Id)}\">{H(e.Id)} – {H(plain[e.Id].Title)}</a></li>");
            sb.AppendLine("</ul>");
        }

        sb.AppendLine($"<p>{entries.Count} tester. Känsliga värden är utbytta mot <code>***</code>. I curl-raderna står en variabel (t.ex. <code>$TOKEN</code>) i stället; sätt den med <code>export</code> innan du kör raden.</p>");

        // Knappen visas bara med JavaScript; utan JavaScript syns alla tester.
        sb.AppendLine("<div class=\"verktyg\" id=\"verktyg\" hidden>");
        sb.AppendLine("<button type=\"button\" id=\"bara-fel\" aria-pressed=\"false\">Visa bara FEL <span class=\"lage\" aria-hidden=\"true\"><span class=\"av\">○ Av</span><span class=\"pa\">✓ På</span></span></button>");
        sb.AppendLine("<span id=\"filterstatus\" class=\"endast-skarmlasare\" role=\"status\"></span>");
        sb.AppendLine("<span class=\"filtertext\">Visar bara testerna med FEL.</span>");
        sb.AppendLine("</div>");

        var gi = 0;
        foreach (var group in entries.GroupBy(e => e.Group))
        {
            gi++;
            var hasFail = group.Any(e => e.Outcome == "FEL");
            sb.AppendLine($"<section class=\"grupp{(hasFail ? "" : " utan-fel")}\" aria-labelledby=\"grupp-{gi}\">");
            sb.AppendLine($"<h2 id=\"grupp-{gi}\">{H(group.Key)}</h2>");
            foreach (var e in group)
            {
                var cls = e.Outcome switch { "OK" => "ok", "FEL" => "fel", _ => "hoppad" };
                var t = plain[e.Id];
                sb.AppendLine($"<section class=\"test {cls}\" id=\"{H(e.Id)}\">");
                sb.AppendLine($"<div class=\"rubrik\"><span class=\"id\">{H(e.Id)}</span> <span class=\"etikett {cls}\">{e.Outcome}</span> <h3>{H(t.Title)}</h3></div>");
                sb.AppendLine($"<p class=\"forklaring\">{H(t.Explanation)}</p>");
                if (t.Hint is not null) sb.AppendLine($"<p class=\"tolkning\"><strong>Tolkning:</strong> {H(t.Hint)}</p>");
                foreach (var w in e.Result.Warnings)
                    sb.AppendLine($"<p class=\"varning\"><strong>Varning:</strong> {H(PlainLanguage.WarningText(w))}</p>");
                if (PlainLanguage.CauseLabel(t.Cause) is { } causeLabel)
                {
                    var follow = e.Result.RootCauseTestId is { } root && entries.Any(x => x.Id == root)
                        ? $". Följd av <a href=\"#{H(root)}\">{H(root)}</a>" : PlainLanguage.FollowLabel(e.Result) is { } f ? ". " + H(f) : "";
                    sb.AppendLine($"<p class=\"orsak\"><strong>{H(causeLabel)}</strong>{follow}</p>");
                }
                var lines = DetailLines(e, mask);
                var curl = CurlOf(e, mask);
                sb.AppendLine($"<details><summary>Tekniska detaljer<span class=\"endast-skarmlasare\"> för {H(e.Id)}</span></summary>");
                sb.AppendLine("<table class=\"teknik\"><thead><tr><th>Anrop</th><th>Test</th><th>Status</th><th>Resultat</th><th>Orsak</th></tr></thead>" +
                              $"<tbody><tr><td data-label=\"Anrop\">{H(e.Call)}</td><td data-label=\"Test\">{H(e.Test)}</td><td data-label=\"Status\">{(e.Result.Status?.ToString() ?? "-")}</td><td data-label=\"Resultat\">{e.Outcome}</td><td data-label=\"Orsak\">{(t.Cause is { } tc ? H(PlainLanguage.CauseName(tc)) : "–")}</td></tr></tbody></table>");
                var text = string.Join("\n", lines) + (curl.Length > 0 ? "\n" + Labelled(curl) : "");
                if (text.Trim().Length > 0) sb.AppendLine($"<pre tabindex=\"0\">{H(text)}</pre>");
                if (ReproOf(e, mask) is { Length: > 0 } repro)
                {
                    sb.AppendLine($"<h4>Återskapa felet (bash)</h4>");
                    sb.AppendLine("<p>Skriptet kör anropen i samma ordning mot ett tomt system. Sätt <code>BASE</code> till adressen till ditt API (och <code>TOKEN</code> om testet använde en token).</p>");
                    sb.AppendLine($"<pre tabindex=\"0\">{H(repro)}</pre>");
                }
                sb.AppendLine("</details>");
                sb.AppendLine("</section>");
            }
            sb.AppendLine("</section>");
        }
        AppendRerunHtml(sb, info, entries.Where(e => e.Outcome == "FEL").ToList(), H);
        sb.AppendLine("</main>");
        sb.AppendLine("<script>" + Script + "</script>");
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    private const string Script = """
        (function(){
          var b=document.getElementById('bara-fel'),v=document.getElementById('verktyg'),
              st=document.getElementById('filterstatus');
          if(!b||!v)return;
          v.hidden=false;
          b.addEventListener('click',function(){
            var on=b.getAttribute('aria-pressed')!=='true';
            b.setAttribute('aria-pressed',on?'true':'false');
            document.body.classList.toggle('bara-fel',on);
            var n=document.querySelectorAll('section.test.fel').length;
            st.textContent=on?('Visar bara FEL. '+n+' test visas.'):'Visar alla tester.';
          });
          function open(all){var d=document.querySelectorAll('details');
            for(var i=0;i<d.length;i++){if(all){d[i].setAttribute('data-var',d[i].open?'1':'0');d[i].open=true;}
              else if(d[i].getAttribute('data-var')==='0'){d[i].open=false;}}}
          window.addEventListener('beforeprint',function(){open(true);});
          window.addEventListener('afterprint',function(){open(false);});
        })();
        """;

    private const string Css = """
        body{font-family:system-ui,-apple-system,"Segoe UI",sans-serif;margin:0;background:#f5f6f8;color:#1d2330;line-height:1.5}
        header,main{max-width:1100px;margin:0 auto;padding:24px}
        header{padding-bottom:0}
        h1{margin:0 0 4px}h2{margin-top:32px}
        .meta{color:#4a5468;overflow-wrap:anywhere}
        .sammanfattning{display:flex;gap:16px;margin:16px 0;flex-wrap:wrap}
        .ruta{flex:1;min-width:140px;padding:16px;border-radius:8px;font-size:1.1rem;font-weight:600;border:2px solid}
        .ruta .tal{display:block;font-size:2.4rem}
        .ruta.ok{background:#e3f4e6;border-color:#2e7d32;color:#1b5e20}
        .ruta.fel{background:#fde2e2;border-color:#c62828;color:#8e1b1b}
        .ruta.hoppad{background:#eceff3;border-color:#6b7587;color:#3e4756}
        .orsaker{padding-left:20px;margin:8px 0}
        .orsaker li{margin:4px 0;overflow-wrap:anywhere}
        .orsak{margin:8px 0;font-weight:600}
        .varning{margin:8px 0;color:#4a5468;font-size:.95rem;overflow-wrap:anywhere}
        .felista{padding-left:20px}
        .felista li{margin:4px 0}
        .felista a{display:inline-block;min-height:24px;padding:2px 0;overflow-wrap:anywhere}
        a{color:#0645ad}
        section.test{background:#fff;border:1px solid #dde1e8;border-left:6px solid #2e7d32;border-radius:8px;padding:12px 16px;margin:12px 0}
        section.test.fel{border-left-color:#c62828;background:#fff6f6}
        section.test.hoppad{border-left-color:#6b7587}
        .rubrik{display:flex;align-items:baseline;gap:10px;flex-wrap:wrap}
        .rubrik h3{margin:0;font-size:1.1rem;overflow-wrap:anywhere}
        .id{font-family:ui-monospace,monospace;color:#4a5468;white-space:nowrap}
        .etikett{font-size:.8rem;font-weight:700;padding:2px 8px;border-radius:10px;white-space:nowrap}
        .etikett.ok{background:#e3f4e6;color:#1b5e20}
        .etikett.fel{background:#fde2e2;color:#8e1b1b}
        .etikett.hoppad{background:#eceff3;color:#3e4756}
        .klartext{font-size:1.25rem;font-weight:600}
        .forklaring{margin:8px 0;overflow-wrap:anywhere}
        .tolkning{margin:8px 0;padding:8px 12px;background:#fff3d6;border-radius:6px}
        table.teknik{border-collapse:collapse;margin:8px 0;font-size:.9rem;max-width:100%}
        th,td{text-align:left;padding:4px 10px;border-bottom:1px solid #dde1e8;vertical-align:top;overflow-wrap:anywhere}
        th{background:#e8ebf0}
        summary{cursor:pointer;color:#34508a;display:list-item;min-height:24px;padding:4px 0}
        pre{white-space:pre-wrap;overflow-wrap:anywhere;word-break:break-all;max-width:100%;box-sizing:border-box;overflow-x:auto;background:#1d2330;color:#e8ebf0;padding:10px;border-radius:6px;font-size:.85rem}
        code{overflow-wrap:anywhere}
        .endast-skarmlasare{position:absolute;width:1px;height:1px;margin:-1px;padding:0;overflow:hidden;clip:rect(0,0,0,0);white-space:nowrap;border:0}
        .verktyg{margin:24px 0 8px}
        .verktyg[hidden]{display:none}
        #bara-fel{font:inherit;min-height:44px;padding:8px 16px;border:2px solid #34508a;border-radius:6px;background:#fff;color:#1d2330;cursor:pointer}
        #bara-fel .pa{display:none}
        #bara-fel[aria-pressed=true]{background:#34508a;color:#fff}
        #bara-fel[aria-pressed=true] .pa{display:inline}
        #bara-fel[aria-pressed=true] .av{display:none}
        .filtertext{display:none;margin-left:8px;font-weight:600}
        body.bara-fel .filtertext{display:inline}
        body.bara-fel section.test.ok,body.bara-fel section.test.hoppad,body.bara-fel .grupp.utan-fel{display:none}
        :focus-visible{outline:3px solid #0b2f7a;outline-offset:2px}
        #bara-fel[aria-pressed=true]:focus-visible{box-shadow:0 0 0 2px #fff}
        @media (max-width:480px){
          header,main{padding:16px}
          table.teknik,table.teknik tbody,table.teknik tr,table.teknik td{display:block;width:100%;box-sizing:border-box}
          table.teknik thead{position:absolute;width:1px;height:1px;overflow:hidden;clip:rect(0,0,0,0)}
          table.teknik td::before{content:attr(data-label) ": ";font-weight:700}
        }
        @media print{
          body{background:#fff}
          .verktyg,#bara-fel,script{display:none!important}
          details::details-content{content-visibility:visible;display:block}
          section.test{break-inside:avoid}
          a{color:#000}
        }
        """;

    // --- Excel --------------------------------------------------------------------------

    private const string SumSheet = "Sammanfattning";
    private const string ResSheet = "Resultat";
    private const string TableName = "Testresultat";

    // Färger (alla text/bakgrund-par har kontrast minst 4,5:1).
    private static readonly XLColor HeaderFill = XLColor.FromHtml("#1F3864");     // vit text: 12:1
    private static readonly XLColor FailFill = XLColor.FromHtml("#F4B6B6");       // text 5A0000: 9:1
    private static readonly XLColor FailText = XLColor.FromHtml("#5A0000");
    private static readonly XLColor FailRowFill = XLColor.FromHtml("#FDE2E2");
    private static readonly XLColor OkFill = XLColor.FromHtml("#E6F3E8");         // text 14532D: 8:1
    private static readonly XLColor OkText = XLColor.FromHtml("#14532D");
    private static readonly XLColor SkipFill = XLColor.FromHtml("#ECEFF3");       // text 3E4756: 8:1
    private static readonly XLColor SkipText = XLColor.FromHtml("#3E4756");
    private static readonly XLColor LinkColor = XLColor.FromHtml("#0645AD");      // på vitt: 7,7:1

    private static readonly string[] ResultHeads =
    {
        "Id", "Resultat", "Vad testet gör", "Förklaring", "Anrop", "Förväntad status", "Faktisk status", "Tid (ms)",
        "Orsak", "Följd av", "Grupp", "Detaljer", "curl", "Svar (hela)", "Återskapa (bash)",
    };
    private const int ColId = 1, ColResult = 2, ColWhat = 3, ColExplain = 4, ColCall = 5, ColExpected = 6, ColActual = 7, ColTime = 8,
        ColCause = 9, ColFollow = 10, ColGroup = 11, ColDetails = 12, ColCurl = 13, ColFull = 14, ColRepro = 15;
    /// <summary>Svaret i kolumnen "Svar (hela)" kortas först efter så här många tecken.</summary>
    public const int FullResponseMax = 30000;
    private const int CellMax = 32000;

    public static void Excel(string fullPath, ReportInfo info, IReadOnlyList<ReportEntry> entries, HeaderMask mask)
    {
        using var wb = new XLWorkbook();
        wb.Properties.Title = $"Testresultat {info.When:yyyy-MM-dd}";
        wb.Properties.Subject = "Resultat av en testkörning";
        var sum = wb.AddWorksheet(SumSheet);   // först, och öppnas först
        var ws = wb.AddWorksheet(ResSheet);

        // --- Resultat: en riktig Excel-tabell ---
        for (var c = 0; c < ResultHeads.Length; c++) ws.Cell(1, c + 1).Value = ResultHeads[c];
        var row = 2;
        var rowOf = new Dictionary<string, int>();
        foreach (var e in entries)
        {
            rowOf[e.Id] = row;
            var t = e.Plain(mask);
            var fail = e.Outcome == "FEL";
            Put(ws, row, ColId, e.Id, mask);
            Put(ws, row, ColResult, e.Outcome, mask);
            Put(ws, row, ColWhat, t.Title, mask);
            Put(ws, row, ColExplain, t.Explanation + (t.Hint is null ? "" : "\n\nTolkning: " + t.Hint), mask);
            Put(ws, row, ColCall, e.Call, mask);
            if (ExpectedStatus(e) is { } exp)
            {
                if (exp is int n) ws.Cell(row, ColExpected).SetValue(n); else ws.Cell(row, ColExpected).SetValue((string)exp);
            }
            if (e.Result.Status is { } st) ws.Cell(row, ColActual).SetValue(st);
            if (ExchangeText.Main(e.Result.Exchanges) is { } main) ws.Cell(row, ColTime).SetValue(main.ElapsedMs);
            if (t.Cause is { } cause) Put(ws, row, ColCause, PlainLanguage.CauseName(cause), mask);
            if (e.Result.RootCauseTestId is { } root)
            {
                Put(ws, row, ColFollow, root, mask);
                if (rowOf.TryGetValue(root, out var rootRow))
                {
                    ws.Cell(row, ColFollow).SetHyperlink(new XLHyperlink($"'{ResSheet}'!A{rootRow}"));
                    ws.Cell(row, ColFollow).Style.Font.FontColor = LinkColor;
                    ws.Cell(row, ColFollow).Style.Font.Underline = XLFontUnderlineValues.Single;
                }
            }
            Put(ws, row, ColGroup, e.Group, mask);

            var full = fail && ExchangeText.Main(e.Result.Exchanges) is { Status: not null, ResponseBody.Length: > 0 } m ? ExchangeText.Pretty(m.ResponseBody!) : null;
            var details = DetailLines(e, mask, withTiming: false);
            // Varningar från svarskontrollen står först i Detaljer (också för OK-rader) så att de syns utan att öppna något annat.
            details.InsertRange(0, e.Result.Warnings.Select(w => mask.Scrub("Varning (påverkar inte resultatet): " + PlainLanguage.WarningText(w))));
            // Rader som lyckades behöver inte alla headers och bodies här (de finns i curl-kolumnen): håll raderna låga.
            if (!fail) details = details.Where(l => l.Length > 0 && !char.IsWhiteSpace(l[0])).ToList();
            if (full is not null)
                details = details.Select(l => l.Contains("... (avkortat") ? l + " – hela svaret finns i kolumnen Svar (hela)" : l).ToList();
            Put(ws, row, ColDetails, string.Join("\n", details), mask);
            Put(ws, row, ColCurl, CurlOf(e, mask), mask);
            if (full is not null) Put(ws, row, ColFull, full, mask, FullResponseMax);
            if (ReproOf(e, mask) is { Length: > 0 } repro) Put(ws, row, ColRepro, repro, mask);

            var r = ws.Cell(row, ColResult);
            switch (e.Outcome)
            {
                case "FEL":
                    ws.Range(row, 1, row, ResultHeads.Length).Style.Fill.BackgroundColor = FailRowFill;
                    r.Style.Font.Bold = true;
                    r.Style.Font.FontColor = FailText;
                    r.Style.Fill.BackgroundColor = FailFill;
                    r.Style.Border.OutsideBorder = XLBorderStyleValues.Medium;
                    r.Style.Border.OutsideBorderColor = FailText;
                    ws.Cell(row, ColId).Style.Font.Bold = true;
                    break;
                case "OK":
                    r.Style.Font.FontColor = OkText;
                    r.Style.Fill.BackgroundColor = OkFill;
                    break;
                default:
                    r.Style.Font.FontColor = SkipText;
                    r.Style.Font.Italic = true;
                    r.Style.Fill.BackgroundColor = SkipFill;
                    break;
            }
            row++;
        }

        var last = Math.Max(row - 1, 1);
        if (entries.Count > 0)
        {
            var table = ws.Range(1, 1, last, ResultHeads.Length).CreateTable(TableName);
            table.Theme = XLTableTheme.TableStyleLight1;   // diskret; rubrikfärg sätts uttryckligen nedan
            table.ShowRowStripes = false;
            table.ShowAutoFilter = true;
        }
        var head = ws.Range(1, 1, 1, ResultHeads.Length);
        head.Style.Font.Bold = true;
        head.Style.Font.FontColor = XLColor.White;
        head.Style.Fill.BackgroundColor = HeaderFill;
        head.Style.Alignment.WrapText = true;
        head.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

        double[] widths = { 8, 11, 40, 60, 30, 12, 12, 10, 26, 11, 22, 70, 90, 70, 90 };
        for (var c = 0; c < widths.Length; c++) ws.Column(c + 1).Width = widths[c];
        var body = ws.Range(2, 1, last, ResultHeads.Length);
        body.Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
        // Radbrytning på alla textkolumner. "Svar (hela)" bryts inte: ett långt svar skulle annars göra raden metervis hög
        // (hela texten ligger i cellen och syns i formelfältet).
        foreach (var c in new[] { ColId, ColResult, ColWhat, ColExplain, ColCall, ColCause, ColGroup, ColDetails, ColCurl, ColRepro })
            ws.Range(2, c, last, c).Style.Alignment.WrapText = true;
        ws.Range(2, ColExpected, last, ColTime).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        ws.SheetView.FreezeRows(1);
        ws.SheetView.FreezeColumns(1);

        // Utskrift: A4 liggande, en sida bred, rubrikraden upprepas. Id–Följd av skrivs ut (Detaljer, curl och hela svaret blir för breda).
        ws.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        ws.PageSetup.PaperSize = XLPaperSize.A4Paper;
        ws.PageSetup.FitToPages(1, 0);
        ws.PageSetup.SetRowsToRepeatAtTop(1, 1);
        ws.PageSetup.PrintAreas.Add(1, 1, last, ColFollow);
        ws.PageSetup.Footer.Center.AddText("Sida &P av &N");

        BuildSummary(sum, info, entries, mask, rowOf);

        sum.SetTabActive();
        sum.TabSelected = true;
        ws.TabSelected = false;
        wb.SaveAs(fullPath);
    }

    /// <summary>Förväntad status som tal när den är exakt en kod; annars (t.ex. 2xx eller flera) som text. Null om okänd.</summary>
    private static object? ExpectedStatus(ReportEntry e)
    {
        var list = e.Info?.ExpectedStatus;
        if (list is null || list.Count == 0) return null;
        if (list.Count == 1 && list[0].From == list[0].To) return list[0].From;
        return string.Join(", ", list.Select(x => x.ToString()));
    }

    private static void BuildSummary(IXLWorksheet sum, ReportInfo info, IReadOnlyList<ReportEntry> entries, HeaderMask mask,
        Dictionary<string, int> rowOf)
    {
        var (ok, fail, skipped) = Count(entries);
        var outcomes = Outcomes(entries, mask);
        sum.Column(1).Width = 26;
        sum.Column(2).Width = 60;
        sum.Column(3).Width = 70;
        sum.Column(4).Width = 70;
        sum.Column(5).Width = 30;
        var r = 1;

        // Beslutsmeningen först ("gick det bra, och var ligger felet?"), sedan orsakerna.
        var verdict = PlainLanguage.Verdict(outcomes);
        var head = sum.Cell(r, 1);
        Put(sum, r, 1, verdict, mask);
        var merged = sum.Range(r, 1, r, 4).Merge();
        merged.Style.Alignment.WrapText = true;
        merged.Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
        head.Style.Font.Bold = true;
        head.Style.Font.FontSize = 16;
        head.Style.Font.FontColor = fail > 0 ? FailText : OkText;
        sum.Row(r).Height = 26 * Math.Max(1, (int)Math.Ceiling(verdict.Length / 100.0));
        r += 2;

        void Label(string text)
        {
            Put(sum, r, 1, text, mask);
            sum.Cell(r, 1).Style.Font.Bold = true;
            sum.Cell(r, 1).Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
            sum.Cell(r, 1).Style.Alignment.WrapText = true;
        }
        void Number(string label, int n, XLColor? fill = null, XLColor? text = null)
        {
            Label(label);
            var c = sum.Cell(r, 2);
            c.SetValue(n);
            c.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
            c.Style.Font.Bold = true;
            if (fill is not null) { c.Style.Fill.BackgroundColor = fill; c.Style.Font.FontColor = text; }
            r++;
        }
        void Pair(string label, string value)
        {
            Label(label);
            Put(sum, r, 2, value, mask);
            sum.Cell(r, 2).Style.Alignment.WrapText = true;
            sum.Cell(r, 2).Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
            r++;
        }

        var causes = PlainLanguage.CauseSummary(outcomes);
        foreach (var c in causes)
            Pair(PlainLanguage.CauseHead(c.Cause), c.IdsText + PlainLanguage.CauseTail(c.Cause));
        if (causes.Count > 0) r++;

        Number("OK", ok, ok > 0 ? OkFill : null, OkText);
        Number("FEL", fail, fail > 0 ? FailFill : null, FailText);
        Number("HOPPAD", skipped, skipped > 0 ? SkipFill : null, SkipText);
        Number("Antal tester", entries.Count);
        r++;
        var results = entries.Select(e => e.Result).ToList();
        if (PlainLanguage.WarningSummary(PlainLanguage.WarningCount(results)) is { } warnSum) Pair("Varningar", warnSum + ". Se kolumnen Detaljer.");
        if (PlainLanguage.EnvironmentNote(results) is { } env) Pair("Miljö svarar inte", env);
        if (PlainLanguage.AuthNote(results) is { } auth) Pair("Inloggning", auth);
        if (PartialLine(info, entries.Count) is { } partialLine) Pair("Delkörning", partialLine["Delkörning: ".Length..]);
        if (info.Notice is { } notice) Pair("Notis", notice);
        if (info.Comparison is { } cmp)
        {
            Pair("Jämförelse med", cmp.File + (cmp.StartedAt is null ? "" : $" (körd {cmp.StartedAt})"));
            foreach (var (label, text) in cmp.Rows) Pair(label, text);
        }
        Pair("Datum", Stamp(info.When));
        if (info.Duration is { } dur) Pair("Tid", FormatDuration(dur));
        Pair("Adress", info.Api);
        if (!string.IsNullOrWhiteSpace(info.Env)) Pair("Miljö", info.Env!);
        Pair("Seed", info.Seed.ToString() + (info.SeedRandom ? " (valdes av verktyget)" : ""));
        if (info.TimeoutSeconds is { } to) Pair("Timeout", $"{to} s per anrop");
        if (info.Version is { } ver) Pair("testgen-version", ver);
        Pair("Obs", "Känsliga värden (t.ex. token) skrivs aldrig i rapporten. I kolumnen curl står en variabel (t.ex. $TOKEN) i stället; sätt den med export innan du kör raden.");
        r++;

        // Tester med FEL, med länk till raden i Resultat.
        var failed = entries.Where(e => e.Outcome == "FEL").ToList();
        if (failed.Count > 0)
        {
            Put(sum, r, 1, "Tester med FEL", mask);
            sum.Cell(r, 1).Style.Font.Bold = true;
            sum.Cell(r, 1).Style.Font.FontSize = 13;
            r++;
            var hdr = r;
            string[] heads = { "Id", "Vad testet gör", "Förklaring", "Kör om bara detta", "Orsak" };
            for (var c = 0; c < heads.Length; c++) sum.Cell(r, c + 1).Value = heads[c];
            var hr = sum.Range(r, 1, r, heads.Length);
            hr.Style.Font.Bold = true;
            hr.Style.Font.FontColor = XLColor.White;
            hr.Style.Fill.BackgroundColor = HeaderFill;
            r++;
            foreach (var e in failed)
            {
                var t = e.Plain(mask);
                var id = sum.Cell(r, 1);
                Put(sum, r, 1, e.Id, mask);
                id.SetHyperlink(new XLHyperlink($"'{ResSheet}'!A{rowOf[e.Id]}"));
                id.Style.Font.FontColor = LinkColor;
                id.Style.Font.Underline = XLFontUnderlineValues.Single;
                Put(sum, r, 2, t.Title, mask);
                Put(sum, r, 3, t.Explanation + (t.Hint is null ? "" : " " + t.Hint), mask);
                if (RerunCommand(info, e.Id) is { } cmd) Put(sum, r, 4, cmd, mask);
                if (PlainLanguage.CauseLabel(t.Cause, e.Result) is { } cl) Put(sum, r, 5, cl["Orsak: ".Length..], mask);
                r++;
            }
            var rows = sum.Range(hdr + 1, 1, r - 1, 5);
            rows.Style.Alignment.WrapText = true;
            rows.Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
            rows.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
            rows.Style.Border.BottomBorderColor = XLColor.FromHtml("#8A93A3");
        }

        // Kommandona sist: de är långa och hör inte till svaret på "gick det bra?".
        if (RerunCommand(info) is { } all)
        {
            r++;
            Pair("Kör om allt", all);
            foreach (var n in RerunNotes(info)) Pair("Obs", n);
        }

        sum.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        sum.PageSetup.PaperSize = XLPaperSize.A4Paper;
        sum.PageSetup.FitToPages(1, 0);
    }

    /// <summary>
    /// Skriver text (maskad, utan styrtecken som Excel inte tillåter). Alltid som text, aldrig som formel.
    /// Längre än <paramref name="max"/> tecken kortas och markeras med "… (avkortat)".
    /// </summary>
    private static void Put(IXLWorksheet ws, int row, int col, string text, HeaderMask mask, int max = CellMax)
    {
        var t = mask.Scrub(text);
        if (t.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
            t = new string(t.Where(c => !char.IsControl(c) || c is '\n' or '\r' or '\t').ToArray());
        if (t.Length > max)
        {
            var note = $"\n… (avkortat, {t.Length} tecken totalt)";
            t = t[..(max - note.Length)] + note;
        }
        ws.Cell(row, col).SetValue(t);
    }
}
