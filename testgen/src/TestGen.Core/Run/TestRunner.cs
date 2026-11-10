using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TestGen.Core;
using TestGen.Core.Run;

namespace TestGen.Core.Run;

public sealed class RunOptions
{
    public required string Spec { get; init; }
    public required string Api { get; init; }
    public string? Excel { get; init; }
    /// <summary>API-projektets namn när det är känt; skrivs i resultatet.</summary>
    public string? Project { get; init; }
    /// <summary>Bara planera: fyll <see cref="TestRunner.PlannedTests"/> och returnera utan att köra något.</summary>
    public bool ListOnly { get; init; }
    /// <summary>Planeringen från en tidigare körning med samma spec, Excel-fil och inställningar (<see cref="TestRunner.Prepared"/>): då läses och genereras inget om.</summary>
    public PreparedPlan? Prepared { get; init; }
    /// <summary>Läser filen i <see cref="Excel"/> (egna tester). Krävs när <see cref="Excel"/> är satt.</summary>
    public ITestSource? Source { get; init; }
    public int? Seed { get; init; }
    public string? Only { get; init; }
    /// <summary>--scenario (kan anges flera gånger): kör bara dessa Excel-scenarier. Implicerar att de genererade testerna hoppas över.</summary>
    public IReadOnlyList<string>? Scenarios { get; init; }
    /// <summary>--excel-only: hoppa över de genererade testerna och kör bara raderna i Excel-filen.</summary>
    public bool ExcelOnly { get; init; }
    /// <summary>Anropas en gång med alla varningar (Excel) innan testerna körs, om det finns några. Webbgränssnittet visar dem.</summary>
    public Action<IReadOnlyList<ExcelIssue>>? OnWarnings { get; init; }
    /// <summary>Sökvägen till testgen.yaml när den pekats ut med --config (för rapporten och "kör om"-kommandot).</summary>
    public string? Config { get; init; }
    /// <summary>Inställningarna för testdatan (fältregler m.m.) ur testgen.yaml. Utan: standardvärden.</summary>
    public GeneratorOptions? Generator { get; init; }
    /// <summary>Miljön är skrivskyddad: tester som skulle skicka POST, PUT, PATCH eller DELETE HOPPAS över (orsak read-only).</summary>
    public bool ReadOnly { get; init; }
    /// <summary>Globala headers som skickas på alla anrop (även förberedande). Redan ersatta ${env:...}.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
    /// <summary>--token: blir rätt header utifrån specens securityScheme (se <see cref="AuthSchemes.TokenHeader"/>).</summary>
    public string? Token { get; init; }
    /// <summary>Token är testgens egen testanvändare (appen körs i testprocessen): ett giltigt anrop som nekas (401/403) hoppas över med orsaken test-user-rejected.</summary>
    public bool UsesTestUser { get; init; }
    /// <summary>Appen körs i testprocessen: ett 502/503/504 förklaras som att appen saknar en tjänst, inte som att miljön är nere.</summary>
    public bool InProcess { get; init; }
    /// <summary>Värdarna som appens utgående anrop blockerats mot hittills, i ordning; ett test visar dem som fått nya under körningen.</summary>
    public Func<IReadOnlyList<string>>? BlockedHosts { get; init; }
    /// <summary>
    /// Inloggningssteget ger token: får ett anrop 401 anropas detta med den token som nekades och ger en ny, och anropet skickas en gång till.
    /// Anrop till <see cref="LoginOperation"/> och anrop som själva sätter sin inloggning rörs inte.
    /// </summary>
    public Func<string, CancellationToken, Task<string>>? Relogin { get; init; }
    /// <summary>Inloggningsoperationen (operationId): den testar inloggningen och hoppas inte över som nekad testanvändare.</summary>
    public string? LoginOperation { get; init; }
    /// <summary>Headers som ska maskas i utskrifter utöver de som alltid är hemliga (se <see cref="HeaderMask"/>).</summary>
    public IEnumerable<string>? SecretHeaderNames { get; init; }
    public int TimeoutSeconds { get; init; } = RunSettings.DefaultTimeoutSeconds;
    public string? EnvName { get; init; }
    /// <summary>--verbose: visa anrop, svar och curl-rad för alla tester, inte bara FEL.</summary>
    public bool Verbose { get; init; }
    /// <summary>--report: skriv rapport (.html eller .xlsx) efter körningen.</summary>
    public string? ReportPath { get; init; }
    /// <summary>--report flera gånger: alla rapporter som ska skrivas (läggs ihop med <see cref="ReportPath"/>).</summary>
    public IReadOnlyList<string>? ReportPaths { get; init; }
    /// <summary>--force: tillåt att skriva över en befintlig fil som inte är en testgen-rapport.</summary>
    public bool ForceReport { get; init; }
    /// <summary>--test T-018 (kan anges flera gånger): kör bara dessa tester, med deras ursprungliga id och testdata.</summary>
    public IReadOnlyList<string>? Tests { get; init; }
    /// <summary>--seed random: välj ett slumpfrö (om <see cref="Seed"/> inte är satt) och skriv ut det.</summary>
    public bool SeedRandom { get; init; }
    /// <summary>--header exakt som de skrevs (före ${env:...}-ersättning), för "kör om"-kommandot. Hemliga värden skrivs aldrig dit.</summary>
    public IReadOnlyList<string>? HeaderArgs { get; init; }
    /// <summary>Så många tidsgränser i rad avbryter körningen (API:et svarar inte). 0 = aldrig.</summary>
    public int MaxConsecutiveTimeouts { get; init; } = DefaultMaxConsecutiveTimeouts;
    public const int DefaultMaxConsecutiveTimeouts = 3;
    /// <summary>--explain: skriv rubrik och förklaring i klartext i stället för den tekniska raden.</summary>
    public bool PlainText { get; init; }
    /// <summary>Orsakerna som ger exitkod 1 (--fail-on). Null = standard: Fel i systemet och Förväntan i testet stämmer inte.</summary>
    public IReadOnlySet<FailureCause>? FailOn { get; init; }
    /// <summary>--baseline: JSON-rapport från en tidigare körning att jämföra med (nya FEL, lagade, kvar).</summary>
    public string? BaselinePath { get; init; }
    /// <summary>--fail-on-new: exitkod 1 bara om det finns NYA FEL (jämfört med <see cref="BaselinePath"/>) av orsak i <see cref="FailOn"/>.</summary>
    public bool FailOnNew { get; init; }
    /// <summary>--allow-empty: en körning utan några planerade tester är inget fel (exitkod 0). Annars exitkod 2.</summary>
    public bool AllowEmpty { get; init; }
    /// <summary>--max-time: högsta tid i sekunder för hela körningen. Därefter avbryts den, partiell rapport skrivs och exitkoden blir 3. Null = ingen gräns.</summary>
    public int? MaxTimeSeconds { get; init; }
    /// <summary>
    /// Sant (kommandoraden): avbryts <c>cancellationToken</c> avslutas körningen i stället för att kasta, med partiell rapport,
    /// notisen "Körningen avbröts (signal)" och exitkod <see cref="InterruptExitCode"/>. Falskt (webbgränssnittet): <see cref="OperationCanceledException"/> kastas.
    /// </summary>
    public bool HandleInterrupt { get; init; }
    /// <summary>Exitkoden vid avbrott: 130 (SIGINT, Ctrl+C) eller 143 (SIGTERM). Läses när körningen avbrutits. Standard 130.</summary>
    public Func<int>? InterruptExitCode { get; init; }
    /// <summary>Förklaring till slumpfröet när det inte var ett vanligt tal (stort tal omgjort, --seed ci). Skrivs ut och sparas i rapporten.</summary>
    public string? SeedNote { get; init; }
    /// <summary>
    /// Anropas efter varje avslutat test (i körordning, på samma tråd som körningen) med id, grupp, klartext och
    /// tekniska detaljer. Används för att strömma resultat live.
    /// </summary>
    public Action<TestOutcome>? OnResult { get; init; }
}

/// <summary>Ett förberedande anrop som misslyckades (testet kunde inte köras).</summary>
/// <param name="Operation">operationId.</param>
/// <param name="Name">Operationens namn i klartext (summary om den finns).</param>
/// <param name="Status">Statuskoden anropet gav.</param>
public sealed record SetupFailure(string Operation, string Name, int Status);

