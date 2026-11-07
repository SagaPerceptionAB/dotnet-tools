using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using TestGen.Core;
using TestGen.Core.Run;

namespace TestGen.Tests;

public class CurlCommandTests
{
    private static Exchange Ex(string method, string url, Dictionary<string, string> headers, string? body, HeaderMask? mask = null) =>
        new(method, url, (mask ?? HeaderMask.Default).Mask(headers), body, 500, "{}", 5);

    [Test]
    public void Builds_line_with_method_headers_and_body()
    {
        var curl = CurlCommand.Build(Ex("POST", "http://x/orders", new() { ["Content-Type"] = "application/json" }, "{\"quantity\":0}"));
        Assert.That(curl, Is.EqualTo("curl -X POST 'http://x/orders' -H 'Content-Type: application/json' --data-raw '{\"quantity\":0}'"));
    }

    [Test]
    public void Single_quote_in_body_and_url_is_escaped()
    {
        var curl = CurlCommand.Build(Ex("POST", "http://x/a?q=it's", new(), "{\"name\":\"O'Brien\"}"));
        Assert.That(curl, Does.Contain("'http://x/a?q=it'\\''s'"));
        Assert.That(curl, Does.Contain("--data-raw '{\"name\":\"O'\\''Brien\"}'"));
    }

    [Test]
    public void Without_body_no_data_flag_and_secret_header_is_masked()
    {
        var mask = HeaderMask.For(new Dictionary<string, string> { ["Authorization"] = "Bearer abc123" });
        var curl = CurlCommand.Build(Ex("GET", "http://x/orders/1", new() { ["Authorization"] = "Bearer abc123" }, null, mask));
        Assert.That(curl, Is.EqualTo("curl -X GET 'http://x/orders/1' -H \"Authorization: Bearer $TOKEN\""));
        Assert.That(curl, Does.Not.Contain("abc123").And.Not.Contain("***"));
    }

    [TestCase("Basic dXNlcjpoZW1saWc=", "Basic")]
    [TestCase("Token hemlig-1", "Token")]
    [TestCase("ApiKey hemlig-1", "ApiKey")]
    public void Curl_keeps_the_scheme_from_Authorization(string raw, string scheme)
    {
        var headers = new Dictionary<string, string> { ["Authorization"] = raw };
        var mask = HeaderMask.For(headers);
        var ex = Ex("GET", "http://x/orders/1", headers, null, mask);
        Assert.That(ex.Headers["Authorization"], Is.EqualTo(scheme + " ***"));
        var curl = CurlCommand.Build(ex);
        Assert.That(curl, Is.EqualTo($"curl -X GET 'http://x/orders/1' -H \"Authorization: {scheme} $TOKEN\""));
        Assert.That(curl, Does.Not.Contain("hemlig-1").And.Not.Contain("dXNlcjpoZW1saWc=").And.Not.Contain("***"));
        Assert.That(CurlCommand.Variables(ex), Is.EqualTo(new[] { ("TOKEN", "Authorization") }));
    }

    [Test]
    public void Curl_without_known_scheme_is_Bearer_as_before()
    {
        var headers = new Dictionary<string, string> { ["Authorization"] = "hemlig-1" };
        var curl = CurlCommand.Build(Ex("GET", "http://x/a", headers, null, HeaderMask.For(headers)));
        Assert.That(curl, Does.EndWith("-H \"Authorization: Bearer $TOKEN\""));
        Assert.That(curl, Does.Not.Contain("hemlig-1"));
    }

    [Test]
    public void Other_secret_headers_get_uppercase_variable_and_regular_headers_are_unchanged()
    {
        var mask = new HeaderMask(new[] { "X-Api-Key" }, new[] { "k-123" });
        var ex = Ex("GET", "http://x/a", new() { ["X-Api-Key"] = "k-123", ["X-Env"] = "test" }, null, mask);
        var curl = CurlCommand.Build(ex);
        Assert.That(curl, Is.EqualTo("curl -X GET 'http://x/a' -H \"X-Api-Key: $X_API_KEY\" -H 'X-Env: test'"));
        Assert.That(CurlCommand.Variables(ex), Is.EqualTo(new[] { ("X_API_KEY", "X-Api-Key") }));
    }
}

public class ExchangeTextCurlTests
{
    private static Exchange Masked(string name)
    {
        var h = new Dictionary<string, string> { [name] = "Bearer hemlig-123" };
        return new("GET", "http://x/orders", new HeaderMask(new[] { name }, new[] { "hemlig-123" }).Mask(h), null, 200, "{}", 5);
    }

