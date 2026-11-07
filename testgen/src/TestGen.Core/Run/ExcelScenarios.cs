using System.Text.Json.Nodes;
using ClosedXML.Excel;
using TestGen.Scenarios;

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
        var col = Columns.TryGetValue(header, out var c) ? c : Array.IndexOf(ExcelScenarioReader.Headers, header) + 1;
        return $"{ExcelScenarioReader.SheetName}!{XLHelper.GetColumnLetterFromNumber(col)}{Row} ({header})";
    }

    /// <summary>Bara cellen för en rubrik, t.ex. <c>Tester!F3</c>.</summary>
    public string Cell(string header)
    {
        var col = Columns.TryGetValue(header, out var c) ? c : Array.IndexOf(ExcelScenarioReader.Headers, header) + 1;
        return $"{ExcelScenarioReader.SheetName}!{XLHelper.GetColumnLetterFromNumber(col)}{Row}";
    }

    public ExcelIssue Issue(string header, string message, bool error = true, string? technical = null) =>
        new(ExcelScenarioReader.SheetName, Row, Columns.TryGetValue(header, out var c) ? c : Array.IndexOf(ExcelScenarioReader.Headers, header) + 1,
            header, message, error, technical);
}

public sealed record ExcelScenario(string Name, List<ExcelStep> Steps);

/// <summary>Det som lästs ur bladet Tester: scenarier plus fel och varningar (ännu ej jämförda med specen).</summary>
public sealed record ExcelReadResult(List<ExcelScenario> Scenarios, List<ExcelIssue> Issues)
{
    public IEnumerable<ExcelIssue> Errors => Issues.Where(i => i.IsError);
    public IEnumerable<ExcelIssue> Warnings => Issues.Where(i => !i.IsError);
}

/// <summary>Läser bladet "Tester". Rader med samma Scenario är steg i samma scenario (i radordning).</summary>
public static class ExcelScenarioReader
{
    public const string SheetName = "Tester";
    public static readonly string[] Headers = { "Scenario", "Anrop", "Indata", "Spara som", "Förväntad status", "Förväntat svar" };

    /// <summary>
    /// Läser och kontrollerar bara skrivsättet (inte mot specen). Kastar <see cref="ExcelValidationException"/> med alla fel.
    /// Körningen använder <see cref="ReadAndCheck"/> som också jämför mot specen.
    /// </summary>
    public static List<ExcelScenario> Read(string path)
    {
        var r = ReadSheet(path, deferStatus: false);
        if (r.Errors.Any()) throw new ExcelValidationException(path, r.Errors, r.Warnings);
        return r.Scenarios;
    }

    /// <summary>
    /// Läser bladet och kontrollerar ALLA rader mot specen (anrop, fält, status, referenser) innan något körs.
    /// Kastar <see cref="ExcelValidationException"/> med alla fel sorterade per rad. Varningar (rubriker m.m.) returneras.
    /// </summary>
    public static (List<ExcelScenario> Scenarios, List<ExcelIssue> Warnings) ReadAndCheck(string path, IReadOnlyList<OperationInfo> ops)
    {
        var r = ReadSheet(path, deferStatus: true);
        var (scenarios, semantic) = ExcelValidator.Validate(r.Scenarios, ops);
        var all = r.Issues.Concat(semantic).ToList();
        var errors = all.Where(i => i.IsError).ToList();
        var warnings = ExcelIssue.InOrder(all.Where(i => !i.IsError)).ToList();
        if (errors.Count > 0) throw new ExcelValidationException(path, errors, warnings);
        return (scenarios, warnings);
    }

