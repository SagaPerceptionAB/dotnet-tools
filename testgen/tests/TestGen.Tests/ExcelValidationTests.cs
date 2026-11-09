using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using NUnit.Framework;
using TestGen.Cli;
using TestGen.Core;
using TestGen.Core.Run;

namespace TestGen.Tests;

/// <summary>Excel-filen kontrolleras helt mot specen innan något test körs; felen anger cell och samlas i en lista.</summary>
public class ExcelValidationTests
{
    private string _dir = "";

    [SetUp] public void SetUp() => _dir = System.IO.Directory.CreateTempSubdirectory("testgen-xlsx-").FullName;
    [TearDown] public void TearDown() => System.IO.Directory.Delete(_dir, true);

    private static readonly string[] Std = ExcelLayout.Headers;

    private string Book(string[][] rows, string[]? headers = null, string name = "t.xlsx")
    {
        var path = Path.Combine(_dir, name);
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Tester");
        var h = headers ?? Std;
        for (var i = 0; i < h.Length; i++) ws.Cell(1, i + 1).Value = h[i];
        for (var r = 0; r < rows.Length; r++)
            for (var c = 0; c < rows[r].Length; c++)
                if (!string.IsNullOrEmpty(rows[r][c])) ws.Cell(r + 2, c + 1).Value = rows[r][c];
        wb.SaveAs(path);
        return path;
    }

    private sealed class Api : HttpMessageHandler
    {
        public int Requests;
        public HashSet<string> FailPaths { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Requests++;
            var path = req.RequestUri!.AbsolutePath;
            if (FailPaths.Contains(path)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{}") });
            var body = req.Content is null ? new JsonObject() : JsonNode.Parse(req.Content.ReadAsStringAsync(ct).Result) as JsonObject ?? new JsonObject();
            body["id"] = 7;
            return Task.FromResult(new HttpResponseMessage(req.Method == HttpMethod.Post ? HttpStatusCode.Created : HttpStatusCode.OK)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            });
        }
    }

    private static IReadOnlyList<OperationInfo> Ops() => OpenApiLoader.Load(ShopSpec.Path);

    private static ExcelValidationException Fails(string path) =>
        Assert.Throws<ExcelValidationException>(() => ExcelScenarioReader.ReadAndCheck(path, Ops()))!;

    private static string[] Lines(ExcelValidationException e) => e.Errors.Select(x => x.Text).ToArray();

    private async Task<(int Exit, string Output, Api Api)> Run(string excel, string[]? scenarios = null, bool excelOnly = false, string? only = null, Api? api = null)
    {
        api ??= new Api();
        var sw = new StringWriter { NewLine = "\n" };
        var exit = await new TestRunner(new RunOptions
        {
            Spec = ShopSpec.Path,
            Api = "http://fake.test",
            Excel = excel,
            Source = new ExcelTestSource(),
            Seed = 1,
            Only = only,
            Scenarios = scenarios,
            ExcelOnly = excelOnly,
        }, sw, api).RunAsync();
        return (exit, sw.ToString(), api);
    }

    // --- Punkt 1 + 2: alla fel i en lista, sorterade per rad, med cell och kolumn från rubrikmatchningen ---

    [Test]
    public void All_errors_collected_sorted_by_row_with_cell_whose_column_comes_from_header()
    {
        // Anrop står i kolumn A och Scenario i kolumn B.
        var headers = new[] { "Anrop", "Scenario", "Indata", "Spara som", "Förväntad status", "Förväntat svar" };
        var path = Book(new[]
        {
            new[] { "createCustomer", "S", "name=Stina", "kund", "201" },
            new[] { "createProdukt", "S", "", "p", "created" },
            new[] { "createOrder", "S", "customerId=${kud.id}; qty=5", "", "201", "sku~[a-" },
            new[] { "getOrder", "S", "id=${order}", "", "200" },
        }, headers);
        var e = Fails(path);
        Assert.That(Lines(e), Is.EqualTo(new[]
        {
            "Tester!A3 (Anrop): okänt anrop 'createProdukt'. Menade du createProduct? Anrop i specen: createCustomer, createOrder, createProduct, getOrder.",
            "Tester!E3 (Förväntad status): förväntad status 'created' är inte en statuskod. Skriv t.ex. 201, eller 2xx/4xx för en hel grupp.",
            "Tester!C4 (Indata): 'qty' är inget fält för createOrder. Giltiga fält: customerId, productId, quantity. Se bladet Fält.",
            "Tester!C4 (Indata): Platshållaren ${kud.id}: inget svar är sparat under namnet 'kud'. Menade du kund? Sparade namn hittills i scenariot: kund, p.",
            "Tester!F4 (Förväntat svar): Mönstret efter ~ för sku går inte att läsa: en [ är inte stängd med ]. Vill du jämföra exakt, skriv = i stället för ~.",
            "Tester!C5 (Indata): Platshållaren ${order}: inget svar är sparat under namnet 'order'. Sparade namn hittills i scenariot: kund, p.",
        }));
        Assert.That(e.Message, Does.StartWith("Excel-filen t.xlsx har 6 fel, så inga tester kördes:"));
    }

