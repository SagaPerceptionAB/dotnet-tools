using System.Text.Json.Nodes;

namespace TestGen.Core.Run;

public sealed record StatusExpectation(int From, int To)
{
    public static readonly StatusExpectation AnySuccess = new(200, 299);
    public bool Matches(int status) => status >= From && status <= To;
    public override string ToString() => From == To ? From.ToString() : To - From == 99 ? $"{From / 100}xx" : $"{From}-{To}";

    /// <summary>Tomt = valfri 2xx. 100-599 eller 2xx/4xx är giltiga.</summary>
    public static bool TryParse(string? text, out StatusExpectation status)
    {
        var t = (text ?? "").Trim();
        status = AnySuccess;
        if (t.Length == 0) return true;
        if (t.Length == 3 && t.EndsWith("xx", StringComparison.OrdinalIgnoreCase) && char.IsDigit(t[0]))
        {
            status = new StatusExpectation((t[0] - '0') * 100, (t[0] - '0') * 100 + 99);
            return true;
        }
        if (int.TryParse(t, out var n) && n is >= 100 and <= 599) { status = new StatusExpectation(n, n); return true; }
        return false;
    }

    public static StatusExpectation Parse(string? text, string where) =>
        TryParse(text, out var s) ? s
            : throw new ArgumentException($"{where}Förväntad status \"{(text ?? "").Trim()}\" är inte en statuskod (t.ex. 201) eller 2xx/4xx.");
}

/// <summary>Bladet Tester: namn, rubriker och kolumnbokstäver. Delas av motorn (cellhänvisningar) och TestGen.Excel (läsaren).</summary>
public static class ExcelLayout
{
    public const string SheetName = "Tester";
    public static readonly string[] Headers = { "Scenario", "Anrop", "Indata", "Spara som", "Förväntad status", "Förväntat svar" };

    /// <summary>1 = A, 27 = AA.</summary>
    public static string ColumnLetter(int column)
    {
        var letters = "";
        for (; column > 0; column = (column - 1) / 26) letters = (char)('A' + (column - 1) % 26) + letters;
        return letters;
    }
}

public sealed record ExcelStep(
    int Row, string Operation, List<KeyValuePair<string, JsonNode?>> Input, string? SaveAs,
    StatusExpectation Status, List<KeyValuePair<string, JsonNode?>> ExpectedBody,
    /// <summary>Förväntat svar skrivet <c>fält~regex</c>: fältets värde (som text) ska matcha regexet.</summary>
    List<KeyValuePair<string, string>>? ExpectedPatterns = null)
{
    public List<KeyValuePair<string, string>> ExpectedPatterns { get; init; } = ExpectedPatterns ?? new();

    /// <summary>Kolumnnummer per rubrik (som rubrikerna faktiskt matchades på rad 1). Tom om steget skapats utan Excel.</summary>
    public IReadOnlyDictionary<string, int> Columns { get; init; } = new Dictionary<string, int>();
    /// <summary>Förväntad status som den stod i cellen; <see cref="StatusInvalid"/> om den inte gick att tolka.</summary>
    public string StatusText { get; init; } = "";
    public bool StatusInvalid { get; init; }

    /// <summary>Cellen för en rubrik, t.ex. <c>Tester!B3 (Anrop)</c>.</summary>
    public string CellRef(string header)
    {
        var col = Columns.TryGetValue(header, out var c) ? c : Array.IndexOf(ExcelLayout.Headers, header) + 1;
        return $"{ExcelLayout.SheetName}!{ExcelLayout.ColumnLetter(col)}{Row} ({header})";
    }

    /// <summary>Bara cellen för en rubrik, t.ex. <c>Tester!F3</c>.</summary>
    public string Cell(string header)
    {
        var col = Columns.TryGetValue(header, out var c) ? c : Array.IndexOf(ExcelLayout.Headers, header) + 1;
        return $"{ExcelLayout.SheetName}!{ExcelLayout.ColumnLetter(col)}{Row}";
    }

    public ExcelIssue Issue(string header, string message, bool error = true, string? technical = null) =>
        new(ExcelLayout.SheetName, Row, Columns.TryGetValue(header, out var c) ? c : Array.IndexOf(ExcelLayout.Headers, header) + 1,
            header, message, error, technical);
}

public sealed record ExcelScenario(string Name, List<ExcelStep> Steps);