    [Test]
    public void Curl_with_masked_header_gets_a_line_before_saying_what_to_set()
    {
        var curl = ExchangeText.Curl(new[] { Masked("Authorization") })!;
        Assert.That(curl.Split('\n'), Is.EqualTo(new[]
        {
            "# Sätt först: byt ut texten i citattecken mot din token och kör raderna nedan.",
            "export TOKEN='din token'",
            "curl -X GET 'http://x/orders' -H \"Authorization: Bearer $TOKEN\"",
        }));
        Assert.That(curl, Does.Not.Contain("hemlig-123").And.Not.Contain("***"));
        var key = ExchangeText.Curl(new[] { Masked("X-Api-Key") })!;
        Assert.That(key, Does.Contain("\nexport X_API_KEY='värdet för X-Api-Key'\ncurl"));
    }

    [Test]
    public void Curl_without_secret_header_has_no_leading_line()
    {
        var curl = ExchangeText.Curl(new[] { new Exchange("GET", "http://x/a", new Dictionary<string, string>(), null, 200, "{}", 5) });
        Assert.That(curl, Is.EqualTo("curl -X GET 'http://x/a'"));
    }

    [Test]
    public void Describe_can_leave_out_the_timing()
    {
        var ex = new[]
        {
            new Exchange("POST", "http://x/c", new Dictionary<string, string>(), "{}", 201, "{}", 12, "createCustomer"),
            new Exchange("POST", "http://x/o", new Dictionary<string, string>(), "{}", 500, "{}", 8),
        };
        Assert.That(ExchangeText.Describe(ex), Does.Contain("svar: 500 (8 ms)").And.Contain("förberedande: POST /c -> 201 (12 ms)"));
        var withoutTiming = ExchangeText.Describe(ex, withTiming: false);
        Assert.That(withoutTiming, Does.Contain("svar: 500").And.Contain("förberedande: POST /c -> 201"));
        Assert.That(string.Join("\n", withoutTiming), Does.Not.Contain(" ms"));
    }
}

public class ExchangeTextTests
{
    [Test]
    public void Long_body_is_shortened_and_marked()
    {
        var body = "{\"items\":[" + string.Join(",", Enumerable.Range(0, 200).Select(i => i.ToString())) + "]}";
        var lines = ExchangeText.Shorten(ExchangeText.Pretty(body));
        Assert.That(lines.Count, Is.LessThanOrEqualTo(ExchangeText.MaxLines + 1));
        Assert.That(lines.Last(), Does.StartWith("... (avkortat"));
        Assert.That(ExchangeText.Shorten("{\"a\":1}"), Is.EqualTo(new[] { "{\"a\":1}" }));
    }

    [Test]
    public void Setup_calls_become_a_short_line_and_the_main_call_is_shown_in_full()
    {
        var ex = new List<Exchange>
        {
            new("POST", "http://x/customers", new Dictionary<string, string>(), "{}", 201, "{}", 12, "createCustomer"),
            new("POST", "http://x/orders", new Dictionary<string, string> { ["X-Env"] = "test" }, "{\"quantity\":0}", 500, "{\"error\":\"x\"}", 8),
        };
        var lines = ExchangeText.Describe(ex);
        Assert.That(lines[0], Is.EqualTo("förberedande: POST /customers -> 201 (12 ms)"));
        Assert.That(lines, Does.Contain("anrop: POST http://x/orders"));
        Assert.That(lines, Does.Contain("svar: 500 (8 ms)"));
        Assert.That(lines, Does.Contain("  headers: X-Env: test"));
    }
}