    [Test]
    public async Task No_tests_run_if_excel_has_errors()
    {
        var path = Book(new[] { new[] { "S", "createProdukt" } });
        var api = new Api();
        await Assert.ThrowsAsync<ExcelValidationException>(async () => await Run(path, api: api));
        Assert.That(api.Requests, Is.EqualTo(0), "ingen request får skickas, varken genererade eller från Excel");
    }

    [Test]
    public void Syntax_and_semantic_errors_come_in_the_same_list()
    {
        var path = Book(new[]
        {
            new[] { "S", "createProdukt" },
            new[] { "S", "createCustomer", "foo" },
            new[] { "S", "", "name=A" },
        });
        var e = Fails(path);
        Assert.That(Lines(e).Select(l => l[..l.IndexOf(' ')]), Is.EqualTo(new[] { "Tester!B2", "Tester!C3", "Tester!B4" }));
        Assert.That(Lines(e)[2], Does.Contain("cellen är tom. Välj ett anrop i listan."));
    }

    [Test]
    public void Errors_get_own_action_and_exit_code_2_via_FriendlyErrors()
    {
        var e = Fails(Book(new[] { new[] { "S", "createProdukt" } }, name: "mina tester.xlsx"));
        var f = FriendlyErrors.Translate(e);
        Assert.That(f.ExitCode, Is.EqualTo(2));
        Assert.That(f.Action, Is.EqualTo("Rätta cellerna ovan i mina tester.xlsx och kör igen. Hjälp finns på bladet Hjälp i Excel-filen."));
        Assert.That(f.Action, Does.Not.Contain("--help"));
        Assert.That(string.Join("\n", f.Lines(false)), Does.StartWith("Kunde inte köra: Excel-filen mina tester.xlsx har 1 fel"));
    }

    // --- Punkt 7: okänt anrop ---

    [Test]
    public void Operation_matched_case_insensitively_and_correct_case_used()
    {
        var (scenarios, _) = ExcelScenarioReader.ReadAndCheck(Book(new[] { new[] { "S", "CREATECUSTOMER", "name=A" } }), Ops());
        Assert.That(scenarios.Single().Steps.Single().Operation, Is.EqualTo("createCustomer"));
    }

    [Test]
    public void Unknown_operation_without_close_match_lists_all_operations_when_few()
    {
        var e = Fails(Book(new[] { new[] { "S", "skapaNagot" } }));
        Assert.That(Lines(e).Single(), Is.EqualTo("Tester!B2 (Anrop): okänt anrop 'skapaNagot'. Anrop i specen: createCustomer, createOrder, createProduct, getOrder."));
    }

    [Test]
    public void Unknown_operation_with_many_operations_in_spec_does_not_list_them()
    {
        var many = Enumerable.Range(1, 20).Select(i => new OperationInfo($"op{i:00}", "GET", $"/x{i}", Array.Empty<ParamInfo>(), null, new[] { 200 })).ToList();
        var (_, issues) = ExcelValidator.Validate(new[] { new ExcelScenario("S", new() { new ExcelStep(2, "ingenting", new(), null, StatusExpectation.AnySuccess, new()) }) }, many);
        Assert.That(issues.Single().Message, Does.Not.Contain("op01"));
        Assert.That(issues.Single().Message, Does.Contain("okänt anrop 'ingenting'"));
    }

