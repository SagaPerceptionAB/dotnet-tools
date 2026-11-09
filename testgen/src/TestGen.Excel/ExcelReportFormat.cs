using ClosedXML.Excel;
using TestGen.Core.Run;

namespace TestGen.Excel;

/// <summary>Rapport som Excel-arbetsbok (.xlsx): bladen Sammanfattning och Resultat.</summary>
public sealed class ExcelReportFormat : IReportFormat
{
    public string Extension => ".xlsx";
    public string Description => "Excel-rapport";

    public bool IsReport(string fullPath)
    {
        using var wb = new XLWorkbook(fullPath);
        return wb.Worksheets.Contains(SumSheet);
    }

    public void WriteFailure(string full, ReportInfo info, string what, string action, int finishedTests, HeaderMask mask)
    {
        using var wb = new XLWorkbook();
        wb.Properties.Title = $"Testresultat {info.When:yyyy-MM-dd}";
        var sum = wb.AddWorksheet("Sammanfattning");
        var rows = new (string, string)[]
        {
                ("Resultat", ReportWriter.FailureHeadline),
                ("Vad hände", what),
                ("Gör så här", action),
                ("Antal tester som hann bli klara", finishedTests.ToString()),
                ("Datum", ReportWriter.Stamp(info.When)),
                ("Adress", info.Api),
                ("Miljö", info.Env ?? ""),
                ("Seed", info.Seed.ToString()),
        };
        for (var i = 0; i < rows.Length; i++) { Put(sum, i + 1, 1, rows[i].Item1, mask); Put(sum, i + 1, 2, rows[i].Item2, mask); }
        sum.Column(1).Style.Font.Bold = true;
        sum.Columns(1, 2).AdjustToContents();
        wb.SaveAs(full);
    }

    private const string SumSheet = "Sammanfattning";
    private const string ResSheet = "Resultat";
    private const string TableName = "Testresultat";

    // Färger (alla text/bakgrund-par har kontrast minst 4,5:1).
    private static readonly XLColor HeaderFill = XLColor.FromHtml("#1F3864");     // vit text: 12:1
    private static readonly XLColor FailFill = XLColor.FromHtml("#F4B6B6");       // text 5A0000: 9:1
    private static readonly XLColor FailText = XLColor.FromHtml("#5A0000");
    private static readonly XLColor FailRowFill = XLColor.FromHtml("#FDE2E2");
    private static readonly XLColor OkFill = XLColor.FromHtml("#E6F3E8");         // text 14532D: 8:1
    private static readonly XLColor OkText = XLColor.FromHtml("#14532D");
    private static readonly XLColor SkipFill = XLColor.FromHtml("#ECEFF3");       // text 3E4756: 8:1
    private static readonly XLColor SkipText = XLColor.FromHtml("#3E4756");
    private static readonly XLColor LinkColor = XLColor.FromHtml("#0645AD");      // på vitt: 7,7:1

    private static readonly string[] ResultHeads =
    {
        "Id", "Resultat", "Vad testet gör", "Förklaring", "Anrop", "Förväntad status", "Faktisk status", "Tid (ms)",
        "Orsak", "Följd av", "Grupp", "Detaljer", "curl", "Svar (hela)", "Återskapa (bash)",
    };
    private const int ColId = 1, ColResult = 2, ColWhat = 3, ColExplain = 4, ColCall = 5, ColExpected = 6, ColActual = 7, ColTime = 8,
        ColCause = 9, ColFollow = 10, ColGroup = 11, ColDetails = 12, ColCurl = 13, ColFull = 14, ColRepro = 15;
    /// <summary>Svaret i kolumnen "Svar (hela)" kortas först efter så här många tecken.</summary>
    public const int FullResponseMax = 30000;
    private const int CellMax = 32000;

