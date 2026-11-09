using System.Reflection;
using TestGen.Core.Projects;
using TestGen.Core.Settings;

namespace SagaPerception.Testing.NUnit;

/// <summary>Det testgen vet om en webbapp som testprojektet pekar ut med namn: samma uppslag av solution, testgen.yaml och miljö som för ett API.</summary>
internal sealed class ViewTarget : ProjectTarget
{
    private ViewTarget(string project, RepositoryContext repository, TestEnvironment environment, string testProjectDirectory, ProcessSettings config)
        : base(project, repository, environment, testProjectDirectory, config) { }

    /// <summary>Appens inställningar i miljön; <c>Url</c> är adressen när appen redan körs någon annanstans.</summary>
    public Endpoint App => Environment.App(Project);

    /// <summary>Claims för testanvändaren (<c>apps.NAMN.auth.claims</c>).</summary>
    public IReadOnlyDictionary<string, string> Claims => Settings.AppClaims(Project);

    protected override string SettingsSection => "apps";

    protected override string? ConfiguredEnvironment => Settings.AppEnvironment(Project);

    public override bool InProcess => RunsInProcess(Environment, Project);

    private static bool RunsInProcess(TestEnvironment environment, string project) =>
        environment.Name.Equals(TestGenSettings.LocalEnvironment, StringComparison.OrdinalIgnoreCase) && environment.App(project).Url is null;

    public static ViewTarget Resolve(Assembly testAssembly, string project, Func<string, string?>? getEnv = null) =>
        Resolve(Path.GetDirectoryName(testAssembly.Location) ?? Directory.GetCurrentDirectory(), testAssembly.GetName().Name, project, getEnv);

    public static ViewTarget Resolve(string startDirectory, string? testProjectName, string project, Func<string, string?>? getEnv = null,
        string? testProjectDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        var repository = RepositoryContext.Load(null, null, needFacts: true, startDirectory, testProjectName);
        var found = repository.Facts!.Project(project);
        if (found is not { Kind: ProjectKind.Web })
        {
            var apps = string.Join(", ", repository.Facts.WebApps.Select(a => a.Name));
            throw new UsageException(found is null ? $"Hittar inte webbappen '{project}' i solutionen." : $"'{project}' är ingen webbapp.",
                apps.Length == 0 ? "Solutionen har inga webbappar." : $"Skriv projektnamnet som i solutionen: {apps}.");
        }
        var environment = repository.Settings.Environment(null, getEnv);
        var config = RunsInProcess(environment, found.Name) ? repository.Settings.AppProcessSettings(found.Name, getEnv) : ProcessSettings.None;
        return new ViewTarget(found.Name, repository, environment, testProjectDirectory ?? TestProjectDirectoryOf(repository, testProjectName, startDirectory), config);
    }
}
