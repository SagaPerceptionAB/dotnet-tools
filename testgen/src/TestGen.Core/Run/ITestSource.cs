namespace TestGen.Core.Run;

/// <summary>Egna tester ur en fil (Excel). Motorn känner bara till det här gränssnittet; TestGen.Excel implementerar det.</summary>
public interface ITestSource
{
    /// <summary>Läser filen och kontrollerar alla rader mot specen. Kastar <see cref="ExcelValidationException"/> med alla fel; varningar returneras.</summary>
    (List<ExcelScenario> Scenarios, List<ExcelIssue> Warnings) ReadAndCheck(string path, IReadOnlyList<OperationInfo> ops);

    /// <summary>Scenarionamnen i filen, utan kontroll mot specen.</summary>
    IReadOnlyList<string> ScenarioNames(string path);
}