    public void Write(string fullPath, ReportInfo info, IReadOnlyList<ReportEntry> entries, HeaderMask mask)
    {
        using var wb = new XLWorkbook();
        wb.Properties.Title = $"Testresultat {info.When:yyyy-MM-dd}";
        wb.Properties.Subject = "Resultat av en testkörning";
        var sum = wb.AddWorksheet(SumSheet);   // först, och öppnas först
        var ws = wb.AddWorksheet(ResSheet);

        // --- Resultat: en riktig Excel-tabell ---
        for (var c = 0; c < ResultHeads.Length; c++) ws.Cell(1, c + 1).Value = ResultHeads[c];
        var row = 2;
        var rowOf = new Dictionary<string, int>();
        foreach (var e in entries)
        {
            rowOf[e.Id] = row;
            var t = e.Plain(mask);
            var fail = e.Outcome == "FEL";
            Put(ws, row, ColId, e.Id, mask);
            Put(ws, row, ColResult, e.Outcome, mask);
            Put(ws, row, ColWhat, t.Title, mask);
            Put(ws, row, ColExplain, t.Explanation + (t.Hint is null ? "" : "\n\nTolkning: " + t.Hint), mask);
            Put(ws, row, ColCall, e.Call, mask);
            if (ExpectedStatus(e) is { } exp)
            {
                if (exp is int n) ws.Cell(row, ColExpected).SetValue(n); else ws.Cell(row, ColExpected).SetValue((string)exp);
            }
            if (e.Result.Status is { } st) ws.Cell(row, ColActual).SetValue(st);
            if (ExchangeText.Main(e.Result.Exchanges) is { } main) ws.Cell(row, ColTime).SetValue(main.ElapsedMs);
            if (t.Cause is { } cause) Put(ws, row, ColCause, PlainLanguage.CauseName(cause), mask);
            if (e.Result.RootCauseTestId is { } root)
            {
                Put(ws, row, ColFollow, root, mask);
                if (rowOf.TryGetValue(root, out var rootRow))
                {
                    ws.Cell(row, ColFollow).SetHyperlink(new XLHyperlink($"'{ResSheet}'!A{rootRow}"));
                    ws.Cell(row, ColFollow).Style.Font.FontColor = LinkColor;
                    ws.Cell(row, ColFollow).Style.Font.Underline = XLFontUnderlineValues.Single;
                }
            }
            Put(ws, row, ColGroup, e.Group, mask);

            var full = fail && ExchangeText.Main(e.Result.Exchanges) is { Status: not null, ResponseBody.Length: > 0 } m ? ExchangeText.Pretty(m.ResponseBody!) : null;
            var details = ReportWriter.DetailLines(e, mask, withTiming: false);
            // Varningar från svarskontrollen står först i Detaljer (också för OK-rader) så att de syns utan att öppna något annat.
            details.InsertRange(0, e.Result.Warnings.Select(w => mask.Scrub("Varning (påverkar inte resultatet): " + PlainLanguage.WarningText(w))));
            // Rader som lyckades behöver inte alla headers och bodies här (de finns i curl-kolumnen): håll raderna låga.
            if (!fail) details = details.Where(l => l.Length > 0 && !char.IsWhiteSpace(l[0])).ToList();
            if (full is not null)
                details = details.Select(l => l.Contains("... (avkortat") ? l + " – hela svaret finns i kolumnen Svar (hela)" : l).ToList();
            Put(ws, row, ColDetails, string.Join("\n", details), mask);
            Put(ws, row, ColCurl, ReportWriter.CurlOf(e, mask), mask);
            if (full is not null) Put(ws, row, ColFull, full, mask, FullResponseMax);
            if (ReportWriter.ReproOf(e, mask) is { Length: > 0 } repro) Put(ws, row, ColRepro, repro, mask);

            var r = ws.Cell(row, ColResult);
            switch (e.Outcome)
            {
                case "FEL":
                    ws.Range(row, 1, row, ResultHeads.Length).Style.Fill.BackgroundColor = FailRowFill;
                    r.Style.Font.Bold = true;
                    r.Style.Font.FontColor = FailText;
                    r.Style.Fill.BackgroundColor = FailFill;
                    r.Style.Border.OutsideBorder = XLBorderStyleValues.Medium;
                    r.Style.Border.OutsideBorderColor = FailText;
                    ws.Cell(row, ColId).Style.Font.Bold = true;
                    break;
                case "OK":
                    r.Style.Font.FontColor = OkText;
                    r.Style.Fill.BackgroundColor = OkFill;
                    break;
                default:
                    r.Style.Font.FontColor = SkipText;
                    r.Style.Font.Italic = true;
                    r.Style.Fill.BackgroundColor = SkipFill;
                    break;
            }
            row++;
        }

        var last = Math.Max(row - 1, 1);
        if (entries.Count > 0)
        {
            var table = ws.Range(1, 1, last, ResultHeads.Length).CreateTable(TableName);
            table.Theme = XLTableTheme.TableStyleLight1;   // diskret; rubrikfärg sätts uttryckligen nedan
            table.ShowRowStripes = false;
            table.ShowAutoFilter = true;
        }
        var head = ws.Range(1, 1, 1, ResultHeads.Length);
        head.Style.Font.Bold = true;
        head.Style.Font.FontColor = XLColor.White;
        head.Style.Fill.BackgroundColor = HeaderFill;
        head.Style.Alignment.WrapText = true;
        head.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

        double[] widths = { 8, 11, 40, 60, 30, 12, 12, 10, 26, 11, 22, 70, 90, 70, 90 };
        for (var c = 0; c < widths.Length; c++) ws.Column(c + 1).Width = widths[c];
        var body = ws.Range(2, 1, last, ResultHeads.Length);
        body.Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
        // Radbrytning på alla textkolumner. "Svar (hela)" bryts inte: ett långt svar skulle annars göra raden metervis hög
        // (hela texten ligger i cellen och syns i formelfältet).
        foreach (var c in new[] { ColId, ColResult, ColWhat, ColExplain, ColCall, ColCause, ColGroup, ColDetails, ColCurl, ColRepro })
            ws.Range(2, c, last, c).Style.Alignment.WrapText = true;
        ws.Range(2, ColExpected, last, ColTime).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        ws.SheetView.FreezeRows(1);
        ws.SheetView.FreezeColumns(1);

        // Utskrift: A4 liggande, en sida bred, rubrikraden upprepas. Id–Följd av skrivs ut (Detaljer, curl och hela svaret blir för breda).
        ws.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        ws.PageSetup.PaperSize = XLPaperSize.A4Paper;
        ws.PageSetup.FitToPages(1, 0);
        ws.PageSetup.SetRowsToRepeatAtTop(1, 1);
        ws.PageSetup.PrintAreas.Add(1, 1, last, ColFollow);
        ws.PageSetup.Footer.Center.AddText("Sida &P av &N");

        BuildSummary(sum, info, entries, mask, rowOf);

        sum.SetTabActive();
        sum.TabSelected = true;
        ws.TabSelected = false;
        wb.SaveAs(fullPath);
    }

