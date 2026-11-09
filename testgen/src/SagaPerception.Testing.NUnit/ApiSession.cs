using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework.Internal;
using SagaPerception.Testing;
using TestGen.Core;
using TestGen.Core.Projects;
using TestGen.Core.Run;
using TestGen.Core.Settings;

namespace SagaPerception.Testing.NUnit;

/// <summary>
/// En fixturs körning mot ett API: appen i testprocessen (TestServer) eller miljöns adress, värdpolicyn för appens utgående anrop,
/// lokala tjänster (databas, lagring, Redis) i containrar när appen behöver dem, och resultaten som skrivs till resultatfilen.
/// </summary>
internal sealed class ApiSession : IResultSession
{
    private readonly ApiPlan _plan;
    private readonly TestGenTarget _target;
    private readonly HostGate _gate;
    private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyDictionary<string, TestOutcome>>>> _groups = new();
    private readonly ConcurrentDictionary<string, ReportEntry> _entries = new();
    private readonly DateTime _startedAt = DateTime.Now;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private IApiFactory? _factory;
    private ServiceContainers? _containers;
    private string? _notReady;
    private string? _startFailure;
    private HeaderMask _mask;
    private readonly TestUser? _testUser;
    private string? _loginToken;
    private readonly SemaphoreSlim _loginLock = new(1, 1);

    private ApiSession(ApiPlan plan)
    {
        _plan = plan;
        _target = plan.Target!;
        _gate = new HostGate(_target.Settings.Hosts, _target.SpecDirectories, _target.Run.Generator.Seed);
        _mask = HeaderMask.Default.WithSecretValues(_target.Config.Secrets);
        // Appen i testprocessen får en testanvändare, om inte en token eller ett inloggningssteg anges.
        if (_target.InProcess && !_target.HasToken && _target.Login is null)
        {
            _testUser = TestUser.Create(_target.Claims);
            _mask = _mask.WithSecretValues([_testUser.Token]);
        }
    }

    internal static async Task<ApiSession> StartAsync(ApiPlan plan)
    {
        var session = new ApiSession(plan);
        if (plan.Target!.InProcess) await session.StartInProcessAsync();
        return session;
    }

    /// <summary>Varför appen inte startar i testprocessen (och vad man ändrar), annars null. Då ska testerna falla, inte hoppas över.</summary>
    public string? StartFailure => _startFailure;

    /// <summary>Containrarna som startades för appens lokala tjänster, annars null.</summary>
    internal ServiceContainers? Containers => _containers;

    /// <summary>Varför testerna inte kan köras för att något saknas i miljön (Docker), annars null.</summary>
    public string? NotReady => _notReady;

    /// <summary>Hanteraren mot appen i testprocessen (för tester), eller null när appen inte körs där.</summary>
    internal HttpMessageHandler? AppHandler => _factory?.CreateHandler();

    private IApiFactory NewFactory(IEnumerable<ServiceValue> replaced)
    {
        var factory = ApiFactory.Create(_target.Project).WithEnvironment(_target.HostEnvironment).WithServices(_gate.Register).WithServices(ProjectTarget.AnswerAsInProduction);
        foreach (var (key, value) in _target.Config.Values) factory.WithSetting(key, value);
        foreach (var service in replaced) factory.WithSetting(service.Key, _containers!.ValueOf(service.Kind));
        if (_testUser is not null) factory.WithServices(_testUser.Register);
        return factory;
    }