    // --- Punkt 8: status ---

    [Test]
    public void Invalid_status_names_the_spec_codes_for_the_operation()
    {
        var e = Fails(Book(new[] { new[] { "S", "createProduct", "", "", "999" }, new[] { "S", "createProduct", "", "", "created" } }));
        Assert.That(Lines(e)[0], Does.Contain("Tester!E2 (Förväntad status): förväntad status '999' är inte en statuskod.").And.Contain("Koderna för createProduct är "));
        Assert.That(Lines(e)[1], Does.Contain("'created'"));
        var codes = Ops().First(o => o.OperationId == "createProduct").ResponseCodes!;
        Assert.That(Lines(e)[0], Does.EndWith(string.Join(", ", codes) + "."));
    }

    [Test]
    public void Status_with_group_code_and_empty_status_are_valid()
    {
        Assert.DoesNotThrow(() => ExcelScenarioReader.ReadAndCheck(Book(new[] { new[] { "S", "createProduct", "", "", "2xx" }, new[] { "S2", "createProduct" } }), Ops()));
    }

    // --- Punkt 6: referenser ---

    [Test]
    public void Reference_to_name_saved_later_gives_clear_error()
    {
        var e = Fails(Book(new[]
        {
            new[] { "S", "getOrder", "id=${order.id}" },
            new[] { "S", "createOrder", "customerId=1; productId=1", "order" },
        }));
        Assert.That(Lines(e).Single(), Does.Contain("Tester!C2 (Indata)").And.Contain("sparas först på rad 3"));
    }

    [Test]
    public void Reference_to_unknown_field_suggests_closest_and_lists_fields()
    {
        var e = Fails(Book(new[]
        {
            new[] { "S", "createCustomer", "name=A", "kund" },
            new[] { "S", "getOrder", "id=${kund.idd}" },
        }));
        var line = Lines(e).Single();
        Assert.That(line, Does.StartWith("Tester!C3 (Indata): Platshållaren ${kund.idd}: svaret för 'kund' har inget fält 'idd'. Menade du id? Fält i svaret: "));
        Assert.That(line, Does.Contain("email").And.Contain("name"));
        Assert.That(line, Does.Not.Contain("{\""), "ingen JSON-dump av hela svaret");
    }

    [Test]
    public void Reference_without_field_to_an_object_suggests_id()
    {
        var e = Fails(Book(new[]
        {
            new[] { "S", "createOrder", "customerId=1; productId=1", "order" },
            new[] { "S", "getOrder", "id=${order}" },
        }));
        Assert.That(Lines(e).Single(), Does.Contain("Tester!C3 (Indata): Platshållaren ${order} är hela svaret (ett objekt)").And.Contain("t.ex. ${order.id}"));
    }

    [Test]
    public void Valid_references_accepted_also_to_auto_run_operations_and_env()
    {
        var path = Book(new[]
        {
            new[] { "S", "createCustomer", "name=A", "kund" },
            new[] { "S", "createOrder", "customerId=${kund.id}; productId=${createProduct.id}; quantity=2", "order", "201", "quantity=2" },
            new[] { "S", "getOrder", "id=${order.id}", "", "200", "customerId=${kund.id}" },
            new[] { "S", "getOrder", "id=${createOrder.id}" },
        });
        Assert.DoesNotThrow(() => ExcelScenarioReader.ReadAndCheck(path, Ops()));
    }

    [Test]
    public void References_apply_only_within_the_same_scenario()
    {
        var e = Fails(Book(new[]
        {
            new[] { "A", "createCustomer", "name=A", "kund" },
            new[] { "B", "createOrder", "customerId=${kund.id}; productId=1" },
        }));
        Assert.That(Lines(e).Single(), Does.StartWith("Tester!C3 (Indata): Platshållaren ${kund.id}: inget svar är sparat under namnet 'kund'."));
    }

    [Test]
    public void Duplicate_save_as_name_gives_warning_pointing_at_both_rows()
    {
        var (_, warnings) = ExcelScenarioReader.ReadAndCheck(Book(new[]
        {
            new[] { "S", "createCustomer", "name=A", "kund" },
            new[] { "S", "createCustomer", "name=B", "kund" },
        }), Ops());
        Assert.That(warnings.Single().Text, Does.StartWith("Tester!D3 (Spara som): namnet 'kund' sparas redan på rad 2 (Tester!D2)"));
    }

