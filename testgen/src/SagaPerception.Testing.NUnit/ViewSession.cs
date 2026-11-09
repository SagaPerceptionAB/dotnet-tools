using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using SagaPerception.Testing;
using TestGen.Browser;
using TestGen.Core.Run;
using TestGen.Core.Views;
using TestGen.Mock;

namespace SagaPerception.Testing.NUnit;

/// <summary>
/// En fixturs körning mot en webbapp: appen på Kestrel i testprocessen med API-anropen pekade på en mock ur specen, eller miljöns adress,
/// en Chromium, och resultaten som skrivs till resultatfilen. Appens övriga utgående anrop går genom värdpolicyn.
/// </summary>
internal sealed class ViewSession : IResultSession
{
    private readonly ViewPlan _plan;
    private readonly ViewTarget _target;
    private readonly HostGate _gate;
    private readonly Dictionary<ApiBinding, MockServer> _mocks = [];
    private readonly ConcurrentDictionary<string, ReportEntry> _entries = new();
    private readonly DateTime _startedAt = DateTime.Now;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly bool _trace;
    private IApiFactory? _factory;
    private BrowserHost? _browser;
    private string? _baseUrl;
    private string? _notReady;
    private string? _startFailure;
    private readonly HeaderMask _mask;
    private readonly TestUser? _testUser;

    private ViewSession(ViewPlan plan)
    {
        _plan = plan;
        _target = plan.Target!;
        _gate = new HostGate(_target.Settings.Hosts, _target.SpecDirectories, _target.Settings.Seed);
        _mask = HeaderMask.Default.WithSecretValues(_target.Config.Secrets);
        // Appen i testprocessen får en testanvändare, och webbläsaren bär dess token till appen.
        if (_target.InProcess)
        {
            _testUser = TestUser.Create(_target.Claims);
            _mask = _mask.WithSecretValues([_testUser.Token]);
        }
        _trace = _target.Settings.UiTrace || Environment.GetEnvironmentVariable("TESTGEN_TRACE") == "1";
    }

    internal static async Task<ViewSession> StartAsync(ViewPlan plan)
    {
        var session = new ViewSession(plan);
        await session.StartAsync();
        return session;
    }

    private async Task StartAsync()
    {
        if (_plan.Cases.Count == 0) return;
        try { _browser = await BrowserHost.StartAsync(); }
        catch (ChromiumMissingException e)
        {
            _notReady = e.Message;
            return;
        }
        if (!_target.InProcess)
        {
            _baseUrl = _target.App.Url;
            return;
        }
        try
        {
            _factory = ApiFactory.Create(_target.Project).WithEnvironment(_target.HostEnvironment).WithServices(_gate.Register).WithServices(ProjectTarget.AnswerAsInProduction);
            if (_testUser is not null) _factory.WithServices(_testUser.Register);
            foreach (var (key, value) in _target.Config.Values) _factory.WithSetting(key, value);
            foreach (var api in _plan.Apis)
            {
                var mock = await MockServer.StartAsync(api.Spec, api.Spec.Title ?? api.Api.Project, new MockOptions { Seed = _target.Settings.Seed });
                _mocks[api] = mock;
                _gate.AllowOwn(mock.Url);
                _factory.WithSetting(api.ConfigKey, mock.Url);
            }
            _baseUrl = _factory.StartOnKestrel();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _startFailure = _target.CouldNotStart(e);
        }
    }

    /// <summary>Varför appen inte startar i testprocessen (och vad man ändrar), annars null. Då ska testerna falla, inte hoppas över.</summary>
    public string? StartFailure => _startFailure;

    /// <summary>Kör testet i en egen webbläsarkontext och ger NUnit utfallet. Fallerar det bifogas skärmdump, loggar och, om det är påslaget, spår.</summary>
    public async Task RunTestAsync(string id)
    {
        if (_startFailure is not null) throw new StartFailureException(_startFailure);
        if (_notReady is not null) Assert.Inconclusive(_notReady);
        var testCase = _plan.Cases.First(c => c.Id == id);
        foreach (var mock in _mocks.Values) mock.Reset();
        var calls = _gate.Calls.Count;

        await using var browser = await _browser!.NewSessionAsync(new SessionOptions(_baseUrl, _trace, Headers: _testUser is null ? null : new Dictionary<string, string> { [_testUser.Header.Key] = _testUser.Header.Value }));
        var context = new UiContext(_plan, _mocks, _gate, _target.Environment.ReadOnly,
            Path.Combine(_target.TestProjectDirectory, "screenshots"), Path.Combine(_target.TestProjectDirectory, ReportWriter.ResultsDirectory, "screenshots"));
        var outcome = await new UiRunner(browser, context).RunAsync(testCase);

        var mask = _mask;
        var message = outcome.Verdict == Verdict.Pass ? "" : MessageOf(outcome, calls);
        _entries[id] = EntryOf(testCase, outcome, mask.Scrub(message));
        if (outcome.Cause is { } cause) OutcomeMapping.AddCategory(CauseCodes.Code(cause));
        if (outcome.Verdict is Verdict.Fail or Verdict.Inconclusive) await AttachAsync(testCase, browser, mask);
        OutcomeMapping.Apply(outcome.Verdict, mask.Scrub(message));
    }