    private async Task StartInProcessAsync()
    {
        ServiceFindings found;
        try
        {
            // Bara tjänsterna byggs, som med dotnet ef: appens kod efter Build() (migreringar, kontroller) körs först när appen startar på riktigt.
            var settings = _target.Config.Values.ToDictionary(kv => kv.Key, kv => (string?)kv.Value);
            using var host = ApiFactory.BuildHost(_target.Project, settings, _target.ProjectFacts.Directory, _target.HostEnvironment);
            found = ServiceDiscovery.Find(host.Services, host.Services.GetRequiredService<IConfiguration>());
        }
        catch (Exception e) when (FriendlyErrors.IsOpenApiVersionConflict(e))
        {
            _startFailure = Explain(e);
            return;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _startFailure = _target.CouldNotStart(e);
            return;
        }
        if (found.Real is { } real)
        {
            _startFailure = _target.RealService(real);
            return;
        }
        if (found.Unmapped is { } unmapped)
        {
            _startFailure = _target.UnmappedDatabase(unmapped);
            return;
        }
        var local = found.Local.ToList();
        if (local.Count > 0)
        {
            try { _containers = await ServiceContainers.StartAsync(found.LocalKinds, ComposeImages.Find(_target.Facts.SolutionDirectory)); }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                _notReady = $"Starta Docker och kör igen: appen använder {string.Join(", ", found.LocalKinds.Select(ServiceCatalog.NameOf))}, som testgen startar i containrar. Tekniskt: {e.Message}";
                return;
            }
        }
        if (_containers is not null)
        {
            // Direkt till stderr: konsolen och TestContext visar inget för tester som går igenom.
            using var stderr = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
            foreach (var kind in found.LocalKinds)
                stderr.WriteLine($"testgen startade {ServiceCatalog.NameOf(kind)} i en container ({_containers.ImageOf(kind)}).");
        }
        try
        {
            _factory = NewFactory(local);
            _ = _factory.Services;
        }
        catch (Exception e) when (FriendlyErrors.IsOpenApiVersionConflict(e))
        {
            _startFailure = Explain(e);
            return;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _startFailure = _target.CouldNotStart(e);
            return;
        }
        if (_target.Login is { } login && !_target.HasToken) await LoginAsync(login);
    }

    /// <summary>Loggar in en gång mot appen; token används i alla testerna. Misslyckas det fäller det fixturen med vad som hände och vad man ändrar.</summary>
    private async Task LoginAsync(LoginSettings login)
    {
        try
        {
            _loginToken = await ObtainTokenAsync(login, CancellationToken.None);
            _mask = _mask.WithSecretValues([_loginToken]);
        }
        catch (LoginException e) { _startFailure = _mask.Scrub(e.Text); }
        catch (Exception e) when (FriendlyErrors.IsOpenApiVersionConflict(e)) { _startFailure = Explain(e); }
    }

    private Task<string> ObtainTokenAsync(LoginSettings login, CancellationToken ct) =>
        new TestRunner(_target.RunOptions(prepared: _plan.Prepared), new Terminal(TextWriter.Null, TextWriter.Null, false), _factory!.CreateHandler()).LoginAsync(login, ct);

    /// <summary>
    /// Loggar in igen när en token nekades (t.ex. efter en utloggning). Ett steg i taget: har en annan körning redan hämtat en ny token
    /// används den i stället för att logga in en gång till.
    /// </summary>
    private async Task<string> ReloginAsync(string rejected, CancellationToken ct)
    {
        await _loginLock.WaitAsync(ct);
        try
        {
            if (_loginToken is { } current && current != rejected) return current;
            var fresh = await ObtainTokenAsync(_target.Login!, ct);
            _loginToken = fresh;
            _mask = _mask.WithSecretValues([fresh]);
            return fresh;
        }
        finally { _loginLock.Release(); }
    }

    /// <summary>Värdarna som appens utgående anrop blockerats mot (värdpolicyn svarade 502), i ordning.</summary>
    private IReadOnlyList<string> BlockedHosts() => _gate.Calls.Where(c => c.Status == 502).Select(c => c.Host).ToList();

    /// <summary>Felet när appen och testgen har olika huvudversioner av Microsoft.OpenApi, på svenska med vad man gör.</summary>
    private static string Explain(Exception e) => string.Join(Environment.NewLine, FriendlyErrors.Translate(e).Lines(verbose: true, prefix: ""));

    /// <summary>Kör testet (och för ett Excel-steg hela scenariot, en gång) och ger NUnit utfallet.</summary>
    public async Task RunTestAsync(string id, CancellationToken ct)
    {
        if (_startFailure is not null) throw new StartFailureException(_startFailure);
        if (_notReady is not null) Assert.Inconclusive(_mask.Scrub(_notReady));
        var info = _plan.Tests.First(t => t.Id == id);
        var ids = info.Step is { } step ? _plan.Tests.Where(t => t.Step?.Scenario == step.Scenario).Select(t => t.Id).ToList() : [id];
        var group = await _groups.GetOrAdd(ids[0], _ => new Lazy<Task<IReadOnlyDictionary<string, TestOutcome>>>(() => RunGroupAsync(ids, ct))).Value;
        var outcome = group[id];
        var verdict = OutcomeMapping.Decide(outcome.Outcome, outcome.Cause);
        if (outcome.Cause is { } cause) OutcomeMapping.AddCategory(CauseCodes.Code(cause));
        if (verdict == Verdict.Pass) return;
        OutcomeMapping.Apply(verdict, FailureText.Build(outcome, _mask, _gate.Calls, _factory?.Telemetry.Logs ?? []));
    }

    private async Task<IReadOnlyDictionary<string, TestOutcome>> RunGroupAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        var progress = new StringWriter();
        var runner = new TestRunner(_target.RunOptions(ids, prepared: _plan.Prepared, token: _testUser?.Token ?? _loginToken, testUser: _testUser is not null,
            blockedHosts: BlockedHosts, relogin: _loginToken is not null ? ReloginAsync : null), new Terminal(TextWriter.Null, progress, false), _factory?.CreateHandler());
        try { await runner.RunAsync(ct); }
        catch (ApiUnreachableException e) { Assert.Inconclusive($"{e.Message} {e.Tip}"); }
        catch (Exception e) when (FriendlyErrors.IsOpenApiVersionConflict(e)) { Assert.Fail(Explain(e)); }
        foreach (var warning in progress.ToString().Split((char)10).Where(l => l.StartsWith("Varning:", StringComparison.Ordinal)))
            TestContext.Out.WriteLine(_mask.Scrub(warning.TrimEnd()));
        _mask = runner.Mask.WithSecretValues(_target.Config.Secrets);
        foreach (var entry in runner.ReportEntries) _entries[entry.Id] = entry;
        return runner.Outcomes.ToDictionary(o => o.Id);
    }

    /// <summary>Hämtar appens spec och jämför den med den incheckade.</summary>
    public async Task CheckSpecAsync(CancellationToken ct)
    {
        if (_startFailure is not null) throw new StartFailureException(_startFailure);
        if (_notReady is not null) Assert.Inconclusive(_mask.Scrub(_notReady));
        if (_target.ProjectFacts.IsGenerated(_target.Run.Spec)) Assert.Inconclusive(SpecCheck.GeneratedAtBuild);
        string? served = null;
        try { served = await SpecCheck.FetchAsync(_factory!.CreateHandler(), ct); }
        catch (Exception e) when (FriendlyErrors.IsOpenApiVersionConflict(e)) { Assert.Fail(Explain(e)); }
        if (served is null) Assert.Inconclusive($"Appen serverar ingen spec på {string.Join(" eller ", SpecCheck.Paths)}, så det finns inget att jämföra med.");
        var diff = SpecCheck.Compare(_target.Run.Spec, served);
        if (diff.Count > 0)
            Assert.Fail($"Specen i repot ({RelativeSpec()}) stämmer inte med appen:{Environment.NewLine}  {string.Join(Environment.NewLine + "  ", diff)}{Environment.NewLine}" +
                        $"Uppdatera filen med appens spec ({SpecCheck.Paths[0]}) och checka in den.");
    }

    private string RelativeSpec() => Sources.IsUrl(_target.Run.Spec)
        ? _target.Run.Spec
        : Terminal.Relative(_target.Run.Spec, _target.Facts.SolutionDirectory);

    /// <summary>Skriver resultatfilen med de tester som körts. Null om inget test kördes.</summary>
    public string? WriteResult()
    {
        if (_entries.IsEmpty) return null;
        var entries = _entries.Values.ToList();
        var info = new ReportInfo(_startedAt, _target.InProcess ? _target.Project : _target.Run.Settings.Api, _target.Environment.Name, _target.Run.Generator.Seed)
        {
            Spec = RelativeSpec(),
            Excel = _target.Run.Excel is { } excel ? Terminal.Relative(excel, _target.Facts.SolutionDirectory) : null,
            TimeoutSeconds = _target.Run.Settings.TimeoutSeconds,
            Version = TestRunner.Version,
            Project = _target.Project,
            Layer = "api",
            PossibleTotal = _plan.Tests.Count,
            Partial = entries.Count < _plan.Tests.Count,
            Duration = _clock.Elapsed,
        };
        return ResultFile.Write(_target.TestProjectDirectory, _target.Project, info, entries, _mask);
    }

    public async ValueTask DisposeAsync()
    {
        _factory?.Dispose();
        await _gate.DisposeAsync();
        if (_containers is not null) await _containers.DisposeAsync();
    }
}
