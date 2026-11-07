using System.Net;
using TestGen.Scenarios;
using TestGen.Cli;
using TestGen.Core;
using TestGen.Core.Run;

namespace TestGen.Tests;

[NonParallelizable]
public class ErrorTests
{
    private string _dir = "";

    [SetUp] public void Up() => _dir = Directory.CreateTempSubdirectory("testgen-fel-").FullName;
    [TearDown] public void Down() => Directory.Delete(_dir, true);

    private string File_(string name, string content)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, content);
        return p;
    }

    /// <summary>Kör Program.Main med omdirigerad stdout/stderr.</summary>
    private static (int Exit, string Out, string Err) Cli(params string[] args)
    {
        var (o, e) = (Console.Out, Console.Error);
        var (so, se) = (new StringWriter(), new StringWriter());
        Console.SetOut(so); Console.SetError(se);
        try { return (Program.Main(args), so.ToString(), se.ToString()); }
        finally { Console.SetOut(o); Console.SetError(e); }
    }

    // --- Översättaren -------------------------------------------------------------------

    [Test]
    public void Spec_that_is_not_openapi_gives_a_Swedish_error()
    {
        var f = File_("hej.yaml", "hello");
        var e = Assert.Throws<SourceException>(() => OpenApiLoader.Load(f))!;
        var t = FriendlyErrors.Translate(e);
        Assert.That(t.What, Does.StartWith("Kunde inte läsa OpenAPI-spec").And.Contain(f));
        Assert.That(t.What, Does.Not.Contain("Microsoft.OpenApi"));
        Assert.That(t.ExitCode, Is.EqualTo(ExitCodes.Usage));
        Assert.That(t.Technical, Is.Not.Empty);
    }

    [Test]
    public void Broken_yaml_gives_line_number_and_no_parser_jargon()
    {
        var f = File_("trasig.yaml", "foo: 'ej avslutad\n  - [x");
        var t = FriendlyErrors.Translate(Assert.Throws<SourceException>(() => OpenApiLoader.Load(f))!);
        Assert.That(t.What, Does.Not.Contain("While parsing").And.Not.Contain("Chr:"));
        Assert.That(t.What, Does.Contain("rad"));
    }

    [Test]
    public void Missing_file_is_shown_with_absolute_path()
    {
        var rel = Path.Combine("finns", "inte.yaml");
        var e = Assert.Throws<MissingSourceException>(() => OpenApiLoader.Load(rel))!;
        var t = FriendlyErrors.Translate(e);
        Assert.That(t.What, Is.EqualTo($"Hittar inte specen: {Path.GetFullPath(rel)}"));
        Assert.That(t.What, Does.Not.Contain("Could not find"));
    }

    [Test]
    public void Raw_FileNotFound_is_translated_with_context()
    {
        var p = Path.Combine(_dir, "nope.xlsx");
        var t = FriendlyErrors.Translate(new FileNotFoundException("Could not find file", p), new ErrorContext { Excel = p });
        Assert.That(t.What, Is.EqualTo($"Hittar inte Excel-filen: {p}"));
        Assert.That(t.ExitCode, Is.EqualTo(2));
    }

    [Test]
    public void Excel_that_is_not_xlsx_gives_the_same_text_on_all_paths()
    {
        var f = File_("falsk.xlsx", "inte en excelfil");
        var a = Assert.Throws<SourceException>(() => ExcelScenarioReader.Read(f))!;
        var b = Assert.Throws<SourceException>(() => EnvironmentSheet.Read(f))!;
        Assert.That(b.Message, Is.EqualTo(a.Message));
        Assert.That(a.Message, Does.Contain("Excel-arbetsbok").And.Not.Contain("corrupted"));
    }

    [Test]
    public void Spec_url_404_gives_no_network_tip()
    {
        var e = new HttpRequestException("Response status code does not indicate success: 404 (Not Found).", null, HttpStatusCode.NotFound);
        var w = (SourceException)FriendlyErrors.Wrap(SourceKind.Spec, "http://x.test/spec.yaml", e);
        Assert.That(w.Message, Does.Contain("servern svarade 404"));
        Assert.That(w.Tip, Does.Not.Contain("nätverk"));
    }

    [Test]
    public void Unreachable_api_gives_exit_code_3()
    {
        var t = FriendlyErrors.Translate(new ApiUnreachableException("http://localhost:5999", ApiProblem.Refused, "Connection refused (localhost:5999)"));
        Assert.That(t.ExitCode, Is.EqualTo(ExitCodes.ApiUnreachable));
        Assert.That(t.What, Does.Not.Contain("Connection refused"));
        Assert.That(t.Technical, Does.Contain("Connection refused"));
    }

    [Test]
    public void Unknown_exception_becomes_unexpected_error_without_stack_trace()
    {
        var t = FriendlyErrors.Translate(new NullReferenceException("x"));
        Assert.That(t.What, Does.StartWith("Ett oväntat fel"));
        Assert.That(string.Join("\n", t.Lines(verbose: false)), Does.Not.Contain(" at "));
    }

    // --- Kommandoraden ------------------------------------------------------------------

    [Test]
    public void Without_arguments_shows_help_with_exit_code_2_and_writes_no_files()
    {
        var before = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_dir);
        try
        {
            var (exit, o, _) = Cli();
            Assert.That(exit, Is.EqualTo(2));
            Assert.That(o, Does.Contain("Kommandon:").And.Contain("generate").And.Contain("Exitkoder:"));
            Assert.That(Directory.GetFileSystemEntries(_dir), Is.Empty);
        }
        finally { Directory.SetCurrentDirectory(before); }
    }

    [Test]
    public void Help_per_command_and_default_values()
    {
        foreach (var cmd in new[] { "run", "template", "ui", "generate" })
        {
            var (exit, o, _) = Cli(cmd, "--help");
            Assert.That(exit, Is.EqualTo(0), cmd);
            Assert.That(o, Does.Contain($"testgen {cmd} -").And.Contain("dotnet run --project testgen/src/TestGen.Cli"), cmd);
        }
        Assert.That(Cli("run", "--help").Out, Does.Contain("default 12345").And.Contain("default 30").And.Contain("--config").And.Contain("Exitkoder"));
    }

    [Test]
    public void Unknown_command_suggests_closest_and_lists_valid_ones()
    {
        var (exit, _, err) = Cli("rnu");
        Assert.That(exit, Is.EqualTo(2));
        Assert.That(err, Does.StartWith("Kunde inte köra: ").And.Contain("Menade du 'run'?").And.Contain("run, template, ui, map, generate"));
    }

    [Test]
    public void Map_level_without_mermaid_gives_an_error()
    {
        var (exit, _, err) = Cli("map", "--solution", "x.slnx", "--level", "all");
        Assert.That(exit, Is.EqualTo(2));
        Assert.That(err, Does.Contain("--level, --direction och --entry gäller Mermaid-filen.").And.Contain("--mermaid karta.mmd"));
    }

    [Test]
    public void Unknown_flag_suggests_closest()
    {
        var (exit, _, err) = Cli("run", "--specc", "x.yaml");
        Assert.That(exit, Is.EqualTo(2));
        Assert.That(err, Does.Contain("Menade du --spec?"));
        Assert.That(Cli("run", "--zzzzzz", "x").Err, Does.Not.Contain("Menade du"));
    }

    [Test]
    public void Old_style_without_command_points_to_generate()
    {
        var (exit, _, err) = Cli("--spec", "a.yaml", "--out", "ut");
        Assert.That(exit, Is.EqualTo(2));
        Assert.That(err, Does.Contain("Kommando saknas"));
        Assert.That(Cli("--out", "ut").Err, Does.Contain("testgen generate"));
    }

    [Test]
    public void Missing_spec_in_run_gives_exit_code_2_and_Swedish_text()
    {
        var (exit, _, err) = Cli("run", "--spec", "nope.yaml", "--api", "http://localhost:5999");
        Assert.That(exit, Is.EqualTo(2));
        Assert.That(err, Does.StartWith("Kunde inte köra: Hittar inte specen: " + Path.GetFullPath("nope.yaml")));
        Assert.That(err, Does.Contain("Gör så här:").And.Not.Contain("Unhandled").And.Not.Contain("Could not find"));
    }

    [Test]
    public void Spec_with_hello_gives_exit_code_2_without_stack_trace()
    {
        var f = File_("hello.yaml", "hello");
        var (exit, _, err) = Cli("run", "--spec", f, "--api", "http://localhost:5999");
        Assert.That(exit, Is.EqualTo(2));
        Assert.That(err, Does.Contain("Kunde inte läsa OpenAPI-spec").And.Not.Contain("Unhandled").And.Not.Contain("   at "));
        Assert.That(Cli("run", "--spec", f, "--api", "http://localhost:5999", "--verbose").Err, Does.Contain("Tekniskt:"));
    }

    [Test]
    public void Excel_that_is_not_xlsx_gives_exit_code_2()
    {
        var x = File_("fel.xlsx", "hej");
        var (exit, _, err) = Cli("run", "--spec", ShopSpec.Path, "--api", "http://localhost:5999", "--excel", x);
        Assert.That(exit, Is.EqualTo(2));
        Assert.That(err, Does.Contain("Excel-arbetsbok").And.Not.Contain("corrupted"));
    }

    [Test]
    public void Unreachable_api_gives_exit_code_3_and_report()
    {
        var report = Path.Combine(_dir, "ut", "r.html");
        var (exit, o, err) = Cli("run", "--spec", ShopSpec.Path, "--api", "http://127.0.0.1:1", "--only", "getOrder", "--report", report);
        Assert.That(exit, Is.EqualTo(3));
        Assert.That(err, Does.StartWith("Kunde inte köra: Kan inte nå API:et på http://127.0.0.1:1"));
        Assert.That(o, Does.Contain("Rapport skriven"));
        Assert.That(WebUtility.HtmlDecode(File.ReadAllText(report)), Does.Contain("Körningen kunde inte genomföras").And.Contain("Kan inte nå API:et"));
    }

    [Test]
    public void Excel_report_is_written_even_when_api_is_unreachable()
    {
        var report = Path.Combine(_dir, "r.xlsx");
        Assert.That(Cli("run", "--spec", ShopSpec.Path, "--api", "http://127.0.0.1:1", "--only", "getOrder", "--report", report).Exit, Is.EqualTo(3));
        using var wb = new ClosedXML.Excel.XLWorkbook(report);
        Assert.That(wb.Worksheet("Sammanfattning").Cell(1, 2).GetString(), Is.EqualTo("Körningen kunde inte genomföras"));
    }

    [Test]
    public void Unwritable_report_folder_stops_before_the_run_with_exit_code_2()
    {
        var blocker = File_("fil", "x");   // en fil kan inte vara katalog
        var (exit, o, err) = Cli("run", "--spec", ShopSpec.Path, "--api", "http://127.0.0.1:1", "--report", Path.Combine(blocker, "under", "r.html"));
        Assert.That(exit, Is.EqualTo(2));
        Assert.That(err, Does.Contain("Rapporten kan inte skrivas").And.Contain("Inget har körts än"));
        Assert.That(o, Does.Not.Contain("Kör "));
    }

    [Test]
    public void Existing_template_gives_exit_code_2()
    {
        var f = File_("t.xlsx", "x");
        var (exit, _, err) = Cli("template", "--spec", ShopSpec.Path, "--out", f);
        Assert.That(exit, Is.EqualTo(2));
        Assert.That(err, Does.Contain("finns redan").And.Contain("--force"));
    }

    [Test]
    public void Test_result_FEL_gives_exit_code_1_and_prefix_Fel_is_not_used_for_stops()
    {
        // Verktygsstopp använder "Kunde inte köra:"; "FEL" är bara testresultat.
        Assert.That(Cli("run").Err, Does.Not.StartWith("Fel:"));
    }

    // --- Inloggningsraden ---------------------------------------------------------------

    [Test]
    public void Auth_note_for_401_403()
    {
        Assert.That(PlainLanguage.AuthNote(Enumerable.Repeat<int?>(401, 10)), Does.StartWith("Alla tester fick 401 – inloggning saknas eller är ogiltig.").And.Contain("--token").And.Contain("Miljöer"));
        Assert.That(PlainLanguage.AuthNote(Enumerable.Repeat<int?>(403, 4)), Does.StartWith("Alla tester fick 403"));
        Assert.That(PlainLanguage.AuthNote(Enumerable.Repeat<int?>(401, 9).Append(201)), Does.StartWith("9 av 10 tester fick 401 eller 403"));
        Assert.That(PlainLanguage.AuthNote(Enumerable.Repeat<int?>(401, 8).Concat(new int?[] { 201, 201 })), Is.Null);
        Assert.That(PlainLanguage.AuthNote(new int?[] { null, null }), Is.Null);
        Assert.That(PlainLanguage.AuthNote(Array.Empty<int?>()), Is.Null);
        var web = PlainLanguage.AuthNote(Enumerable.Repeat<int?>(401, 10), web: true);
        Assert.That(web, Does.StartWith("Alla tester fick 401").And.Contain("fältet Token").And.Contain("Miljöer").And.Not.Contain("--token"));
    }

    private sealed class Always(HttpStatusCode code) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent("{}") });
    }

    [Test]
    public async Task Summary_and_report_mention_login_in_plain_language()
    {
        var sw = new StringWriter();
        var report = Path.Combine(_dir, "a.html");
        var exit = await new TestRunner(new RunOptions { Spec = ShopSpec.Path, Api = "http://fake.test", Only = "createCustomer", Seed = 1, ReportPath = report },
            sw, new Always(HttpStatusCode.Unauthorized)).RunAsync();
        Assert.That(exit, Is.EqualTo(ExitCodes.Inconclusive), "401 överallt säger inget om systemet");
        Assert.That(sw.ToString(), Does.Contain("Alla tester fick 401 – inloggning saknas eller är ogiltig."));
        Assert.That(WebUtility.HtmlDecode(File.ReadAllText(report)), Does.Contain("Alla tester fick 401"));
    }

    [Test]
    public void Conflict_text_is_channel_neutral()
    {
        Assert.That(PlainLanguage.StatusMeaning(409), Is.Not.Empty);
        var t = PlainLanguage.Explain(TestInfo.Basic("T-1", "g", "POST /x", "d") with { ExpectedStatus = new[] { new StatusExpectation(201, 201) } },
            new CaseResult(409, false, false, "FEL", new()));
        Assert.That(t.Hint, Does.Not.Contain("Starta om API:et"));
    }

    [Test]
    public void Levenshtein_finds_close_words()
    {
        Assert.That(Suggest.Closest("specc", new[] { "spec", "api" }), Is.EqualTo("spec"));
        Assert.That(Suggest.Closest("rnu", HelpText.Commands), Is.EqualTo("run"));
        Assert.That(Suggest.Closest("qqqqq", HelpText.Commands), Is.Null);
    }
}