public sealed record CaseResult(int? Status, bool Ok, bool Skipped, string Verdict, List<string> Details, bool Conflict = false)
{
    /// <summary>Alla HTTP-anrop i testet i den ordning de skickades (även förberedande), maskade.</summary>
    public List<Exchange> Exchanges { get; init; } = new();
    /// <summary>Satt om testet inte kunde köras för att ett förberedande anrop misslyckades.</summary>
    public SetupFailure? Setup { get; init; }
    /// <summary>Anropet fick inget svar inom tidsgränsen.</summary>
    public bool TimedOut { get; init; }
    /// <summary>Testet som är orsak när detta test HOPPADes eller föll i ett förberedande anrop (t.ex. "T-033"), annars null.</summary>
    public string? RootCauseTestId { get; init; }
    /// <summary>Kort beskrivning av rotorsaken, t.ex. "steg 1 'Skapa kund' fick FEL"; satt tillsammans med <see cref="RootCauseTestId"/>.</summary>
    public string? RootCauseText { get; init; }
    /// <summary>Förklaring (klartext) när testet HOPPADes av en annan orsak än ett tidigare fel: bodytypen stöds inte (Unsupported), eller internt fel (Tool).</summary>
    public string? SkipNote { get; init; }
    /// <summary>Orsaken när testet HOPPADes av en annan orsak än Följdfel eller Fel i testgen, t.ex. <see cref="FailureCause.ReadOnly"/>.</summary>
    public FailureCause? SkipCause { get; init; }
    /// <summary>Värdarna som appens utgående anrop blockerades mot under testet (värdpolicyn), när appen körs i testprocessen.</summary>
    public IReadOnlyList<string> BlockedHosts { get; init; } = [];
    /// <summary>
    /// Varningar från svarskontrollen (t.ex. extra fält som inte finns i specen). De ändrar aldrig utfallet:
    /// ett test kan vara OK och ändå ha varningar. Antal: <c>Warnings.Count</c>.
    /// </summary>
    public List<string> Warnings { get; init; } = new();
}

/// <summary>
/// Genererar giltiga + negativa fall i minnet, kör dem mot API:et med HttpClient och skriver en tabell.
/// Varje exekvering får ett eget salt (räknare) i seeden: unika fält (e-post, sku ...) kolliderar inte,
/// men samma spec/seed/ordning ger exakt samma data.
/// </summary>
public sealed partial class TestRunner
{
    private sealed class CaseContext
    {
        public ResponseStore Responses { get; } = new();
        public HashSet<string> InProgress { get; } = new();
        /// <summary>Ett steg i Excel-scenariot har misslyckats; resten hoppas över.</summary>
        public bool Failed { get; set; }
        /// <summary>Det första steget som fick FEL, t.ex. "steg 2 'Skapa order'" (för texten på de hoppade stegen).</summary>
        public string? FailedStep { get; set; }
        /// <summary>Inspelade anrop för testet (se <see cref="Exchange"/>).</summary>
        public List<Exchange> Exchanges { get; set; } = new();
        /// <summary>Operationen som just nu förbereds (setup/beroende), annars null.</summary>
        public string? Preparing { get; set; }
        /// <summary>Testets id (T-018); sätts av körloopen. Följer med de inspelade anropen.</summary>
        public string? TestId { get; set; }
        /// <summary>Stegnummer när testet är ett steg i ett Excel-scenario.</summary>
        public int? Step { get; set; }
    }

    /// <summary>Testet kan inte köras av verktygsskäl (inte systemets fel): bodytypen stöds inte, eller ett internt fel när testet byggdes.</summary>
    private sealed class ToolLimitException(string verdict, string note, string? detail = null, FailureCause cause = FailureCause.Tool) : Exception(note)
    {
        public FailureCause Cause { get; } = cause;
        public string Verdict { get; } = verdict;
        public string? Detail { get; } = detail;
    }

    private static ToolLimitException Unsupported(OperationInfo op) => new(
        "HOPPAD (testgen stöder inte bodyn)",
        $"testgen stöder inte bodyn ({op.UnsupportedBody}) ännu, så testet skickades inte. Det är en känd begränsning och säger inget om systemet.",
        cause: FailureCause.Unsupported);

    private static ToolLimitException Internal(Exception e) => new(
        "HOPPAD (internt fel i testgen)",
        "Verktyget kunde inte bygga testet (internt fel): " + e.Message + " Rapportera till den som underhåller testgen.",
        e.GetType().Name);

    /// <summary>Anropet skulle skriva i en skrivskyddad miljö och skickades därför inte.</summary>
    private sealed class ReadOnlyException(string method) : Exception($"Miljön är skrivskyddad, så {method} skickades inte. Det säger inget om systemet.");

    private sealed class SetupFailedException(string message, int status, string operation, string name) : Exception(message)
    {
        public int Status { get; } = status;
        public SetupFailure Failure { get; } = new(operation, name, status);
    }

    private sealed record Planned(string Signature, string Description, Func<CaseContext, Task<CaseResult>> Run, string Group, TestInfo Info, string Id = "", bool Selected = true);

    private readonly List<TestOutcome> _outcomes = new();

    /// <summary>Alla färdiga tester i den ordning de kördes (fylls på under körningen, se även <see cref="RunOptions.OnResult"/>).</summary>
    public IReadOnlyList<TestOutcome> Outcomes => _outcomes;

    private readonly RunOptions _opt;
    private readonly Terminal _term;
    private TextWriter _out => _term.Out;
    private HttpClient _http = null!;
    private ScenarioGenerator _gen = null!;
    private Dictionary<string, OperationInfo> _ops = null!;
    private int _saltBase;
    private int _saltN;
    private Dictionary<string, string> _globalHeaders = new(StringComparer.OrdinalIgnoreCase);
    private HeaderMask _mask = HeaderMask.Default;
    private readonly HttpMessageHandler? _handler;
    private bool _secretTipShown;
    private CancellationToken _ct;
    private int _seed;

    /// <summary>Masken för körningen (döljer token m.m.). Färdig när körningen startat; används för rapporter.</summary>
    public HeaderMask Mask => _mask;

    /// <summary>Seed som användes (satt när körningen startat).</summary>
    public int Seed => _seed;

    /// <summary>Specens operationer per operationId (tom innan specen lästs).</summary>
    public IReadOnlyDictionary<string, OperationInfo> Operations => _ops ?? new Dictionary<string, OperationInfo>();

    /// <summary>Allmänt om körningen, för rapporter. Starttid är när planen började köras.</summary>
    public ReportInfo? ReportInfo { get; private set; }

    /// <summary>Alla möjliga tester i planens ordning, före urval (satt när planen är gjord).</summary>
    public IReadOnlyList<TestInfo> PlannedTests { get; private set; } = [];

    /// <summary>Det dyra i planen, att ge vidare i <see cref="RunOptions.Prepared"/>. Satt när planen är gjord.</summary>
    public PreparedPlan? Prepared { get; private set; }

    /// <summary>Antal tester en full körning har (före urval). Satt när planen är gjord.</summary>
    public int PossibleTotal { get; private set; }

    /// <summary>Sant om färre tester planerades än de möjliga (--only, --scenario, --excel-only eller --test).</summary>
    public bool Partial { get; private set; }

    /// <summary>Urvalet i ord, t.ex. "bara createProduct", "scenario Stamkund", "bara Excel-testerna", "T-018". Null utan urval.</summary>
    public string? FilterDescription { get; private set; }

    /// <summary>Total tid för körningen (satt när den är slut, även vid avbrott).</summary>
    public TimeSpan? Duration { get; private set; }

    /// <summary>Notis när körningen avbröts i förtid (flera tidsgränser i rad) men resultaten hittills behölls.</summary>
    public string? Notice { get; private set; }

    /// <summary>testgens version (assemblyns version, utan byggmärke).</summary>
    public static string Version
    {
        get
        {
            var asm = typeof(TestRunner).Assembly;
            var info = asm.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
            var v = info?.Split('+')[0];
            return string.IsNullOrWhiteSpace(v) ? (asm.GetName().Version?.ToString(3) ?? "okänd") : v;
        }
    }

    /// <summary>Rapportposter för alla färdiga tester hittills.</summary>
    public IReadOnlyList<ReportEntry> ReportEntries =>
        _outcomes.Select(o => new ReportEntry(o.Id, o.Group, o.Test.Call, o.Test.Description, o.Result) { Info = o.Test }).ToList();

    [GeneratedRegex(@"\{([^{}$]+)\}")]
    private static partial Regex PathParam();

    /// <summary>Båda strömmarna skrivs till <paramref name="output"/>, som med 2&gt;&amp;1.</summary>
    public TestRunner(RunOptions options, TextWriter output, HttpMessageHandler? handler = null)
        : this(options, new Terminal(output, output, false), handler) { }

    /// <param name="terminal">Resultat och sammanfattning går till <see cref="Terminal.Out"/>; rubrik, förlopp, varningar och bekräftelser till <see cref="Terminal.Err"/>.</param>
    /// <param name="handler">Valfri HTTP-handler; tester använder en fejkad så att inget nät behövs.</param>
    public TestRunner(RunOptions options, Terminal terminal, HttpMessageHandler? handler = null)
    {
        _opt = options;
        _term = terminal;
        _handler = handler;
    }

