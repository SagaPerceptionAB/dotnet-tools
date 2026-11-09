namespace TestGen.Core.Run;

/// <summary>
/// Ett fel eller en varning i en bestämd cell i Excel-filen. Visas som <c>Tester!B3 (Anrop): text</c>
/// (eller <c>Tester!E1: text</c> när ingen rubrik hör till). Kolumnbokstaven kommer från rubrikmatchningen.
/// </summary>
/// <param name="Technical">Rå systemtext (ofta engelska); visas bara med --verbose.</param>
/// <param name="Scope">Miljön en Miljöer-rad hör till (null = gäller hela bladet).</param>
public sealed record ExcelIssue(string Sheet, int Row, int Col, string? Header, string Message, bool IsError = true, string? Technical = null, string? Scope = null)
{
    public string Cell => $"{Sheet}!{ExcelLayout.ColumnLetter(Col)}{Row}";
    public string Text => Header is null ? $"{Cell}: {Message}" : $"{Cell} ({Header}): {Message}";

    /// <summary>Sorterar per rad och kolumn.</summary>
    public static IEnumerable<ExcelIssue> InOrder(IEnumerable<ExcelIssue> issues) =>
        issues.OrderBy(i => i.Row).ThenBy(i => i.Col);
}

/// <summary>
/// Excel-filen har ett eller flera fel som måste rättas innan något körs. Alla fel är samlade, sorterade per rad.
/// Översätts av <see cref="FriendlyErrors"/> till exitkod 2 med åtgärden "Rätta cellerna ovan ...".
/// </summary>
public sealed class ExcelValidationException : Exception
{
    public string FileName { get; }
    public IReadOnlyList<ExcelIssue> Errors { get; }
    public IReadOnlyList<ExcelIssue> Warnings { get; }
    public string? Technical { get; }

    public ExcelValidationException(string path, IEnumerable<ExcelIssue> errors, IEnumerable<ExcelIssue>? warnings = null, bool noTestsRan = true)
        : base(Describe(Path.GetFileName(path), ExcelIssue.InOrder(errors).ToList(), ExcelIssue.InOrder(warnings ?? Enumerable.Empty<ExcelIssue>()).ToList(), noTestsRan))
    {
        FileName = Path.GetFileName(path);
        Errors = ExcelIssue.InOrder(errors).ToList();
        Warnings = ExcelIssue.InOrder(warnings ?? Enumerable.Empty<ExcelIssue>()).ToList();
        var tech = Errors.Where(e => e.Technical is { Length: > 0 }).Select(e => $"{e.Cell}: {e.Technical}").ToList();
        Technical = tech.Count == 0 ? null : string.Join("\n", tech);
    }

    private static string Describe(string file, List<ExcelIssue> errors, List<ExcelIssue> warnings, bool noTestsRan)
    {
        var lines = new List<string>
        {
            $"Excel-filen {file} har {(errors.Count == 1 ? "1 fel" : errors.Count + " fel")}" +
            (noTestsRan ? ", så inga tester kördes:" : ":"),
        };
        lines.AddRange(errors.Select(e => "  " + e.Text));
        if (warnings.Count > 0)
        {
            lines.Add(warnings.Count == 1 ? "Varning (stoppar inte):" : "Varningar (stoppar inte):");
            lines.AddRange(warnings.Select(w => "  " + w.Text));
        }
        return string.Join(Environment.NewLine, lines);
    }

    public static string Action(string fileName) =>
        $"Rätta cellerna ovan i {fileName} och kör igen. Hjälp finns på bladet Hjälp i Excel-filen.";
}
