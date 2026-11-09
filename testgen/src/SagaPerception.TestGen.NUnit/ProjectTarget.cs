using TestGen.Core.Projects;
using TestGen.Core.Run;
using TestGen.Core.Settings;

namespace SagaPerception.TestGen.NUnit;

/// <summary>Det API- och UI-testerna har gemensamt om projektet de testar: repots fakta, testgen.yaml, miljön och var resultatet skrivs.</summary>
internal abstract class ProjectTarget(string project, RepositoryContext repository, TestEnvironment environment, string testProjectDirectory, ProcessSettings config)
{
    public string Project { get; } = project;

    /// <summary>Katalogen med testprojektets .csproj; resultatfilen läggs under den.</summary>
    public string TestProjectDirectory { get; } = testProjectDirectory;

    public RepositoryFacts Facts => repository.Facts!;

    public TestGenSettings Settings => repository.Settings;

    public TestEnvironment Environment => environment;

    /// <summary>Konfigurationen appen får när den körs i testprocessen (<c>settings</c> i testgen.yaml). Tom när miljön anger en adress.</summary>
    public ProcessSettings Config => config;

    public ProjectFacts ProjectFacts => Facts.Project(Project)!;

    /// <summary>
    /// Appen ska svara som i drift. I Development kastar minimal API:er <c>BadHttpRequestException</c> vid fel i kroppen (felsökningsstöd,
    /// <c>ThrowOnBadRequest</c>), vilket appens felhanterare ofta gör till 500 i stället för 400.
    /// </summary>
    public static void AnswerAsInProduction(Microsoft.Extensions.DependencyInjection.IServiceCollection services) =>
        Microsoft.Extensions.DependencyInjection.OptionsServiceCollectionExtensions.PostConfigure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(services, o => o.ThrowOnBadRequest = false);

    /// <summary>Miljön appen körs i i testprocessen: <c>environment</c> i testgen.yaml, annars Testing om appsettings.Testing.json finns, annars launch-profilens, annars Development.</summary>
    public string HostEnvironment => ProjectFacts.EnvironmentFor(ConfiguredEnvironment);

    /// <summary>Miljön testgen.yaml anger för appen, eller null.</summary>
    protected abstract string? ConfiguredEnvironment { get; }

    /// <summary>Var appens konfiguration står i testgen.yaml: <c>apis</c> eller <c>apps</c>.</summary>
    protected abstract string SettingsSection { get; }

    /// <summary>Appen körs i testprocessen: miljön är local och anger ingen adress. Annars testas miljöns adress.</summary>
    public abstract bool InProcess { get; }

    /// <summary>Katalogerna där en spec för mockade värdar letas: testgen.yaml, solutionen och projektet.</summary>
    public IReadOnlyList<string> SpecDirectories =>
        new[] { Settings.BaseDirectory, Facts.SolutionDirectory, ProjectFacts.Directory }.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Felet när appen inte startar i testprocessen: vad som hände och var det appen saknar anges. Hemligheter är maskade.</summary>
    public string CouldNotStart(Exception e) =>
        HeaderMask.Default.WithSecretValues(Config.Secrets).Scrub($"Appen {Project} kunde inte startas i testprocessen: {Describe(e)}{System.Environment.NewLine}" +
            $"Gör så här: ange det appen saknar under {SettingsSection}.{Project}.settings i {TestGenSettings.FileName}.");

    /// <summary>Felet när ett värde pekar på en riktig tjänst: testgen byter bara ut lokala. Värdet visas inte, bara nyckeln och värden.</summary>
    public string RealService(ServiceValue service) =>
        $"Appen {Project} pekar på en riktig tjänst: {service.Key} ({ServiceCatalog.NameOf(service.Kind)}{(service.Host.Length > 0 ? ", " + service.Host : "")}). testgen ansluter inte till riktiga tjänster.{System.Environment.NewLine}" +
        $"Gör så här: ange en lokal under {SettingsSection}.{Project}.settings i {TestGenSettings.FileName} ({service.Key}: <lokal värd>); testgen byter en lokal mot en container.";

    /// <summary>Felet när appens databas finns men testgen inte hittar vilken nyckel som har anslutningssträngen.</summary>
    public string UnmappedDatabase(ServiceKind kind) =>
        $"Appen {Project} använder {ServiceCatalog.NameOf(kind)}, men testgen hittar inte vilken konfigurationsnyckel som har anslutningssträngen.{System.Environment.NewLine}" +
        $"Gör så här: ange nyckeln under {SettingsSection}.{Project}.settings i {TestGenSettings.FileName} med valfritt värde (t.ex. ConnectionStrings:Database: testgen); testgen byter värdet mot containerns.";

    private static string Describe(Exception e)
    {
        var messages = new List<string>();
        for (var x = e; x is not null && messages.Count < 4; x = x.InnerException)
            if (x is not (System.Reflection.TargetInvocationException or AggregateException) && !messages.Contains(x.Message)) messages.Add(x.Message);
        return string.Join(" – ", messages);
    }

    /// <summary>Katalogen resultatet läggs i: testprojektets katalog, om den hittas bland solutionens projekt.</summary>
    protected static string TestProjectDirectoryOf(RepositoryContext repository, string? testProjectName, string fallback) =>
        (testProjectName is null ? null : repository.Facts!.Project(testProjectName))?.Directory ?? fallback;
}