    /// <summary>
    /// Returnerar exitkoden: 0 om allt gick bra, 1 vid FEL i systemet/förväntan (se <see cref="ExitPolicy"/>), 4 om inget säkert svar fanns, 3 om körningen
    /// avbröts av --max-time eller API:et inte svarade, 130 om den avbröts av en signal (se <see cref="RunOptions.HandleInterrupt"/>). Kastar <see cref="ApiUnreachableException"/> om API:et inte nås. Avbryts <paramref name="cancellationToken"/> kastas
    /// <see cref="OperationCanceledException"/>; testerna som hunnit bli klara finns då i <see cref="Outcomes"/>.
    /// </summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        _ct = cancellationToken;
        using var maxTime = _opt.MaxTimeSeconds is > 0 ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken) : null;
        if (maxTime is not null) { maxTime.CancelAfter(TimeSpan.FromSeconds(_opt.MaxTimeSeconds!.Value)); _ct = maxTime.Token; }
        bool MaxTimeHit() => maxTime is { IsCancellationRequested: true } && !cancellationToken.IsCancellationRequested;
        if (_opt.FailOnNew && _opt.BaselinePath is null)
            throw new UsageException("--fail-on-new behöver --baseline <fil> att jämföra med.", "Lägg till --baseline forra.json (en JSON-rapport från en tidigare körning), eller ta bort --fail-on-new.");
        var baseline = _opt.BaselinePath is null ? null : Baseline.Load(_opt.BaselinePath);   // läses före körningen: samma fil får gärna skrivas om av --report
        var reports = new List<string>();
        if (_opt.ReportPath is not null) reports.Add(_opt.ReportPath);
        foreach (var rp in _opt.ReportPaths ?? Array.Empty<string>()) if (!reports.Contains(rp)) reports.Add(rp);
        // Fel typ, otillgänglig katalog eller en rapport som skulle skriva över indata ska stoppa innan körningen startar.
        foreach (var rp in reports) { ReportWriter.Validate(rp); ReportWriter.EnsureWritable(rp); }
        ReportWriter.CheckTargets(reports, new (string, string?)[] { ("--excel", _opt.Excel), ("--spec", _opt.Spec), ("--config", _opt.Config) }, _opt.ForceReport);
        var cfg = _opt.Generator ?? new GeneratorOptions();
        var randomSeed = false;
        if (_opt.Seed is { } seed) cfg.Seed = seed;
        else if (_opt.SeedRandom) { cfg.Seed = Random.Shared.Next(1, 1_000_000_000); randomSeed = true; }
        _seed = cfg.Seed;

        var apiSpec = _opt.Prepared?.Spec ?? OpenApiLoader.LoadSpec(_opt.Spec);
        var ops = apiSpec.Operations.ToList();
        SetUpHeaders(apiSpec);
        _ops = ops.ToDictionary(o => o.OperationId);
        _gen = new ScenarioGenerator(ops, cfg);
        if (_opt.Only is not null && !_ops.ContainsKey(_opt.Only))
            throw new ArgumentException($"--only: okänd operation '{_opt.Only}'. Finns: {string.Join(", ", _ops.Keys)}");

        // Excel-filen kontrolleras helt mot specen innan något test körs: alla fel samlas och stoppar körningen (exit 2).
        var warnings = new List<ExcelIssue>();
        List<ExcelScenario> excelScenarios = new();
        List<ExcelScenario> chosenScenarios = new();
        if (_opt.Excel is null)
        {
            if (_opt.Scenarios is { Count: > 0 }) throw new UsageException("--scenario kräver --excel <fil> (scenarierna finns i bladet Tester).", "Lägg till --excel <fil>, eller ta bort --scenario.");
            if (_opt.ExcelOnly) throw new UsageException("--excel-only kräver --excel <fil>.", "Lägg till --excel <fil>, eller ta bort --excel-only.");
        }
        else
        {
            var (read, excelWarnings) = _opt.Prepared is { } prepared ? (prepared.ExcelScenarios, prepared.ExcelWarnings)
                : (_opt.Source ?? throw new InvalidOperationException("RunOptions.Source saknas: --excel behöver en ITestSource.")).ReadAndCheck(_opt.Excel, ops);
            warnings.AddRange(excelWarnings);
            excelScenarios = read;
            chosenScenarios = SelectScenarios(read, _opt.Scenarios);
        }
        var skipGenerated = _opt.ExcelOnly || _opt.Scenarios is { Count: > 0 };

        ReportInfo MakeInfo(DateTime when, IReadOnlyList<string> ids, int possible, bool partial) => new(when, _opt.Api, _opt.EnvName, cfg.Seed)
        {
            Spec = _opt.Spec,
            Excel = _opt.Excel,
            Config = _opt.Config,
            UsesToken = _opt.Token is not null,
            ExtraHeaders = _opt.EnvName is null && (_opt.Headers?.Count ?? 0) > 0,
            PossibleTotal = possible,
            Partial = partial,
            FilterDescription = FilterDescription,
            TimeoutSeconds = _opt.TimeoutSeconds,
            Version = Version,
            Project = _opt.Project,
            SeedRandom = randomSeed,
            SeedNote = _opt.SeedNote,
            Only = _opt.Only,
            Scenarios = _opt.Scenarios is { Count: > 0 } ? chosenScenarios.Select(sc => sc.Name).ToList() : Array.Empty<string>(),
            ExcelOnly = _opt.ExcelOnly,
            TestIds = ids,
            RerunHeaders = RerunHeaderList(),
        };
        // Inga tester alls är nästan alltid ett misstag (fel spec, filter som inte matchar): exitkod 2, om inte --allow-empty.
        int EmptyRun(string what, IReadOnlyList<string> ids, int possible, bool partial)
        {
            if (!_opt.AllowEmpty)
                throw new UsageException(what, "Kontrollera att --spec pekar på rätt fil och att filtren (--only, --scenario, --excel-only, --test) stämmer. Vill du att en körning utan tester ska räknas som OK (exitkod 0): lägg till --allow-empty.");
            _term.Err.WriteLine("Inga tester att köra.");
            var when = DateTime.Now;
            Duration = TimeSpan.Zero;
            ReportInfo = MakeInfo(when, ids, possible, partial) with { Duration = Duration, ExitCode = ExitCodes.Ok, Comparison = baseline?.Compare(_outcomes) };
            foreach (var rp in reports) _term.Confirm(ReportWriter.Write(rp, ReportInfo, Array.Empty<ReportEntry>(), _mask), ReportWriter.Describe(rp));
            return ExitCodes.Ok;
        }

        var plan = new List<Planned>();
        // Planen innehåller alla möjliga tester; urvalet (--only, --scenario, --excel-only, --test) markeras bara, så att id och data inte ändras.
        var generated = _opt.Prepared?.Cases ?? ops.Where(o => !cfg.SkipOperations.Contains(o.OperationId))
            .SelectMany(op => _gen.GenerateCases(op).Select(c => (op, c))).ToList();
        Prepared = new PreparedPlan(apiSpec, generated, excelScenarios, warnings.ToList());
        foreach (var (op, generatedCase) in generated)
        {
            var (s, meta) = (generatedCase.Scenario, generatedCase.Meta);
            var sig = $"{op.Method} {op.Path}";
            var info = new TestInfo("", "Genererade tester", sig, op.Method, op.OperationId, op.Summary, Describe(meta),
                Expectations(s.ExpectedStatus), meta);
            plan.Add(new Planned(sig, info.Description, ctx => RunGenerated(ctx, op, s.Name, s.ExpectedStatus), info.Group, info,
                Selected: !skipGenerated && (_opt.Only is null || op.OperationId == _opt.Only)));
        }

        var excelCount = 0;
        if (_opt.Excel is not null)
        {
            foreach (var sc in excelScenarios)
            {
                var scSelected = chosenScenarios.Contains(sc) && (_opt.Only is null || sc.Steps.Any(st => st.Operation == _opt.Only));
                var shared = new CaseContext();   // alla steg i ett scenario delar sparade svar
                var prior = new List<Exchange>();   // anropen från de tidigare stegen, så att ett felande steg visar hela kedjan
                for (var i = 0; i < sc.Steps.Count; i++)
                {
                    var st = sc.Steps[i];
                    var n = i + 1;
                    var sig = _ops.TryGetValue(st.Operation, out var o) ? $"{o.Method} {o.Path}" : "?";
                    var excelGroup = $"Excel-tester ({Path.GetFileName(_opt.Excel)})";
                    var stepInfo = new TestInfo("", excelGroup, sig, o?.Method ?? "", st.Operation, o?.Summary, $"{sc.Name}, steg {i + 1}",
                        new[] { st.Status }, null,
                        new ExcelStepInfo(sc.Name, i + 1, st.ExpectedBody.Select(kv => kv.Key).Concat(st.ExpectedPatterns.Select(kv => kv.Key)).ToList(),
                            st.Cell("Förväntad status"), st.Cell("Förväntat svar")));
                    plan.Add(new Planned(sig, stepInfo.Description, async ctx =>
                    {
                        shared.Exchanges = ctx.Exchanges;   // varje steg spelar in sina egna anrop
                        shared.TestId = ctx.TestId;
                        shared.Step = n;
                        var before = prior.Count;
                        if (!shared.Failed) ctx.Exchanges.AddRange(prior);
                        var r = await RunExcelStep(shared, st);
                        prior.AddRange(ctx.Exchanges.Skip(shared.Failed ? ctx.Exchanges.Count : before)
                            .Select(e => e with { EarlierStep = true, Preparation = e.Preparation ?? st.Operation }));
                        if (!r.Ok && !r.Skipped && shared.FailedStep is null) shared.FailedStep = $"steg {n} '{o?.Summary ?? st.Operation}'";
                        shared.Failed |= !r.Ok;
                        return r;
                    }, excelGroup, stepInfo, Selected: scSelected));
                    excelCount++;
                }
            }
        }

        if (warnings.Count > 0)
        {
            foreach (var w in warnings) _term.Warning(_mask.Scrub(w.Text));
            _opt.OnWarnings?.Invoke(warnings);
        }
        if (plan.Count == 0)
            return EmptyRun("Det finns inga tester att köra: specen har inga anrop att testa (paths är tom, eller alla anrop är bortvalda i testgen.yaml).",
                new List<string>(), 0, false);

        // Löpnummer i den fulla planens ordning: samma spec/seed/Excel ger samma id:n, med eller utan urval.
        plan = plan.Select((p, i) => p with { Id = $"T-{i + 1:000}", Info = p.Info with { Id = $"T-{i + 1:000}", Names = Names } }).ToList();
        PlannedTests = plan.Select(p => p.Info).ToList();
        if (_opt.ListOnly) return ExitCodes.Ok;
        var testIds = new List<string>();
        foreach (var raw in _opt.Tests ?? Array.Empty<string>())
        {
            var m = Regex.Match(raw.Trim(), @"^[Tt]-?(\d+)$");
            if (!m.Success)
                throw new UsageException($"--test: '{raw}' är inget test-id. Skriv t.ex. --test T-018.", "Id:n står i resultatet och i rapporten (T-001, T-002 ...).");
            var id = $"T-{int.Parse(m.Groups[1].Value):000}";
            if (plan.All(p => p.Id != id))
                throw new UsageException($"--test: det finns inget test {id}. Med den här specen, Excel-filen och inställningarna finns T-001 till {plan[^1].Id}.",
                    "Id:n hänger ihop med spec, Excel-fil och seed. Använd samma som när rapporten gjordes (kör-om-kommandot i rapporten har dem).");
            if (!testIds.Contains(id)) testIds.Add(id);
        }
        var run = plan.Where(p => p.Selected && (testIds.Count == 0 || testIds.Contains(p.Id))).ToList();
        PossibleTotal = plan.Count;
        Partial = run.Count < plan.Count;
        var filters = new List<string>();
        if (_opt.ExcelOnly) filters.Add("bara Excel-testerna");
        if (_opt.Scenarios is { Count: > 0 }) filters.Add("scenario " + string.Join(", ", chosenScenarios.Select(sc => sc.Name)));
        if (_opt.Only is not null) filters.Add("bara " + _opt.Only);
        if (testIds.Count > 0) filters.Add(string.Join(", ", testIds));
        FilterDescription = filters.Count == 0 ? null : string.Join("; ", filters);
        if (run.Count == 0)
            return EmptyRun($"Inga tester matchade urvalet ({FilterDescription ?? "inget"}): 0 av {PossibleTotal} möjliga tester valdes.", testIds, PossibleTotal, Partial);

        var idW = plan.Max(p => p.Id.Length);
        var w1 = run.Max(p => p.Signature.Length);
        var w2 = Math.Min(run.Max(p => p.Description.Length), 60);
        var started = DateTime.Now;
        var details = new List<string> { $"spec {(Sources.IsUrl(_opt.Spec) ? _opt.Spec : Terminal.Relative(_opt.Spec))}", $"seed {cfg.Seed}" };
        if (_opt.Excel is not null) details.Add($"excel {Terminal.Relative(_opt.Excel)}");
        if (_opt.EnvName is not null) details.Add($"miljö {_opt.EnvName}");
        if (_opt.TimeoutSeconds != RunSettings.DefaultTimeoutSeconds) details.Add($"timeout {_opt.TimeoutSeconds} s");
        _term.Err.WriteLine($"testgen {Version} – {run.Count} {(run.Count == 1 ? "test" : "tester")} mot {_opt.Api} ({string.Join(", ", details)})");
        if (Partial) _term.Err.WriteLine($"Delkörning: {FilterDescription} ({run.Count} av {PossibleTotal} möjliga tester)");
        else if (FilterDescription is not null) _term.Err.WriteLine($"Urval: {FilterDescription} (alla {PossibleTotal} möjliga tester)");
        if (randomSeed) _term.Err.WriteLine($"Slumpfröet valdes av verktyget. Upprepa körningen med --seed {cfg.Seed}");
        if (_opt.SeedNote is not null) _term.Err.WriteLine(_opt.SeedNote);
        if (_globalHeaders.Count > 0) _term.Err.WriteLine($"Headers: {_mask.Format(_globalHeaders)}");
        if (AuthSchemes.MissingLoginNote(apiSpec, _globalHeaders.Keys) is { } note) _term.Err.WriteLine(note);

        _http = CreateHttpClient();
        int ok = 0, fail = 0, skipped = 0, excelRan = 0;
        string? group = null;
        var entries = new List<ReportEntry>();
        var clock = Stopwatch.StartNew();
        ReportInfo = MakeInfo(started, testIds, PossibleTotal, Partial);
        var consecutiveTimeouts = 0;
        var responded = false;
        var timeoutAbort = false;
        var interrupted = false;
        ApiUnreachableException Unreachable(string msg, ApiProblem problem, string? technical)
        {
            var u = new ApiUnreachableException(_opt.Api, problem, technical, msg);
            foreach (var rp in reports)
            {
                var written = ReportWriter.WriteFailure(rp, ReportInfo! with { Duration = clock.Elapsed }, u.Message, u.Tip, entries.Count, _mask);
                _term.Confirm(written, ReportWriter.Describe(rp) + ", körningen kunde inte genomföras");
            }
            return u;
        }
        try
        {
            foreach (var p in run)
            {
                _ct.ThrowIfCancellationRequested();
                _term.Progress(entries.Count + 1, run.Count, $"{p.Id} {p.Signature}");
                if (p.Group != group)
                {
                    group = p.Group;
                    _out.WriteLine();
                    _out.WriteLine(group);
                }
                CaseResult r;
                var blockedBefore = _opt.BlockedHosts?.Invoke().Count ?? 0;
                var ctx = new CaseContext { TestId = p.Id };
                _saltBase = int.Parse(p.Id[2..]) * 1000;
                _saltN = 0;
                try { r = await p.Run(ctx); }
                catch (SetupFailedException e) when (e.Status is 401 or 403 && IdentityIsOurs && e.Failure.Operation != _opt.LoginOperation)
                {
                    r = RejectedIdentity(e.Status, $"HOPPAD (förberedande anrop {e.Failure.Operation} nekades)", $"Det förberedande anropet {e.Failure.Operation} fick {e.Status}");
                }
                catch (SetupFailedException e) { r = new CaseResult(null, false, false, $"FEL ({e.Message})", new(), e.Status == 409) { Setup = e.Failure }; }
                catch (InvalidOperationException e) { r = new CaseResult(null, false, false, "FEL", new() { e.Message }); }
                catch (ArgumentException e) { r = new CaseResult(null, false, false, "FEL", new() { e.Message }); }
                catch (TaskCanceledException) when (!_ct.IsCancellationRequested) { r = new CaseResult(null, false, false, "FEL (timeout)", new()) { TimedOut = true }; }
                catch (ReadOnlyException e) { r = new CaseResult(null, false, true, "HOPPAD (miljön är skrivskyddad)", new()) { SkipNote = e.Message, SkipCause = FailureCause.ReadOnly }; }
                catch (ToolLimitException e) { r = new CaseResult(null, false, true, e.Verdict, e.Detail is null ? new() : new() { e.Detail }) { SkipNote = e.Message, SkipCause = e.Cause }; }
                catch (Exception e) when (e is not (OperationCanceledException or HttpRequestException or ExcelValidationException or UsageException or SourceException))
                {
                    var t = Internal(e);
                    r = new CaseResult(null, false, true, t.Verdict, new() { t.Detail! }) { SkipNote = t.Message, SkipCause = t.Cause };
                }

                r = r with { Exchanges = ctx.Exchanges, BlockedHosts = BlockedSince(blockedBefore) };
                r = WithRootCause(p.Info, r);
                var entry = new ReportEntry(p.Id, p.Group, p.Signature, p.Description, r) { Info = p.Info };
                entries.Add(entry);
                if (r.Skipped) skipped++; else if (r.Ok) ok++; else fail++;
                if (p.Info.Step is not null) excelRan++;
                var text = PlainLanguage.Explain(p.Info, r, _mask);
                var failed = !r.Ok && !r.Skipped;
                var causeLabel = PlainLanguage.CauseLabel(text.Cause, r);
                // Många FEL med samma orsak och status: full detalj för de tre första, sedan en rad per test (alla detaljer med --verbose).
                var n = 0;
                var groupKey = (text.Cause, r.Status ?? r.Setup?.Status);
                if (failed) { n = _causeCount.GetValueOrDefault(groupKey) + 1; _causeCount[groupKey] = n; }
                var brief = failed && !_opt.Verbose && n > FullDetailPerCause;
                if (_opt.PlainText) PrintPlain(p.Id.PadRight(idW), entry.Outcome, text, causeLabel, brief);
                else
                {
                    var status = r.Status?.ToString() ?? "-";
                    var row = $"{p.Id.PadRight(idW)}  {p.Signature.PadRight(w1)}  {p.Description.PadRight(w2)}  {status,3}  {r.Verdict}";
                    _out.WriteLine(_mask.Scrub(causeLabel is null ? row : $"{row}  – {causeLabel}"));
                }
                if (brief)
                {
                    // Test 4 och 5 i en grupp skrivs i full detalj efter listan om gruppen aldrig blir större än 5.
                    if (n <= BriefThreshold) _deferred.Add((groupKey, n, p.Id, DetailLines(r, failed)));
                    else _deferred.RemoveAll(d => d.Key.Equals(groupKey));
                }
                else foreach (var l in DetailLines(r, failed)) _out.WriteLine(l);

                var outcome = new TestOutcome(_outcomes.Count + 1, run.Count, p.Info, r, text, entry.Outcome,
                    ReportWriter.DetailLines(entry, _mask), ExchangeText.Curl(r.Exchanges) is { } c ? _mask.Scrub(c) : null);
                _outcomes.Add(outcome);
                _opt.OnResult?.Invoke(outcome);

                // API:et tar emot anslutningen men svarar aldrig: sluta efter några tidsgränser i rad i stället för att vänta på alla tester.
                var gotAnswer = r.Exchanges.Any(x => x.Status is not null);
                if (gotAnswer) responded = true;
                if (r.TimedOut) consecutiveTimeouts++; else if (gotAnswer) consecutiveTimeouts = 0;
                if (_opt.MaxConsecutiveTimeouts > 0 && consecutiveTimeouts >= _opt.MaxConsecutiveTimeouts && p != run[^1])
                {
                    timeoutAbort = true;
                    break;
                }
            }
            if (timeoutAbort && !responded)
            {
                throw Unreachable($"API:et på {_opt.Api} svarar inte (inget svar inom {_opt.TimeoutSeconds} s på {consecutiveTimeouts} anrop i rad). Körningen avbröts.",
                    ApiProblem.Timeout, null);
            }
        }
        catch (OperationCanceledException) when (_ct.IsCancellationRequested && (_opt.HandleInterrupt || MaxTimeHit()))
        {
            interrupted = true;   // signal eller --max-time: behåll det som hann bli klart
        }
        catch (HttpRequestException e)
        {
            var problem = e.HttpRequestError switch
            {
                HttpRequestError.ConnectionError => ApiProblem.Refused,
                HttpRequestError.NameResolutionError => ApiProblem.NameNotFound,
                HttpRequestError.SecureConnectionError => ApiProblem.Tls,
                _ => ApiProblem.Other,
            };
            var unreachable = new ApiUnreachableException(_opt.Api, problem, e.Message);
            throw Unreachable(unreachable.Message, problem, e.Message);
        }
        finally
        {
            _http.Dispose();
            Duration = clock.Elapsed;
            ReportInfo = ReportInfo! with { Duration = Duration };
        }

        var maxHit = interrupted && MaxTimeHit();
        string? abortKind = timeoutAbort ? "timeouts" : null;
        if (interrupted)
        {
            abortKind = maxHit ? "max-time" : "signal";
            var left = $"{entries.Count} av de {run.Count} planerade testerna hann köras. Resultaten gäller bara dem.";
            Notice = maxHit
                ? $"Körningen avbröts för att --max-time {_opt.MaxTimeSeconds} s löpte ut. {left}"
                : $"Körningen avbröts (signal). {left}";
            ReportInfo = ReportInfo! with { Notice = Notice, AbortKind = abortKind };
            _term.Err.WriteLine("Obs: " + Notice);
        }
        if (timeoutAbort)
        {
            Notice = $"Körningen avbröts efter {consecutiveTimeouts} anrop i rad utan svar (inget svar inom {_opt.TimeoutSeconds} s). " +
                     $"{run.Count - entries.Count} av de {run.Count} planerade testerna kördes inte. Resultaten gäller bara de {entries.Count} testerna som hann köras.";
            ReportInfo = ReportInfo! with { Notice = Notice, AbortKind = abortKind };
            _term.Err.WriteLine("Obs: " + Notice);
        }

        PrintGroupTails();
        _out.WriteLine();
        if (_opt.PlainText) _out.WriteLine(PlainLanguage.Summary(ok, fail, skipped, PlainLanguage.ToolSkipped(_outcomes), PlainLanguage.UnsupportedSkipped(_outcomes)));
        var results = entries.Select(en => en.Result).ToList();
        _out.WriteLine($"Sammanfattning: {entries.Count} tester, {ok} OK, {fail} FEL, {skipped} HOPPAD, tid {ReportWriter.FormatDuration(Duration ?? TimeSpan.Zero)}" +
                       (excelRan > 0 ? $" ({excelRan} av testerna kom från Excel)" : ""));
        _out.WriteLine(PlainLanguage.Verdict(_outcomes, PossibleTotal, Partial));
        if (PlainLanguage.WarningSummary(PlainLanguage.WarningCount(results)) is { } warnLine) _out.WriteLine(warnLine);
        var causes = PlainLanguage.CauseSummary(_outcomes);
        foreach (var line in causes) _out.WriteLine("  " + line.ListText);
        if (PlainLanguage.EnvironmentNote(results) is { } envNote) _out.WriteLine(envNote);
        if (PlainLanguage.AuthNote(results, names: Names) is { } authNote) _out.WriteLine(authNote);
        if (causes.Any(c => c.Cause == FailureCause.TestData))
            _out.WriteLine("Tips: kör igen med --seed random (eller ett annat tal), eller be någon nollställa testsystemet.");
        var comparison = baseline?.Compare(_outcomes);
        if (comparison is not null)
        {
            _out.WriteLine(comparison.Heading + ":");
            foreach (var (label, text) in comparison.Rows) _out.WriteLine($"  {label}: {_mask.Scrub(text)}");
        }
        // 130/143 vid signal, 3 när API:et inte svarar eller --max-time löpte ut, annars 0/1/4 efter orsakerna.
        var exit = interrupted && !maxHit ? _opt.InterruptExitCode?.Invoke() ?? ExitCodes.Interrupted
            : timeoutAbort || maxHit ? ExitCodes.Unreachable
            : ExitPolicy.Decide(_outcomes, _opt.FailOn, comparison, _opt.FailOnNew);
        ReportInfo = ReportInfo! with { ExitCode = exit, Comparison = comparison };
        foreach (var rp in reports)
        {
            _term.Confirm(ReportWriter.Write(rp, ReportInfo!, entries, _mask), ReportWriter.Describe(rp));
        }
        return exit;
    }

    /// <summary>Det första misslyckade steget per Excel-scenario (test-id och text), för HOPPAD-stegen efter det.</summary>
    private readonly Dictionary<string, (string Id, string Text)> _firstFailedStep = new();

    /// <summary>
    /// Sätter rotorsak på resultatet: ett HOPPAD steg pekar på scenariots första misslyckade steg, och ett förberedande anrop som föll
    /// pekar på det tidigare testet som körde samma anrop (giltig indata, samma operation) och föll med samma status, om ett sådant finns.
    /// </summary>
    private CaseResult WithRootCause(TestInfo info, CaseResult r)
    {
        if (r.Skipped && info.Step is { } skipped && _firstFailedStep.TryGetValue(skipped.Scenario, out var first))
            return r with { RootCauseTestId = first.Id, RootCauseText = first.Text };
        if (r.Setup is { } setup)
        {
            var same = _outcomes.FirstOrDefault(o => o.Test.OperationId == setup.Operation && o.Test.Case is { Rule: TestRule.Valid, Field: null }
                && o.Outcome == "FEL" && o.Result.Setup is null && o.Result.Status == setup.Status);
            if (same is not null) r = r with { RootCauseTestId = same.Id, RootCauseText = $"samma anrop '{setup.Name}' fick FEL" };
        }
        if (!r.Ok && !r.Skipped && info.Step is { } step)
            _firstFailedStep.TryAdd(step.Scenario, (info.Id, $"steg {step.Number} '{info.Summary ?? info.OperationId}' fick FEL"));
        return r;
    }

    /// <summary>
    /// --header till "kör om"-kommandot: ${env:...} står kvar som det skrevs, hemliga värden (efter namn) blir ${env:NAMN}
    /// (headernamnet i versaler), andra värden skrivs som de är. Hemliga värden hamnar aldrig i kommandot.
    /// </summary>
    private List<string> RerunHeaderList()
    {
        var result = new List<string>();
        foreach (var raw in _opt.HeaderArgs ?? Array.Empty<string>())
        {
            var i = raw.IndexOf(':');
            if (i <= 0) continue;
            var name = raw[..i].Trim();
            var value = raw[(i + 1)..].Trim();
            if (EnvVars.HasPlaceholder(value) || !_mask.IsSecret(name)) { result.Add($"{name}: {value}"); continue; }
            var envName = new string(name.ToUpperInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray());
            result.Add($"{name}: ${{env:{envName}}}");
        }
        return result;
    }

    private const int WrapAt = 100;

    /// <summary>--explain: id, OK/FEL och rubrik på en rad, sedan förklaring och tolkning (radbrutna och indragna).</summary>
    private void PrintPlain(string id, string outcome, PlainText text, string? causeLabel = null, bool brief = false)
    {
        if (brief) { _out.WriteLine(_mask.Scrub($"{id}  {outcome,-6}  {text.Title}  – {causeLabel}")); return; }
        _out.WriteLine($"{id}  {outcome,-6}  {text.Title}");
        if (causeLabel is not null) _out.WriteLine("      " + _mask.Scrub(causeLabel));
        foreach (var l in Wrap(text.Explanation, WrapAt)) _out.WriteLine("      " + l);
        if (text.Hint is not null)
            foreach (var l in Wrap("Tolkning: " + text.Hint, WrapAt)) _out.WriteLine("      " + l);
    }

    private static IEnumerable<string> Wrap(string text, int width)
    {
        var line = new StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width) { yield return line.ToString(); line.Clear(); }
            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }
        if (line.Length > 0) yield return line.ToString();
    }

    private const int FullDetailPerCause = 3;
    private const int BriefThreshold = 5;
    private readonly Dictionary<(FailureCause? Cause, int? Status), int> _causeCount = new();
    private readonly List<((FailureCause? Cause, int? Status) Key, int N, string Id, List<string> Lines)> _deferred = new();

    /// <summary>Detaljrader under en resultatrad (indragna, maskade): felrader, anrop, svar, curl och med --verbose ett återskapningsskript.</summary>
    private List<string> DetailLines(CaseResult r, bool failed)
    {
        var lines = new List<string>();
        foreach (var d in r.Details) lines.Add($"      {_mask.Scrub(d)}");
        foreach (var w in r.Warnings) lines.Add($"      {_mask.Scrub(w)}");
        if (failed || _opt.Verbose) lines.AddRange(ExchangeLines(r.Exchanges, failed));
        return lines;
    }

    /// <summary>
    /// Anrop, svar och curl-rad under resultatraden (indraget). Vid FEL med full text för varje anrop och,
    /// med --verbose, ett skript som återskapar felet.
    /// </summary>
    private IEnumerable<string> ExchangeLines(IReadOnlyList<Exchange> exchanges, bool failed)
    {
        foreach (var l in ExchangeText.Describe(exchanges, full: failed)) yield return "      " + _mask.Scrub(l);
        if (ExchangeText.Curl(exchanges) is { } curl)
        {
            yield return "      curl för anropet (kopiera raderna nedan):";
            foreach (var l in curl.Split('\n')) yield return "      " + _mask.Scrub(l);
            if (!_secretTipShown && curl.StartsWith(ExchangeText.SetupMarker, StringComparison.Ordinal))
            {
                _secretTipShown = true;
                yield return "      " + ExchangeText.SecretTip;
            }
        }
        if (failed && _opt.Verbose && ExchangeText.ReproScript(exchanges) is { } script)
        {
            yield return "      Återskapa felet (bash, mot ett tomt system):";
            foreach (var l in script.Split('\n')) yield return "      " + _mask.Scrub(l);
        }
    }

    /// <summary>Efter listan: vilka grupper som kortades ned ("… 520 till med samma orsak (401)") och detaljerna för grupper på 4–5 test.</summary>
    private void PrintGroupTails()
    {
        foreach (var d in _deferred)
        {
            _out.WriteLine();
            _out.WriteLine($"Detaljer för {d.Id}:");
            foreach (var l in d.Lines) _out.WriteLine(l);
        }
        var shortened = _causeCount.Where(kv => kv.Value > BriefThreshold && !_opt.Verbose).ToList();
        if (shortened.Count > 0) _out.WriteLine();
        foreach (var (key, count) in shortened)
        {
            var same = key.Status is { } st ? st.ToString() : PlainLanguage.CauseName(key.Cause ?? FailureCause.System);
            _out.WriteLine($"… {count - FullDetailPerCause} till med samma orsak ({same}). Se rapporten eller kör med --verbose för alla detaljer.");
        }
    }

    /// <summary>Slår ihop globala headers och --token (en header som redan är satt vinner) och bygger masken.</summary>
    private void SetUpHeaders(ApiSpec spec)
    {
        _globalHeaders = new Dictionary<string, string>(_opt.Headers ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        var secretNames = (_opt.SecretHeaderNames ?? Enumerable.Empty<string>()).Concat(AuthSchemes.SecretHeaderNames(spec)).ToList();
        var secretValues = new List<string>();
        _spec = spec;
        _token = _opt.Token;
        if (_opt.Token is { Length: > 0 } token)
        {
            var (name, value) = AuthSchemes.TokenHeader(spec, token);
            _globalHeaders.TryAdd(name, value);
            secretNames.Add(name);
            secretValues.Add(token);
        }
        _mask = HeaderMask.For(_globalHeaders, secretNames, secretValues);
        foreach (var w in _mask.Warnings) _term.Warning(w);
    }

    // --- Genererade fall ----------------------------------------------------------------

    private async Task<CaseResult> RunGenerated(CaseContext ctx, OperationInfo op, string name, List<int> expected)
    {
        if (op.UnsupportedBody is not null) throw Unsupported(op);
        Scenario scenario;
        try { scenario = GenerateFresh(op, name); }
        catch (Exception e) when (e is not OperationCanceledException) { throw Internal(e); }
        var (status, body, _) = await ExecuteAsync(ctx, scenario);
        var check = CheckResponse(op, status, body);
        // 429 efter att Retry-After följts (eller utan den): systemet orkar inte ta emot testet, så det säger inget om funktionen.
        if (status == 429 && !expected.Contains(429))
            return new CaseResult(status, false, true, "HOPPAD (för många anrop)", new())
            {
                SkipCause = FailureCause.RateLimited,
                SkipNote = PlainNames.Fill("Systemet svarade 429 (för många anrop), även om testgen väntade enligt Retry-After när det angavs, så anropet kunde inte testas. Höj gränsen i miljön som testas, eller kör igen om en stund.", Names),
            };
        // Testanvändaren som testgen loggar in som godtogs inte: appen slår troligen upp användaren själv.
        // Gäller även negativa fall: ett nekat anrop är lika lite testbart. Inloggningsoperationen själv testar inloggningen och rörs inte.
        if (IdentityIsOurs && status is 401 or 403 && !expected.Contains(status) && op.OperationId != _opt.LoginOperation)
            return RejectedIdentity(status, _opt.Relogin is null ? "HOPPAD (testanvändaren godtogs inte)" : "HOPPAD (inloggningen godtogs inte)", $"Appen svarade {status}");
        // Ett dokumenterat 404 på ett giltigt anrop som ingen förberedelse skapat något åt: värdet vi hittade på finns inte, så testet är osäkert.
        if (status == 404 && scenario.Kind != "negative" && !expected.Contains(404) && (op.ResponseCodes ?? []).Contains("404") && ctx.Exchanges.All(x => x.Preparation is null))
            return new CaseResult(status, false, true, "HOPPAD (värdet finns inte)", new())
            {
                SkipCause = FailureCause.MissingData,
                SkipNote = PlainNames.Fill("Systemet svarade 404 (dokumenterat i specen) på ett värde som testgen hittade på, så anropet kunde inte testas. Ange ett värde som finns under apis.<api>.bindings i testgen.yaml.", Names),
            };
        if (expected.Contains(status))
        {
            return !check.HasErrors
                ? new CaseResult(status, true, false, "OK", new()) { Warnings = check.Warnings }
                : new CaseResult(status, false, false, "FEL (svaret följer inte specen)", check.Errors) { Warnings = check.Warnings };
        }
        return new CaseResult(status, false, false, $"FEL (väntade {string.Join("/", expected)})", ExtraSpecInfo(status, check)) { Warnings = check.Warnings };
    }

    /// <summary>Testgen står själv för identiteten (testanvändaren eller inloggningssteget).</summary>
    private bool IdentityIsOurs => _opt.UsesTestUser || _opt.Relogin is not null;

    private CaseResult RejectedIdentity(int status, string verdict, string what) => new(status, false, true, verdict, new())
    {
        SkipCause = FailureCause.TestUserRejected,
        SkipNote = PlainNames.Fill(_opt.Relogin is null
            ? $"{what} på ett anrop som testgen skickade som sin testanvändare, så anropet kunde inte testas. Appen slår troligen upp användaren själv. Ange claims under apis.<api>.auth.claims, eller ett inloggningssteg under apis.<api>.login i testgen.yaml."
            : $"{what} på ett anrop som testgen skickade med inloggningen från apis.<api>.login, även efter ny inloggning, så anropet kunde inte testas. Kontrollera att användaren från inloggningen får göra anropet.", Names),
    };

    private PlainNames Names => new(_opt.Project, _opt.EnvName, _opt.InProcess);

    private List<string> BlockedSince(int before) =>
        _opt.BlockedHosts?.Invoke().Skip(before).Distinct().ToList() ?? [];

    private HttpClient CreateHttpClient() =>
        new(new RateLimitHandler(_handler ?? new HttpClientHandler(), disposeInner: _handler is null), disposeHandler: true)
        {
            BaseAddress = new Uri(_opt.Api.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(_opt.TimeoutSeconds),
        };

    /// <summary>
    /// Kontrollerar svaret mot specens svarsschema för den faktiska statuskoden (om specen har ett). Gäller alla koder, även när
    /// statusen avviker från förväntad: avvikelserna blir då tilläggsinformation till felet. Ett avvikande 2xx utan eget schema
    /// kontrolleras mot specens schema för lyckade anrop.
    /// </summary>
    private static SchemaCheckResult CheckResponse(OperationInfo op, int status, JsonNode? body)
    {
        if (op.ResponseSchemas is not { } schemas) return SchemaCheckResult.Empty;
        if (schemas.TryGetValue(status, out var schema)) return ResponseSchemaCheck.Check(body, schema);
        // Ett annat 2xx än specen anger (t.ex. 200 i stället för 201): kontrollera mot specens svarsschema för lyckade anrop,
        // så att t.ex. ett läckt lösenord syns ändå.
        if (status is >= 200 and <= 299
            && op.SuccessStatuses.Where(schemas.ContainsKey).Select(c => schemas[c]).FirstOrDefault() is { } success)
            return ResponseSchemaCheck.Check(body, success);
        return SchemaCheckResult.Empty;
    }

    /// <summary>Detaljrader när statusen redan är fel: en rubrikrad och sedan avvikelserna från specens svarsschema (tomt om svaret följer det).</summary>
    private static List<string> ExtraSpecInfo(int status, SchemaCheckResult check) =>
        check.HasErrors ? check.Errors.Prepend($"Svaret ({status}) följer dessutom inte specen:").ToList() : new List<string>();

    /// <summary>Genererar om operationen med nytt salt och väljer fallet med givet namn (default: det giltiga).</summary>
    private Scenario GenerateFresh(OperationInfo op, string? name = null)
    {
        var list = _gen.Generate(op, _saltBase + ++_saltN, includeNegatives: name is not null);   // salt per test (inte per position i körningen): samma test får samma data även vid urval
        return name is null ? list.First(x => x.Kind == "valid") : list.First(x => x.Name == name);
    }

    /// <summary>Kör scenariots setup och beroenden och sedan själva anropet.</summary>
    private async Task<(int Status, JsonNode? Body, string Raw)> ExecuteAsync(CaseContext ctx, Scenario s)
    {
        foreach (var dep in s.Setup) await EnsureAsync(ctx, dep);
        await EnsureDependenciesAsync(ctx, s.Path, s.Body, s.Query, s.Headers);
        return await SendAsync(ctx, s.Method, s.Path, s.Query, s.Headers, s.Body, s.ContentType, s.OperationId);
    }

    /// <summary>Kör operationen (giltigt fall, färsk data) och sparar svaret under operationId.</summary>
    private async Task EnsureAsync(CaseContext ctx, string operationId)
    {
        if (ctx.Responses.Has(operationId)) return;
        if (!_ops.TryGetValue(operationId, out var op))
            throw new InvalidOperationException($"Okänd operation '{operationId}'.");
        if (op.UnsupportedBody is not null) throw Unsupported(op);
        if (!ctx.InProgress.Add(operationId))
            throw new InvalidOperationException($"Cirkulärt beroende via '{operationId}'.");
        var outer = ctx.Preparing;
        ctx.Preparing = operationId;
        try
        {
            var (status, body, _) = await ExecuteAsync(ctx, GenerateFresh(op));
            if (status is < 200 or > 299) throw new SetupFailedException($"förberedande anrop {operationId} gav {status}", status, operationId, op.Summary ?? operationId);
            ctx.Responses.Save(operationId, body);
            MarkSaved(ctx, operationId);
        }
        finally { ctx.InProgress.Remove(operationId); ctx.Preparing = outer; }
    }

    /// <summary>Kör operationer som refereras via ${op.fält} men inte körts ännu.</summary>
    private async Task EnsureDependenciesAsync(CaseContext ctx, string path, JsonNode? body,
        IDictionary<string, string> query, IDictionary<string, string> headers)
    {
        var text = string.Join("\n", new[] { path, body?.ToJsonString() ?? "" }.Concat(query.Values).Concat(headers.Values));
        foreach (var alias in ResponseStore.ReferencedAliases(text))
            if (!ctx.Responses.Has(alias) && _ops.ContainsKey(alias))
                await EnsureAsync(ctx, alias);
    }

    // --- Excel --------------------------------------------------------------------------

    private async Task<CaseResult> RunExcelStep(CaseContext ctx, ExcelStep st)
    {
        if (ctx.Failed)
            return new CaseResult(null, false, true,
                ctx.FailedStep is { } failed ? $"HOPPAD ({failed} i scenariot fick FEL)" : "HOPPAD (ett tidigare steg i scenariot misslyckades)", new());
        if (!_ops.TryGetValue(st.Operation, out var op))   // fångas normalt redan av kontrollen före körningen
            return new CaseResult(null, false, false, "FEL", new() { $"{st.CellRef("Anrop")}: okänt anrop '{st.Operation}'. Välj ett i listan." });

        if (op.UnsupportedBody is not null) throw Unsupported(op);
        try
        {
            var s = GenerateFresh(op);
            var path = s.Path;
            var query = new Dictionary<string, string>(s.Query);
            var headers = new Dictionary<string, string>(s.Headers);
            var body = s.Body is JsonObject b ? (JsonObject)b.DeepClone() : null;
            var otherBody = body is null ? s.Body?.DeepClone() : null;   // lista eller enkelt värde på toppnivå skickas som den är

            var known = op.Parameters.Select(p => p.Name)
                .Concat(op.BodySchema is not null ? SchemaUtil.Flatten(op.BodySchema).Props.Keys : Enumerable.Empty<string>()).ToList();
            foreach (var (field, value) in st.Input)
            {
                var param = op.Parameters.FirstOrDefault(p => p.Name.Equals(field, StringComparison.OrdinalIgnoreCase));
                if (param is not null)
                {
                    var text = ResponseStore.ToText(value);
                    switch (param.In)
                    {
                        case "path": path = ReplacePathParam(op.Path, s.Path, param.Name, text); break;
                        case "query": query[param.Name] = text; break;
                        case "header": headers[param.Name] = text; break;
                    }
                }
                else if (body is not null && known.Contains(field, StringComparer.OrdinalIgnoreCase))
                {
                    var key = body.Select(kv => kv.Key).FirstOrDefault(k => k.Equals(field, StringComparison.OrdinalIgnoreCase)) ?? field;
                    body[key] = value?.DeepClone();
                }
                else
                {
                    return new CaseResult(null, false, false, "FEL", new()
                    {
                        $"{st.CellRef("Indata")}: '{field}' är inget fält för {op.OperationId}. Giltiga fält: {(known.Count == 0 ? "(inga)" : string.Join(", ", known))}. Se bladet Fält.",
                    });
                }
            }

            await EnsureDependenciesAsync(ctx, path, body, query, headers);
            var (status, response, _) = await SendAsync(ctx, s.Method, path, query, headers, body ?? otherBody, s.ContentType, op.OperationId);

            ctx.Responses.Save(st.SaveAs ?? st.Operation, response);
            if (st.SaveAs is not null && status is >= 200 and <= 299) ctx.Responses.Save(st.Operation, response);
            MarkSaved(ctx, st.SaveAs ?? st.Operation, st.SaveAs is not null && status is >= 200 and <= 299 ? st.Operation : null);

            var check = CheckResponse(op, status, response);
            if (!st.Status.Matches(status))
                return new CaseResult(status, false, false, $"FEL (väntade {st.Status})", ExtraSpecInfo(status, check)) { Warnings = check.Warnings };

            var expected = st.ExpectedBody.Select(kv => KeyValuePair.Create(kv.Key, ctx.Responses.Substitute(kv.Value?.DeepClone()))).ToList();
            var mismatches = BodyMatcher.Mismatches(expected, response);
            mismatches.AddRange(BodyMatcher.RegexMismatches(st.ExpectedPatterns, response));
            var broken = check.Errors;
            if (mismatches.Count == 0 && broken.Count == 0) return new CaseResult(status, true, false, "OK", new()) { Warnings = check.Warnings };
            // Både Excel-förväntan och schemafel: visa båda. Ett schemafel är alltid ett fel i systemet/specen (orsak System).
            var verdict = broken.Count == 0 ? "FEL (svaret stämmer inte)"
                : mismatches.Count == 0 ? "FEL (svaret följer inte specen)"
                : "FEL (svaret följer inte specen och stämmer inte med Excel)";
            return new CaseResult(status, false, false, verdict, mismatches.Concat(broken).ToList()) { Warnings = check.Warnings };
        }
        catch (SetupFailedException e)
        {
            return new CaseResult(null, false, false, "FEL", new() { $"{st.CellRef("Anrop")}: {e.Message}" }) { Setup = e.Failure };
        }
        catch (InvalidOperationException e)
        {
            return new CaseResult(null, false, false, "FEL", new() { $"{st.CellRef("Anrop")}: {e.Message}" });
        }
    }

    /// <summary>Väljer scenarierna som --scenario pekar ut (skiftlägesokänsligt). Okänt namn ger fel med "Menade du".</summary>
    private static List<ExcelScenario> SelectScenarios(List<ExcelScenario> all, IReadOnlyList<string>? wanted)
    {
        if (wanted is not { Count: > 0 }) return all;
        var names = all.Select(s => s.Name).ToList();
        var chosen = new List<ExcelScenario>();
        foreach (var w in wanted)
        {
            var hit = all.FirstOrDefault(s => s.Name.Equals(w.Trim(), StringComparison.OrdinalIgnoreCase));
            if (hit is null)
                throw new UsageException($"--scenario: det finns inget scenario som heter '{w}' i Excel-filen." + Suggest.DidYouMean(w, names),
                    names.Count == 0 ? "Bladet Tester har inga scenarier. Fyll i kolumnen Scenario." : $"Scenarier i filen: {string.Join(", ", names)}. Skriv namnet exakt som i kolumnen Scenario (använd citattecken om det innehåller mellanslag).");
            if (!chosen.Contains(hit)) chosen.Add(hit);
        }
        return all.Where(chosen.Contains).ToList();   // i filens ordning
    }

    /// <summary>Byter ut värdet för en sökvägsparameter genom att matcha den genererade sökvägen mot mallen.</summary>
    private static string ReplacePathParam(string template, string generated, string name, string value)
    {
        // Mallen /orders/{id} + genererad /orders/${createOrder.id}: byt det genererade segmentet mot värdet.
        var names = PathParam().Matches(template).Select(m => m.Groups[1].Value).ToList();
        var pattern = "^" + Regex.Escape(template) + "$";
        foreach (var n in names) pattern = pattern.Replace(Regex.Escape("{" + n + "}"), $"(?<{Regex.Escape(n)}>.+?)");
        var m2 = Regex.Match(generated, pattern);
        if (!m2.Success) return template.Replace("{" + name + "}", value);
        var result = template;
        foreach (var n in names)
            result = result.Replace("{" + n + "}", n == name ? value : m2.Groups[n].Value);
        return result;
    }

    // --- HTTP ---------------------------------------------------------------------------

    private static readonly System.Text.Json.JsonSerializerOptions BodyJson =
        new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Bodyns text: JSON, eller (application/x-www-form-urlencoded) fält=värde&fält=värde. Listor upprepar nyckeln; objekt blir JSON-text.</summary>
    public static string BodyText(JsonNode body, string? contentType)
    {
        if (contentType is null || !contentType.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
            return body.ToJsonString(BodyJson);
        var pairs = new List<string>();
        void Add(string key, JsonNode? v) => pairs.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(v is null ? "" : v is JsonValue ? ResponseStore.ToText(v) : v.ToJsonString(BodyJson))}");
        if (body is JsonObject o)
            foreach (var (k, v) in o)
            {
                if (v is JsonArray a) foreach (var item in a) Add(k, item);
                else Add(k, v);
            }
        return string.Join("&", pairs);
    }

    /// <summary>Förväntade statuskoder som intervall: en följd av minst 100 koder i rad (4xx-gruppen) blir ett intervall.</summary>
    private static List<StatusExpectation> Expectations(IEnumerable<int> codes)
    {
        var sorted = codes.Distinct().OrderBy(c => c).ToList();
        var result = new List<StatusExpectation>();
        for (var i = 0; i < sorted.Count;)
        {
            var j = i;
            while (j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1) j++;
            if (j - i + 1 >= 100) result.Add(new StatusExpectation(sorted[i], sorted[j]));
            else for (var k = i; k <= j; k++) result.Add(new StatusExpectation(sorted[k], sorted[k]));
            i = j + 1;
        }
        return result;
    }

    /// <summary>Anger vilka namn det just inspelade svaret sparades under (för ${namn.fält} i senare anrop).</summary>
    private static void MarkSaved(CaseContext ctx, params string?[] names)
    {
        if (ctx.Exchanges.Count == 0) return;
        ctx.Exchanges[^1] = ctx.Exchanges[^1] with { SavedAs = names.Where(n => n is not null).Select(n => n!).Distinct().ToList() };
    }

    private async Task<(int Status, JsonNode? Body, string Raw)> SendAsync(CaseContext ctx, string method, string path,
        IDictionary<string, string> query, IDictionary<string, string> headers, JsonNode? rawBody, string? contentType = null, string? operationId = null, bool retried = false)
    {
        if (_opt.ReadOnly && !IsReadingMethod(method)) throw new ReadOnlyException(method.ToUpperInvariant());
        var store = ctx.Responses;
        var startedAt = DateTimeOffset.Now;
        var body = rawBody is null ? null : store.Substitute(rawBody.DeepClone());
        string BuildUrl(Func<string, string> sub, bool template)
        {
            var u = sub(path).TrimStart('/');
            if (query.Count > 0)
                u += "?" + string.Join("&", query.Select(kv =>
                    $"{Uri.EscapeDataString(kv.Key)}={(template && kv.Value.Contains("${") ? kv.Value : Uri.EscapeDataString(sub(kv.Value)))}"));
            return u;
        }
        var url = BuildUrl(store.SubstituteString, false);
        var uses = UseCollector.Collect(store, path, rawBody, query, headers);

        using var req = new HttpRequestMessage(new HttpMethod(method.ToUpperInvariant()), url);
        foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, store.SubstituteString(v));
        // Globala headers (--header/--token/miljö); en header som anropet själv sätter vinner.
        foreach (var (k, v) in _globalHeaders)
            if (!headers.Keys.Contains(k, StringComparer.OrdinalIgnoreCase)) req.Headers.TryAddWithoutValidation(k, v);
        string? bodyText = null;
        if (body is not null && !method.Equals("GET", StringComparison.OrdinalIgnoreCase) && !method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
        {
            bodyText = BodyText(body, contentType);   // riktiga tecken (ä), inte \u00e4: samma text visas och skickas
            req.Content = new StringContent(bodyText, Encoding.UTF8, contentType ?? "application/json");
        }

        var sent = req.Headers.Concat(req.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .Where(h => !h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            .Select(h => KeyValuePair.Create(h.Key, string.Join(", ", h.Value))).ToList();
        var baseUrl = _http.BaseAddress!.AbsoluteUri;
        var fullUrl = _mask.Scrub(new Uri(_http.BaseAddress!, url).AbsoluteUri);
        var urlTemplate = uses.Count > 0 && BuildUrl(t => t, true) is var ut && ut != url ? _mask.Scrub(baseUrl + ut) : null;
        var sentBody = bodyText is null ? null : _mask.Scrub(bodyText);
        var bodyTemplate = bodyText is not null && uses.Count > 0 && BodyText(rawBody!, contentType) is var bt && bt != bodyText ? _mask.Scrub(bt) : null;
        void Record(int? status, string? responseBody, long ms, HttpResponseMessage? resp) => ctx.Exchanges.Add(new Exchange(
            req.Method.Method, fullUrl, _mask.Mask(sent), sentBody, status, responseBody is null ? null : _mask.Scrub(responseBody), ms, ctx.Preparing,
            resp is null ? null : ResponseHeaders(resp), startedAt, ctx.TestId, ctx.Step, false, urlTemplate, bodyTemplate,
            uses.Count == 0 ? null : uses, null, baseUrl, _opt.TimeoutSeconds));

        var clock = Stopwatch.StartNew();
        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(req, _ct); }
        catch (TaskCanceledException) { Record(null, null, clock.ElapsedMilliseconds, null); throw; }
        var raw = await resp.Content.ReadAsStringAsync(_ct);
        Record((int)resp.StatusCode, raw, clock.ElapsedMilliseconds, resp);
        var status = (int)resp.StatusCode;
        resp.Dispose();
        // Inloggningen kan ha upphört (t.ex. efter en utloggning): logga in igen och skicka anropet en gång till.
        if (status == 401 && !retried && await TryReloginAsync(headers, operationId))
            return await SendAsync(ctx, method, path, query, headers, rawBody, contentType, operationId, retried: true);
        return (status, ResponseStore.ParseBody(raw), raw);
    }

    private ApiSpec? _spec;
    private string? _token;

    /// <summary>Hämtar en ny token när den nuvarande nekades och byter den i de globala headers. Falskt om anropet inte ska göras om.</summary>
    private async Task<bool> TryReloginAsync(IDictionary<string, string> callHeaders, string? operationId)
    {
        if (_opt.Relogin is not { } relogin || _token is not { } stale || _spec is null || operationId == _opt.LoginOperation) return false;
        var (name, _) = AuthSchemes.TokenHeader(_spec, stale);
        if (callHeaders.Keys.Contains(name, StringComparer.OrdinalIgnoreCase)) return false;   // anropet bär sin egen inloggning
        string fresh;
        try { fresh = await relogin(stale, _ct); }
        catch (Exception e) when (e is not OperationCanceledException) { return false; }
        if (fresh.Length == 0) return false;
        var (headerName, headerValue) = AuthSchemes.TokenHeader(_spec, fresh);
        _globalHeaders[headerName] = headerValue;
        _mask = _mask.WithSecretValues([fresh]);
        _token = fresh;
        return true;
    }

    private static bool IsReadingMethod(string method) => method.ToUpperInvariant() is "GET" or "HEAD" or "OPTIONS";

    /// <summary>Svarshuvudena som maskad lista: Set-Cookie m.fl. hemliga blir ***, kända hemliga värden utbytta.</summary>
    private IReadOnlyDictionary<string, string> ResponseHeaders(HttpResponseMessage resp) =>
        _mask.Mask(resp.Headers.Concat(resp.Content.Headers).Select(h => KeyValuePair.Create(h.Key, string.Join(", ", h.Value))))
            .ToDictionary(kv => kv.Key, kv => _mask.Scrub(kv.Value), StringComparer.OrdinalIgnoreCase);

    // --- Text ---------------------------------------------------------------------------

    /// <summary>Kort beskrivning av fallet (samma fras som i rubriken, se <see cref="PlainLanguage.RulePhrase"/>).</summary>
    private static string Describe(CaseMeta m) => PlainLanguage.RulePhrase(m);
}
