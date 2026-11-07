using System.Text.Json.Nodes;
using ClosedXML.Excel;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using TestGen.Scenarios;
using TestGen.Core;
using TestGen.Core.Run;

namespace TestGen.Tests;

/// <summary>Mallens utseende och hjälptexter: Hjälp-bladet, inmatningsmeddelanden, Fält-bladet, utskrift och metadata.</summary>
public class ExcelTemplateTests
{
    private static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "samples"))) d = d.Parent;   // repots rot, där exemplet ligger
        return d?.FullName ?? throw new InvalidOperationException("Hittar inte repots rot.");
    }

    private static XLWorkbook Build(out string path)
    {
        path = Path.Combine(Path.GetTempPath(), $"mall-{Guid.NewGuid():N}.xlsx");
        ExcelTemplate.Create(OpenApiLoader.Load(Path.Combine(Root(), "samples", "shop.openapi.yaml")), path);
        return new XLWorkbook(path);
    }

    private static void With(Action<XLWorkbook, string> check)
    {
        using var wb = Build(out var path);
        try { check(wb, path); } finally { File.Delete(path); }
    }

    [Test]
    public void Template_and_sample_tests_give_no_warning_about_unknown_header()
    {
        With((_, path) =>
        {
            var ops = OpenApiLoader.Load(Path.Combine(Root(), "samples", "shop.openapi.yaml"));
            var (_, warnings) = ExcelScenarioReader.ReadAndCheck(path, ops);
            Assert.That(warnings, Is.Empty, "Hjälp-hänvisningen i H1 är ingen kolumnrubrik");
            var (_, sampleWarnings) = ExcelScenarioReader.ReadAndCheck(Path.Combine(Root(), "samples", "tester.xlsx"), ops);
            Assert.That(sampleWarnings, Is.Empty);
        });
    }

    [Test]
    public void Sheet_order_is_Tests_Help_Environments_Fields_and_hidden_Lists()
    {
        With((wb, _) =>
        {
            Assert.That(wb.Worksheets.Select(w => w.Name), Is.EqualTo(new[] { "Tester", "Hjälp", "Miljöer", "Fält", "Listor" }));
            Assert.That(wb.Worksheet("Listor").Visibility, Is.EqualTo(XLWorksheetVisibility.Hidden));
        });
    }

    [Test]
    public void Help_has_steps_glossary_pattern_table_and_no_merged_cells()
    {
        With((wb, _) =>
        {
            var ws = wb.Worksheet("Hjälp");
            Assert.That(ws.MergedRanges, Is.Empty);
            var text = string.Join("\n", ws.CellsUsed().Select(c => c.GetString()));
            foreach (var must in new[]
            {
                "Gör så här första gången", "1. Gå till bladet Tester", "4. Kör testerna",
                "Kolumn", "Vad du skriver", "Exempel", "Ordlista", "Vanliga mönster för fält~mönster", "Bra att veta",
                "OK – det gick bra.", "Skapad", "Felaktiga uppgifter", "Inte inloggad", "Saknar behörighet", "Finns inte", "Finns redan", "Fel i systemet",
                "svar", "anrop", "mönster (regex)", "Exakt värde", "Ska se ut så här", "inom citattecken",
                "sortera", "Rad 500", "Microsoft Excel", "Google Sheets", "skriv då koden själv", "${env:NAMN}",
            })
                Assert.That(text, Does.Contain(must).IgnoreCase, must);
            // Rubriker är större och fetstilta, och kolumnerna är måttligt breda med radbrytning.
            var first = ws.Cell("A1");
            Assert.That(first.Style.Font.Bold, Is.True);
            Assert.That(first.Style.Font.FontSize, Is.GreaterThan(12));
            Assert.That(ws.Cell("B5").Style.Alignment.WrapText, Is.True);
            Assert.That(ws.Column(1).Width + ws.Column(2).Width + ws.Column(3).Width, Is.InRange(90, 140));
        });
    }

    [Test]
    public void All_examples_in_the_pattern_table_work_with_FieldList_and_BodyMatcher()
    {
        Assert.That(ExcelTemplate.CommonPatterns.Length, Is.InRange(8, 10));
        foreach (var (pattern, _, example) in ExcelTemplate.CommonPatterns)
        {
            var value = example == "(tomt)" ? "" : example;
            var (equals, patterns) = FieldList.ParseExpected("f~" + pattern);
            Assert.That(equals, Is.Empty, pattern);
            Assert.That(patterns.Single().Value, Is.EqualTo(pattern));
            var body = new JsonObject { ["f"] = value };
            Assert.That(BodyMatcher.RegexMismatches(patterns, body), Is.Empty, $"{pattern} ska passa \"{example}\"");
        }
    }

    [Test]
    public void Patterns_are_not_too_permissive()
    {
        // Några värden som inte ska passa, så att tabellens förklaringar stämmer.
        (string Pattern, string Value)[] no =
        {
            ("^ORD-", "Beställning ORD-1"), ("\\d+", "abc"), ("^\\d+$", "12a"), ("^\\d{6}$", "12345"), ("@", "stina.example.se"),
            ("^[A-Z]{3}-[0-9]{4}$", "abc-1234"), ("(?i)^stina", "Anna Stina"), ("^(active|inactive)$", "activeX"), ("^$", "x"),
        };
        foreach (var (pattern, value) in no)
        {
            var (_, patterns) = FieldList.ParseExpected("f~" + pattern);
            Assert.That(BodyMatcher.RegexMismatches(patterns, new JsonObject { ["f"] = value }), Is.Not.Empty, $"{pattern} ska inte passa \"{value}\"");
        }
    }

    [Test]
    public void Pattern_with_semicolon_in_quotes_as_Help_describes_works()
    {
        var (_, patterns) = FieldList.ParseExpected("kod~\"^A;B$\"; namn=Anna");
        Assert.That(patterns.Single().Value, Is.EqualTo("^A;B$"));
        Assert.That(BodyMatcher.RegexMismatches(patterns, new JsonObject { ["kod"] = "A;B" }), Is.Empty);
        Assert.That(() => FieldList.ParseExpected("kod~^A;B$"), Throws.InstanceOf<ArgumentException>(), "utan citattecken delas mönstret vid ;");
    }

    [Test]
    public void Tests_sheet_has_input_message_and_header_comment_on_all_columns()
    {
        With((wb, _) =>
        {
            var ws = wb.Worksheet("Tester");
            for (var col = 1; col <= ExcelScenarioReader.Headers.Length; col++)
            {
                var dv = ws.DataValidations.Single(v => v.Ranges.Any(r => r.RangeAddress.FirstAddress.ColumnNumber == col));
                var (title, text) = ExcelTemplate.TesterPrompts[col - 1];
                Assert.That(dv.ShowInputMessage, Is.True, ExcelScenarioReader.Headers[col - 1]);
                Assert.That(dv.InputTitle, Is.EqualTo(title));
                Assert.That(dv.InputMessage, Is.EqualTo(text));
                Assert.That(title.Length, Is.LessThanOrEqualTo(32));
                Assert.That(text.Length, Is.LessThanOrEqualTo(255));
                Assert.That(ws.Cell(1, col).GetComment().Text, Is.EqualTo(text), "kommentar på rubriken");
            }
            Assert.That(ExcelTemplate.TesterPrompts[0].Text, Is.EqualTo("Namn på flödet. Tomt = samma som raden ovanför."));
            Assert.That(ExcelTemplate.TesterPrompts[3].Text, Is.EqualTo("Ett namn, t.ex. kund. Senare rader använder ${kund.id}."));
        });
    }

    [Test]
    public void Tests_sheet_has_autofilter_Help_link_and_examples_with_400_and_pattern()
    {
        With((wb, path) =>
        {
            var ws = wb.Worksheet("Tester");
            Assert.That(ws.AutoFilter.IsEnabled, Is.True);
            Assert.That(ws.Cell("H1").GetString(), Does.Contain("Hjälp finns på bladet Hjälp"));
            Assert.That(ws.Cell("H1").GetHyperlink().InternalAddress, Does.Contain("Hjälp"));

            var rows = Enumerable.Range(2, 8).Select(r => Enumerable.Range(1, 6).Select(c => ws.Cell(r, c).GetString()).ToArray()).Where(r => r[1].Length > 0).ToList();
            Assert.That(rows.Any(r => r[4] == "400"), Is.True, "negativt exempel");
            Assert.That(rows.Any(r => r[5].Contains('~')), Is.True, "exempel med ~");
            // Alla exempelrader går att läsa av verktyget (filen är redan sparad av Create).
            var scenarios = ExcelScenarioReader.Read(path);
            Assert.That(scenarios.Select(s => s.Name), Is.EqualTo(new[] { "Stamkund", "Orimligt antal" }));
        });
    }

    [Test]
    public void Environments_sheet_has_messages_examples_and_token_warning()
    {
        With((wb, path) =>
        {
            var ws = wb.Worksheet("Miljöer");
            for (var col = 1; col <= EnvironmentSheet.Headers.Length; col++)
            {
                var dv = ws.DataValidations.Single(v => v.Ranges.Any(r => r.RangeAddress.FirstAddress.ColumnNumber == col));
                var (title, text) = ExcelTemplate.EnvironmentPrompts[col - 1];
                Assert.That(dv.InputTitle, Is.EqualTo(title));
                Assert.That(dv.InputMessage, Is.EqualTo(text));
                Assert.That(title.Length, Is.LessThanOrEqualTo(32));
                Assert.That(text.Length, Is.LessThanOrEqualTo(255));
                Assert.That(ws.Cell(1, col).GetComment().Text, Is.EqualTo(text));
            }
            var all = string.Join("\n", ws.CellsUsed().Select(c => c.GetString()));
            Assert.That(all, Does.Contain("${env:API_TOKEN}").And.Contain("X-Tenant: acme").And.Contain("timeout")
                .And.Contain("Skriv aldrig en riktig token här – använd ${env:NAMN}"));

            // Exemplen ligger utanför kolumn A-C: bara miljön "lokal" med api läses, så ensam miljö väljs som förut.
            var envs = EnvironmentSheet.Read(path)!;
            Assert.That(envs.Select(e => e.Name), Is.EqualTo(new[] { "lokal" }));
            Assert.That(envs[0].Api, Is.EqualTo("http://localhost:5080"));
            Assert.That(envs[0].Token, Is.Null);
            Assert.That(envs[0].Headers, Is.Empty);
        });
    }

    [Test]
    public void Fields_sheet_has_everyday_types_and_ready_input_text_that_parses()
    {
        With((wb, _) =>
        {
            var ws = wb.Worksheet("Fält");
            Assert.That(Enumerable.Range(1, 6).Select(c => ws.Cell(1, c).GetString()),
                Is.EqualTo(new[] { "Anrop", "Fält", "Typ", "Obligatoriskt", "Villkor", "Skriv så här i Indata" }));
            var rows = ws.RowsUsed().Skip(1).Select(r => (Op: r.Cell(1).GetString(), Field: r.Cell(2).GetString(), Type: r.Cell(3).GetString(), Input: r.Cell(6).GetString())).ToList();
            var all = string.Join("\n", ws.CellsUsed().Select(c => c.GetString()));
            Assert.That(all, Does.Not.Contain("int32").And.Not.Contain("double").And.Not.Contain("body").And.Not.Contain("sökväg"));
            Assert.That(rows.Single(r => r.Field == "quantity").Input, Is.EqualTo("quantity=5"));
            Assert.That(rows.Single(r => r.Op == "getOrder" && r.Field == "id").Input, Is.EqualTo("id=5"));
            Assert.That(rows.Single(r => r.Field == "price").Type, Is.EqualTo("decimaltal"));
            Assert.That(rows.Single(r => r.Field == "quantity").Type, Is.EqualTo("heltal"));
            Assert.That(rows.Single(r => r.Field == "email").Type, Is.EqualTo("e-postadress"));
            Assert.That(rows.Single(r => r.Field == "status").Input, Is.EqualTo("status=active"));
            Assert.That(rows.Single(r => r.Field == "personnummer").Input, Is.EqualTo("personnummer=19900101-1239"), "från specens example");
            Assert.That(rows.Single(r => r.Field == "sku").Input, Does.Match("^sku=[A-Z]{3}-[0-9]{4}$"), "följer specens pattern");
            foreach (var r in rows.Where(r => r.Input.Length > 0))
                Assert.That(FieldList.Parse(r.Input).Single().Key, Is.EqualTo(r.Field));
        });
    }

    [Test]
    public void Input_example_respects_limits_and_quotes_when_needed()
    {
        OpenApiSchema Int(decimal? min = null, decimal? max = null) => new() { Type = "integer", Minimum = min, Maximum = max };
        Assert.That(ExcelTemplate.InputExample("n", Int()), Is.EqualTo("n=5"));
        Assert.That(ExcelTemplate.InputExample("n", Int(min: 10)), Is.EqualTo("n=10"));
        Assert.That(ExcelTemplate.InputExample("n", Int(max: 3)), Is.EqualTo("n=3"));
        Assert.That(ExcelTemplate.InputExample("n", new OpenApiSchema { Type = "boolean" }), Is.EqualTo("n=true"));
        Assert.That(ExcelTemplate.InputExample("d", new OpenApiSchema { Type = "string", Format = "date" }), Is.EqualTo("d=2025-01-31"));
        Assert.That(ExcelTemplate.InputExample("l", new OpenApiSchema { Type = "array" }), Is.EqualTo(""));
        // Ett textvärde som annars blir tal, eller innehåller ;, skrivs inom citattecken och tolkas som text.
        var zip = new OpenApiSchema { Type = "string", Enum = { new OpenApiString("123") } };
        Assert.That(ExcelTemplate.InputExample("kod", zip), Is.EqualTo("kod=\"123\""));
        var semi = new OpenApiSchema { Type = "string", Example = new OpenApiString("a;b") };
        Assert.That(ExcelTemplate.InputExample("kod", semi), Is.EqualTo("kod=\"a;b\""));
        Assert.That(FieldList.Parse(ExcelTemplate.InputExample("kod", semi)).Single().Value!.GetValue<string>(), Is.EqualTo("a;b"));
    }

    [Test]
    public void Print_is_A4_landscape_one_page_wide_with_repeated_header_row_and_workbook_has_title()
    {
        With((wb, _) =>
        {
            foreach (var name in new[] { "Tester", "Hjälp", "Miljöer", "Fält" })
            {
                var ps = wb.Worksheet(name).PageSetup;
                Assert.That(ps.PaperSize, Is.EqualTo(XLPaperSize.A4Paper), name);
                Assert.That(ps.PageOrientation, Is.EqualTo(XLPageOrientation.Landscape), name);
                Assert.That(ps.PagesWide, Is.EqualTo(1), name);
                Assert.That(ps.PagesTall, Is.EqualTo(0), name);
            }
            foreach (var name in new[] { "Tester", "Fält" })
            {
                var ps = wb.Worksheet(name).PageSetup;
                Assert.That((ps.FirstRowToRepeatAtTop, ps.LastRowToRepeatAtTop), Is.EqualTo((1, 1)), name);
            }
            Assert.That(wb.Properties.Title, Is.Not.Empty);
            Assert.That(wb.Properties.Subject, Is.Not.Empty);
        });
    }
}