    /// <summary>Förväntad status som tal när den är exakt en kod; annars (t.ex. 2xx eller flera) som text. Null om okänd.</summary>
    private static object? ExpectedStatus(ReportEntry e)
    {
        var list = e.Info?.ExpectedStatus;
        if (list is null || list.Count == 0) return null;
        if (list.Count == 1 && list[0].From == list[0].To) return list[0].From;
        return string.Join(", ", list.Select(x => x.ToString()));
    }

    private static void BuildSummary(IXLWorksheet sum, ReportInfo info, IReadOnlyList<ReportEntry> entries, HeaderMask mask,
        Dictionary<string, int> rowOf)
    {
        var (ok, fail, skipped) = ReportWriter.Count(entries);
        var outcomes = ReportWriter.Outcomes(entries, mask);
        sum.Column(1).Width = 26;
        sum.Column(2).Width = 60;
        sum.Column(3).Width = 70;
        sum.Column(4).Width = 70;
        sum.Column(5).Width = 30;
        var r = 1;

        // Beslutsmeningen först ("gick det bra, och var ligger felet?"), sedan orsakerna.
        var verdict = PlainLanguage.Verdict(outcomes);
        var head = sum.Cell(r, 1);
        Put(sum, r, 1, verdict, mask);
        var merged = sum.Range(r, 1, r, 4).Merge();
        merged.Style.Alignment.WrapText = true;
        merged.Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
        head.Style.Font.Bold = true;
        head.Style.Font.FontSize = 16;
        head.Style.Font.FontColor = fail > 0 ? FailText : OkText;
        sum.Row(r).Height = 26 * Math.Max(1, (int)Math.Ceiling(verdict.Length / 100.0));
        r += 2;

        void Label(string text)
        {
            Put(sum, r, 1, text, mask);
            sum.Cell(r, 1).Style.Font.Bold = true;
            sum.Cell(r, 1).Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
            sum.Cell(r, 1).Style.Alignment.WrapText = true;
        }
        void Number(string label, int n, XLColor? fill = null, XLColor? text = null)
        {
            Label(label);
            var c = sum.Cell(r, 2);
            c.SetValue(n);
            c.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
            c.Style.Font.Bold = true;
            if (fill is not null) { c.Style.Fill.BackgroundColor = fill; c.Style.Font.FontColor = text; }
            r++;
        }
        void Pair(string label, string value)
        {
            Label(label);
            Put(sum, r, 2, value, mask);
            sum.Cell(r, 2).Style.Alignment.WrapText = true;
            sum.Cell(r, 2).Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
            r++;
        }

        var causes = PlainLanguage.CauseSummary(outcomes);
        foreach (var c in causes)
            Pair(PlainLanguage.CauseHead(c.Cause), c.IdsText + PlainLanguage.CauseTail(c.Cause));
        if (causes.Count > 0) r++;

        Number("OK", ok, ok > 0 ? OkFill : null, OkText);
        Number("FEL", fail, fail > 0 ? FailFill : null, FailText);
        Number("HOPPAD", skipped, skipped > 0 ? SkipFill : null, SkipText);
        Number("Antal tester", entries.Count);
        r++;
        var results = entries.Select(e => e.Result).ToList();
        if (PlainLanguage.WarningSummary(PlainLanguage.WarningCount(results)) is { } warnSum) Pair("Varningar", warnSum + ". Se kolumnen Detaljer.");
        if (PlainLanguage.EnvironmentNote(results) is { } env) Pair("Miljö svarar inte", env);
        if (PlainLanguage.AuthNote(results, names: new PlainNames(info.Project, info.Env)) is { } auth) Pair("Inloggning", auth);
        if (ReportWriter.PartialLine(info, entries.Count) is { } partialLine) Pair("Delkörning", partialLine["Delkörning: ".Length..]);
        if (info.Notice is { } notice) Pair("Notis", notice);
        if (info.Comparison is { } cmp)
        {
            Pair("Jämförelse med", cmp.File + (cmp.StartedAt is null ? "" : $" (körd {cmp.StartedAt})"));
            foreach (var (label, text) in cmp.Rows) Pair(label, text);
        }
        Pair("Datum", ReportWriter.Stamp(info.When));
        if (info.Duration is { } dur) Pair("Tid", ReportWriter.FormatDuration(dur));
        Pair("Adress", info.Api);
        if (!string.IsNullOrWhiteSpace(info.Env)) Pair("Miljö", info.Env!);
        Pair("Seed", info.Seed.ToString() + (info.SeedRandom ? " (valdes av verktyget)" : ""));
        if (info.TimeoutSeconds is { } to) Pair("Timeout", $"{to} s per anrop");
        if (info.Version is { } ver) Pair("testgen-version", ver);
        Pair("Obs", "Känsliga värden (t.ex. token) skrivs aldrig i rapporten. I kolumnen curl står en variabel (t.ex. $TOKEN) i stället; sätt den med export innan du kör raden.");
        r++;

        // Tester med FEL, med länk till raden i Resultat.
        var failed = entries.Where(e => e.Outcome == "FEL").ToList();
        if (failed.Count > 0)
        {
            Put(sum, r, 1, "Tester med FEL", mask);
            sum.Cell(r, 1).Style.Font.Bold = true;
            sum.Cell(r, 1).Style.Font.FontSize = 13;
            r++;
            var hdr = r;
            string[] heads = { "Id", "Vad testet gör", "Förklaring", "Kör om bara detta", "Orsak" };
            for (var c = 0; c < heads.Length; c++) sum.Cell(r, c + 1).Value = heads[c];
            var hr = sum.Range(r, 1, r, heads.Length);
            hr.Style.Font.Bold = true;
            hr.Style.Font.FontColor = XLColor.White;
            hr.Style.Fill.BackgroundColor = HeaderFill;
            r++;
            foreach (var e in failed)
            {
                var t = e.Plain(mask);
                var id = sum.Cell(r, 1);
                Put(sum, r, 1, e.Id, mask);
                id.SetHyperlink(new XLHyperlink($"'{ResSheet}'!A{rowOf[e.Id]}"));
                id.Style.Font.FontColor = LinkColor;
                id.Style.Font.Underline = XLFontUnderlineValues.Single;
                Put(sum, r, 2, t.Title, mask);
                Put(sum, r, 3, t.Explanation + (t.Hint is null ? "" : " " + t.Hint), mask);
                if (ReportWriter.RerunCommand(info, e.Id) is { } cmd) Put(sum, r, 4, cmd, mask);
                if (PlainLanguage.CauseLabel(t.Cause, e.Result) is { } cl) Put(sum, r, 5, cl["Orsak: ".Length..], mask);
                r++;
            }
            var rows = sum.Range(hdr + 1, 1, r - 1, 5);
            rows.Style.Alignment.WrapText = true;
            rows.Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
            rows.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
            rows.Style.Border.BottomBorderColor = XLColor.FromHtml("#8A93A3");
        }

        // Kommandona sist: de är långa och hör inte till svaret på "gick det bra?".
        if (ReportWriter.RerunCommand(info) is { } all)
        {
            r++;
            Pair("Kör om allt", all);
            foreach (var n in ReportWriter.RerunNotes(info)) Pair("Obs", n);
        }

        sum.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        sum.PageSetup.PaperSize = XLPaperSize.A4Paper;
        sum.PageSetup.FitToPages(1, 0);
    }

    /// <summary>
    /// Skriver text (maskad, utan styrtecken som Excel inte tillåter). Alltid som text, aldrig som formel.
    /// Längre än <paramref name="max"/> tecken kortas och markeras med "… (avkortat)".
    /// </summary>
    private static void Put(IXLWorksheet ws, int row, int col, string text, HeaderMask mask, int max = CellMax)
    {
        var t = mask.Scrub(text);
        if (t.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
            t = new string(t.Where(c => !char.IsControl(c) || c is '\n' or '\r' or '\t').ToArray());
        if (t.Length > max)
        {
            var note = $"\n… (avkortat, {t.Length} tecken totalt)";
            t = t[..(max - note.Length)] + note;
        }
        ws.Cell(row, col).SetValue(t);
    }
}