    [Test]
    public void ResponseStore_lists_names_and_fields_on_runtime_error()
    {
        var store = new ResponseStore();
        store.Save("kund", JsonNode.Parse("""{"id":5,"name":"A","a1":1,"a2":1,"a3":1,"a4":1,"a5":1,"a6":1,"a7":1,"a8":1,"a9":1}"""));
        var unknown = Assert.Throws<InvalidOperationException>(() => store.SubstituteString("${kud.id}"))!;
        Assert.That(unknown.Message, Does.Contain("Menade du kund?").And.Contain("Sparade namn hittills i scenariot: kund."));
        var field = Assert.Throws<InvalidOperationException>(() => store.SubstituteString("${kund.idd}"))!;
        Assert.That(field.Message, Does.Contain("Menade du id?").And.Contain("id, name, a1").And.Contain(", ..."));
        Assert.That(field.Message, Does.Not.Contain("\"name\":\"A\""));
        var whole = Assert.Throws<InvalidOperationException>(() => store.SubstituteString("${kund}"))!;
        Assert.That(whole.Message, Does.Contain("t.ex. ${kund.id}"));
        store.Save("n", JsonValue.Create(5));
        Assert.That(store.SubstituteString("${n}"), Is.EqualTo("5"), "ett enskilt värde får användas utan fält");
        Assert.That(store.Has("KUND"), Is.True, "namn är skiftlägesokänsliga");
    }

    // --- Punkt 4 + 5: mönster och JSON ---

    [TestCase("[a-", "en [ är inte stängd med ]")]
    [TestCase("(abc", "en ( är inte stängd med )")]
    [TestCase("abc)", "en ) saknar en ( framför sig")]
    [TestCase("abc\\", "en \\ står sist")]
    [TestCase("*abc", "ogiltig kvantifierare")]
    [TestCase("[z-a]", "intervallet i [ ] är bakvänt")]
    public void Broken_pattern_explained_in_Swedish_with_raw_text_as_technical_info(string regex, string reason)
    {
        var e = Assert.Throws<FieldListException>(() => FieldList.ParseExpected($"name~{regex}"))!;
        Assert.That(e.Message, Does.StartWith("Mönstret efter ~ för name går inte att läsa: " + reason).And.EndWith("Vill du jämföra exakt, skriv = i stället för ~."));
        Assert.That(e.Message, Does.Not.Contain("Invalid pattern").And.Not.Contain("offset"));
        Assert.That(e.Technical, Does.Contain(regex));
    }

    [Test]
    public void Row_with_broken_pattern_shows_raw_text_only_with_verbose()
    {
        var e = Fails(Book(new[] { new[] { "S", "createProduct", "", "", "", "name~[a-" } }));
        var f = FriendlyErrors.Translate(e);
        Assert.That(f.Technical, Does.Contain("Unterminated"));
        Assert.That(string.Join("\n", f.Lines(false)), Does.Not.Contain("Unterminated"));
        Assert.That(string.Join("\n", f.Lines(true)), Does.Contain("Tekniskt:").And.Contain("Unterminated"));
    }

    [Test]
    public void Broken_JSON_in_input_gives_error_but_complete_JSON_and_quoted_text_are_fine()
    {
        var e = Fails(Book(new[] { new[] { "S", "createCustomer", "name={\"a\": 1", }, new[] { "S", "createCustomer", "name=[1, 2" } }));
        Assert.That(Lines(e)[0], Does.StartWith("Tester!C2 (Indata): värdet för name ser ut som JSON men är inte komplett (saknar } eller ])."));
        Assert.That(Lines(e)[1], Does.StartWith("Tester!C3 (Indata): värdet för name ser ut som JSON"));
        Assert.That(FieldList.Parse("a={\"x\":1}; b=[1,2]").Select(kv => kv.Value!.GetValueKind().ToString()), Is.EqualTo(new[] { "Object", "Array" }));
        Assert.That(FieldList.Parse("a=\"[ej json\"")[0].Value!.GetValue<string>(), Is.EqualTo("[ej json"));
    }

