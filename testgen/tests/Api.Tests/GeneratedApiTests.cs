using TestGen.Scenarios;

namespace Api.Tests;

/// <summary>Ett testfall per genererat scenario i scenarios/generated/*.json.</summary>
[TestFixture]
public class GeneratedApiTests
{
    public static IEnumerable<TestCaseData> Scenarios()
    {
        foreach (var s in ScenarioCatalog.All)
            yield return new TestCaseData(s)
                .SetName($"{s.OperationId} [{s.Kind}] {s.Name}")
                .SetCategory(s.Kind == "negative" ? "negative" : "valid");
    }

    [TestCaseSource(nameof(Scenarios))]
    public async Task Scenario_returns_expected_status(Scenario scenario)
    {
        ApiEnvironment.RequireOrSkip();
        await new ScenarioRunner(ApiEnvironment.Request).RunAsync(scenario);
    }
}
