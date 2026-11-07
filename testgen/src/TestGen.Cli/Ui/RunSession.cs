using System.Text;
using System.Text.Json;
using TestGen.Core;
using TestGen.Core.Run;

namespace TestGen.Cli.Ui;

/// <summary>Ett händelse-event till webbläsaren (Server-Sent Events): namn och JSON.</summary>
internal sealed record UiEvent(string Name, string Json);

/// <summary>Inställningarna för en körning, utan token. Skickas till sidan så att den kan fylla i formuläret igen.</summary>
internal sealed record RunInfo(string Spec, string? SpecUploadId, string? Excel, string? UploadId, string? Env, string Api, string? Seed, string? Only, string? Timeout,
    IReadOnlyList<string> Scenarios, bool ExcelOnly);

/// <summary>Det som skickas vidare till <see cref="TestRunner"/> utöver inställningarna: filer, slumpfrö och urval.</summary>
internal sealed record RunInput(string Spec, string? Excel, int? Seed, string? Only, IReadOnlyList<string> Scenarios, bool ExcelOnly)
{
    /// <summary>--seed slumpa: verktyget väljer fröet.</summary>
    public bool SeedRandom { get; init; }
    /// <summary>Test-id (T-018) som ska köras, med ursprungligt id och testdata.</summary>
    public IReadOnlyList<string> Tests { get; init; } = Array.Empty<string>();
}

