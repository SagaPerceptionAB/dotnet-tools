using System.Text.Json.Nodes;
using ClosedXML.Excel;

using TestGen.Core.Run;

namespace TestGen.Excel;

/// <summary>Det som lästs ur bladet Tester: scenarier plus fel och varningar (ännu ej jämförda med specen).</summary>
public sealed record ExcelReadResult(List<ExcelScenario> Scenarios, List<ExcelIssue> Issues)
{
    public IEnumerable<ExcelIssue> Errors => Issues.Where(i => i.IsError);
    public IEnumerable<ExcelIssue> Warnings => Issues.Where(i => !i.IsError);
}

/// <summary>Läser bladet "Tester". Rader med samma Scenario är steg i samma scenario (i radordning).</summary>
public static class ExcelScenarioReader
{

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
        if (!wb.TryGetWorksheet(ExcelLayout.SheetName, out var ws))
            throw new InvalidOperationException($"Excel-filen saknar bladet \"{ExcelLayout.SheetName}\". Skapa en ny med: testgen template.");

        var issues = new List<ExcelIssue>();

        // Kolumner hittas via rubrikerna på rad 1 (ordningen får ändras); annars standardordning.
        var col = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < ExcelLayout.Headers.Length; i++) col[ExcelLayout.Headers[i]] = i + 1;
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unknown = new List<(string Text, int Col)>();
        foreach (var c in ws.Row(1).CellsUsed())
        {
            var h = c.GetString().Trim();
            if (h.Length == 0 || h == ExcelTemplate.HelpLinkText) continue;
            var known = ExcelLayout.Headers.FirstOrDefault(x => x.Equals(h, StringComparison.OrdinalIgnoreCase));
            if (known is not null) { col[known] = c.Address.ColumnNumber; found.Add(known); continue; }
            unknown.Add((h, c.Address.ColumnNumber));
        }
        var missing = ExcelLayout.Headers.Where(h => !found.Contains(h)).ToList();
        foreach (var (h, c) in unknown)
        {
            var guess = GuessHeader(h);
            // Ligger den okända rubriken där en saknad kolumn läses på standardplatsen används kolumnen ändå: säg det i en och samma varning.
            var read = missing.FirstOrDefault(m => col[m] == c);
            if (read is not null) missing.Remove(read);
            issues.Add(new ExcelIssue(ExcelLayout.SheetName, 1, c, null,
                $"rubriken '{h}' känns inte igen" + (read is null ? " och ignoreras." : $". Kolumnen läses ändå som '{read}' (standardplatsen); skriv rubriken '{read}' exakt så för att tysta varningen.") +
                (guess is null || guess == read ? "" : $" Menade du '{guess}'?") + $" Kända rubriker: {string.Join(", ", ExcelLayout.Headers)}.", IsError: false));
        }
        foreach (var h in missing)
            issues.Add(new ExcelIssue(ExcelLayout.SheetName, 1, col[h], null,
                $"rubriken '{h}' saknas på rad 1, så kolumn {ExcelLayout.ColumnLetter(col[h])} läses i stället på standardplatsen. Skriv rubriken '{h}' där om kolumnen ska användas.",
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
                issues.Add(new ExcelIssue(ExcelLayout.SheetName, r, col[header], header, message, true, technical));
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
                Columns = columns,
                StatusText = statusText,
                StatusInvalid = !statusOk,
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
        return Suggest.Closest(h, ExcelLayout.Headers) ??
               (plain.Length < 4 ? null : ExcelLayout.Headers.FirstOrDefault(k => k.ToLowerInvariant().Contains(plain) || plain.Contains(k.ToLowerInvariant())));
    }
}