    // --- Punkt 9: rubriker ---

    [Test]
    public void Unknown_header_gives_warning_and_does_not_stop()
    {
        var headers = new[] { "Scenario", "Anrop", "Indata", "Spara som", "Status", "Förväntat svar", "Kommentar" };
        var (_, warnings) = ExcelScenarioReader.ReadAndCheck(Book(new[] { new[] { "S", "createProduct" } }, headers), Ops());
        Assert.That(warnings.Select(w => w.Text), Is.EqualTo(new[]
        {
            "Tester!E1: rubriken 'Status' känns inte igen. Kolumnen läses ändå som 'Förväntad status' (standardplatsen); skriv rubriken 'Förväntad status' exakt så för att tysta varningen. Kända rubriker: Scenario, Anrop, Indata, Spara som, Förväntad status, Förväntat svar.",
            "Tester!G1: rubriken 'Kommentar' känns inte igen och ignoreras. Kända rubriker: Scenario, Anrop, Indata, Spara som, Förväntad status, Förväntat svar.",
        }));
    }

    [Test]
    public void Header_missing_from_default_position_warns_about_default_and_did_you_mean()
    {
        var headers = new[] { "Scenario", "Anrop", "Indata", "Spara som", "Forvantad status", "Förväntat svar" };
        var (_, warnings) = ExcelScenarioReader.ReadAndCheck(Book(new[] { new[] { "S", "createProduct" } }, headers), Ops());
        Assert.That(warnings.Single().Text, Does.StartWith("Tester!E1: rubriken 'Forvantad status' känns inte igen."));

        var swapped = new[] { "Scenario", "Anrop", "Indata", "Spara som", "Förväntat svar", "Hmm" };   // Förväntad status saknas helt
        var (_, w2) = ExcelScenarioReader.ReadAndCheck(Book(new[] { new[] { "S", "createProduct" } }, swapped, "b.xlsx"), Ops());
        Assert.That(w2.Select(w => w.Text), Has.Some.Contains("Tester!F1: rubriken 'Hmm' känns inte igen"));
        Assert.That(w2.Select(w => w.Text), Has.Some.StartWith("Tester!E1: rubriken 'Förväntad status' saknas på rad 1, så kolumn E läses i stället på standardplatsen."));
    }

    [Test]
    public async Task Warnings_printed_before_the_run_which_continues()
    {
        var headers = new[] { "Scenario", "Anrop", "Indata", "Spara som", "Förväntad status", "Förväntat svar", "Kommentar" };
        var (exit, output, _) = await Run(Book(new[] { new[] { "S", "createCustomer", "name=A" } }, headers), excelOnly: true);
        Assert.That(exit, Is.EqualTo(0));
        Assert.That(output, Does.StartWith("Varning: Tester!G1: rubriken 'Kommentar' känns inte igen och ignoreras."));
        Assert.That(output, Does.Contain("S, steg 1"));
    }

    // --- Punkt 11: --scenario och --excel-only ---

    [Test]
    public void Flags_parse_repeated_scenario_and_excel_only_with_alias()
    {
        var a = CommandArgs.Parse("run", new[] { "--scenario", "A B", "--scenario=C", "--excel-only" }, new[] { "scenario" }, "excel-only");
        Assert.That(a.GetAll("scenario"), Is.EqualTo(new[] { "A B", "C" }));
        Assert.That(a.Flag("excel-only"), Is.True);
        Assert.That(CommandArgs.Parse("run", new[] { "--excel-only" }, Array.Empty<string>(), "excel-only").Flag("excel-only"), Is.True);
    }

    [Test]
    public async Task Excel_only_skips_the_generated_tests()
    {
        var path = Book(new[] { new[] { "S", "createCustomer", "name=A" } });
        var all = await Run(path);
        var only = await Run(path, excelOnly: true);
        Assert.That(all.Output, Does.Contain("Genererade tester"));
        Assert.That(only.Output, Does.Not.Contain("Genererade tester"));
        Assert.That(only.Output, Does.Contain(" – 1 test mot ").And.Contain("Sammanfattning: 1 tester, 1 OK, 0 FEL"));
        Assert.That(only.Exit, Is.EqualTo(0));
    }