    private string MessageOf(UiOutcome outcome, int hostCallsBefore)
    {
        var sb = new StringBuilder(outcome.Message);
        AppendAppContext(sb, hostCallsBefore);
        return sb.ToString().TrimEnd();
    }

    private void AppendAppContext(StringBuilder sb, int hostCallsBefore) =>
        FailureText.AppendAppContext(sb, _gate.Calls.Skip(hostCallsBefore).ToList(), _factory?.Telemetry.Logs ?? []);

    private ReportEntry EntryOf(UiCase testCase, UiOutcome outcome, string message)
    {
        const string group = "Gränssnittstester";
        var info = new TestInfo(testCase.Id, group, testCase.View.Route, "", "", null, testCase.Description, []) { Key = testCase.Key };
        var lines = message.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var result = outcome.Verdict switch
        {
            Verdict.Pass => new CaseResult(null, true, false, "OK", []),
            Verdict.Fail => new CaseResult(null, false, false, lines[0], lines),
            Verdict.Inconclusive => new CaseResult(null, false, true, lines[0], lines) { SkipNote = message },
            _ => new CaseResult(null, false, true, lines[0], lines) { SkipCause = outcome.Cause },
        };
        return new ReportEntry(testCase.Id, group, testCase.View.Route, testCase.Description, result) { Info = info };
    }

    private async Task AttachAsync(UiCase testCase, BrowserSession browser, HeaderMask mask)
    {
        var directory = Path.Combine(_target.TestProjectDirectory, ReportWriter.ResultsDirectory, "attachments", _target.Project, testCase.Id);
        Directory.CreateDirectory(directory);
        void Attach(string name, string description) => TestContext.AddTestAttachment(Path.Combine(directory, name), description);
        void AttachText(string name, string description, IEnumerable<string> lines)
        {
            File.WriteAllText(Path.Combine(directory, name), mask.Scrub(string.Join(Environment.NewLine, lines)) + Environment.NewLine);
            Attach(name, description);
        }

        try
        {
            await File.WriteAllBytesAsync(Path.Combine(directory, "screenshot.png"), await browser.ScreenshotAsync());
            Attach("screenshot.png", "Sidan när testet föll");
        }
        catch (Exception e) when (e is Microsoft.Playwright.PlaywrightException or IOException) { /* sidan kan vara stängd; bilagan är en bonus */ }
        AttachText("console.txt", "Webbläsarens konsol", browser.ConsoleLog);
        foreach (var (api, mock) in _mocks)
            AttachText($"mock-{api.Api.Project}.txt", $"Anrop till mocken för {api.Api.Project}",
                mock.Calls.Select(c => $"{c.Method} {c.Path}{(c.Query.Length > 0 ? "?" + c.Query : "")} → {c.Status}{(c.Problems.Count > 0 ? " " + string.Join("; ", c.Problems) : "")}"));
        if (_factory is not null)
            AttachText("app-log.txt", "Appens logg", _factory.Telemetry.Logs.Select(l => $"[{l.Level}] {l.Category}: {l.Message}"));
        if (_trace && await browser.SaveTraceAsync(Path.Combine(directory, "trace.zip"))) Attach("trace.zip", "Playwrights spårfil (kan innehålla känsliga uppgifter)");
    }

    /// <summary>Skriver resultatfilen med de tester som körts. Null om inget test kördes.</summary>
    public string? WriteResult()
    {
        if (_entries.IsEmpty) return null;
        var entries = _entries.Values.ToList();
        var info = new ReportInfo(_startedAt, _target.Project, _target.Environment.Name, _target.Settings.Seed)
        {
            Spec = _plan.ViewsPath,
            TimeoutSeconds = _target.App.TimeoutSeconds,
            Version = TestRunner.Version,
            Project = _target.Project,
            Layer = "ui",
            PossibleTotal = _plan.Cases.Count,
            Partial = entries.Count < _plan.Cases.Count,
            Duration = _clock.Elapsed,
        };
        return ResultFile.Write(_target.TestProjectDirectory, _target.Project, info, entries, _mask);
    }

    public async ValueTask DisposeAsync()
    {
        _factory?.Dispose();
        foreach (var mock in _mocks.Values) await mock.DisposeAsync();
        if (_browser is not null) await _browser.DisposeAsync();
        await _gate.DisposeAsync();
    }
}
