using System.Reflection;
using TestGen.Core;
using TestGen.Core.Projects;
using TestGen.Core.Run;
using TestGen.Core.Settings;
using TestGen.Excel;

namespace SagaPerception.Testing.NUnit;

/// <summary>
/// Allt testgen vet om ett API-projekt som testprojektet pekar ut med namn: solutionen hittas uppåt från testassemblyn, sedan projektfakta,
/// testgen.yaml och miljön (<c>TESTGEN_ENV</c>, annars <c>local</c>). Samma uppslag som <c>testgen run</c>.
/// </summary>
internal sealed class TestGenTarget : ProjectTarget
{
    public const string InProcessUrl = "http://localhost";

    private TestGenTarget(string project, RunTarget run, string testProjectDirectory, ProcessSettings config)
        : base(project, run.Repository, run.Environment, testProjectDirectory, config) => Run = run;

    public RunTarget Run { get; }

    /// <summary>Inloggningssteget (<c>apis.NAMN.login</c>), eller null.</summary>
    public LoginSettings? Login => Settings.LoginOf(Project);

    /// <summary>Claims för testanvändaren (<c>apis.NAMN.auth.claims</c>).</summary>
    public IReadOnlyDictionary<string, string> Claims => Settings.ApiClaims(Project);

    /// <summary>Token har angetts (testgen.yaml eller kommandoraden): då används varken testanvändaren eller inloggningssteget.</summary>
    public bool HasToken => Run.Settings.Token is not null;

    protected override string SettingsSection => "apis";

    protected override string? ConfiguredEnvironment => Settings.ApiEnvironment(Project);

    public override bool InProcess => RunsInProcess(Environment, Project);

    private static bool RunsInProcess(TestEnvironment environment, string project) =>
        environment.Name.Equals(TestGenSettings.LocalEnvironment, StringComparison.OrdinalIgnoreCase) && environment.Api(project).Url is null;

    public static TestGenTarget Resolve(Assembly testAssembly, string project, Func<string, string?>? getEnv = null) =>
        Resolve(Path.GetDirectoryName(testAssembly.Location) ?? Directory.GetCurrentDirectory(), testAssembly.GetName().Name, project, getEnv);

    /// <param name="startDirectory">Katalogen solutionen letas uppåt från (testassemblyns katalog).</param>
    /// <param name="testProjectName">Testprojektets namn, för att hitta katalogen där resultatfilen läggs.</param>
    public static TestGenTarget Resolve(string startDirectory, string? testProjectName, string project, Func<string, string?>? getEnv = null,
        string? testProjectDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        var run = RunTarget.Resolve(null, null, project, null, null, getEnv: getEnv, startDirectory: startDirectory, localUrl: InProcessUrl, testProject: testProjectName);
        var name = run.Project ?? project;
        var config = RunsInProcess(run.Environment, name) ? run.Repository.Settings.ApiProcessSettings(name, getEnv) : ProcessSettings.None;
        return new TestGenTarget(name, run, testProjectDirectory ?? TestProjectDirectoryOf(run.Repository, testProjectName, startDirectory), config);
    }

    /// <summary>Alternativen för motorn: samma som <c>testgen run</c>, med urval av tester och utan utskrift av rapporter.</summary>
    /// <param name="token">Token från testanvändaren eller inloggningssteget, om ingen token angetts.</param>
    /// <param name="testUser">Token är testgens testanvändare (ett nekat giltigt anrop hoppas då över).</param>
    public RunOptions RunOptions(IReadOnlyList<string>? tests = null, bool listOnly = false, PreparedPlan? prepared = null, string? token = null, bool testUser = false,
        Func<IReadOnlyList<string>>? blockedHosts = null, Func<string, CancellationToken, Task<string>>? relogin = null) => new()
        {
            Spec = Run.Spec,
            Api = Run.Settings.Api,
            Headers = Run.Settings.Headers,
            Token = Run.Settings.Token ?? token,
            UsesTestUser = testUser && Run.Settings.Token is null,
            InProcess = InProcess,
            BlockedHosts = blockedHosts,
            Relogin = Run.Settings.Token is null ? relogin : null,
            LoginOperation = Run.Settings.Token is null && relogin is not null ? Login?.Operation : null,
            SecretHeaderNames = Run.Settings.SecretHeaderNames,
            TimeoutSeconds = Run.Settings.TimeoutSeconds,
            EnvName = Run.Settings.EnvName,
            Excel = Run.Excel,
            Source = Run.Excel is null ? null : new ExcelTestSource(),
            Generator = Run.Generator,
            ReadOnly = Run.Settings.ReadOnly,
            Project = Project,
            Config = Run.ConfigPath,
            Tests = tests,
            ListOnly = listOnly,
            Prepared = prepared,
            AllowEmpty = true,
        };
}
