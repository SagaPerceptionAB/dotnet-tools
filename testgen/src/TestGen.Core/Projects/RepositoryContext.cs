using TestGen.Core.Run;
using TestGen.Core.Settings;

namespace TestGen.Core.Projects;

/// <summary>Vad ett kommando arbetar mot: repots fakta (om en solution finns) och inställningarna i testgen.yaml.</summary>
public sealed record RepositoryContext(RepositoryFacts? Facts, TestGenSettings Settings)
{
    /// <summary>
    /// Läser solutionen (<paramref name="solutionArg"/> eller den som hittas från aktuell mapp) och testgen.yaml (<paramref name="configArg"/>
    /// eller den som hittas i testprojektet eller från solutionens mapp, se <see cref="TestGenSettings.Locate"/>). Är <paramref name="needFacts"/> falskt får solutionen saknas eller vara tvetydig.
    /// </summary>
    public static RepositoryContext Load(string? solutionArg, string? configArg, bool needFacts, string? startDirectory = null, string? testProject = null)
    {
        var start = startDirectory ?? Directory.GetCurrentDirectory();
        string? solution = solutionArg;
        if (solution is null)
        {
            try { solution = SolutionFinder.TryFind(start, "testgen"); }
            catch (UsageException) when (!needFacts) { /* flera att välja på, men inget behövs ur solutionen */ }
        }
        var facts = solution is null ? null : RepositoryFacts.Read(Path.GetFullPath(solution, solutionArg is null ? start : Directory.GetCurrentDirectory()));
        var settings = configArg is not null ? TestGenSettings.LoadFile(configArg) : facts?.LoadSettings(testProject) ?? TestGenSettings.Load(start);
        return new RepositoryContext(facts, settings);
    }

    /// <summary>
    /// API-projektet som <paramref name="apiArg"/> pekar ut med namn. Utan namn, eller med en adress, används det enda API:et i solutionen.
    /// Är <paramref name="required"/> sant och inget API kan väljas blir det ett fel som säger hur man pekar ut ett.
    /// </summary>
    public ProjectFacts? PickApi(string? apiArg, bool required)
    {
        var apis = Facts?.Apis.ToList() ?? [];
        var names = string.Join(", ", apis.Select(a => a.Name));
        if (apiArg is not null && !IsAddress(apiArg))
        {
            if (Facts is null)
                throw new UsageException($"Hittar ingen solution att slå upp API:et '{apiArg}' i.", "Gå till mappen där solutionen ligger, ange den med --solution, eller ange en adress: --api http://localhost:5080");
            return Facts.Apis.FirstOrDefault(a => a.Name.Equals(apiArg, StringComparison.OrdinalIgnoreCase))
                ?? throw new UsageException($"Okänt API '{apiArg}'." + Suggest.DidYouMean(apiArg, apis.Select(a => a.Name)) + (apis.Count == 0 ? " Solutionen har inga API-projekt." : $" API:er i solutionen: {names}."),
                    "Skriv projektnamnet som i solutionen, t.ex. --api ShopApi, eller en adress: --api http://localhost:5080");
        }
        if (apis.Count == 1) return apis[0];
        if (!required) return null;
        throw new UsageException(apis.Count == 0 ? "API:et som ska testas är inte utpekat." : $"Solutionen har flera API:er ({names}).",
            apis.Count == 0 ? "Ange adressen med --api http://localhost:5080 (och specen med --spec <fil>)." : $"Välj ett med --api <projektnamn>, t.ex. --api {apis[0].Name}.");
    }

    /// <summary>Specen: <paramref name="specArg"/>, annars den som testgen.yaml anger för API:et, annars den som finns vid projektet.</summary>
    public string SpecFor(ProjectFacts? api, string? specArg)
    {
        if (specArg is not null) return specArg;
        if (api is null) throw new UsageException("Specen är inte utpekad.", "Ange den med --spec <fil|url>, eller gå till en solution med ett API-projekt.");
        return Settings.SpecOf(api.Name) ?? Facts?.SpecOf(api)
            ?? throw new UsageException($"Hittar ingen spec för {api.Name}.", SpecLocator.MissingAdvice(api, $"apis.{api.Name}.spec"));
    }

    /// <summary>En adress (eller en <c>${env:NAMN}</c> som blir en adress), inte ett projektnamn.</summary>
    public static bool IsAddress(string text) => Sources.IsUrl(text) || EnvVars.HasPlaceholder(text);
}

/// <summary>Allt <c>testgen run</c> behöver veta om vad som ska testas: specen, inställningarna för körningen och testdatan.</summary>
/// <param name="Excel">Excel-filen som testgen.yaml anger för API:et (fullständig sökväg), annars null.</param>
public sealed record RunTarget(string Spec, RunSettings Settings, GeneratorOptions Generator, string? Project, string? ConfigPath,
    string? Excel, TestEnvironment Environment, RepositoryContext Repository)
{
    /// <summary>
    /// Slår ihop kommandoraden, solutionen och testgen.yaml. <paramref name="api"/> är ett projektnamn eller en adress; utan den används solutionens
    /// enda API. Utan <paramref name="spec"/> används specen för API:et. Adressen kommer från miljön (i <c>local</c> ur launchSettings.json).
    /// </summary>
    public static RunTarget Resolve(string? solution, string? config, string? api, string? spec, string? env,
        string? token = null, IEnumerable<string>? headers = null, string? timeout = null, Func<string, string?>? getEnv = null,
        string? startDirectory = null, string? localUrl = null, string? testProject = null)
    {
        var address = api is not null && RepositoryContext.IsAddress(api);
        var repo = RepositoryContext.Load(solution, config, needFacts: spec is null || api is null || !address, startDirectory, testProject);
        var project = repo.PickApi(api, required: api is null);
        var specPath = repo.SpecFor(project, spec);
        var environment = repo.Settings.Environment(env, getEnv);
        var settings = RunSettings.Resolve(address ? api : null, headers ?? [], token, timeout,
            environment, project?.Name, localUrl ?? project?.LocalUrl, getEnv);
        return new RunTarget(specPath, settings, repo.Settings.GeneratorFor(project?.Name), project?.Name, repo.Settings.Path,
            project is null ? null : repo.Settings.ExcelOf(project.Name), environment, repo);
    }
}
