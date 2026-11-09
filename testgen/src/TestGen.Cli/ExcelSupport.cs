using TestGen.Core.Run;
using TestGen.Excel;

namespace TestGen.Cli;

/// <summary>Kopplar Excel (egna tester och .xlsx-rapporter) till motorn. Det enda stället i CLI:t som känner till TestGen.Excel.</summary>
internal static class ExcelSupport
{
    public static readonly ITestSource Source = new ExcelTestSource();

    public static void Register() => ReportWriter.Register(new ExcelReportFormat());
}