public class ReportWriterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("testgen-rapport-").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    private const string Token = "tok-9f8e7d";
    private static readonly HeaderMask Mask = HeaderMask.For(new Dictionary<string, string> { ["Authorization"] = "Bearer " + Token });

    private static List<ReportEntry> Entries()
    {
        var failed = new CaseResult(500, false, false, "FEL (väntade 400)", new() { "svar.quantity: väntade 5, fick 2" })
        {
            Exchanges =
            {
                new Exchange("POST", "http://x/orders", Mask.Mask(new Dictionary<string, string> { ["Authorization"] = "Bearer " + Token }),
                    "{\"name\":\"<b>O'Brien</b>\"}", 500, "{\"error\":\"<script>alert(1)</script>\"}", 8),
            },
        };
        return new()
        {
            new ReportEntry("T-001", "Genererade tester", "POST /customers", "giltig indata", new CaseResult(201, true, false, "OK", new()))
            {
                Info = new TestInfo("T-001", "Genererade tester", "POST /customers", "POST", "createCustomer", "Skapa kund", "giltig indata",
                    new[] { new StatusExpectation(201, 201) }, CaseMeta.Valid),
            },
            new ReportEntry("T-002", "Genererade tester", "POST /orders", "<img src=x onerror=alert(1)>", failed)
            {
                Info = new TestInfo("T-002", "Genererade tester", "POST /orders", "POST", "createOrder", "Skapa order", "quantity under minimum",
                    new[] { new StatusExpectation(400, 400) }, new CaseMeta(TestRule.BelowMinimum, "quantity", "1", "0")),
            },
            new ReportEntry("T-003", "Excel-tester", "GET /orders/{id}", "steg 2", new CaseResult(null, false, true, "HOPPAD (tidigare steg)", new())),
        };
    }

    private static readonly ReportInfo Info = new(new DateTime(2026, 11, 1, 10, 30, 0), "http://localhost:5080", "test", 12345);

    [Test]
    public void Html_is_standalone_escaped_and_without_token()
    {
        var html = ReportWriter.Html(Info, Entries(), Mask);
        Assert.That(html, Does.Not.Contain(Token));
        // Ett enda inbäddat skript (filterknappen); inga externa resurser och inget oescapat från indata.
        Assert.That(System.Text.RegularExpressions.Regex.Matches(html, "<script").Count, Is.EqualTo(1));
        Assert.That(html, Does.Not.Contain("<script src").And.Not.Contain("http://localhost:5080/").And.Not.Contain("@import"));
        Assert.That(html, Does.Not.Contain("<img"));
        Assert.That(html, Does.Contain("&lt;img src=x onerror=alert(1)&gt;"));
        Assert.That(html, Does.Contain("&lt;script&gt;alert(1)&lt;/script&gt;"));
        Assert.That(html, Does.Not.Contain("<link").And.Not.Contain("src=\""));
        Assert.That(html, Does.Contain("Authorization: Bearer ***"));
        Assert.That(html, Does.Contain("T-002").And.Contain("FEL").And.Contain("Visa bara FEL"));
        Assert.That(html, Does.Contain("seed 12345").And.Contain("http://localhost:5080").And.Contain("test"));
        Assert.That(html, Does.Contain("curl -X POST"));
    }

    [Test]
    public void Html_has_plain_text_as_main_content_and_technical_details_below()
    {
        var html = WebUtility.HtmlDecode(ReportWriter.Html(Info, Entries(), Mask));   // åäö skrivs som entiteter
        Assert.That(html, Does.Contain("1 fel i systemet (T-002)."));
        Assert.That(html, Does.Contain("<h3>Skapa order – quantity för lågt (under minimum)</h3>"));
        Assert.That(html, Does.Contain("Vi skickade quantity = 0, men minsta tillåtna är 1."));
        Assert.That(html, Does.Contain("<strong>Tolkning:</strong> Det tyder på ett fel i systemet, inte i testet."));
        Assert.That(html, Does.Contain("Hoppades över eftersom ett tidigare steg i flödet misslyckades."));
        Assert.That(html, Does.Contain("etikett fel\">FEL").And.Contain("etikett ok\">OK"));
        // Tekniken ligger efter klartexten, inom "Tekniska detaljer".
        var t2 = html[html.IndexOf("id=\"T-002\"", StringComparison.Ordinal)..];
        Assert.That(t2.IndexOf("Vi skickade quantity", StringComparison.Ordinal), Is.LessThan(t2.IndexOf("Tekniska detaljer", StringComparison.Ordinal)));
        Assert.That(t2.IndexOf("Tekniska detaljer", StringComparison.Ordinal), Is.LessThan(t2.IndexOf("curl -X POST", StringComparison.Ordinal)));
        Assert.That(t2.IndexOf("Tekniska detaljer", StringComparison.Ordinal), Is.LessThan(t2.IndexOf("POST /orders", StringComparison.Ordinal)));
    }

    [Test]
    public void Html_has_a_button_instead_of_a_hidden_checkbox_and_hides_groups_without_failures()
    {
        var entries = Entries().Select(e => e with { Group = e.Outcome == "FEL" ? "Grupp med fel" : "Grupp utan fel" }).ToList();
        var html = WebUtility.HtmlDecode(ReportWriter.Html(Info, entries, Mask));
        Assert.That(html, Does.Contain("<button type=\"button\" id=\"bara-fel\" aria-pressed=\"false\">Visa bara FEL"));
        Assert.That(html, Does.Not.Contain("type=\"checkbox\"").And.Not.Contain("left:-9999px"));
        Assert.That(html, Does.Contain("id=\"verktyg\" hidden"), "knappen visas bara med JavaScript");
        Assert.That(html, Does.Contain("<section class=\"grupp utan-fel\"").And.Contain("<section class=\"grupp\""));
        Assert.That(html, Does.Contain("body.bara-fel .grupp.utan-fel{display:none}"));
        Assert.That(html.IndexOf("id=\"bara-fel\"", StringComparison.Ordinal), Is.LessThan(html.IndexOf("<h2 id=\"grupp-1\">", StringComparison.Ordinal)),
            "knappen ligger före grupperna i tabbordningen");
    }

    [Test]
    public void Html_has_clear_link_and_summary_names_title_and_landmarks()
    {
        var html = WebUtility.HtmlDecode(ReportWriter.Html(Info, Entries(), Mask));
        Assert.That(html, Does.Contain("<title>Testrapport – 1 fel i systemet, 1 kunde inte köras färdigt – 2026-11-01</title>"));
        Assert.That(html, Does.Contain("<a href=\"#T-002\">T-002 – Skapa order – quantity för lågt (under minimum)</a>"));
        Assert.That(html, Does.Contain("<summary>Tekniska detaljer<span class=\"endast-skarmlasare\"> för T-002</span></summary>"));
        Assert.That(html, Does.Contain("<html lang=\"sv\">").And.Contain("<header>").And.Contain("<main>"));
        Assert.That(html, Does.Contain("@media print").And.Contain(":focus-visible{outline:3px").And.Contain("@media (max-width:480px)"));
        var h = System.Text.RegularExpressions.Regex.Matches(html, @"<h([1-6])[ >]").Select(m => int.Parse(m.Groups[1].Value)).ToList();
        Assert.That(h[0], Is.EqualTo(1));
        for (var i = 1; i < h.Count; i++) Assert.That(h[i] - h[i - 1], Is.LessThanOrEqualTo(1), "inget hopp i rubriknivå");
    }

    [Test]
    public void Html_shows_seed_for_the_web_ui_even_without_spec_and_the_terminal_command_under_its_own_heading()
    {
        var withoutSpec = WebUtility.HtmlDecode(ReportWriter.Html(Info, Entries(), Mask));
        Assert.That(withoutSpec, Does.Contain("Vill du köra samma tester igen i webbgränssnittet: använd slumpfrö 12345."));
        Assert.That(withoutSpec, Does.Not.Contain("För dig som kör testgen i terminalen"));
        var withSpec = WebUtility.HtmlDecode(ReportWriter.Html(RerunInfo, Entries(), Mask));
        Assert.That(withSpec.IndexOf("använd slumpfrö 12345", StringComparison.Ordinal),
            Is.LessThan(withSpec.IndexOf("<h3>För dig som kör testgen i terminalen</h3>", StringComparison.Ordinal)));
        Assert.That(withSpec.IndexOf("<h3>För dig som kör testgen i terminalen</h3>", StringComparison.Ordinal),
            Is.LessThan(withSpec.IndexOf("testgen.sh run --spec", StringComparison.Ordinal)));
    }

    [Test]
    public void Html_timestamp_is_local_time_with_offset()
    {
        Assert.That(ReportWriter.HtmlStamp(new DateTime(2026, 11, 1, 16, 41, 0)), Does.Match(@"^2026-11-01 16:41 \(UTC[+-]\d\d:\d\d\)$"));
        Assert.That(ReportWriter.Html(Info, Entries(), Mask), Does.Match(@"Körd 2026-11-01 10:30 \(UTC[+-]\d\d:\d\d\)"));
    }

    private static readonly ReportInfo RerunInfo = Info with { Spec = "samples/shop.openapi.yaml", Excel = "samples/tester.xlsx", UsesToken = true };

    [Test]
    public void Excel_opens_on_summary_and_results_is_a_table_with_correct_columns_and_cell_types()
    {
        var path = ReportWriter.Write(Path.Combine(_dir, "under", "r.xlsx"), RerunInfo, Entries(), Mask);
        Assert.That(File.Exists(path));
        using var wb = new XLWorkbook(path);
        Assert.That(wb.Worksheets.Select(w => w.Name), Is.EqualTo(new[] { "Sammanfattning", "Resultat" }));
        Assert.That(wb.Worksheet("Sammanfattning").TabSelected);
        Assert.That(wb.Worksheet("Resultat").TabSelected, Is.False);
        Assert.That(wb.Properties.Title, Is.EqualTo("Testresultat 2026-11-01"));

        var ws = wb.Worksheet("Resultat");
        Assert.That(Enumerable.Range(1, 15).Select(c => ws.Cell(1, c).GetString()), Is.EqualTo(new[]
        {
            "Id", "Resultat", "Vad testet gör", "Förklaring", "Anrop", "Förväntad status", "Faktisk status", "Tid (ms)",
            "Orsak", "Följd av", "Grupp", "Detaljer", "curl", "Svar (hela)", "Återskapa (bash)",
        }));
        Assert.That(ws.Tables.Count(), Is.EqualTo(1));
        var table = ws.Tables.Single();
        Assert.That(table.ShowAutoFilter);
        Assert.That(table.Fields.Select(f => f.Name).Distinct().Count(), Is.EqualTo(15), "unika rubriker");
        Assert.That(table.DataRange.RowCount(), Is.EqualTo(3));
        Assert.That(ws.SheetView.SplitRow, Is.EqualTo(1));
        Assert.That(ws.SheetView.SplitColumn, Is.EqualTo(1));

        // T-002 är FEL: status och tid som tal, resultatcellen tydligt markerad.
        Assert.That(ws.Cell(3, 1).GetString(), Is.EqualTo("T-002"));
        Assert.That(ws.Cell(3, 2).GetString(), Is.EqualTo("FEL"));
        Assert.That(ws.Cell(3, 2).Style.Font.Bold);
        Assert.That(ws.Cell(3, 2).Style.Fill.BackgroundColor, Is.EqualTo(XLColor.FromHtml("#F4B6B6")));
        Assert.That(ws.Cell(3, 2).Style.Border.TopBorder, Is.Not.EqualTo(XLBorderStyleValues.None));
        Assert.That(ws.Cell(3, 1).Style.Fill.BackgroundColor, Is.Not.EqualTo(ws.Cell(2, 1).Style.Fill.BackgroundColor));
        Assert.That(ws.Cell(3, 2).Style.Fill.BackgroundColor, Is.Not.EqualTo(ws.Cell(2, 2).Style.Fill.BackgroundColor));
        Assert.That(ws.Cell(3, 6).DataType, Is.EqualTo(XLDataType.Number));
        Assert.That(ws.Cell(3, 6).GetDouble(), Is.EqualTo(400));
        Assert.That(ws.Cell(3, 7).DataType, Is.EqualTo(XLDataType.Number));
        Assert.That(ws.Cell(3, 7).GetDouble(), Is.EqualTo(500));
        Assert.That(ws.Cell(3, 8).DataType, Is.EqualTo(XLDataType.Number));
        Assert.That(ws.Cell(3, 8).GetDouble(), Is.EqualTo(8));
        Assert.That(ws.Cell(3, 3).GetString(), Is.EqualTo("Skapa order – quantity för lågt (under minimum)"));
        Assert.That(ws.Cell(3, 4).GetString(), Does.StartWith("Vi skickade quantity = 0, men minsta tillåtna är 1.")
            .And.Contain("svarade 500 – internt fel i systemet").And.Contain("Tolkning: Det tyder på ett fel i systemet, inte i testet."));
        Assert.That(ws.Cell(3, 5).GetString(), Is.EqualTo("POST /orders"));

        // Detaljer utan tid (stabil mellan körningar); curl med variabel och rad före.
        var details = ws.Cell(3, 12).GetString();
        Assert.That(details, Does.Contain("svar.quantity: väntade 5, fick 2").And.Contain("svar: 500"));
        Assert.That(details, Does.Not.Contain(" ms)"));
        Assert.That(ws.Cell(3, 13).GetString(), Does.Contain("\nexport TOKEN='din token'\ncurl -X POST").And.Contain("Authorization: Bearer $TOKEN"));
        Assert.That(ws.Cell(3, 14).GetString(), Does.Contain("alert(1)"));

        // OK-rad och HOPPAD-rad: egna diskreta stilar, utan hela svar.
        Assert.That(ws.Cell(2, 2).GetString(), Is.EqualTo("OK"));
        Assert.That(ws.Cell(2, 6).GetDouble(), Is.EqualTo(201));
        Assert.That(ws.Cell(2, 7).GetDouble(), Is.EqualTo(201));
        Assert.That(ws.Cell(2, 14).GetString(), Is.Empty);
        Assert.That(ws.Cell(4, 2).GetString(), Is.EqualTo("HOPPAD"));
        Assert.That(ws.Cell(4, 7).IsEmpty(), "inget svar: tomt, inte text");
        Assert.That(ws.Cell(4, 8).IsEmpty());
        Assert.That(ws.Cell(4, 4).GetString(), Is.EqualTo("Hoppades över eftersom ett tidigare steg i flödet misslyckades."));
        Assert.That(ws.Cell(4, 2).Style.Fill.BackgroundColor, Is.Not.EqualTo(ws.Cell(2, 2).Style.Fill.BackgroundColor));

        // Radbrytning, utskrift.
        foreach (var c in new[] { 3, 4, 5, 9, 12, 13 }) Assert.That(ws.Cell(3, c).Style.Alignment.WrapText, $"kolumn {c}");
        Assert.That(ws.PageSetup.PageOrientation, Is.EqualTo(XLPageOrientation.Landscape));
        Assert.That(ws.PageSetup.PaperSize, Is.EqualTo(XLPaperSize.A4Paper));
        Assert.That(ws.PageSetup.PagesWide, Is.EqualTo(1));
        Assert.That(ws.PageSetup.FirstRowToRepeatAtTop, Is.EqualTo(1));

        Assert.That(string.Join("|", ws.CellsUsed().Select(c => c.GetString())), Does.Not.Contain(Token));
    }

    [Test]
    public void Excel_summary_answers_first_lists_failures_with_links_and_gives_rerun_without_token()
    {
        var path = ReportWriter.Write(Path.Combine(_dir, "r.xlsx"), RerunInfo, Entries(), Mask);
        using var wb = new XLWorkbook(path);
        var sum = wb.Worksheet("Sammanfattning");
        Assert.That(sum.Cell(1, 1).GetString(), Does.StartWith("1 fel i systemet (T-002)."));
        // Antal som tal.
        var labels = Enumerable.Range(1, 24).Select(r => sum.Cell(r, 1).GetString()).Where(l => l.Length > 0)
            .Distinct().ToDictionary(l => l, l => sum.Cell(Enumerable.Range(1, 24).First(r => sum.Cell(r, 1).GetString() == l), 2));
        Assert.That(labels["OK"].DataType, Is.EqualTo(XLDataType.Number));
        Assert.That(labels["OK"].GetDouble(), Is.EqualTo(1));
        Assert.That(labels["FEL"].GetDouble(), Is.EqualTo(1));
        Assert.That(labels["HOPPAD"].GetDouble(), Is.EqualTo(1));
        Assert.That(labels["Antal tester"].GetDouble(), Is.EqualTo(3));
        Assert.That(labels["Datum"].GetString(), Does.Match(@"^2026-11-01 10:30 \(UTC([+-]\d+(:\d\d)?)?\)$"));
        Assert.That(labels["Adress"].GetString(), Is.EqualTo("http://localhost:5080"));
        Assert.That(labels["Miljö"].GetString(), Is.EqualTo("test"));
        Assert.That(labels["Seed"].GetString(), Is.EqualTo("12345"));

        var all = string.Join("|", sum.CellsUsed().Select(c => c.GetString()));
        Assert.That(all, Does.Not.Contain(Token));
        Assert.That(all, Does.Contain("./testgen.sh run --spec samples/shop.openapi.yaml --api http://localhost:5080 --excel samples/tester.xlsx --env test --seed 12345"));
        Assert.That(all, Does.Not.Contain("--token"), "med --env hämtas token ur miljön");
        Assert.That(all, Does.Contain("--seed 12345 --test T-002"));

        // Listan med FEL: bara T-002, id är en länk till raden i Resultat.
        var idCell = sum.CellsUsed().Single(c => c.Address.ColumnNumber == 1 && c.GetString() == "T-002");
        Assert.That(idCell.HasHyperlink);
        Assert.That(idCell.GetHyperlink().InternalAddress, Does.Contain("Resultat").And.Contain("A3"));
        Assert.That(sum.CellsUsed().Any(c => c.GetString() == "T-001"), Is.False, "OK-test hör inte hemma i FEL-listan");
        Assert.That(sum.Cell(idCell.Address.RowNumber, 2).GetString(), Is.EqualTo("Skapa order – quantity för lågt (under minimum)"));
        Assert.That(sum.Cell(idCell.Address.RowNumber, 3).GetString(), Does.Contain("Vi skickade quantity = 0"));
    }

    [Test]
    public void Excel_summary_omits_empty_rows_and_shows_the_login_notice()
    {
        var unauthorized = Enumerable.Range(1, 5).Select(i =>
            new ReportEntry($"T-00{i}", "Genererade tester", "GET /x", "d", new CaseResult(401, false, false, "FEL (väntade 200)", new()))).ToList();
        var path = ReportWriter.Write(Path.Combine(_dir, "r.xlsx"), new ReportInfo(Info.When, Info.Api, null, 7), unauthorized, Mask);
        using var wb = new XLWorkbook(path);
        var sum = wb.Worksheet("Sammanfattning");
        var labels = sum.CellsUsed().Where(c => c.Address.ColumnNumber == 1).Select(c => c.GetString()).ToList();
        Assert.That(labels, Does.Not.Contain("Miljö"));
        Assert.That(labels, Does.Not.Contain("Kör om allt"), "spec okänd: inget kommando att visa");
        Assert.That(labels, Does.Contain("Inloggning"));
        Assert.That(string.Join("|", sum.CellsUsed().Select(c => c.GetString())), Does.Contain("Alla tester fick 401"));
        Assert.That(sum.CellsUsed().Where(c => c.DataType == XLDataType.Text).All(c => c.GetString().Length > 0));
    }

    [Test]
    public void Excel_gives_the_full_response_for_failures_but_marks_if_shortened()
    {
        var big = "{\"data\":\"" + new string('x', 45000) + "\"}";
        var failed = new CaseResult(500, false, false, "FEL (väntade 200)", new())
        {
            Exchanges = { new Exchange("GET", "http://x/a", new Dictionary<string, string>(), null, 500, big, 3) },
        };
        var mid = "{\"data\":\"" + new string('y', 5000) + "\"}";
        var failed2 = new CaseResult(500, false, false, "FEL (väntade 200)", new())
        {
            Exchanges = { new Exchange("GET", "http://x/b", new Dictionary<string, string>(), null, 500, mid, 3) },
        };
        var path = ReportWriter.Write(Path.Combine(_dir, "r.xlsx"),
            Info, new List<ReportEntry> { new("T-001", "g", "GET /a", "d", failed), new("T-002", "g", "GET /b", "d", failed2) }, Mask);
        using var wb = new XLWorkbook(path);
        var ws = wb.Worksheet("Resultat");
        var cut = ws.Cell(2, 14).GetString();
        Assert.That(cut.Length, Is.LessThanOrEqualTo(ReportWriter.FullResponseMax));
        Assert.That(cut, Does.Match(@"… \(avkortat, \d+ tecken totalt\)$"));
        Assert.That(ws.Cell(2, 12).GetString(), Does.Contain("hela svaret finns i kolumnen Svar (hela)"));
        var whole = ws.Cell(3, 14).GetString();
        Assert.That(whole, Does.Contain(new string('y', 5000)).And.Not.Contain("avkortat"), "5 000 tecken ryms helt");
        Assert.That(ws.Cell(3, 12).GetString(), Does.Contain("avkortat"), "Detaljer är fortfarande kortad");
    }

    [Test]
    public void Excel_contains_no_token_anywhere_in_the_unpacked_file()
    {
        var path = ReportWriter.Write(Path.Combine(_dir, "r.xlsx"), RerunInfo, Entries(), Mask);
        using var zip = System.IO.Compression.ZipFile.OpenRead(path);
        foreach (var part in zip.Entries)
        {
            using var rd = new StreamReader(part.Open());
            Assert.That(rd.ReadToEnd(), Does.Not.Contain(Token), part.FullName);
        }
    }

    [Test]
    public void Rerun_command_never_has_a_token_value_and_html_shows_the_same_line()
    {
        var noEnv = RerunInfo with { Env = null };
        var all = ReportWriter.RerunCommand(noEnv)!;
        Assert.That(all, Is.EqualTo("./testgen.sh run --spec samples/shop.openapi.yaml --api http://localhost:5080 --excel samples/tester.xlsx --seed 12345 --token '${env:TOKEN}'"));
        Assert.That(ReportWriter.RerunCommand(noEnv, "T-018"), Does.EndWith(" --token '${env:TOKEN}' --test T-018"));
        Assert.That(ReportWriter.RerunCommand(Info with { Spec = "min spec.yaml" })!, Does.Contain("--spec 'min spec.yaml'").And.Not.Contain("--token"));
        Assert.That(ReportWriter.RerunCommand(Info), Is.Null);
        var html = WebUtility.HtmlDecode(ReportWriter.Html(noEnv, Entries(), Mask));
        Assert.That(html, Does.Contain(all).And.Contain("--test T-").And.Contain("<h2>Kör om</h2>").And.Contain("(Windows: testgen.cmd, eller dotnet run --project testgen/src/TestGen.Cli -- …)").And.Not.Contain("nya id-nummer"));
        Assert.That(html, Does.Contain("export TOKEN='din token'\ncurl -X POST").And.Not.Contain("curl: curl"));
        Assert.That(html, Does.Not.Contain(Token));
    }

    [Test]
    public void Timestamp_has_time_zone()
    {
        Assert.That(ReportWriter.Stamp(new DateTime(2026, 11, 1, 16, 41, 0)), Does.Match(@"^2026-11-01 16:41 \(UTC([+-]\d+(:\d\d)?)?\)$"));
    }

    [Test]
    public void Unknown_file_extension_gives_a_clear_error()
    {
        var e = Assert.Throws<ArgumentException>(() => ReportWriter.Validate("r.pdf"))!;
        Assert.That(e.Message, Does.Contain(".html").And.Contain(".xlsx"));
        Assert.DoesNotThrow(() => ReportWriter.Validate("R.HTML"));
    }
}