    public static ExcelReadResult ReadSheet(string path, bool deferStatus)
    {
        if (!File.Exists(path)) throw new MissingSourceException(SourceKind.Excel, Path.GetFullPath(path));
        // FileShare.ReadWrite så att det går att köra medan filen är öppen i Excel.
        using var stream = FriendlyErrors.Guard(SourceKind.Excel, path, () => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        using var wb = FriendlyErrors.Guard(SourceKind.Excel, path, () => new XLWorkbook(stream));
        if (!wb.TryGetWorksheet(SheetName, out var ws))
            throw new InvalidOperationException($"Excel-filen saknar bladet \"{SheetName}\". Skapa en ny med: testgen template.");

        var issues = new List<ExcelIssue>();

        // Kolumner hittas via rubrikerna på rad 1 (ordningen får ändras); annars standardordning.
        var col = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < Headers.Length; i++) col[Headers[i]] = i + 1;
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unknown = new List<(string Text, int Col)>();
        foreach (var c in ws.Row(1).CellsUsed())
        {
            var h = c.GetString().Trim();
            if (h.Length == 0 || h == ExcelTemplate.HelpLinkText) continue;
            var known = Headers.FirstOrDefault(x => x.Equals(h, StringComparison.OrdinalIgnoreCase));
            if (known is not null) { col[known] = c.Address.ColumnNumber; found.Add(known); continue; }
            unknown.Add((h, c.Address.ColumnNumber));
        }
        var missing = Headers.Where(h => !found.Contains(h)).ToList();
        foreach (var (h, c) in unknown)
        {
            var guess = GuessHeader(h);
            // Ligger den okända rubriken där en saknad kolumn läses på standardplatsen används kolumnen ändå: säg det i en och samma varning.
            var read = missing.FirstOrDefault(m => col[m] == c);
            if (read is not null) missing.Remove(read);
            issues.Add(new ExcelIssue(SheetName, 1, c, null,
                $"rubriken '{h}' känns inte igen" + (read is null ? " och ignoreras." : $". Kolumnen läses ändå som '{read}' (standardplatsen); skriv rubriken '{read}' exakt så för att tysta varningen.") +
                (guess is null || guess == read ? "" : $" Menade du '{guess}'?") + $" Kända rubriker: {string.Join(", ", Headers)}.", IsError: false));
        }
        foreach (var h in missing)
            issues.Add(new ExcelIssue(SheetName, 1, col[h], null,
                $"rubriken '{h}' saknas på rad 1, så kolumn {XLHelper.GetColumnLetterFromNumber(col[h])} läses i stället på standardplatsen. Skriv rubriken '{h}' där om kolumnen ska användas.",
                IsError: false));

        var columns = new Dictionary<string, int>(col, StringComparer.OrdinalIgnoreCase);
        string Cell(IXLRow r, string header) => r.Cell(col[header]).GetString().Trim();

        var scenarios = new List<ExcelScenario>();
        var byName = new Dictionary<string, ExcelScenario>(StringComparer.OrdinalIgnoreCase);
        var current = "";
        var last = ws.LastRowUsed()?.RowNumber() ?? 1;
        for (var r = 2; r <= last; r++)
        {
            var row = ws.Row(r);
            var call = Cell(row, "Anrop");
            var name = Cell(row, "Scenario");
            if (call.Length == 0 && name.Length == 0 && Cell(row, "Indata").Length == 0) continue;   // tom rad
            void Err(string header, string message, string? technical = null)
            {
                issues.Add(new ExcelIssue(SheetName, r, col[header], header, message, true, technical));
            }

            if (call.Length == 0) Err("Anrop", "cellen är tom. Välj ett anrop i listan.");
            if (name.Length == 0) name = current.Length > 0 ? current : $"Rad {r}";   // tomt namn = samma scenario som raden ovanför
            current = name;

            var input = new List<KeyValuePair<string, JsonNode?>>();
            var expectedBody = new List<KeyValuePair<string, JsonNode?>>();
            var expectedPatterns = new List<KeyValuePair<string, string>>();
            try { input = FieldList.Parse(Cell(row, "Indata")); }
            catch (FieldListException e) { Err("Indata", e.Message, e.Technical); }
            try { (expectedBody, expectedPatterns) = FieldList.ParseExpected(Cell(row, "Förväntat svar")); }
            catch (FieldListException e) { Err("Förväntat svar", e.Message, e.Technical); }

            var statusText = Cell(row, "Förväntad status");
            var statusOk = StatusExpectation.TryParse(statusText, out var status);
            if (!statusOk && !deferStatus)
                Err("Förväntad status", $"förväntad status '{statusText}' är inte en statuskod. Skriv t.ex. 201, eller 2xx/4xx för en hel grupp.");

            // Raden tas med även om en cell är trasig, så att senare rader som refererar till dess "Spara som" inte ger följdfel.
            var step = new ExcelStep(r, call, input, Cell(row, "Spara som") is { Length: > 0 } save ? save : null, status, expectedBody, expectedPatterns)
            {
                Columns = columns, StatusText = statusText, StatusInvalid = !statusOk,
            };
            if (!byName.TryGetValue(name, out var sc))
            {
                sc = new ExcelScenario(name, new List<ExcelStep>());
                byName[name] = sc;
                scenarios.Add(sc);
            }
            sc.Steps.Add(step);
        }
        return new ExcelReadResult(scenarios, issues);
    }

    // "Status" eller "Forvantad status" -> "Förväntad status".
    private static string? GuessHeader(string h)
    {
        var plain = h.ToLowerInvariant();
        return Suggest.Closest(h, Headers) ??
               (plain.Length < 4 ? null : Headers.FirstOrDefault(k => k.ToLowerInvariant().Contains(plain) || plain.Contains(k.ToLowerInvariant())));
    }
}
