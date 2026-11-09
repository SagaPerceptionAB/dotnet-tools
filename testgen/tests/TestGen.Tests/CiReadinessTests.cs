using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Schema;
using ClosedXML.Excel;
using TestGen.Cli;
using TestGen.Core;
using TestGen.Core.Run;
using TestGen.Core.Settings;

namespace TestGen.Tests;

/// <summary>
/// CI-redo: JSON- och JUnit-rapport, exitkoder (inkl. 4), --fail-on, tomma körningar, tomma hemligheter, --seed, avbrott/--max-time,
/// spec-URL som inte svarar och --baseline.
/// </summary>
public class CiReadinessTests
{
    private const string Token = "tok-9f8e7d";
    private const string ApiKey = "key-5d4c3b2a";
    private string _dir = "";

    [SetUp]
    public void SetUp() => _dir = Directory.CreateTempSubdirectory("testgen-ci-").FullName;

    [TearDown]
    public void TearDown() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    // --- Hjälpmedel -------------------------------------------------------------------------

    /// <summary>Fejk-API: POST ger 201 med id (order med quantity 0 ger 500 som ekar hemligheterna).</summary>
    private sealed class FakeShop : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var body = req.Content is null ? null : JsonNode.Parse(await req.Content.ReadAsStringAsync(ct)) as JsonObject;
            if (req.RequestUri!.AbsolutePath == "/orders" && body?["quantity"]?.GetValue<int>() == 0)
                return Json(HttpStatusCode.InternalServerError, $"{{\"error\":\"internal\",\"auth\":\"Bearer {Token}\",\"key\":\"{ApiKey}\"}}");
            if (req.Method == HttpMethod.Post && body is not null) { body["id"] = 7; return Json(HttpStatusCode.Created, body.ToJsonString()); }
            return Json(HttpStatusCode.OK, "{}");
        }
    }

    private sealed class Always(HttpStatusCode code) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) => Task.FromResult(Json(code, "{}"));
    }

    /// <summary>Svarar de <paramref name="answers"/> första anropen och hänger sedan tills anropet avbryts.</summary>
    private sealed class HangsAfter(int answers, Action? onHang = null) : HttpMessageHandler
    {
        private int _n;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _n) > answers) { onHang?.Invoke(); await Task.Delay(Timeout.Infinite, ct); }
            return Json(HttpStatusCode.BadRequest, "{}");
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body) => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private RunOptions Opts(Action<RunOptionsBuilder>? tweak = null)
    {
        var b = new RunOptionsBuilder();
        tweak?.Invoke(b);
        return new RunOptions
        {
            Spec = b.Spec ?? ShopSpec.Path,
            Api = "http://fake.test",
            Seed = 1,
            Token = Token,
            Headers = new Dictionary<string, string> { ["X-Api-Key"] = ApiKey },
            SecretHeaderNames = new[] { "X-Api-Key" },
            Only = b.Only,
            Excel = b.Excel,
            Source = new ExcelTestSource(),
            ReportPaths = b.Reports,
            FailOn = b.FailOn,
            BaselinePath = b.Baseline,
            FailOnNew = b.FailOnNew,
            AllowEmpty = b.AllowEmpty,
            MaxTimeSeconds = b.MaxTime,
            HandleInterrupt = b.HandleInterrupt,
            InterruptExitCode = b.InterruptExitCode,
            Tests = b.Tests,
        };
    }

    private sealed class RunOptionsBuilder
    {
        public string? Spec, Only, Excel, Baseline;
        public bool FailOnNew, AllowEmpty, HandleInterrupt;
        public List<string> Reports = new();
        public IReadOnlySet<FailureCause>? FailOn;
        public int? MaxTime;
        public Func<int>? InterruptExitCode;
        public List<string> Tests = new();
    }

    private async Task<(int Exit, string Out)> Go(HttpMessageHandler h, Action<RunOptionsBuilder>? tweak = null, CancellationToken ct = default)
    {
        var sw = new StringWriter { NewLine = "\n" };
        var exit = await new TestRunner(Opts(tweak), sw, h).RunAsync(ct);
        return (exit, sw.ToString());
    }

    private string Out(string name) => Path.Combine(_dir, name);

    private static (int Exit, string Out, string Err) Cli(params string[] args)
    {
        var (o, e) = (Console.Out, Console.Error);
        var (so, se) = (new StringWriter { NewLine = "\n" }, new StringWriter { NewLine = "\n" });
        Console.SetOut(so); Console.SetError(se);
        try { return (Program.Main(args), so.ToString(), se.ToString()); }
        finally { Console.SetOut(o); Console.SetError(e); }
    }

    private static string MakeExcel(string dir, string[][] rows)
    {
        var xlsx = Path.Combine(dir, "t.xlsx");
        ExcelTemplate.Create(OpenApiLoader.Load(ShopSpec.Path), xlsx);
        using var wb = new XLWorkbook(xlsx);
        var ws = wb.Worksheet(ExcelLayout.SheetName);
        foreach (var r in ws.RowsUsed().Skip(1).ToList()) r.Clear();
        for (var i = 0; i < rows.Length; i++)
            for (var c = 0; c < 6; c++) ws.Cell(2 + i, c + 1).Value = rows[i][c];
        wb.Save();
        return xlsx;
    }

    private static TestEnvironment Local => TestGenSettings.Defaults.Environment("local");

    private static JsonObject Read(string path) => (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;

    private static string SchemaPath()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var f = Path.Combine(d.FullName, "docs", "ci", "testgen-result.schema.json");
            if (File.Exists(f)) return f;
        }
        throw new FileNotFoundException("docs/ci/testgen-result.schema.json");
    }

    private static List<string> SchemaErrors(JsonNode? value, JsonObject root) => JsonSchemaCheck.Errors(value, root);

    private static List<string> JUnitErrors(string xml)
    {
        var errors = new List<string>();
        var set = new XmlSchemaSet();
        set.Add(null, Path.Combine(AppContext.BaseDirectory, "Schemas", "junit-10.xsd"));
        var settings = new XmlReaderSettings { ValidationType = ValidationType.Schema, Schemas = set };
        settings.ValidationEventHandler += (_, e) => errors.Add(e.Message);
        using var r = XmlReader.Create(new StringReader(xml), settings);
        while (r.Read()) { }
        return errors;
    }

    private static ReportInfo Info(Action<ReportInfo>? _ = null) =>
        new(new DateTime(2026, 11, 1, 21, 51, 43, DateTimeKind.Local), "http://x.test", null, 12345)
        {
            Spec = "samples/shop.openapi.yaml",
            Version = "1.0.0",
            Duration = TimeSpan.FromSeconds(1.5),
            PossibleTotal = 7,
            TimeoutSeconds = 30,
        };

    private static ReportEntry Entry(string id, CaseResult r, string group = "Genererade tester", string op = "createOrder", ExcelStepInfo? step = null)
    {
        var info = new TestInfo(id, group, "POST /orders", "POST", op, "Skapa order", step is null ? "quantity för lågt (under minimum)" : $"{step.Scenario}, steg {step.Number}",
            new[] { new StatusExpectation(400, 400) }, step is null ? new CaseMeta(TestRule.BelowMinimum, "quantity", "1", "0") : null, step);
        return new ReportEntry(id, group, "POST /orders", info.Description, r) { Info = info };
    }

    private static Exchange Ex(int? status, long ms = 12, string? body = "{}") =>
        new("POST", "http://x.test/orders", new Dictionary<string, string> { ["Content-Type"] = "application/json" }, "{\"quantity\":0}", status, body, ms);

    private static CaseResult Res(int? status, bool ok = false, bool skipped = false, string verdict = "FEL", string? root = null, bool timedOut = false) =>
        new(status, ok, skipped, verdict, new()) { Exchanges = new() { Ex(status) }, RootCauseTestId = root, TimedOut = timedOut };

    /// <summary>En post av varje sort: OK, fel i systemet, förväntan, inloggning, testdata, miljö (timeout), hoppat följdfel.</summary>
    private static List<ReportEntry> OneOfEach() => new()
    {
        Entry("T-001", Res(400, true, verdict: "OK")),
        Entry("T-002", Res(500)),
        Entry("T-003", Res(401)),
        Entry("T-004", Res(409)),
        Entry("T-005", new CaseResult(null, false, false, "FEL (timeout)", new()) { TimedOut = true, Exchanges = new() { Ex(null, 30000) } }),
        Entry("T-006", Res(200, verdict: "FEL (svaret stämmer inte)"), "Excel-tester (t.xlsx)", step: new ExcelStepInfo("Flöde", 1, new[] { "name" })),
        Entry("T-007", new CaseResult(null, false, true, "HOPPAD", new()) { RootCauseTestId = "T-006" }, "Excel-tester (t.xlsx)", step: new ExcelStepInfo("Flöde", 2, Array.Empty<string>())),
    };

    private static readonly HeaderMask Mask = new(new[] { "X-Api-Key" }, new[] { Token, ApiKey });

    // --- JSON ----------------------------------------------------------------------------------

    [Test]
    public void Json_with_all_causes_follows_schema_and_has_all_field_groups()
    {
        var entries = OneOfEach();
        var info = Info() with { ExitCode = 1 };
        var json = JsonResult.Build(info, entries, Mask);
        var schema = (JsonObject)JsonNode.Parse(File.ReadAllText(SchemaPath()))!;
        Assert.That(SchemaErrors(json, schema), Is.Empty);

        Assert.That(json["schemaVersion"]!.GetValue<int>(), Is.EqualTo(1));
        Assert.That(json["totals"]!.ToJsonString(), Is.EqualTo("{\"tests\":7,\"passed\":1,\"failed\":5,\"skipped\":1,\"warnings\":0}"));
        Assert.That(json["verdictKind"]!.GetValue<string>(), Is.EqualTo("system"));
        Assert.That(json["causes"]!.AsArray().Select(c => c!["code"]!.GetValue<string>()), Is.EqualTo(new[] { "system", "expectation", "auth", "environment", "test-data", "dependency" }), "bara orsaker som förekommer");
        Assert.That(json["startedAt"]!.GetValue<string>(), Does.Match(@"^2026-11-01T21:51:43\.000[+-]\d\d:\d\d$"));
        Assert.That(json["finishedAt"]!.GetValue<string>(), Does.Match(@"T21:51:44\.500[+-]\d\d:\d\d$"));
        Assert.That(json["durationSeconds"]!.GetValue<double>(), Is.EqualTo(1.5));
        var t = json["tests"]!.AsArray();
        Assert.That(t[1]!["cause"]!.GetValue<string>(), Is.EqualTo("system"));
        Assert.That(t[1]!["causeName"]!.GetValue<string>(), Is.EqualTo("Fel i systemet"));
        Assert.That(t[1]!["rule"]!.GetValue<string>(), Is.EqualTo("below-minimum"));
        Assert.That(t[1]!["field"]!.GetValue<string>(), Is.EqualTo("quantity"));
        Assert.That(t[1]!["path"]!.GetValue<string>(), Is.EqualTo("/orders"));
        Assert.That(t[1]!["method"]!.GetValue<string>(), Is.EqualTo("POST"));
        Assert.That(t[1]!["expectedStatus"]!.ToJsonString(), Is.EqualTo("[400]"));
        Assert.That(t[1]!["actualStatus"]!.GetValue<int>(), Is.EqualTo(500));
        Assert.That(t[1]!["rerun"]!.GetValue<string>(), Does.EndWith("--seed 12345 --test T-002"));
        Assert.That(t[0]!["cause"], Is.Null, "OK har ingen orsak");
        Assert.That(t[0]!["rerun"], Is.Null);
        Assert.That(t[5]!["rule"]!.GetValue<string>(), Is.EqualTo("excel"));
        Assert.That(t[5]!["scenario"]!.GetValue<string>(), Is.EqualTo("Flöde"));
        Assert.That(t[5]!["cause"]!.GetValue<string>(), Is.EqualTo("expectation"));
        Assert.That(t[6]!["outcome"]!.GetValue<string>(), Is.EqualTo("skipped"));
        Assert.That(t[6]!["rootCauseTestId"]!.GetValue<string>(), Is.EqualTo("T-006"));
        Assert.That(t[6]!["cause"]!.GetValue<string>(), Is.EqualTo("dependency"));
        Assert.That(t.Select(x => x!["key"]!.GetValue<string>()).Distinct().Count(), Is.EqualTo(7), "nycklarna är unika");
    }

    // --- Fel i testgen (verktyg) och varningar ------------------------------------------------------

    private static CaseResult ToolSkip() =>
        new(null, false, true, "HOPPAD (internt fel i testgen)", new() { "NullReferenceException" })
        { SkipNote = "Verktyget kunde inte bygga testet (internt fel): boom Rapportera till den som underhåller testgen." };

    private const string ExtraFieldWarning = "svaret har extra fält som inte finns i specen: tillagt (varning)";

    private static List<ReportEntry> ToolAndWarnings() => new()
    {
        Entry("T-001", Res(400, true, verdict: "OK") with { Warnings = new() { ExtraFieldWarning, "svaret har extra fält som inte finns i specen: annat (varning)" } }),
        Entry("T-002", ToolSkip()),
    };

    [Test]
    public void Tool_error_gives_cause_tool_exit_code_4_and_uncertain_verdict()
    {
        var entries = ToolAndWarnings();
        var outcomes = ReportWriter.Outcomes(entries, Mask);
        Assert.That(PlainLanguage.Cause(entries[1].Info!, entries[1].Result), Is.EqualTo(FailureCause.Tool));
        Assert.That(PlainLanguage.CauseName(FailureCause.Tool), Is.EqualTo("Fel i testgen"));
        Assert.That(PlainLanguage.CauseExplanation(FailureCause.Tool), Does.Contain("Verktyget kunde inte bygga eller skicka testet").And.Contain("rapportera till den som underhåller testgen"));
        Assert.That(CauseCodes.Code(FailureCause.Tool), Is.EqualTo("tool"));
        Assert.That(ExitPolicy.Decide(outcomes), Is.EqualTo(4), "osäkert, inte 1");
        Assert.That(ExitPolicy.Decide(outcomes, CauseCodes.ParseFailOn("tool")), Is.EqualTo(1), "kan väljas med --fail-on");
        Assert.That(VerdictKinds.Of(outcomes), Is.EqualTo("uncertain"));
        var verdict = PlainLanguage.Verdict(outcomes);
        Assert.That(verdict, Does.Contain("fel i testgen").And.Not.Contain("följdfel").And.Not.Contain("tidigare test"));
        Assert.That(PlainLanguage.CauseSummary(outcomes).Single().ListText, Does.StartWith("Fel i testgen: T-002"));
        Assert.That(PlainLanguage.Summary(1, 0, 1, toolSkipped: 1), Does.Contain("1 test kunde inte byggas av testgen").And.Not.Contain("tidigare steg"));
        Assert.That(PlainLanguage.Summary(1, 0, 2, toolSkipped: 1), Does.Contain("1 test hoppades över eftersom ett tidigare steg").And.Contain("1 test kunde inte byggas av testgen"));
    }

    [Test]
    public void Tool_error_and_warnings_in_json_validate_against_schema()
    {
        var json = JsonResult.Build(Info() with { ExitCode = 4 }, ToolAndWarnings(), Mask);
        var schema = (JsonObject)JsonNode.Parse(File.ReadAllText(SchemaPath()))!;
        Assert.That(SchemaErrors(json, schema), Is.Empty);
        Assert.That(json["totals"]!["warnings"]!.GetValue<int>(), Is.EqualTo(2));
        Assert.That(json["verdictKind"]!.GetValue<string>(), Is.EqualTo("uncertain"));
        Assert.That(json["causes"]!.AsArray().Select(c => c!["code"]!.GetValue<string>()), Is.EqualTo(new[] { "tool" }));
        var t = json["tests"]!.AsArray();
        Assert.That(t[1]!["cause"]!.GetValue<string>(), Is.EqualTo("tool"));
        Assert.That(t[1]!["causeName"]!.GetValue<string>(), Is.EqualTo("Fel i testgen"));
        Assert.That(t[0]!["warnings"]!.AsArray().Select(w => w!.GetValue<string>()),
            Is.EqualTo(new[] { "svaret har extra fält som inte finns i specen: tillagt", "svaret har extra fält som inte finns i specen: annat" }), "utan intern markering");
        Assert.That(t[1]!["warnings"]!.AsArray(), Is.Empty);
        Assert.That(json["exitCode"]!.GetValue<int>(), Is.EqualTo(4));
    }

    [Test]
    public void Tool_error_is_skipped_with_message_in_junit()
    {
        var xml = JUnitResult.Text(Info(), ToolAndWarnings(), Mask);
        Assert.That(JUnitErrors(xml), Is.Empty);
        var doc = new XmlDocument(); doc.LoadXml(xml);
        var skipped = (XmlElement)doc.SelectSingleNode("//testcase[starts-with(@name,'T-002 ')]/skipped")!;
        Assert.That(skipped.GetAttribute("message"), Does.StartWith("Fel i testgen: ").And.Contain("Verktyget kunde inte bygga testet"));
        Assert.That(doc.DocumentElement!.GetAttribute("errors"), Is.EqualTo("0"));
        Assert.That(doc.DocumentElement.GetAttribute("failures"), Is.EqualTo("0"));
    }

    [Test]
    public void Warnings_show_in_html_excel_and_summary()
    {
        var entries = ToolAndWarnings();
        var html = ReportWriter.Html(Info(), entries, Mask);
        Assert.That(html, Does.Contain("<strong>Varning:</strong> svaret har extra f&#228;lt som inte finns i specen: tillagt"));
        Assert.That(html, Does.Contain("2 varningar – p&#229;verkar inte resultatet"));
        Assert.That(PlainLanguage.Explain(entries[0].Info!, entries[0].Result).Explanation, Does.Not.Contain("Varning"), "varningarna visas separat, inte i förklaringen");
        Assert.That(html, Does.Contain("Fel i testgen"));
        var xlsx = Out("v.xlsx");
        ReportWriter.Write(xlsx, Info(), entries, Mask);
        using var wb = new XLWorkbook(xlsx);
        var sum = wb.Worksheet("Sammanfattning").CellsUsed().Select(c => c.GetString()).ToList();
        Assert.That(sum, Has.Some.Contains("2 varningar – påverkar inte resultatet"));
        var res = wb.Worksheets.First(w => w.Name != "Sammanfattning");
        var details = res.Cell(2, 12).GetString();
        Assert.That(details, Does.StartWith("Varning (påverkar inte resultatet): svaret har extra fält som inte finns i specen: tillagt"));
        Assert.That(res.Cell(3, 9).GetString(), Is.EqualTo("Fel i testgen"));
        Assert.That(PlainLanguage.WarningSummary(1), Is.EqualTo("1 varning – påverkar inte resultatet"));
        Assert.That(PlainLanguage.WarningSummary(0), Is.Null);
    }

    [TestCase("/usr/local/bin/testgen", "testgen")]
    [TestCase("/home/u/.dotnet/tools/testgen", "testgen")]
    [TestCase(@"C:\tools\testgen.exe", "testgen")]
    [TestCase("/usr/share/dotnet/dotnet", "./testgen.sh")]
    [TestCase("/x/testhost", "./testgen.sh")]
    [TestCase(null, "./testgen.sh")]
    public void Rerun_command_starts_with_testgen_only_when_process_is_the_tool(string? processPath, string expected)
    {
        // Path.GetFileName delar på / (och på \ bara på Windows): prova med bara filnamnet också.
        var name = processPath is null ? null : processPath.Replace('\\', '/');
        Assert.That(ReportWriter.RerunProgramFor(name), Is.EqualTo(expected));
    }

    [Test]
    public void Json_is_written_with_dot_and_iso_even_with_swedish_culture_and_without_bom()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
            var path = Out("r.json");
            ReportWriter.Write(path, Info(), OneOfEach(), Mask);
            var bytes = File.ReadAllBytes(path);
            Assert.That(bytes.Take(3), Is.Not.EqualTo(new byte[] { 0xEF, 0xBB, 0xBF }), "ingen BOM");
            var text = Encoding.UTF8.GetString(bytes);
            Assert.That(text, Does.Contain("\"durationSeconds\": 1.5").And.Not.Contain("1,5"));
            Assert.That(text, Does.Contain("å").Or.Contain("ä"), "svenska tecken skrivs som de är, inte som \\u00e5");
            var junit = Out("r.xml");
            ReportWriter.Write(junit, Info(), OneOfEach(), Mask);
            Assert.That(File.ReadAllText(junit), Does.Match("time=\"1\\.5\"").And.Not.Match("time=\"\\d+,\\d+\""));
        }
        finally { CultureInfo.CurrentCulture = old; }
    }

    [Test]
    public async Task Json_from_real_run_follows_schema_and_contains_no_secrets()
    {
        var xlsx = MakeExcel(_dir, new[]
        {
            new[] { "Flöde", "createCustomer", "name=Anna", "", "201", "name=Fel" },
            new[] { "Flöde", "createCustomer", "name=Bo", "", "201", "name=Bo" },
        });
        var json = Out("run.json"); var xml = Out("run.junit.xml"); var html = Out("run.html"); var xl = Out("run.xlsx");
        var (exit, output) = await Go(new FakeShop(), b => { b.Excel = xlsx; b.Reports.AddRange(new[] { json, xml, html, xl }); });
        Assert.That(exit, Is.EqualTo(ExitCodes.Failed));
        Assert.That(output, Does.Not.Contain(Token).And.Not.Contain(ApiKey));
        var schema = (JsonObject)JsonNode.Parse(File.ReadAllText(SchemaPath()))!;
        var doc = Read(json);
        Assert.That(SchemaErrors(doc, schema), Is.Empty);
        Assert.That(doc["exitCode"]!.GetValue<int>(), Is.EqualTo(1));
        Assert.That(doc["tests"]!.AsArray().Count, Is.EqualTo(doc["totals"]!["tests"]!.GetValue<int>()));
        Assert.That(doc["tests"]!.AsArray().Any(t => t!["outcome"]!.GetValue<string>() == "skipped"), "Excel-flödet har ett hoppat steg");
        foreach (var f in new[] { json, xml, html })
        {
            var text = File.ReadAllText(f);
            Assert.That(text, Does.Not.Contain(Token).And.Not.Contain(ApiKey), f);
        }
        Assert.That(JUnitErrors(File.ReadAllText(xml)), Is.Empty);
        // Excel-filen är en zip: leta i det uppackade innehållet.
        using (var zip = System.IO.Compression.ZipFile.OpenRead(xl))
            foreach (var e in zip.Entries.Where(e => e.FullName.EndsWith(".xml")))
            {
                using var rd = new StreamReader(e.Open());
                var t = rd.ReadToEnd();
                Assert.That(t, Does.Not.Contain(Token).And.Not.Contain(ApiKey), e.FullName);
            }
    }

    // --- JUnit ---------------------------------------------------------------------------------

    [Test]
    public void Junit_maps_causes_to_failure_error_and_skipped_and_validates_against_xsd()
    {
        var xml = JUnitResult.Text(Info() with { ExitCode = 1 }, OneOfEach(), Mask);
        Assert.That(JUnitErrors(xml), Is.Empty);
        var doc = new XmlDocument(); doc.LoadXml(xml);
        var suites = doc.SelectNodes("/testsuites/testsuite")!.Cast<XmlElement>().ToList();
        Assert.That(suites.Select(s => s.GetAttribute("name")), Is.EqualTo(new[] { "Genererade tester", "Excel-tester (t.xlsx)" }), "en testsuite per grupp");
        var root = doc.DocumentElement!;
        Assert.That(new[] { "tests", "failures", "errors" }.Select(a => root.GetAttribute(a)), Is.EqualTo(new[] { "7", "2", "3" }));
        XmlElement Case(string id) => doc.SelectSingleNode($"//testcase[starts-with(@name,'{id} ')]") as XmlElement ?? throw new AssertionException(id);
        Assert.That(Case("T-001").GetAttribute("name"), Is.EqualTo("T-001 quantity för lågt (under minimum)"));
        Assert.That(Case("T-001").GetAttribute("classname"), Is.EqualTo("createOrder"));
        Assert.That(Case("T-001").ChildNodes.Count, Is.EqualTo(0), "OK har inga barn");
        Assert.That(Case("T-002")["failure"]!.GetAttribute("type"), Is.EqualTo("system"));
        Assert.That(Case("T-002")["failure"]!.GetAttribute("message"), Is.EqualTo("väntade 400, fick 500 (Fel i systemet)"));
        Assert.That(Case("T-003")["error"]!.GetAttribute("type"), Is.EqualTo("auth"));
        Assert.That(Case("T-004")["error"]!.GetAttribute("type"), Is.EqualTo("test-data"));
        Assert.That(Case("T-005")["error"]!.GetAttribute("type"), Is.EqualTo("environment"));
        Assert.That(Case("T-006")["failure"]!.GetAttribute("type"), Is.EqualTo("expectation"));
        Assert.That(Case("T-006").GetAttribute("name"), Is.EqualTo("T-006 Flöde, steg 1"));
        Assert.That(Case("T-007")["skipped"]!.GetAttribute("message"), Is.EqualTo("Följd av T-006"));
        Assert.That(Case("T-002")["system-out"]!.InnerText, Does.Contain("curl -X POST").And.Contain("kör om: ./testgen.sh run --spec samples/shop.openapi.yaml --api http://x.test --seed 12345 --test T-002"));
        var props = suites[0].SelectNodes("properties/property")!.Cast<XmlElement>().ToDictionary(p => p.GetAttribute("name"), p => p.GetAttribute("value"));
        Assert.That(props["seed"], Is.EqualTo("12345"));
        Assert.That(props["api"], Is.EqualTo("http://x.test"));
        Assert.That(props["exitCode"], Is.EqualTo("1"));
        Assert.That(props["verdict"], Is.EqualTo("system"));
        Assert.That(props["toolVersion"], Is.EqualTo("1.0.0"));
    }

    [Test]
    public void Junit_handles_control_and_special_characters_in_text_and_masks_secrets()
    {
        var bad = new CaseResult(500, false, false, "FEL", new() { $"svaret ekade Bearer {Token} och <b>&\u0001 tecken\u001f" }) { Exchanges = new() { Ex(500, 5, $"{{\"k\":\"{ApiKey}\"}}") } };
        var xml = JUnitResult.Text(Info(), new List<ReportEntry> { Entry("T-001", bad) }, Mask);
        Assert.That(JUnitErrors(xml), Is.Empty);
        new XmlDocument().LoadXml(xml);   // välformad
        Assert.That(xml, Does.Not.Contain(Token).And.Not.Contain(ApiKey));
    }

    [Test]
    public void Junit_for_zero_tests_and_for_failed_run_is_valid()
    {
        Assert.That(JUnitErrors(JUnitResult.Text(Info(), new List<ReportEntry>(), Mask)), Is.Empty);
        var failure = JUnitResult.TextFailure(Info(), "Kan inte nå API:et", "Starta det", Mask);
        Assert.That(JUnitErrors(failure), Is.Empty);
        Assert.That(failure, Does.Contain("type=\"environment\""));
        var json = JsonResult.BuildFailure(Info(), "Kan inte nå API:et", "Starta det", 3, Mask);
        Assert.That(SchemaErrors(json, (JsonObject)JsonNode.Parse(File.ReadAllText(SchemaPath()))!), Is.Empty);
        Assert.That(json["exitCode"]!.GetValue<int>(), Is.EqualTo(3));
        Assert.That(json["verdictKind"]!.GetValue<string>(), Is.EqualTo("unreachable"));
    }

    [TestCase("x.json")]
    [TestCase("x.xml")]
    [TestCase("x.junit.xml")]
    [TestCase("X.JSON")]
    public void Report_types_json_xml_and_junit_xml_are_accepted(string name)
    {
        Assert.DoesNotThrow(() => ReportWriter.Validate(name));
    }

    [Test]
    public void Unknown_report_type_mentions_the_new_extensions()
    {
        var e = Assert.Throws<ArgumentException>(() => ReportWriter.Validate("x.txt"))!;
        Assert.That(e.Message, Does.Contain(".html").And.Contain(".xlsx").And.Contain(".json").And.Contain(".xml").And.Contain(".junit.xml"));
    }

    [Test]
    public async Task Multiple_reports_at_once_and_json_xml_are_written_even_when_API_is_unreachable()
    {
        var json = Out("a.json"); var xml = Out("a.junit.xml"); var html = Out("a.html");
        var down = new ThrowingApi();
        await Assert.ThrowsAsync<ApiUnreachableException>(async () =>
            await new TestRunner(Opts(b => { b.Reports.AddRange(new[] { json, xml, html }); }), new StringWriter { NewLine = "\n" }, down).RunAsync());
        var doc = Read(json);
        Assert.That(doc["exitCode"]!.GetValue<int>(), Is.EqualTo(3));
        Assert.That(doc["verdictKind"]!.GetValue<string>(), Is.EqualTo("unreachable"));
        Assert.That(JUnitErrors(File.ReadAllText(xml)), Is.Empty);
        Assert.That(File.Exists(html));
    }

    private sealed class ThrowingApi : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) =>
            throw new HttpRequestException("nere", null, null) { };
    }

    // --- Exitkoder -----------------------------------------------------------------------------

    [Test]
    public async Task Exit_code_4_for_401_environment_and_409_without_system_errors()
    {
        Assert.That((await Go(new Always(HttpStatusCode.Unauthorized))).Exit, Is.EqualTo(4), "401 överallt");
        Assert.That((await Go(new Always(HttpStatusCode.Forbidden))).Exit, Is.EqualTo(4));
        Assert.That((await Go(new Always(HttpStatusCode.Conflict))).Exit, Is.EqualTo(4));
        Assert.That((await Go(new Always(HttpStatusCode.BadGateway))).Exit, Is.EqualTo(4), "502 är miljön");
        Assert.That((await Go(new Always(HttpStatusCode.ServiceUnavailable))).Exit, Is.EqualTo(4));
        Assert.That((await Go(new Always(HttpStatusCode.GatewayTimeout))).Exit, Is.EqualTo(4));
        Assert.That((await Go(new Always(HttpStatusCode.InternalServerError))).Exit, Is.EqualTo(1), "500 är fel i systemet");
    }

    [Test]
    public async Task Exit_code_1_applies_even_with_uncertain_responses_and_0_for_all_OK()
    {
        // FakeShop: giltig indata OK, order med quantity 0 ger 500; mixen innehåller alltså både fel i systemet och OK.
        Assert.That((await Go(new FakeShop(), b => b.Only = "createOrder")).Exit, Is.EqualTo(1));
        var entries = OneOfEach();
        Assert.That(ExitPolicy.Decide(ReportWriter.Outcomes(entries, Mask)), Is.EqualTo(1), "system + inloggning + testdata + miljö + följd");
        Assert.That(ExitPolicy.Decide(ReportWriter.Outcomes(entries.Where(e => e.Id is "T-001" or "T-003" or "T-004").ToList(), Mask)), Is.EqualTo(4));
        Assert.That(ExitPolicy.Decide(ReportWriter.Outcomes(entries.Take(1).ToList(), Mask)), Is.EqualTo(0));
        Assert.That(ExitPolicy.Decide(ReportWriter.Outcomes(entries.Where(e => e.Id == "T-006").ToList(), Mask)), Is.EqualTo(1), "förväntan räknas som fel");
    }

    [Test]
    public async Task Fail_on_controls_which_causes_give_exit_code_1()
    {
        Assert.That((await Go(new Always(HttpStatusCode.Unauthorized), b => b.FailOn = CauseCodes.ParseFailOn("system,expectation,auth"))).Exit, Is.EqualTo(1));
        Assert.That((await Go(new Always(HttpStatusCode.Unauthorized), b => b.FailOn = CauseCodes.ParseFailOn("environment"))).Exit, Is.EqualTo(4));
        Assert.That((await Go(new Always(HttpStatusCode.InternalServerError), b => b.FailOn = CauseCodes.ParseFailOn("auth"))).Exit, Is.EqualTo(4),
            "fel i systemet räknas inte om det inte står i --fail-on");
        Assert.That((await Go(new Always(HttpStatusCode.BadGateway), b => b.FailOn = CauseCodes.ParseFailOn("environment"))).Exit, Is.EqualTo(1));
        Assert.Throws<UsageException>(() => CauseCodes.ParseFailOn("miljö"), "å, ä och ö tolkas inte");
        Assert.Throws<UsageException>(() => CauseCodes.ParseFailOn("forvantan"), "svenska koder finns inte");
    }

    [Test]
    public void Fail_on_gives_clear_error_for_unknown_code_and_empty_list()
    {
        var e = Assert.Throws<UsageException>(() => CauseCodes.ParseFailOn("system,sysstem"))!;
        Assert.That(e.Message, Does.Contain("'sysstem' är ingen orsakskod").And.Contain("Menade du system?"));
        Assert.That(e.Tip, Does.Contain("system, expectation, auth, environment, test-data, missing-data, test-user-rejected, rate-limited, tool, dependency"));
        Assert.Throws<UsageException>(() => CauseCodes.ParseFailOn(" , "));
        Assert.That(CauseCodes.ParseFailOn("System, EXPECTATION"), Is.EquivalentTo(CauseCodes.DefaultFailOn));
    }

    [Test]
    public void Cli_fail_on_and_max_time_are_validated_and_help_describes_exit_code_4()
    {
        var (exit, _, err) = Cli("run", "--spec", ShopSpec.Path, "--api", "http://127.0.0.1:1", "--fail-on", "bogus");
        Assert.That(exit, Is.EqualTo(2));
        Assert.That(err, Does.Contain("--fail-on: 'bogus' är ingen orsakskod"));
        Assert.That(Cli("run", "--spec", ShopSpec.Path, "--api", "http://127.0.0.1:1", "--max-time", "0").Exit, Is.EqualTo(2));
        Assert.That(Cli("run", "--spec", ShopSpec.Path, "--api", "http://127.0.0.1:1", "--max-time", "abc").Err, Does.Contain("--max-time måste vara ett positivt antal sekunder"));
        var help = HelpText.For("run")!;
        foreach (var word in new[] { "Osäkert resultat", "--fail-on", "--baseline", "--fail-on-new", "--seed ci", "--max-time", "--allow-empty", ".json", ".junit.xml", "130", "143" })
            Assert.That(help, Does.Contain(word), word);
    }

    // --- Tomma körningar ------------------------------------------------------------------------

    private string EmptySpec()
    {
        var f = Out("tom.yaml");
        File.WriteAllText(f, "openapi: 3.0.1\ninfo:\n  title: tom\n  version: \"1\"\npaths: {}\n");
        return f;
    }

    [Test]
    public async Task Empty_spec_gives_exit_2_with_explanation_and_allow_empty_gives_0_with_report()
    {
        var spec = EmptySpec();
        var e = (await Assert.ThrowsAsync<UsageException>(async () => await Go(new Always(HttpStatusCode.OK), b => b.Spec = spec)))!;
        Assert.That(e.Message, Does.Contain("inga tester att köra").And.Contain("paths är tom"));
        Assert.That(e.Tip, Does.Contain("--allow-empty"));
        Assert.That(FriendlyErrors.Translate(e).ExitCode, Is.EqualTo(2));

        var json = Out("tom.json"); var xml = Out("tom.xml");
        var (exit, output) = await Go(new Always(HttpStatusCode.OK), b => { b.Spec = spec; b.AllowEmpty = true; b.Reports.AddRange(new[] { json, xml }); });
        Assert.That(exit, Is.EqualTo(0));
        Assert.That(output, Does.Contain("Inga tester att köra."));
        var doc = Read(json);
        Assert.That(doc["verdictKind"]!.GetValue<string>(), Is.EqualTo("empty"));
        Assert.That(doc["totals"]!["tests"]!.GetValue<int>(), Is.EqualTo(0));
        Assert.That(SchemaErrors(doc, (JsonObject)JsonNode.Parse(File.ReadAllText(SchemaPath()))!), Is.Empty);
        Assert.That(JUnitErrors(File.ReadAllText(xml)), Is.Empty);
    }

    [Test]
    public async Task Filter_that_matches_nothing_gives_exit_2_but_allow_empty_is_fine()
    {
        // --only getOrder valjer bara T-032; --test T-001 finns men ingår inte i urvalet.
        var e = (await Assert.ThrowsAsync<UsageException>(async () => await Go(new Always(HttpStatusCode.OK), b => { b.Only = "getOrder"; b.Tests.Add("T-001"); })))!;
        Assert.That(e.Message, Does.Contain("Inga tester matchade urvalet").And.Contain("0 av 34 möjliga"));
        var (exit, _) = await Go(new Always(HttpStatusCode.OK), b => { b.Only = "getOrder"; b.Tests.Add("T-001"); b.AllowEmpty = true; });
        Assert.That(exit, Is.EqualTo(0));
    }

    // --- Tomma hemligheter ----------------------------------------------------------------------

    [Test]
    public void Empty_token_and_empty_header_are_errors_with_env_tip()
    {
        var e = Assert.Throws<UsageException>(() => RunSettings.Resolve("http://x", Array.Empty<string>(), "", null, Local))!;
        Assert.That(e.Message, Is.EqualTo("--token är tom – saknas hemligheten i CI? Använd hellre --token '${env:TOKEN}' så får du ett tydligt fel om variabeln saknas."));
        Assert.Throws<UsageException>(() => RunSettings.Resolve("http://x", Array.Empty<string>(), "   ", null, Local));
        var h = Assert.Throws<UsageException>(() => RunSettings.Resolve("http://x", new[] { "X-Api-Key: " }, null, null, Local))!;
        Assert.That(h.Message, Does.Contain("\"X-Api-Key: \" har inget värde").And.Contain("--header 'X-Api-Key: ${env:X_API_KEY}'"));
        // ${env:} på en tom variabel är redan ett tydligt fel; ett vanligt värde och ingen token går bra.
        Assert.Throws<ArgumentException>(() => RunSettings.Resolve("http://x", Array.Empty<string>(), "${env:SAKNAS}", null, Local, getEnv: _ => null));
        Assert.DoesNotThrow(() => RunSettings.Resolve("http://x", new[] { "X-Api-Key: abc" }, "t", null, Local));
        Assert.DoesNotThrow(() => RunSettings.Resolve("http://x", Array.Empty<string>(), null, null, Local));
    }

    [Test]
    public void Cli_empty_token_gives_exit_2()
    {
        var (exit, _, err) = Cli("run", "--spec", ShopSpec.Path, "--api", "http://127.0.0.1:1", "--token", "");
        Assert.That(exit, Is.EqualTo(2));
        Assert.That(err, Does.Contain("--token är tom").And.Contain("${env:TOKEN}"));
        var (exit2, _, err2) = Cli("run", "--spec", ShopSpec.Path, "--api", "http://127.0.0.1:1", "--header", "X-Api-Key: ");
        Assert.That(exit2, Is.EqualTo(2));
        Assert.That(err2, Does.Contain("har inget värde"));
    }

    // --- Seed -----------------------------------------------------------------------------------

    [Test]
    public void Seed_large_numbers_hash_stably_and_non_numbers_give_clear_error()
    {
        Assert.That(SeedSpec.Parse("12345").Seed, Is.EqualTo(12345));
        Assert.That(SeedSpec.Parse("-7").Seed, Is.EqualTo(-7));
        Assert.That(SeedSpec.Parse("2147483647").Seed, Is.EqualTo(int.MaxValue));
        var big = SeedSpec.Parse("12345678901");
        Assert.That(big.Seed, Is.GreaterThan(0));
        Assert.That(big.Note, Does.Contain("12345678901").And.Contain(big.Seed.ToString()));
        Assert.That(SeedSpec.Parse("12345678901").Seed, Is.EqualTo(big.Seed), "stabilt");
        Assert.That(SeedSpec.Parse("12345678902").Seed, Is.Not.EqualTo(big.Seed));
        Assert.That(SeedSpec.Parse("99999999999999999999999999").Seed, Is.GreaterThan(0), "längre än long går också");
        Assert.That(SeedSpec.Parse("random").Random);
        Assert.Throws<UsageException>(() => SeedSpec.Parse("slumpa"), "svenska ord tas inte emot");
        Assert.That(SeedSpec.Parse("RANDOM").Random);
        foreach (var bad in new[] { "hej", "12x", "1.5", "", "--1", "1e5" })
        {
            var e = Assert.Throws<UsageException>(() => SeedSpec.Parse(bad), bad)!;
            Assert.That(e.Message, Does.Contain("--seed måste vara ett heltal"));
        }
    }

    [Test]
    public void Seed_ci_takes_build_from_ci_variables_and_otherwise_randomizes()
    {
        Func<string, string?> Env(params (string, string)[] vars) => n => vars.Where(v => v.Item1 == n).Select(v => v.Item2).FirstOrDefault();
        var a = SeedSpec.Parse("ci", Env(("GITHUB_RUN_ID", "9876543210"), ("GITHUB_RUN_ATTEMPT", "1")));
        var again = SeedSpec.Parse("ci", Env(("GITHUB_RUN_ID", "9876543210"), ("GITHUB_RUN_ATTEMPT", "1")));
        var retry = SeedSpec.Parse("ci", Env(("GITHUB_RUN_ID", "9876543210"), ("GITHUB_RUN_ATTEMPT", "2")));
        Assert.That(a.Seed, Is.Not.Null.And.EqualTo(again.Seed), "samma bygge, samma frö");
        Assert.That(retry.Seed, Is.Not.EqualTo(a.Seed), "ny omkörning, nytt frö");
        Assert.That(a.Note, Does.Contain("GITHUB_RUN_ID=9876543210").And.Contain("GITHUB_RUN_ATTEMPT=1").And.Contain(a.Seed.ToString()));
        Assert.That(SeedSpec.Parse("ci", Env(("BUILD_BUILDID", "4711"))).Seed, Is.Not.Null);
        Assert.That(SeedSpec.Parse("ci", Env(("CI_PIPELINE_ID", "88"))).Seed, Is.Not.Null);
        Assert.That(SeedSpec.Parse("ci", Env(("BUILD_NUMBER", "17"))).Seed, Is.Not.Null);
        var none = SeedSpec.Parse("ci", Env());
        Assert.That(none.Random);
        Assert.That(none.Seed, Is.Null);
        Assert.That(none.Note, Does.Contain("GITHUB_RUN_ID"));
    }

    [Test]
    public void Cli_seed_with_large_number_works_and_does_not_give_exit_2()
    {
        var (exit, _, err) = Cli("run", "--spec", ShopSpec.Path, "--api", "http://127.0.0.1:1", "--only", "getOrder", "--seed", "12345678901");
        Assert.That(exit, Is.EqualTo(3), "API:et nås inte, men seeden godtogs");
        Assert.That(err, Does.Contain("Talet 12345678901 är större än ett 32-bitars heltal"));
        Assert.That(Cli("run", "--spec", ShopSpec.Path, "--api", "http://127.0.0.1:1", "--seed", "hej").Exit, Is.EqualTo(2));
    }

    [Test]
    public async Task Seed_note_is_written_to_terminal_and_json()
    {
        var json = Out("s.json");
        var sw = new StringWriter { NewLine = "\n" };
        var big = SeedSpec.Parse("12345678901");
        var exit = await new TestRunner(new RunOptions { Spec = ShopSpec.Path, Api = "http://fake.test", Only = "getOrder", Seed = big.Seed, SeedNote = big.Note, ReportPaths = new[] { json } }, sw, new Always(HttpStatusCode.OK)).RunAsync();
        Assert.That(exit, Is.GreaterThanOrEqualTo(0));
        Assert.That(sw.ToString(), Does.Contain(big.Note!));
        var doc = Read(json);
        Assert.That(doc["seed"]!.GetValue<int>(), Is.EqualTo(big.Seed));
        Assert.That(doc["seedNote"]!.GetValue<string>(), Is.EqualTo(big.Note));
    }

    // --- Avbrott och --max-time ------------------------------------------------------------------

    [Test]
    public async Task Signal_aborts_call_writes_partial_report_and_gives_130_or_143()
    {
        foreach (var code in new[] { 130, 143 })
        {
            var json = Out($"sig{code}.json"); var xml = Out($"sig{code}.xml"); var html = Out($"sig{code}.html");
            using var cts = new CancellationTokenSource();
            var (exit, output) = await Go(new HangsAfter(3, () => cts.Cancel()), b => { b.HandleInterrupt = true; b.InterruptExitCode = () => code; b.Reports.AddRange(new[] { json, xml, html }); }, cts.Token);
            Assert.That(exit, Is.EqualTo(code));
            Assert.That(output, Does.Contain("Obs: Körningen avbröts (signal)").And.Contain("3 av de 34 planerade testerna hann köras"));
            var doc = Read(json);
            Assert.That(doc["aborted"]!.GetValue<bool>());
            Assert.That(doc["abortKind"]!.GetValue<string>(), Is.EqualTo("signal"));
            Assert.That(doc["exitCode"]!.GetValue<int>(), Is.EqualTo(code));
            Assert.That(doc["notice"]!.GetValue<string>(), Does.StartWith("Körningen avbröts (signal)"));
            Assert.That(doc["tests"]!.AsArray().Count, Is.EqualTo(3));
            Assert.That(SchemaErrors(doc, (JsonObject)JsonNode.Parse(File.ReadAllText(SchemaPath()))!), Is.Empty);
            Assert.That(JUnitErrors(File.ReadAllText(xml)), Is.Empty);
            Assert.That(WebUtility.HtmlDecode(File.ReadAllText(html)), Does.Contain("Körningen avbröts (signal)"));
        }
    }

    [Test]
    public async Task Without_HandleInterrupt_abort_is_thrown_as_for_web_ui()
    {
        using var cts = new CancellationTokenSource();
        var runner = new TestRunner(Opts(), new StringWriter { NewLine = "\n" }, new HangsAfter(2, () => cts.Cancel()));
        await Assert.CatchAsync<OperationCanceledException>(async () => await runner.RunAsync(cts.Token));
        Assert.That(runner.Outcomes, Has.Count.EqualTo(2), "det som hunnit bli klart finns kvar");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Max_time_aborts_writes_partial_report_and_gives_exit_3_with_notice()
    {
        var json = Out("mt.json");
        var started = DateTime.UtcNow;
        var (exit, output) = await Go(new HangsAfter(2), b => { b.MaxTime = 1; b.Reports.Add(json); });
        Assert.That(DateTime.UtcNow - started, Is.LessThan(TimeSpan.FromSeconds(10)));
        Assert.That(exit, Is.EqualTo(ExitCodes.Unreachable));
        Assert.That(output, Does.Contain("Obs: Körningen avbröts för att --max-time 1 s löpte ut").And.Contain("2 av de 34 planerade testerna hann köras"));
        var doc = Read(json);
        Assert.That(doc["abortKind"]!.GetValue<string>(), Is.EqualTo("max-time"));
        Assert.That(doc["exitCode"]!.GetValue<int>(), Is.EqualTo(3));
        Assert.That(doc["tests"]!.AsArray().Count, Is.EqualTo(2));
    }

    // --- Spec-URL som inte svarar --------------------------------------------------------------------

    [Test]
    public void Spec_url_not_responding_gives_exit_3_but_404_and_wrong_content_give_2()
    {
        const string url = "http://spec.test/openapi.yaml";
        var noAnswer = FriendlyErrors.Wrap(SourceKind.Spec, url, new HttpRequestException(HttpRequestError.ConnectionError, "refused"));
        Assert.That(noAnswer, Is.TypeOf<SpecUnreachableException>());
        Assert.That(FriendlyErrors.Translate(noAnswer).ExitCode, Is.EqualTo(3));
        Assert.That(FriendlyErrors.Translate(noAnswer).What, Does.Contain("Kan inte hämta OpenAPI-spec från http://spec.test/openapi.yaml").And.Contain("ingen svarar"));
        var dns = FriendlyErrors.Wrap(SourceKind.Spec, url, new HttpRequestException(HttpRequestError.NameResolutionError, "dns"));
        Assert.That(FriendlyErrors.Translate(dns).ExitCode, Is.EqualTo(3));
        var timeout = FriendlyErrors.Wrap(SourceKind.Spec, url, new TaskCanceledException("t", new TimeoutException()));
        Assert.That(FriendlyErrors.Translate(timeout).ExitCode, Is.EqualTo(3));
        Assert.That(FriendlyErrors.Translate(timeout).What, Does.Contain("svarade inte i tid"));
        var notFound = FriendlyErrors.Wrap(SourceKind.Spec, url, new HttpRequestException(HttpRequestError.Unknown, "404", null, HttpStatusCode.NotFound));
        Assert.That(FriendlyErrors.Translate(notFound).ExitCode, Is.EqualTo(2));
        var junk = FriendlyErrors.Wrap(SourceKind.Spec, url, new InvalidDataException("html"));
        Assert.That(FriendlyErrors.Translate(junk).ExitCode, Is.EqualTo(2));
        // En lokal fil som saknas är fortfarande 2.
        Assert.That(FriendlyErrors.Translate(FriendlyErrors.Wrap(SourceKind.Spec, "nope.yaml", new FileNotFoundException("x", "nope.yaml"))).ExitCode, Is.EqualTo(2));
    }

    [Test]
    public void Cli_spec_url_without_response_gives_exit_3()
    {
        var (exit, _, err) = Cli("run", "--spec", "http://127.0.0.1:1/openapi.yaml", "--api", "http://127.0.0.1:1");
        Assert.That(exit, Is.EqualTo(3));
        Assert.That(err, Does.Contain("Kan inte hämta OpenAPI-spec"));
    }

    // --- UTF-8 ----------------------------------------------------------------------------------

    [Test]
    public void Main_sets_utf8_without_bom_on_console()
    {
        var before = Console.OutputEncoding;
        try
        {
            Cli("--help");
            Assert.That(Console.OutputEncoding.CodePage, Is.EqualTo(65001));
            Assert.That(Console.OutputEncoding.GetPreamble(), Is.Empty);
        }
        finally { try { Console.OutputEncoding = before; } catch (IOException) { } }
    }

    // --- Jämförelse med förra körningen -------------------------------------------------------------

    private async Task<string> Baseline(string name = "forra.json")
    {
        var path = Out(name);
        var (exit, _) = await Go(new FakeShop(), b => { b.Only = "createOrder"; b.Reports.Add(path); });
        Assert.That(exit, Is.EqualTo(1));
        return path;
    }

    [Test]
    public async Task Compare_gives_new_fixed_and_persisting_and_matches_on_key_not_id()
    {
        var prev = await Baseline();
        var doc = Read(prev);
        var tests = doc["tests"]!.AsArray();
        // Gör om förra rapporten: ett fel lagas (var FEL, är nu OK), ett nytt fel (fanns inte), och alla id:n ändras.
        var firstOk = tests.First(t => t!["outcome"]!.GetValue<string>() == "passed")!.AsObject();
        var firstFail = tests.First(t => t!["outcome"]!.GetValue<string>() == "failed")!.AsObject();
        var secondFail = tests.Last(t => t!["outcome"]!.GetValue<string>() == "failed")!.AsObject();
        Assert.That(firstFail["key"]!.GetValue<string>(), Is.Not.EqualTo(secondFail["key"]!.GetValue<string>()));
        firstOk["outcome"] = "failed"; firstOk["cause"] = "system";
        var removed = secondFail["key"]!.GetValue<string>();
        tests.Remove(secondFail);
        foreach (var t in tests) t!["id"] = "T-9" + t["id"]!.GetValue<string>()[2..];   // andra id:n, samma nycklar
        File.WriteAllText(prev, doc.ToJsonString());

        var now = Out("nu.json");
        var (exit, output) = await Go(new FakeShop(), b => { b.Only = "createOrder"; b.Baseline = prev; b.Reports.Add(now); });
        Assert.That(exit, Is.EqualTo(1));
        var cmp = Read(now)["comparison"]!;
        Assert.That(cmp["newFailures"]!.AsArray().Select(i => i!["key"]!.GetValue<string>()), Is.EqualTo(new[] { removed }));
        Assert.That(cmp["fixed"]!.AsArray().Select(i => i!["key"]!.GetValue<string>()), Is.EqualTo(new[] { firstOk["key"]!.GetValue<string>() }));
        Assert.That(cmp["persisting"]!.AsArray().Count, Is.EqualTo(tests.Count(t => t!["outcome"]!.GetValue<string>() == "failed") - 1));
        Assert.That(cmp["newFailures"]![0]!["previousId"], Is.Null, "fanns inte förra gången");
        Assert.That(cmp["persisting"]![0]!["previousId"]!.GetValue<string>(), Does.StartWith("T-9"), "id:t ändrades men testet kändes igen");
        Assert.That(output, Does.Contain("Jämförelse med ").And.Contain("Nya FEL sedan förra körningen: T-").And.Contain("Lagade sedan sist: T-").And.Contain("Kvar sedan tidigare: T-"));
        Assert.That(SchemaErrors(Read(now), (JsonObject)JsonNode.Parse(File.ReadAllText(SchemaPath()))!), Is.Empty);
    }

    [Test]
    public async Task Compare_shows_in_html_excel_and_junit()
    {
        var prev = await Baseline();
        var html = Out("j.html"); var xlsx = Out("j.xlsx"); var xml = Out("j.xml");
        await Go(new FakeShop(), b => { b.Only = "createOrder"; b.Baseline = prev; b.Reports.AddRange(new[] { html, xlsx, xml }); });
        var text = WebUtility.HtmlDecode(File.ReadAllText(html));
        Assert.That(text, Does.Contain("Nya FEL sedan förra körningen:").And.Contain("Lagade sedan sist:").And.Contain("Kvar sedan tidigare:"));
        using (var wb = new XLWorkbook(xlsx))
        {
            var cells = wb.Worksheet("Sammanfattning").CellsUsed().Select(c => c.GetString()).ToList();
            Assert.That(cells, Does.Contain("Nya FEL sedan förra körningen").And.Contain("Lagade sedan sist").And.Contain("Kvar sedan tidigare"));
        }
        var junit = File.ReadAllText(xml);
        Assert.That(junit, Does.Contain("name=\"baseline\"").And.Contain("name=\"persisting\""));
        Assert.That(JUnitErrors(junit), Is.Empty);
    }

    [Test]
    public async Task Fail_on_new_gives_exit_1_only_for_new_failures()
    {
        var prev = await Baseline();
        // Samma fel som förra gången: utan --fail-on-new exit 1, med den exit 0.
        Assert.That((await Go(new FakeShop(), b => { b.Only = "createOrder"; b.Baseline = prev; })).Exit, Is.EqualTo(1));
        Assert.That((await Go(new FakeShop(), b => { b.Only = "createOrder"; b.Baseline = prev; b.FailOnNew = true; })).Exit, Is.EqualTo(0));
        // Ta bort ett fel ur förra rapporten: nu är det ett nytt FEL i systemet.
        var doc = Read(prev);
        var tests = doc["tests"]!.AsArray();
        tests.Remove(tests.First(t => t!["outcome"]!.GetValue<string>() == "failed"));
        File.WriteAllText(prev, doc.ToJsonString());
        Assert.That((await Go(new FakeShop(), b => { b.Only = "createOrder"; b.Baseline = prev; b.FailOnNew = true; })).Exit, Is.EqualTo(1));
        // Nya FEL av osäker orsak (401 överallt) är inte 1 utan 4.
        Assert.That((await Go(new Always(HttpStatusCode.Unauthorized), b => { b.Only = "createOrder"; b.Baseline = prev; b.FailOnNew = true; })).Exit, Is.EqualTo(4));
    }

    [Test]
    public async Task Fail_on_new_without_compare_is_a_usage_error()
    {
        var e = (await Assert.ThrowsAsync<UsageException>(async () => await Go(new FakeShop(), b => b.FailOnNew = true)))!;
        Assert.That(e.Message, Does.Contain("--fail-on-new").And.Contain("--baseline"));
        await Task.CompletedTask;
    }

    [Test]
    public void Compare_gives_clear_error_for_missing_file_wrong_format_and_other_schema_version()
    {
        var missing = Assert.Throws<UsageException>(() => Baseline_Load(Out("finns-inte.json")))!;
        Assert.That(missing.Message, Does.Contain("Hittar inte filen att jämföra med (--baseline)"));
        Assert.That(FriendlyErrors.Translate(missing).ExitCode, Is.EqualTo(2));

        File.WriteAllText(Out("trasig.json"), "{ inte json");
        Assert.That(Assert.Throws<UsageException>(() => Baseline_Load(Out("trasig.json")))!.Message, Does.Contain("är ingen JSON-rapport från testgen"));
        File.WriteAllText(Out("annat.json"), "{\"foo\": 1}");
        Assert.That(Assert.Throws<UsageException>(() => Baseline_Load(Out("annat.json")))!.Message, Does.Contain("ser inte ut som en JSON-rapport från testgen"));
        File.WriteAllText(Out("lista.json"), "[1,2]");
        Assert.Throws<UsageException>(() => Baseline_Load(Out("lista.json")));
        File.WriteAllText(Out("v2.json"), "{\"schemaVersion\": 2, \"tests\": []}");
        var v2 = Assert.Throws<UsageException>(() => Baseline_Load(Out("v2.json")))!;
        Assert.That(v2.Message, Does.Contain("schemaVersion 2").And.Contain("läser schemaVersion 1"));
        File.WriteAllText(Out("html.json"), "<html></html>");
        Assert.Throws<UsageException>(() => Baseline_Load(Out("html.json")));
        File.WriteAllText(Out("ok.json"), "{\"schemaVersion\": 1, \"tests\": []}");
        Assert.DoesNotThrow(() => Baseline_Load(Out("ok.json")));
    }

    private static TestGen.Core.Run.Baseline Baseline_Load(string path) => TestGen.Core.Run.Baseline.Load(path);

    [Test]
    public void Cli_compare_with_missing_file_gives_exit_2_and_fail_on_new_needs_baseline()
    {
        var (exit, _, err) = Cli("run", "--spec", ShopSpec.Path, "--api", "http://127.0.0.1:1", "--baseline", Out("nej.json"));
        Assert.That(exit, Is.EqualTo(2));
        Assert.That(err, Does.Contain("Hittar inte filen att jämföra med"));
        Assert.That(Cli("run", "--spec", ShopSpec.Path, "--api", "http://127.0.0.1:1", "--baseline", Out("nej.json")).Exit, Is.EqualTo(2));
        var (e2, _, err2) = Cli("run", "--spec", ShopSpec.Path, "--api", "http://127.0.0.1:1", "--fail-on-new");
        Assert.That(e2, Is.EqualTo(2));
        Assert.That(err2, Does.Contain("--fail-on-new"));
    }

    [Test]
    public async Task Report_to_same_file_as_compare_is_allowed_but_not_to_other_non_report()
    {
        var prev = await Baseline();
        // Läses före körningen, så den får skrivas över av den nya rapporten.
        var (exit, _) = await Go(new FakeShop(), b => { b.Only = "createOrder"; b.Baseline = prev; b.Reports.Add(prev); });
        Assert.That(exit, Is.EqualTo(1));
        Assert.That(Read(prev)["comparison"], Is.Not.Null);
        // En JSON-fil som inte är en testgen-rapport skrivs inte över utan --force.
        var other = Out("annan.json");
        File.WriteAllText(other, "{\"min\": \"fil\"}");
        var e = (await Assert.ThrowsAsync<UsageException>(async () => await Go(new FakeShop(), b => { b.Only = "createOrder"; b.Reports.Add(other); })))!;
        Assert.That(e.Message, Does.Contain("ser inte ut som en rapport"));
    }

    // --- Verdict och orsaker --------------------------------------------------------------------

    [Test]
    public void Verdict_kind_and_gateway_text()
    {
        var outcomes = ReportWriter.Outcomes(OneOfEach(), Mask);
        Assert.That(VerdictKinds.Of(outcomes), Is.EqualTo("system"));
        Assert.That(VerdictKinds.Of(outcomes.Where(o => o.Id == "T-006").ToList()), Is.EqualTo("expectation"));
        Assert.That(VerdictKinds.Of(outcomes.Where(o => o.Id is "T-003" or "T-004").ToList()), Is.EqualTo("uncertain"));
        Assert.That(VerdictKinds.Of(outcomes.Take(1).ToList()), Is.EqualTo("ok"));
        Assert.That(VerdictKinds.Of(new List<TestOutcome>()), Is.EqualTo("empty"));
        var gateway = ReportWriter.Outcomes(new List<ReportEntry> { Entry("T-001", Res(503)), Entry("T-002", Res(502)) }, Mask);
        Assert.That(PlainLanguage.Verdict(gateway), Does.StartWith("Inget säkert svar: 2 tester fick inget svar – miljön svarade inte (svar 502/503 från en gateway"));
    }

    [Test]
    public void Keys_are_stable_and_unique()
    {
        var a = Entry("T-001", Res(400, true, verdict: "OK"));
        var b = Entry("T-009", Res(400, true, verdict: "OK"));
        var keys = TestKeys.For(new[] { a.Info!, b.Info! });
        Assert.That(keys, Is.EqualTo(new[] { "api:createOrder:below-minimum:quantity", "api:createOrder:below-minimum:quantity#2" }));
        Assert.That(TestKeys.Base("createOrder", "excel", null, "Flöde", 2), Is.EqualTo("api:createOrder:excel:Flöde:2"));
    }
}