public class RunnerWithFakeApiTests : IDisposable
{
    private const string Token = "tok-9f8e7d";
    private readonly string _dir = Directory.CreateTempSubdirectory("testgen-runner-").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    private static string Spec()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var f = Path.Combine(d.FullName, "samples", "shop.openapi.yaml");
            if (File.Exists(f)) return f;
        }
        throw new FileNotFoundException("samples/shop.openapi.yaml");
    }

    /// <summary>Fejk-API: skapa kund/produkt ger 201; order med quantity 0 ger 500 (som ekar token), annars 201.</summary>
    private sealed class FakeApi : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            var body = req.Content is null ? null : JsonNode.Parse(await req.Content.ReadAsStringAsync(ct)) as JsonObject;
            if (path == "/orders" && body?["quantity"]?.GetValue<int>() == 0)
                return Json(HttpStatusCode.InternalServerError, $"{{\"error\":\"internal error\",\"auth\":\"Bearer {Token}\"}}");
            if (req.Method == HttpMethod.Post && body is not null)
            {
                body["id"] = 7;
                return Json(HttpStatusCode.Created, body.ToJsonString());
            }
            return Json(HttpStatusCode.OK, "{}");
        }

        private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
            new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private async Task<(int Exit, string Output)> Run(bool verbose = false, string? report = null, string? only = "createOrder")
    {
        var sw = new StringWriter();
        var exit = await new TestRunner(new RunOptions
        {
            Spec = Spec(), Api = "http://fake.test", Token = Token, Only = only, Seed = 1, Verbose = verbose, ReportPath = report,
        }, sw, new FakeApi()).RunAsync();
        return (exit, sw.ToString());
    }

    [Test]
    public async Task Failure_shows_request_response_and_curl_without_secrets()
    {
        var (exit, output) = await Run();
        Assert.That(exit, Is.EqualTo(1));
        Assert.That(output, Does.Not.Contain(Token));
        Assert.That(output, Does.Match(@"T-\d{3}\s+POST /orders\s+quantity för lågt \(under minimum\)\s+500\s+FEL"));
        Assert.That(output, Does.Contain("förberedande 1: POST /customers → 201, gav id=").And.Contain("(används som customerId)"));
        Assert.That(output, Does.Contain("anrop: POST http://fake.test/orders"));
        Assert.That(output, Does.Contain("headers: Authorization: Bearer ***"));
        Assert.That(output, Does.Contain("svar: 500 ("));
        Assert.That(output, Does.Contain("\"error\": \"internal error\""));
        Assert.That(output, Does.Contain("export TOKEN='din token'"));
        Assert.That(output, Does.Contain("curl -X POST 'http://fake.test/orders' -H \"Authorization: Bearer $TOKEN\"").And.Not.Contain("curl: curl"));
        Assert.That(output, Does.Contain("--max-time 30"));
        Assert.That(output, Does.Contain("--data-raw '{\"customerId\":7,\"productId\":7,\"quantity\":0}'"));
        Assert.That(output, Does.Contain("Tips: ***"));
        Assert.That(output.Split("Tips: ***").Length, Is.EqualTo(2), "tipset skrivs en gång");
    }

    [Test]
    public async Task Ok_shows_request_details_only_with_verbose()
    {
        var (_, quiet) = await Run(only: "createCustomer");
        // T-001 (giltig indata) blir OK; fejk-API:et ger 201 även på ogiltig indata, så senare tester blir FEL.
        string First(string o) => o[o.IndexOf("T-001", StringComparison.Ordinal)..o.IndexOf("T-002", StringComparison.Ordinal)];
        Assert.That(First(quiet), Does.Contain("OK").And.Not.Contain("anrop:").And.Not.Contain("curl:"));
        var (_, verbose) = await Run(verbose: true, only: "createCustomer");
        Assert.That(First(verbose), Does.Contain("anrop: POST http://fake.test/customers").And.Contain("curl -X POST"));
        Assert.That(verbose, Does.Not.Contain(Token));
    }

    [Test]
    public async Task Test_id_is_a_sequence_number_and_the_same_on_a_new_run()
    {
        var ids = (string o) => System.Text.RegularExpressions.Regex.Matches(o, @"^T-\d{3}", System.Text.RegularExpressions.RegexOptions.Multiline).Select(m => m.Value).ToList();
        var a = ids((await Run()).Output);
        var b = ids((await Run()).Output);
        Assert.That(b, Is.EqualTo(a));
        // Urval numrerar inte om: createOrder är inte först i en full körning, men id:t är detsamma som i den.
        var all = ids((await Run(only: null)).Output);
        Assert.That(all.First(), Is.EqualTo("T-001"));
        Assert.That(all, Is.EqualTo(Enumerable.Range(1, all.Count).Select(i => $"T-{i:000}")));
        Assert.That(a, Is.SubsetOf(all));
        Assert.That(a.First(), Is.Not.EqualTo("T-001"));
    }

    [Test]
    public async Task Report_is_written_even_on_failure_and_without_token()
    {
        var path = Path.Combine(_dir, "ny katalog", "r.html");
        var (exit, output) = await Run(report: path);
        Assert.That(exit, Is.EqualTo(1));
        Assert.That(output, Does.Contain("Rapport skriven: " + path));
        var html = File.ReadAllText(path);
        Assert.That(html, Does.Contain("T-0").And.Contain("FEL").And.Contain("curl -X POST"));
        Assert.That(html, Does.Not.Contain(Token));
    }

    [Test]
    public void Unknown_report_type_stops_before_the_run()
    {
        Assert.ThrowsAsync<ArgumentException>(async () => await Run(report: Path.Combine(_dir, "r.txt")));
    }
}