    [Test]
    public async Task Scenario_runs_only_selected_excel_scenarios_case_insensitively()
    {
        var path = Book(new[]
        {
            new[] { "Ett", "createCustomer", "name=A" },
            new[] { "Två", "createCustomer", "name=B" },
            new[] { "Tre", "createCustomer", "name=C" },
        });
        var r = await Run(path, scenarios: new[] { "tre", "ETT" });
        Assert.That(r.Output, Does.Contain("Ett, steg 1").And.Contain("Tre, steg 1").And.Not.Contain("Två, steg 1").And.Not.Contain("Genererade tester"));
        Assert.That(r.Output, Does.Contain("Sammanfattning: 2 tester"));
    }

    [Test]
    public async Task Unknown_scenario_gives_did_you_mean_and_list_and_requires_excel()
    {
        var path = Book(new[] { new[] { "Stamkund", "createCustomer", "name=A" } });
        var e = (await Assert.ThrowsAsync<UsageException>(async () => await Run(path, scenarios: new[] { "Stamkudn" })))!;
        Assert.That(e.Message, Is.EqualTo("--scenario: det finns inget scenario som heter 'Stamkudn' i Excel-filen. Menade du Stamkund?"));
        Assert.That(e.Tip, Does.Contain("Scenarier i filen: Stamkund"));

        var none = (await Assert.ThrowsAsync<UsageException>(async () =>
            await new TestRunner(new RunOptions { Spec = ShopSpec.Path, Api = "http://fake.test", Scenarios = new[] { "x" } }, new StringWriter { NewLine = "\n" }, new Api()).RunAsync()))!;
        Assert.That(none.Message, Does.Contain("--scenario kräver --excel"));
        var none2 = (await Assert.ThrowsAsync<UsageException>(async () =>
            await new TestRunner(new RunOptions { Spec = ShopSpec.Path, Api = "http://fake.test", ExcelOnly = true }, new StringWriter { NewLine = "\n" }, new Api()).RunAsync()))!;
        Assert.That(none2.Message, Does.Contain("--excel-only kräver --excel"));
    }

    // --- Punkt 12: sammanfattning av hoppade steg ---

    [Test]
    public async Task Skipped_step_names_which_step_failed()
    {
        var path = Book(new[]
        {
            new[] { "Flöde", "createCustomer", "name=A", "kund", "201" },
            new[] { "Flöde", "createProduct", "", "produkt", "201" },
            new[] { "Flöde", "createOrder", "customerId=${kund.id}; productId=${produkt.id}; quantity=1", "order", "201" },
            new[] { "Flöde", "getOrder", "id=${order.id}", "", "200" },
        });
        var api = new Api();
        api.FailPaths.Add("/products");
        var r = await Run(path, excelOnly: true, api: api);
        var summary = ops().First(o => o.OperationId == "createProduct").Summary ?? "createProduct";
        Assert.That(r.Output, Does.Contain($"HOPPAD (steg 2 '{summary}' i scenariot fick FEL)"));
        Assert.That(Regex.Matches(r.Output, "HOPPAD \\(steg 2").Count, Is.EqualTo(2), "båda de följande stegen");
        Assert.That(r.Output, Does.Contain("Sammanfattning: 4 tester, 1 OK, 1 FEL, 2 HOPPAD"));
        Assert.That(r.Exit, Is.EqualTo(1));

        static IReadOnlyList<OperationInfo> ops() => Ops();
    }

    // --- Suggest ---

    [Test]
    public void Suggest_lists_at_most_ten_names()
    {
        Assert.That(Suggest.List(Enumerable.Range(1, 12).Select(i => "f" + i)), Is.EqualTo("f1, f2, f3, f4, f5, f6, f7, f8, f9, f10, ..."));
        Assert.That(Suggest.List(new[] { "a", "b" }), Is.EqualTo("a, b"));
        Assert.That(Suggest.DidYouMean("lokla", new[] { "lokal" }), Is.EqualTo(" Menade du lokal?"));
        Assert.That(Suggest.DidYouMean("zzzzz", new[] { "lokal" }), Is.EqualTo(""));
    }
}
