namespace TestGen.Core.Run;

/// <summary>
/// Det dyra i planeringen (specen inläst, testfallen genererade, Excel-filen kontrollerad), färdigt att återanvända. Ges av
/// <see cref="TestRunner.Prepared"/> och lämnas till <see cref="RunOptions.Prepared"/> i en körning med samma spec, Excel-fil och
/// inställningar; bara urvalet (<see cref="RunOptions.Tests"/>) och HTTP-hanteraren får skilja.
/// </summary>
public sealed class PreparedPlan
{
    internal PreparedPlan(ApiSpec spec, IReadOnlyList<(OperationInfo Operation, GeneratedCase Case)> cases,
        List<ExcelScenario> excelScenarios, List<ExcelIssue> excelWarnings)
    {
        Spec = spec;
        Cases = cases;
        ExcelScenarios = excelScenarios;
        ExcelWarnings = excelWarnings;
    }

    internal ApiSpec Spec { get; }

    internal IReadOnlyList<(OperationInfo Operation, GeneratedCase Case)> Cases { get; }

    internal List<ExcelScenario> ExcelScenarios { get; }

    internal List<ExcelIssue> ExcelWarnings { get; }
}
