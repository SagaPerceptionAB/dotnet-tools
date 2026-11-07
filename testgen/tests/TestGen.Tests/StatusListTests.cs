using ClosedXML.Excel;
using TestGen.Core;
using TestGen.Core.Run;

namespace TestGen.Tests;

public class StatusListTests
{
    private static string Spec => Path.Combine(FindRoot(), "samples", "shop.openapi.yaml");

    private static string FindRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "samples"))) d = d.Parent;   // repots rot, där exemplet ligger
        return d?.FullName ?? throw new InvalidOperationException("Hittar inte repots rot.");
    }

    private static XLWorkbook Build(out string path)
    {
        path = Path.Combine(Path.GetTempPath(), $"statuslista-{Guid.NewGuid():N}.xlsx");
        ExcelTemplate.Create(OpenApiLoader.Load(Spec), path);
        return new XLWorkbook(path);
    }

    private static List<string> Codes(IXLWorksheet lists, string opId)
    {
        var row = lists.Column(1).CellsUsed().First(c => c.GetString() == opId).Address.RowNumber;
        var n = (int)lists.Cell(row, 2).GetDouble();
        return Enumerable.Range(0, n).Select(i => lists.Cell(row, i + 3).GetString()).ToList();
    }

    [Test]
    public void Expected_status_has_dependent_list_with_warning()
    {
        using var wb = Build(out var path);
        try
        {
            var dv = wb.Worksheet("Tester").DataValidations
                .Single(v => v.Ranges.Any(r => r.RangeAddress.FirstAddress.ColumnNumber == 5));
            Assert.That(dv.AllowedValues, Is.EqualTo(XLAllowedValues.List));
            Assert.That(dv.ErrorStyle, Is.EqualTo(XLErrorStyle.Warning));
            Assert.That(dv.ShowErrorMessage, Is.True);
            Assert.That(dv.ErrorMessage, Does.Contain("finns inte i specen"));
            var r = dv.Ranges.Single().RangeAddress;
            Assert.That((r.FirstAddress.RowNumber, r.LastAddress.RowNumber, r.LastAddress.ColumnNumber), Is.EqualTo((2, 500, 5)));
            Assert.That(dv.Value, Does.Contain("OFFSET").And.Contain("MATCH($B2,"));
            Assert.That(wb.Worksheet("Tester").Cell("E2").Style.NumberFormat.Format, Is.EqualTo("@"));
        }
        finally { File.Delete(path); }
    }

    [Test]
    public void Lists_have_codes_from_the_spec_and_2xx_4xx_last_without_duplicates()
    {
        using var wb = Build(out var path);
        try
        {
            var lists = wb.Worksheet("Listor");
            Assert.That(lists.Visibility, Is.EqualTo(XLWorksheetVisibility.Hidden));
            var create = Codes(lists, "createOrder");
            Assert.That(create.Take(create.Count - 2), Does.Contain("201").And.Contain("400"));
            Assert.That(create.TakeLast(2), Is.EqualTo(new[] { "2xx", "4xx" }));
            foreach (var op in OpenApiLoader.Load(Spec))
            {
                var codes = Codes(lists, op.OperationId);
                Assert.That(codes, Is.Unique, op.OperationId);
                Assert.That(codes.TakeLast(2), Is.EqualTo(new[] { "2xx", "4xx" }), op.OperationId);
                Assert.That(codes, Does.Not.Contain("default"));
            }
        }
        finally { File.Delete(path); }
    }

    [Test]
    public void Status_codes_are_normalized_and_tail_is_deduplicated()
    {
        var op = new OperationInfo("x", "GET", "/x", Array.Empty<ParamInfo>(), null, new[] { 200 },
            ResponseCodes: new[] { "4xx", "default", "500", "200", "2xx", "404" });
        Assert.That(ExcelTemplate.StatusCodes(op), Is.EqualTo(new[] { "200", "404", "500", "2xx", "4xx" }));
        Assert.That(ExcelTemplate.StatusCodes(null), Is.EqualTo(new[] { "2xx", "4xx" }));
    }

    [Test]
    public void Formula_parts_are_computed_for_one_row()
    {
        // ClosedXML saknar OFFSET, så radnummer och antal (de delar som väljer område) räknas fram här;
        // själva OFFSET-listan verifieras i LibreOffice (se rapporten).
        using var wb = Build(out var path);
        try
        {
            var testsSheet = wb.Worksheet("Tester");
            var f = testsSheet.DataValidations.Single(v => v.Ranges.Any(r => r.RangeAddress.FirstAddress.ColumnNumber == 5)).Value;
            var rowExpr = f[(f.IndexOf("IFERROR(", StringComparison.Ordinal) + 8)..];
            rowExpr = rowExpr[..(rowExpr.IndexOf(",0)", StringComparison.Ordinal) + 3)]; // MATCH(...,0)
            var countExpr = f[f.LastIndexOf("INDEX(", StringComparison.Ordinal)..^1];
            var ops = OpenApiLoader.Load(Spec);
            var idx = ops.FindIndex(o => o.OperationId == "createOrder");
            var expected = Codes(wb.Worksheet("Listor"), "createOrder");

            testsSheet.Cell("B2").SetValue("createOrder");
            testsSheet.Cell("H3").FormulaA1 = rowExpr;
            testsSheet.Cell("H4").FormulaA1 = countExpr;
            Assert.That((int)testsSheet.Cell("H3").Value.GetNumber(), Is.EqualTo(idx + 1));
            Assert.That((int)testsSheet.Cell("H4").Value.GetNumber(), Is.EqualTo(expected.Count));

            testsSheet.Cell("B2").Clear();
            testsSheet.Cell("H4").FormulaA1 = countExpr;
            Assert.That((int)testsSheet.Cell("H4").Value.GetNumber(), Is.EqualTo(2));
        }
        finally { File.Delete(path); }
    }
}
