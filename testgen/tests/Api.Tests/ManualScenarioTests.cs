namespace Api.Tests;

/// <summary>Handskrivna flöden i scenarios/manual/*.yaml.</summary>
[TestFixture]
public class ManualScenarioTests
{
    public static IEnumerable<TestCaseData> Scenarios()
    {
        foreach (var m in ManualScenarioReader.LoadAll())
            yield return new TestCaseData(m).SetName(m.ToString()).SetCategory("manual");
    }

    [TestCaseSource(nameof(Scenarios))]
    public async Task Manual_scenario_passes(ManualScenario scenario)
    {
        ApiEnvironment.RequireOrSkip();
        await new ScenarioRunner(ApiEnvironment.Request).RunAsync(scenario);
    }
}