/// <summary>
/// En körning i bakgrunden. Resultat läggs i en trådsäker kanal som SSE-endpointen läser. Token finns bara i
/// <see cref="RunSettings"/>/<see cref="TestRunner"/> medan körningen pågår; när den är klar byggs rapporterna (maskade)
/// och allt som innehåller token släpps.
/// </summary>
internal sealed class RunSession
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // Alla händelser sparas så att en omladdad sida kan spela upp dem från början och fortsätta följa körningen.
    private readonly List<UiEvent> _log = new();
    private readonly object _lock = new();
    private TaskCompletionSource _more = NewSignal();
    private bool _complete;
    private readonly CancellationTokenSource _cts = new();
    private volatile bool _cancelRequested;
    private int _resultCount;

    public string Id { get; } = Guid.NewGuid().ToString("N");
    /// <summary>Det som visas i formuläret efter omladdning (aldrig token).</summary>
    public RunInfo? Info { get; private set; }
    public bool CancelRequested => _cancelRequested;
    public int ResultCount => Volatile.Read(ref _resultCount);
    public int? Total { get; private set; }
    /// <summary>Sant när körningen är helt klar (rapporterna byggda). Sätts före slut-eventet skrivs.</summary>
    public volatile bool Finished;
    public byte[]? Html { get; private set; }
    public byte[]? Xlsx { get; private set; }
    public DateTime Started { get; } = DateTime.Now;

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Hämtar händelser från och med <paramref name="from"/> (0 = från början). <c>Complete</c> är sant när inga fler kommer.
    /// Flera läsare kan följa samma körning, och en ny läsare får först allt som redan hänt.
    /// </summary>
    public (IReadOnlyList<UiEvent> Batch, bool Complete) Read(int from)
    {
        lock (_lock) return (_log.Skip(from).ToList(), _complete);
    }

    /// <summary>Väntar tills det finns mer än <paramref name="have"/> händelser eller körningen är slut.</summary>
    public Task WaitForMore(int have, CancellationToken ct)
    {
        Task t;
        lock (_lock)
        {
            if (_log.Count > have || _complete) return Task.CompletedTask;
            t = _more.Task;
        }
        return t.WaitAsync(ct);
    }

    public void Cancel()
    {
        _cancelRequested = true;
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    public void Start(RunSettings settings, RunInput input, string tempDir, HttpMessageHandler? handler, RunInfo info)
    {
        Info = info;
        _ = Task.Run(() => ExecuteAsync(settings, input, tempDir, handler));
    }

    private void Emit(string name, object data)
    {
        lock (_lock)
        {
            _log.Add(new UiEvent(name, JsonSerializer.Serialize(data, Json)));
            var old = _more; _more = NewSignal();
            old.TrySetResult();
        }
    }

    private void Complete()
    {
        lock (_lock)
        {
            _complete = true;
            var old = _more; _more = NewSignal();
            old.TrySetResult();
        }
    }

    private static readonly TimeSpan CancelGrace = TimeSpan.FromSeconds(8);

    private static async Task CancelGraceAfter(CancellationToken ct)
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (ct.Register(() => cancelled.TrySetResult())) await cancelled.Task;
        await Task.Delay(CancelGrace);
    }

    private async Task ExecuteAsync(RunSettings settings, RunInput input, string tempDir, HttpMessageHandler? handler)
    {
        var (spec, excel) = (input.Spec, input.Excel);
        // Reservmask: dold token även om ett fel uppstår innan runnern hunnit bygga sin egen mask.
        var fallback = HeaderMask.For(settings.Headers, settings.SecretHeaderNames, settings.Token is null ? null : new[] { settings.Token });
        TestRunner? runner = null;
        string Safe(string s) => fallback.Scrub(runner?.Mask.Scrub(s) ?? s);
        string? SafeOrNull(string? s) => s is null ? null : Safe(s);

        try
        {
            runner = new TestRunner(new RunOptions
            {
                Spec = spec, Api = settings.Api, Headers = settings.Headers, Token = settings.Token,
                SecretHeaderNames = settings.SecretHeaderNames, TimeoutSeconds = settings.TimeoutSeconds, EnvName = settings.EnvName,
                Excel = excel, Seed = input.Seed, Only = input.Only, Scenarios = input.Scenarios, ExcelOnly = input.ExcelOnly, Warnings = settings.Warnings,
                SeedRandom = input.SeedRandom, Tests = input.Tests,
                OnWarnings = w => Emit("warning", new { title = w.Count == 1 ? "Varning (körningen fortsätter)" : "Varningar (körningen fortsätter)", items = w.Select(i => Safe(UiErrors.FieldNames(i.Text))).ToList() }),
                OnResult = o => { if (Finished) return; Total = o.Total; Interlocked.Increment(ref _resultCount); Emit("result", ToDto(o, Safe, SafeOrNull, runner?.Operations ?? new Dictionary<string, OperationInfo>())); },
            }, TextWriter.Null, handler);

            var runTask = runner.RunAsync(_cts.Token);
            // Avbryt ska alltid ge svar: hänger något som inte tar emot avbrottet (t.ex. hämtning av specen) väntar vi högst 8 s.
            var grace = _cts.Token.IsCancellationRequested ? Task.Delay(CancelGrace) : CancelGraceAfter(_cts.Token);
            if (await Task.WhenAny(runTask, grace) == grace)
            {
                _ = runTask.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                Finish(runner, cancelled: true, tempDir, Safe, settings);
                return;
            }
            await runTask;
            Finish(runner, cancelled: false, tempDir, Safe, settings);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            Finish(runner, cancelled: true, tempDir, Safe, settings);
        }
        catch (Exception e)
        {
            var f = UiErrors.Describe(e, new ErrorContext { Spec = spec, Excel = excel, Api = settings.Api });
            var done = runner?.Outcomes.Count ?? 0;
            Finished = true;
            Emit("error", new
            {
                message = Safe(f.What), tip = Safe(f.Action), technical = SafeOrNull(f.Technical), done,
                items = f.Items.Select(Safe).ToList(), warnings = f.Warnings.Select(Safe).ToList(),
            });
        }
        finally
        {
            Complete();
            runner = null;
            _cts.Dispose();
        }
    }

    private void Finish(TestRunner? runner, bool cancelled, string tempDir, Func<string, string> safe, RunSettings settings)
    {
        var outcomes = runner?.Outcomes ?? Array.Empty<TestOutcome>();
        int ok = outcomes.Count(o => o.Outcome == "OK"), fail = outcomes.Count(o => o.Outcome == "FEL"), skipped = outcomes.Count(o => o.Outcome == "HOPPAD");
        var hasReport = false;
        if (runner is not null && outcomes.Count > 0 && runner.ReportInfo is { } info)
        {
            var entries = runner.ReportEntries;
            Html = new UTF8Encoding(false).GetBytes(ReportWriter.Html(info, entries, runner.Mask));
            var tmp = Path.Combine(tempDir, $"rapport-{Id}.xlsx");
            ReportWriter.Excel(tmp, info, entries, runner.Mask);
            Xlsx = File.ReadAllBytes(tmp);
            File.Delete(tmp);
            hasReport = true;
        }
        var total = ok + fail + skipped;
        var toolSkipped = PlainLanguage.ToolSkipped(outcomes);
        var warningCount = PlainLanguage.WarningCount(outcomes.Select(o => o.Result));
        var summary = cancelled
            ? (total == 0
                ? "Du avbröt körningen innan något test hann bli klart."
                : $"Körningen avbröts efter {total} tester. " + PlainLanguage.Summary(ok, fail, skipped, toolSkipped))
            : PlainLanguage.Summary(ok, fail, skipped, toolSkipped);
        if (runner?.Notice is { } stopNotice) summary += " " + stopNotice;   // t.ex. avbruten efter flera tidsgränser i rad
        var results = outcomes.Select(o => o.Result).ToList();
        var authNote = PlainLanguage.AuthNote(results, web: true);
        var envNote = PlainLanguage.EnvironmentNote(results, web: true);
        // Beslutsmeningen och orsaksraderna kommer från PlainLanguage, samma källa som terminalen och rapporterna.
        var verdict = total == 0 ? null : (cancelled ? $"Körningen avbröts efter {total} tester. " : "") + PlainLanguage.Verdict(outcomes);
        var causes = PlainLanguage.CauseSummary(outcomes).Select(c => new
        {
            code = CauseCodes.Code(c.Cause), name = c.Name, head = PlainLanguage.CauseHead(c.Cause), tail = PlainLanguage.CauseTail(c.Cause),
            count = c.Count, ids = c.TestIds, more = c.More, sentence = safe(c.Sentence),
        }).ToList();
        var partialLine = runner?.ReportInfo is { } ri ? ReportWriter.PartialLine(ri, total) : null;
        Finished = true;
        Emit("done", new
        {
            authNote = authNote is null ? null : safe(authNote),
            envNote = envNote is null ? null : safe(envNote),
            verdict = verdict is null ? null : safe(verdict), causes,
            partialText = partialLine is null ? null : safe(partialLine),
            total, ok, fail, skipped, cancelled, hasReport,
            warnings = warningCount, warningText = PlainLanguage.WarningSummary(warningCount),
            // Delkörning, tid och notis (t.ex. avbruten efter flera tidsgränser i rad): data till sidan och till sammanfattningen.
            possibleTotal = runner?.ReportInfo?.PossibleTotal, partial = runner?.ReportInfo?.Partial ?? false,
            filter = runner?.ReportInfo?.FilterDescription, seconds = runner?.Duration?.TotalSeconds,
            notice = runner?.Notice is { } notice ? safe(notice) : null,
            summary = safe(summary),
            api = safe(settings.Api), env = settings.EnvName, seed = runner?.ReportInfo?.Seed,
        });
    }

    private static object ToDto(TestOutcome o, Func<string, string> safe, Func<string?, string?> safeOrNull, IReadOnlyDictionary<string, OperationInfo> operations) => new
    {
        index = o.Index, total = o.Total, id = o.Id, group = safe(o.Group), outcome = o.Outcome,
        call = safe(o.Test.Call),
        title = safe(o.Text.Title), explanation = safe(UiErrors.CardText(o.Text.Explanation)), hint = safeOrNull(o.Text.Hint is null ? null : UiErrors.CardText(o.Text.Hint)),
        technical = o.TechnicalLines.Select(safe).ToList(), curl = safeOrNull(o.Curl),
        // Bash-skript som återskapar hela kedjan av anrop för ett FEL-test (bara vid FEL).
        script = o.Outcome == "FEL" ? safeOrNull(ExchangeText.ReproScript(o.Result.Exchanges, o.Text.Title)) : null,
        // 409 = uppgifterna fanns redan (även när det var ett förberedande anrop som fick 409).
        // Orsaken (Fel i systemet, Testdata fanns redan ...) och vilket test felet är en följd av.
        cause = o.Cause is { } cause ? PlainLanguage.CauseName(cause) : null, causeCode = o.Cause is { } code ? CauseCodes.Code(code) : null,
        followsFrom = o.Result.RootCauseTestId,
        warnings = o.Result.Warnings.Select(w => safe(PlainLanguage.WarningText(w))).ToList(),
        conflict = o.Outcome == "FEL" && (o.Result.Status == 409 || o.Result.Conflict),
        // Satt när anropet som curl-raden visar är ett förberedande steg, inte själva testet.
        preparing = PreparingCall(o) is { } p ? safe(p) : null,
        // Anropen i testet i ordning, kopplade till specens operationer så att sidan kan visa vägen genom systemet (systemkartan).
        chain = o.Result.Exchanges.Select(e => ChainStep(e, o.Test.OperationId, safe, safeOrNull, operations)).ToList(),
    };

    /// <summary>
    /// Ett anrop i kedjan. Förberedande anrop och tidigare steg vet vilken operation de gjordes för (<see cref="Exchange.Preparation"/>);
    /// övriga är testets egen operation. Sidan hittar anropet på systemkartan via operationId (eller metod och sökväg).
    /// </summary>
    private static object ChainStep(
        Exchange e, string testOperationId, Func<string, string> safe, Func<string?, string?> safeOrNull, IReadOnlyDictionary<string, OperationInfo> operations)
    {
        var op = operations.GetValueOrDefault(e.Preparation ?? testOperationId);
        return new
        {
            method = e.Method, path = safe(op?.Path ?? PathOf(e.Url)), operationId = op?.OperationId, name = op?.Summary ?? op?.OperationId,
            status = e.Status, ms = e.ElapsedMs, preparation = e.Preparation is not null, earlierStep = e.EarlierStep,
            uses = (e.Uses ?? []).Select(u => new { field = safe(u.Target), value = safe(u.Value) }).ToList(),
            sent = safeOrNull(Compact(e.RequestBody)),
        };
    }

    /// <summary>JSON på en rad, högst 300 tecken (hela kroppen finns under Tekniska detaljer).</summary>
    private static string? Compact(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        string text;
        try { text = System.Text.Json.Nodes.JsonNode.Parse(body)?.ToJsonString(Json) ?? body; }
        catch (JsonException) { text = body.ReplaceLineEndings(" "); }
        return TestGen.Scenarios.ResponseStore.Shorten(text, 300);
    }

    private static string PathOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.AbsolutePath : url;

    private static string? PreparingCall(TestOutcome o) =>
        o.Curl is not null && ExchangeText.Main(o.Result.Exchanges) is { Preparation: not null } main ? $"{main.Method} {PathOf(main.Url)}" : null;
}
