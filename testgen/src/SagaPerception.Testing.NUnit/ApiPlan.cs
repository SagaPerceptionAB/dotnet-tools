using System.Collections.Concurrent;
using System.Reflection;
using TestGen.Core.Run;

namespace SagaPerception.Testing.NUnit;

/// <summary>
/// Testfallen för ett API-projekt, planerade av motorn utan att något körs. Delas mellan upptäckten och körningen eftersom NUnit
/// bygger testfallen med en annan fixtur-instans än den som kör dem.
/// </summary>
internal sealed class ApiPlan
{
    public const string SpecCheckName = "Specen i repot stämmer med appen";
    public const string ErrorName = "testgen kunde inte förbereda testerna";

    private static readonly ConcurrentDictionary<(string Assembly, string Project), ApiPlan> Cache = new();

    private ApiPlan(TestGenTarget? target, IReadOnlyList<TestInfo> tests, IReadOnlyList<string> keys, string? error, PreparedPlan? prepared = null)
    {
        Target = target;
        Tests = tests;
        Keys = keys;
        Error = error;
        Prepared = prepared;
    }

    /// <summary>Null när det inte gick att förbereda; då finns <see cref="Error"/>.</summary>
    public TestGenTarget? Target { get; }

    public IReadOnlyList<TestInfo> Tests { get; }

    /// <summary>Stabil nyckel per test i samma ordning som <see cref="Tests"/> (<c>api:createProduct:missing:name</c>).</summary>
    public IReadOnlyList<string> Keys { get; }

    /// <summary>Specen och testfallen, inlästa och genererade en gång; körningarna av enskilda tester återanvänder dem.</summary>
    public PreparedPlan? Prepared { get; }

    /// <summary>Varför testerna inte kunde planeras (på svenska, med vad man ska göra), annars null.</summary>
    public string? Error { get; }

    /// <summary>Appen serverar sin spec och körs i testprocessen: då jämförs den med den incheckade.</summary>
    public bool HasSpecCheck => Target is { InProcess: true } t && t.ProjectFacts.ServesSpec;

    public static ApiPlan For(Assembly testAssembly, string project) =>
        Cache.GetOrAdd((testAssembly.Location, project), _ => Create(testAssembly, project));

    private static ApiPlan Create(Assembly testAssembly, string project) =>
        Create(() => TestGenTarget.Resolve(testAssembly, project), project);

    /// <summary>Planerar testerna. Ett fel i konfigurationen blir <see cref="Error"/>, inte ett undantag.</summary>
    internal static ApiPlan Create(Func<TestGenTarget> resolve, string project)
    {
        try
        {
            var target = resolve();
            var runner = new TestRunner(target.RunOptions(listOnly: true), new Terminal(TextWriter.Null, TextWriter.Null, false));
            runner.RunAsync().GetAwaiter().GetResult();
            var keys = TestKeys.For(runner.PlannedTests);
            return new ApiPlan(target, runner.PlannedTests, keys, runner.PlannedTests.Count == 0 && !(target.InProcess && target.ProjectFacts.ServesSpec)
                ? $"Specen för {project} har inga anrop att testa (eller alla är bortvalda i testgen.yaml)."
                : null, runner.Prepared);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new ApiPlan(null, [], [], string.Join(Environment.NewLine, FriendlyErrors.Translate(e).Lines(verbose: true, prefix: "")));
        }
    }

    /// <summary>Testnamnet: <c>T-001 POST /customers giltig indata</c>.</summary>
    public static string NameOf(TestInfo test) => $"{test.Id} {test.Call} {test.Description}";

    public IEnumerable<TestCaseData> CaseData()
    {
        if (Error is not null)
        {
            yield return new TestCaseData(ApiTests.ErrorKind, "").SetName(ErrorName);
            yield break;
        }
        for (var i = 0; i < Tests.Count; i++)
        {
            var test = Tests[i];
            var data = new TestCaseData(ApiTests.TestKind, test.Id).SetName(NameOf(test)).SetCategory("api");
            if (OutcomeMapping.IsMutating(test.Method)) data.SetCategory("mutating");
            data.SetProperty("key", Keys[i]);
            yield return data;
        }
        if (HasSpecCheck)
            yield return new TestCaseData(ApiTests.SpecKind, "").SetName(SpecCheckName).SetCategory("api");
    }
}
