namespace SagaPerception.Testing.NUnit;

/// <summary>
/// API-testerna för ett projekt, i <c>dotnet test</c>: samma testfall som <c>testgen run</c>, mot appen i testprocessen. Hela testklassen:
/// <code>public sealed class ShopApiTests() : ApiTests("ShopApi");</code>
/// Testprojektet behöver en ProjectReference till API-projektet. Miljön väljs med <c>TESTGEN_ENV</c> (standard <c>local</c>).
/// </summary>
/// <param name="project">API-projektets namn, som i solutionen och i testgen.yaml.</param>
[TestFixture]
public abstract class ApiTests(string project) : IPlannedFixture
{
    internal const string TestKind = "test";
    internal const string SpecKind = "spec";
    internal const string ErrorKind = "error";

    private ApiSession? _session;

    /// <summary>API-projektets namn.</summary>
    internal string Project => project;

    IEnumerable<TestCaseData> IPlannedFixture.PlannedCases(System.Reflection.Assembly testAssembly) => ApiPlan.For(testAssembly, Project).CaseData();

    [OneTimeSetUp]
    public async Task StartApp()
    {
        var plan = ApiPlan.For(GetType().Assembly, Project);
        if (plan.Error is not null) return;
        _session = await ApiSession.StartAsync(plan);
        // Ett fel här fäller alla fixturens tester med samma meddelande (utan stack).
        if (_session.StartFailure is { } failure) throw new StartFailureException(failure);
    }

    [OneTimeTearDown]
    public Task StopApp() => ResultFile.FinishAsync(_session);

    [PlannedCases]
    public async Task Run(string kind, string id)
    {
        if (kind == ErrorKind) Assert.Fail(ApiPlan.For(GetType().Assembly, Project).Error ?? "okänt fel");
        var ct = TestContext.CurrentContext.CancellationToken;
        if (kind == SpecKind) await _session!.CheckSpecAsync(ct);
        else await _session!.RunTestAsync(id, ct);
    }
}
