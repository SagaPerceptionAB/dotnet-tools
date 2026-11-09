namespace SagaPerception.TestGen.NUnit;

/// <summary>
/// UI-testerna för en webbapp, i <c>dotnet test</c>: appen körs i testprocessen och dess API-anrop går till en mock ur API:ets spec, så ett
/// formulär som tillåter mer än API:et hittas. Hela testklassen:
/// <code>public sealed class ShopWebViewTests() : ViewTests("ShopWeb");</code>
/// Testprojektet behöver en ProjectReference till webbappen, en <c>views.json</c> (<c>testgen views</c>) och Chromium
/// (<c>playwright install chromium</c>). Miljön väljs med <c>TESTGEN_ENV</c> (standard <c>local</c>).
/// </summary>
/// <param name="project">Webbappens projektnamn, som i solutionen och i testgen.yaml.</param>
[TestFixture]
public abstract class ViewTests(string project) : IPlannedFixture
{
    internal const string TestKind = "test";
    internal const string ErrorKind = "error";

    private ViewSession? _session;

    /// <summary>Webbappens projektnamn.</summary>
    internal string Project => project;

    IEnumerable<TestCaseData> IPlannedFixture.PlannedCases(System.Reflection.Assembly testAssembly) => ViewPlan.For(testAssembly, Project).CaseData();

    [OneTimeSetUp]
    public async Task StartApp()
    {
        var plan = ViewPlan.For(GetType().Assembly, Project);
        if (plan.Error is not null) return;
        _session = await ViewSession.StartAsync(plan);
        // Ett fel här fäller alla fixturens tester med samma meddelande (utan stack).
        if (_session.StartFailure is { } failure) throw new StartFailureException(failure);
    }

    [OneTimeTearDown]
    public Task StopApp() => ResultFile.FinishAsync(_session);

    [PlannedCases]
    public async Task Run(string kind, string id)
    {
        if (kind == ErrorKind) Assert.Fail(ViewPlan.For(GetType().Assembly, Project).Error ?? "okänt fel");
        await _session!.RunTestAsync(id);
    }
}
