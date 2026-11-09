using TestGen.Core.Run;

namespace TestGen.Excel;

/// <summary>Egna tester ur bladet Tester i en Excel-fil.</summary>
public sealed class ExcelTestSource : ITestSource
{
    public (List<ExcelScenario> Scenarios, List<ExcelIssue> Warnings) ReadAndCheck(string path, IReadOnlyList<OperationInfo> ops) =>
        ExcelScenarioReader.ReadAndCheck(path, ops);

    public IReadOnlyList<string> ScenarioNames(string path) =>
        ExcelScenarioReader.ReadSheet(path, deferStatus: true).Scenarios
            .Select(s => s.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();
}
