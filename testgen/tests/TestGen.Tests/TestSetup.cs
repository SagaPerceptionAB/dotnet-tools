using TestGen.Core.Run;
using TestGen.Excel;

namespace TestGen.Tests;

/// <summary>Kopplar Excel-rapporten till motorn en gång för alla tester (CLI:t gör detsamma vid start).</summary>
[SetUpFixture]
public class TestSetup
{
    [OneTimeSetUp]
    public void RegisterReportFormats() => ReportWriter.Register(new ExcelReportFormat());
}
