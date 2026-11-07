using TestGen.Scenarios;
using ClosedXML.Excel;
using Microsoft.OpenApi.Models;

namespace TestGen.Core.Run;

/// <summary>Skapar arbetsboken testare fyller i: blad Tester, Hjälp, Miljöer och Fält (samt dolt Listor).</summary>
public static class ExcelTemplate
{
    /// <summary>Texten i cellen till höger om rubrikerna på bladet Tester. Är ingen kolumnrubrik, så läsaren varnar inte för den.</summary>
    public const string HelpLinkText = "Hjälp finns på bladet Hjälp";

    public const string HelpSheet = "Hjälp";
    public const string FieldsSheet = "Fält";
    private const int MaxRows = 500;
    private static readonly int Columns = ExcelScenarioReader.Headers.Length;

    public static void Create(IReadOnlyList<OperationInfo> ops, string path)
    {
        using var wb = new XLWorkbook();
        var testsSheet = wb.AddWorksheet(ExcelScenarioReader.SheetName);
        var help = wb.AddWorksheet(HelpSheet);
        var envs = wb.AddWorksheet(EnvironmentSheet.SheetName);
        var fields = wb.AddWorksheet(FieldsSheet);
        var lists = wb.AddWorksheet("Listor");

        BuildLists(lists, ops);
        lists.Visibility = XLWorksheetVisibility.Hidden;

        BuildTestsSheet(testsSheet, lists, ops.Count);
        BuildEnvironments(envs);
        BuildHelp(help);
        BuildFields(fields, ops);

        foreach (var ws in new[] { testsSheet, help, envs, fields }) PrintSetup(ws, repeatHeader: ws != help);
        wb.Properties.Title = "Testfall för API-test (testgen)";
        wb.Properties.Subject = "Mall där du skriver testfall för ett API: anrop, indata och förväntat svar";
        wb.Properties.Keywords = "testgen; API; test";

        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        wb.SaveAs(path);
    }

    /// <summary>
    /// Kolumn A: operationId (en rad per anrop). Kolumn B: antal statuskoder på raden. Kolumn C...: koderna.
    /// Raden efter sista anropet (utan namn) har bara 2xx och 4xx och används när Anrop är tomt/okänt.
    /// </summary>
    private static void BuildLists(IXLWorksheet lists, IReadOnlyList<OperationInfo> ops)
    {
        void Row(int row, string? id, IReadOnlyList<string> codes)
        {
            if (id is not null) lists.Cell(row, 1).SetValue(id);
            lists.Cell(row, 2).Value = codes.Count;
            for (var c = 0; c < codes.Count; c++) lists.Cell(row, c + 3).SetValue(codes[c]);
        }
        for (var i = 0; i < ops.Count; i++) Row(i + 1, ops[i].OperationId, StatusCodes(ops[i]));
        Row(ops.Count + 1, null, StatusCodes(null));
    }

    /// <summary>Specens koder (heltal stigande, sedan t.ex. 5xx), därefter 2xx och 4xx sist utan dubbletter.</summary>
    public static IReadOnlyList<string> StatusCodes(OperationInfo? op)
    {
        var tail = new[] { "2xx", "4xx" };
        var codes = (op?.ResponseCodes ?? Array.Empty<string>())
            .Select(k => k.Trim().ToLowerInvariant()).Where(k => k.Length > 0 && k != "default" && !tail.Contains(k)).Distinct()
            .OrderBy(k => int.TryParse(k, out _) ? 0 : 1).ThenBy(k => k, StringComparer.Ordinal).ToList();
        codes.AddRange(tail);
        return codes;
    }

    // ---- Texter som visas som inmatningsmeddelande och som kommentar på rubrikcellen ----

    /// <summary>Hjälptext per kolumn på bladet Tester (samma ordning som <see cref="ExcelScenarioReader.Headers"/>).</summary>
    public static readonly (string Title, string Text)[] TesterPrompts =
    {
        ("Scenario", "Namn på flödet. Tomt = samma som raden ovanför."),
        ("Anrop", "Välj i listan. Se bladet Fält för vilka fält varje anrop har."),
        ("Indata", "t.ex. name=Anna; quantity=5. Tomt = verktyget hittar på giltiga värden. Se bladet Fält."),
        ("Spara som", "Ett namn, t.ex. kund. Senare rader använder ${kund.id}."),
        ("Förväntad status", "Välj i listan eller skriv en kod, t.ex. 201. Tomt = valfri 2xx (lyckat)."),
        ("Förväntat svar", "t.ex. name=Anna (exakt) eller ordernr~^ORD- (mönster). Se Hjälp."),
    };

    public static readonly (string Title, string Text)[] EnvironmentPrompts =
    {
        ("Miljö", "Namnet du skriver efter --env, t.ex. lokal eller test. Flera rader med samma namn hör ihop."),
        ("Inställning", "Välj: api (adress), header (extra rubrik), token (inloggning) eller timeout (sekunder)."),
        ("Värde", "api: adressen. header: Namn: värde. Skriv aldrig en riktig token här, använd ${env:NAMN}."),
    };

    /// <summary>Vanliga mönster för <c>fält~mönster</c>: (mönster, vad det betyder, ett värde som passar). "(tomt)" = tom text.</summary>
    public static readonly (string Pattern, string Meaning, string Example)[] CommonPatterns =
    {
        ("^ORD-", "Börjar med ORD-", "ORD-000123"),
        ("\\d+", "Innehåller minst en siffra", "Rum 12"),
        ("^\\d+$", "Bara siffror", "12345"),
        ("^\\d{6}$", "Exakt sex siffror", "123456"),
        ("@", "Innehåller @", "stina@example.se"),
        ("^[A-Z]{3}-[0-9]{4}$", "Tre versaler, bindestreck, fyra siffror", "ABC-1234"),
        ("(?i)^stina", "Börjar med stina, oavsett stora/små bokstäver", "Stina Stamkund"),
        ("^(active|inactive)$", "Exakt active eller inactive", "active"),
        ("^$", "Tomt", "(tomt)"),
    };

    private static void BuildTestsSheet(IXLWorksheet ws, IXLWorksheet lists, int opCount)
    {
        for (var i = 0; i < ExcelScenarioReader.Headers.Length; i++)
        {
            ws.Cell(1, i + 1).Value = ExcelScenarioReader.Headers[i];
            Comment(ws.Cell(1, i + 1), TesterPrompts[i].Text);
        }
        Header(ws.Range(1, 1, 1, Columns));

        // Hänvisning till Hjälp, synlig till höger om rubrikerna.
        var link = ws.Cell(1, Columns + 2);
        link.SetValue(HelpLinkText);
        link.SetHyperlink(new XLHyperlink($"'{HelpSheet}'!A1", "Gå till bladet Hjälp"));
        link.Style.Font.Bold = true;
        link.Style.Font.FontColor = XLColor.FromHtml("#1F3864");
        link.Style.Font.Underline = XLFontUnderlineValues.Single;

        // Exempel: Stamkund-flödet (motsvarar tests/Api.Tests/scenarios/manual/stamkund.yaml) och ett negativt fall.
        // email~@ och createdAt~... visar fält~mönster mot exempel-API:ts svar.
        var rows = new[]
        {
            new[] { "Stamkund", "createCustomer", "name=Stina Stamkund", "kund", "201", "name=Stina Stamkund; email~@" },
            new[] { "Stamkund", "createProduct", "", "produkt", "201", "" },
            new[] { "Stamkund", "createOrder", "customerId=${kund.id}; productId=${produkt.id}; quantity=5", "order", "201", "quantity=5" },
            new[] { "Stamkund", "getOrder", "id=${order.id}", "", "200", "quantity=5; createdAt~^\\d{4}-\\d{2}-\\d{2}T" },
            new[] { "Orimligt antal", "createOrder", "quantity=100000", "", "400", "" },
        };
        for (var r = 0; r < rows.Length; r++)
            for (var c = 0; c < Columns; c++)
                if (rows[r][c].Length > 0) ws.Cell(r + 2, c + 1).SetValue(rows[r][c]);

        // Text i alla datacellerna så att Excel inte "hjälper till" (t.ex. gör om 201 eller datum).
        ws.Range(2, 1, MaxRows, Columns).Style.NumberFormat.Format = "@";
        ws.Range(2, 1, MaxRows, Columns).Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
        ws.Range(2, 3, MaxRows, 3).Style.Alignment.WrapText = true;
        ws.Range(2, Columns, MaxRows, Columns).Style.Alignment.WrapText = true;

        // Inmatningsmeddelande på alla kolumner. Kolumnerna utan lista får en "valfritt värde"-regel som bara bär meddelandet.
        foreach (var col in new[] { 1, 3, 4, 6 }) Prompt(ws.Range(2, col, MaxRows, col).CreateDataValidation(), TesterPrompts[col - 1]);

        var dv = ws.Range(2, 2, MaxRows, 2).CreateDataValidation();
        dv.List(lists.Range(1, 1, Math.Max(opCount, 1), 1), true);
        dv.ErrorTitle = "Okänt anrop";
        dv.ErrorMessage = "Välj ett anrop i listan (operationId från specen).";
        dv.ShowErrorMessage = true;
        Prompt(dv, TesterPrompts[1]);

        // Förväntad status: koderna för anropet på samma rad (se BuildLists). MATCH ger radnumret i Listor,
        // OFFSET/INDEX hämtar koderna – operationId används aldrig som namn. Okänt/tomt anrop ger sista raden (2xx, 4xx).
        var last = opCount + 1;
        var m = $"MATCH($B2,Listor!$A$1:$A${Math.Max(opCount, 1)},0)";
        var statusDv = ws.Range(2, 5, MaxRows, 5).CreateDataValidation();
        statusDv.AllowedValues = XLAllowedValues.List;
        statusDv.Value = $"OFFSET(Listor!$C$1,IFERROR({m},{last})-1,0,1,INDEX(Listor!$B$1:$B${last},IFERROR({m},{last})))";
        statusDv.IgnoreBlanks = true;
        statusDv.InCellDropdown = true;
        statusDv.ShowErrorMessage = true;
        statusDv.ErrorStyle = XLErrorStyle.Warning;
        statusDv.ErrorTitle = "Okänd statuskod";
        statusDv.ErrorMessage = "Statuskoden finns inte i specen för det här anropet. Vill du använda den ändå?";
        Prompt(statusDv, TesterPrompts[4]);

        foreach (var (w, i) in new[] { 22, 22, 55, 14, 18, 44, 3, 30 }.Select((w, i) => (w, i + 1))) ws.Column(i).Width = w;
        ws.SheetView.FreezeRows(1);
        ws.Range(1, 1, MaxRows, Columns).SetAutoFilter();
    }

    private static void BuildEnvironments(IXLWorksheet ws)
    {
        for (var i = 0; i < EnvironmentSheet.Headers.Length; i++)
        {
            ws.Cell(1, i + 1).Value = EnvironmentSheet.Headers[i];
            Comment(ws.Cell(1, i + 1), EnvironmentPrompts[i].Text);
        }
        Header(ws.Range(1, 1, 1, EnvironmentSheet.Headers.Length));
        ws.Range(2, 1, MaxRows, 3).Style.NumberFormat.Format = "@";
        ws.Cell(2, 1).SetValue("lokal");
        ws.Cell(2, 2).SetValue("api");
        ws.Cell(2, 3).SetValue("http://localhost:5080");

        foreach (var col in new[] { 1, 3 }) Prompt(ws.Range(2, col, MaxRows, col).CreateDataValidation(), EnvironmentPrompts[col - 1]);
        var dv = ws.Range(2, 2, MaxRows, 2).CreateDataValidation();
        dv.List(string.Join(",", EnvironmentSheet.Settings), true);
        dv.ErrorTitle = "Okänd inställning";
        dv.ErrorMessage = "Välj api, header, token eller timeout.";
        dv.ShowErrorMessage = true;
        Prompt(dv, EnvironmentPrompts[1]);

        // Exempel till höger (kolumn E-G). Verktyget läser bara kolumn A-C, så exemplen påverkar inget.
        // Kopiera en rad till tabellen till vänster för att använda den.
        var note = ws.Cell(1, 5);
        note.SetValue("Exempel – kopiera en rad till tabellen till vänster");
        note.Style.Font.Bold = true;
        note.Style.Font.FontSize = 12;
        string[][] examples =
        {
            new[] { "Miljö", "Inställning", "Värde" },
            new[] { "lokal", "token", "${env:API_TOKEN}" },
            new[] { "lokal", "header", "X-Tenant: acme" },
            new[] { "lokal", "timeout", "60" },
        };
        for (var r = 0; r < examples.Length; r++)
            for (var c = 0; c < 3; c++) ws.Cell(r + 2, c + 5).SetValue(examples[r][c]);
        Header(ws.Range(2, 5, 2, 7));
        ws.Range(3, 5, 5, 7).Style.Fill.BackgroundColor = XLColor.FromHtml("#F2F2F2");
        var warn = ws.Cell(7, 5);
        warn.SetValue("Skriv aldrig en riktig token här – använd ${env:NAMN}.");
        warn.Style.Font.Bold = true;
        warn.Style.Font.FontColor = XLColor.FromHtml("#C00000");
        ws.Cell(8, 5).SetValue("${env:API_TOKEN} hämtar värdet från miljövariabeln API_TOKEN på din dator.");
        ws.Cell(9, 5).SetValue("Mer hjälp finns på bladet Hjälp.");

        foreach (var (w, i) in new[] { 18, 16, 50, 3, 18, 16, 28 }.Select((w, i) => (w, i + 1))) ws.Column(i).Width = w;
        ws.SheetView.FreezeRows(1);
    }

    private static void BuildHelp(IXLWorksheet ws)
    {
        var row = 1;
        var titleRows = new List<int>();
        void Title(string text)
        {
            titleRows.Add(row);
            ws.Cell(row, 1).SetValue(text);
            var band = ws.Range(row, 1, row, 3);
            band.Style.Font.Bold = true;
            band.Style.Font.FontSize = row == 1 ? 18 : 14;
            band.Style.Font.FontColor = XLColor.FromHtml("#1F3864");
            band.Style.Fill.BackgroundColor = XLColor.FromHtml("#DDEBF7");
            row++;
        }
        void Table(string[] heads, params string[][] data)
        {
            for (var i = 0; i < heads.Length; i++) if (heads[i].Length > 0) ws.Cell(row, i + 1).SetValue(heads[i]);
            Header(ws.Range(row, 1, row, 3));
            row++;
            foreach (var d in data)
            {
                for (var i = 0; i < d.Length; i++) if (d[i].Length > 0) ws.Cell(row, i + 1).SetValue(d[i]);
                ws.Cell(row, 1).Style.Font.Bold = true;
                row++;
            }
            row++;   // tom rad efter tabellen
        }

        Title("Hjälp");
        row++;
        Title("Gör så här första gången");
        Table(new[] { "Steg", "Gör så här", "Exempel" },
            new[] { "1. Gå till bladet Tester", "Varje rad är ett anrop mot systemet du testar. Raderna med exempel kan du skriva över eller ta bort. Lägg nya rader under.", "Se rad 2–6 på bladet Tester" },
            new[] { "2. Välj anrop", "Klicka i kolumnen Anrop och välj i listan. På bladet Fält ser du vilka fält varje anrop har.", "createOrder" },
            new[] { "3. Skriv vad du vill kontrollera", "Skriv Indata (det du skickar), Förväntad status och Förväntat svar. Lämnar du Indata tomt hittar verktyget på giltiga värden.", "quantity=5 | 201 | quantity=5" },
            new[] { "4. Kör testerna", "Spara filen och kör kommandot. Filen får vara öppen i Excel. Resultatet visas som OK eller FEL per rad.", "testgen run --spec <spec> --api <adress> --excel tester.xlsx" });

        Title("Kolumnerna på bladet Tester");
        Table(new[] { "Kolumn", "Vad du skriver", "Exempel" },
            new[] { "Scenario", "Ett namn på flödet. Samma namn på flera rader = flera steg i samma flöde. Tomt namn = samma som raden ovanför.", "Stamkund" },
            new[] { "Anrop", "Välj i listan. Namnen kommer från specen (beskrivningen av systemet).", "createCustomer" },
            new[] { "Indata", "De fält du vill bestämma, som fält=värde, flera med ; emellan. Fält du lämnar ut fylls i med giltiga värden. ${namn.fält} hämtar ett värde från ett tidigare sparat svar.", "name=Anna; quantity=5" },
            new[] { "Spara som", "Ett namn på svaret, så att senare rader kan använda det. Tomt = anropets eget namn.", "kund" },
            new[] { "Förväntad status", "Svarskoden du väntar dig. Välj i listan eller skriv själv. Tomt = valfri 2xx (lyckat). Du kan skriva 4xx för \"något fel i anropet\".", "201" },
            new[] { "Förväntat svar", "Fält du vill kontrollera i svaret. = betyder exakt värde, ~ betyder att värdet ska se ut på ett visst sätt (mönster). Övriga fält i svaret ignoreras.", "name=Anna; ordernr~^ORD-" });

        Title("Ordlista");
        Table(new[] { "Ord", "Betyder", "" },
            new[] { "200", "OK – det gick bra." },
            new[] { "201", "Skapad – något nytt har lagts till." },
            new[] { "400", "Felaktiga uppgifter – systemet säger nej till det du skickade." },
            new[] { "401", "Inte inloggad." },
            new[] { "403", "Saknar behörighet." },
            new[] { "404", "Finns inte." },
            new[] { "409", "Finns redan." },
            new[] { "500", "Fel i systemet." },
            new[] { "anrop", "En fråga eller ett kommando du skickar till systemet, t.ex. \"skapa kund\"." },
            new[] { "svar", "Det systemet skickar tillbaka efter ett anrop." },
            new[] { "indata", "Det du skickar med i anropet." },
            new[] { "mönster (regex)", "Ett sätt att beskriva hur en text ska se ut, i stället för att skriva hela texten. Se tabellen nedan." });

        Title("Förväntat svar: = eller ~");
        Table(new[] { "Tecken", "Betyder", "Exempel" },
            new[] { "=", "Exakt värde. Svaret ska vara precis det du skriver.", "status=active" },
            new[] { "~", "Ska se ut så här (mönster). Det räcker att mönstret finns någonstans i texten. Använd ^ för början och $ för slutet om hela texten ska stämma.", "ordernr~^ORD-" },
            new[] { "; inom citattecken", "Flera kontroller skiljs åt med ;. Ett mönster som själv innehåller ; måste stå inom citattecken.", "kod~\"^A;B$\"" });

        Title("Vanliga mönster för fält~mönster");
        Table(new[] { "Mönster", "Betyder", "Exempel som passar" },
            CommonPatterns.Select(p => new[] { p.Pattern, p.Meaning, p.Example }).ToArray());

        Title("Bra att veta");
        Table(new[] { "Ämne", "Så här är det", "" },
            new[] { "Ordning", "Raderna körs uppifrån och ned. Sortera därför inte tabellen – då hamnar stegen i fel ordning." },
            new[] { "Samma scenarionamn", "Rader med samma scenarionamn slås ihop till ett flöde, även om de står på olika ställen." },
            new[] { "Tomt scenarionamn", "Tomt scenarionamn betyder samma scenario som raden ovanför." },
            new[] { "Rad 500", "Mallen är förberedd (rullistor och format) till och med rad 500." },
            new[] { "Rullistor", "Rullistorna fungerar i Microsoft Excel på dator. I Google Sheets, Numbers och Excel på webben kan statuslistan saknas – skriv då koden själv." },
            new[] { "Hemligheter", "Skriv aldrig en riktig token eller ett lösenord i bladet Miljöer. Skriv ${env:NAMN} så hämtas värdet från din dator." },
            new[] { "Miljöer", "På bladet Miljöer samlar du adress (api), extra rubriker (header), inloggning (token) och väntetid (timeout, sekunder) per miljö. Kör med --env lokal så behövs inte --api. Flaggor på kommandoraden gäller före bladet." });

        ws.Column(1).Width = 26;
        ws.Column(2).Width = 62;
        ws.Column(3).Width = 42;
        var used = ws.Range(1, 1, row, 3);
        used.Style.Alignment.WrapText = true;
        used.Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
        foreach (var t in titleRows) ws.Range(t, 1, t, 3).Style.Alignment.WrapText = false;   // rubriker bryts inte
    }

    private static void BuildFields(IXLWorksheet ws, IReadOnlyList<OperationInfo> ops)
    {
        string[] heads = { "Anrop", "Fält", "Typ", "Obligatoriskt", "Villkor", "Skriv så här i Indata" };
        for (var i = 0; i < heads.Length; i++) ws.Cell(1, i + 1).Value = heads[i];
        Header(ws.Range(1, 1, 1, heads.Length));

        var r = 2;
        foreach (var op in ops)
        {
            var start = r;
            void Add(string name, OpenApiSchema schema, bool required)
            {
                ws.Cell(r, 1).Value = op.OperationId;
                ws.Cell(r, 2).Value = name;
                ws.Cell(r, 3).Value = TypeText(schema);
                ws.Cell(r, 4).Value = required ? "Ja" : "Nej";
                ws.Cell(r, 5).Value = Constraints(schema);
                ws.Cell(r, 6).SetValue(InputExample(name, schema));
                r++;
            }
            foreach (var p in op.Parameters.OrderBy(p => p.Name, StringComparer.Ordinal)) Add(p.Name, p.Schema, p.Required);
            if (op.BodySchema is not null && SchemaUtil.IsObject(op.BodySchema))
            {
                var (props, required) = SchemaUtil.Flatten(op.BodySchema);
                foreach (var (name, schema) in props) Add(name, schema, required.Contains(name));
            }
            if (r == start) { ws.Cell(r, 1).Value = op.OperationId; ws.Cell(r, 2).Value = "(inga fält)"; r++; }
        }
        ws.Range(1, 1, Math.Max(r - 1, 1), heads.Length).SetAutoFilter();
        ws.Range(2, 5, Math.Max(r - 1, 2), 6).Style.Alignment.WrapText = true;
        ws.Range(2, 1, Math.Max(r - 1, 2), heads.Length).Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
        ws.SheetView.FreezeRows(1);
        foreach (var (w, i) in new[] { 22, 20, 18, 15, 50, 36 }.Select((w, i) => (w, i + 1))) ws.Column(i).Width = w;
    }

    private static void Header(IXLRange range)
    {
        range.Style.Font.Bold = true;
        range.Style.Font.FontColor = XLColor.White;
        range.Style.Fill.BackgroundColor = XLColor.FromHtml("#2F5496");
    }

    /// <summary>Rubrikcellens kommentar (samma text som inmatningsmeddelandet).</summary>
    private static void Comment(IXLCell cell, string text)
    {
        var c = cell.CreateComment();
        c.AddText(text);
    }

    /// <summary>Inmatningsmeddelande (titel högst 32 tecken, text högst 255 – Excels gränser).</summary>
    private static void Prompt(IXLDataValidation dv, (string Title, string Text) p)
    {
        if (p.Title.Length > 32 || p.Text.Length > 255) throw new InvalidOperationException($"För långt inmatningsmeddelande: {p.Title}");
        dv.ShowInputMessage = true;
        dv.InputTitle = p.Title;
        dv.InputMessage = p.Text;
    }

    /// <summary>A4, liggande, en sida bred; rubrikraden upprepas på varje sida om <paramref name="repeatHeader"/>.</summary>
    private static void PrintSetup(IXLWorksheet ws, bool repeatHeader)
    {
        ws.PageSetup.PaperSize = XLPaperSize.A4Paper;
        ws.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        ws.PageSetup.FitToPages(1, 0);
        if (repeatHeader) ws.PageSetup.SetRowsToRepeatAtTop(1, 1);
    }

    /// <summary>Vardagliga typnamn (heltal, decimaltal, text, ja/nej, datum ...).</summary>
    public static string TypeText(OpenApiSchema s)
    {
        var t = SchemaUtil.TypeOf(s) ?? "string";
        return t switch
        {
            "integer" => "heltal",
            "number" => "decimaltal",
            "boolean" => "ja/nej",
            "array" => "lista",
            "object" => "objekt",
            _ => s.Format switch
            {
                "date" => "datum",
                "date-time" => "datum och tid",
                "email" => "e-postadress",
                "uuid" => "unik kod (uuid)",
                "uri" or "url" => "webbadress",
                _ => "text",
            },
        };
    }

    private static string Constraints(OpenApiSchema s)
    {
        var parts = new List<string>();
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (s.Enum.Count > 0) parts.Add("ett av: " + string.Join(", ", s.Enum.Select(e => ResponseStore.ToText(SchemaUtil.ToNode(e)))));
        if (s.Minimum is { } mn) parts.Add($"{(s.ExclusiveMinimum == true ? "större än" : "minst")} {mn.ToString(inv)}");
        if (s.Maximum is { } mx) parts.Add($"{(s.ExclusiveMaximum == true ? "mindre än" : "högst")} {mx.ToString(inv)}");
        if (s.MinLength is { } ml) parts.Add($"minst {ml} tecken");
        if (s.MaxLength is { } xl) parts.Add($"högst {xl} tecken");
        if (!string.IsNullOrEmpty(s.Pattern)) parts.Add($"ska följa mönstret {s.Pattern}");
        return string.Join("; ", parts);
    }

    /// <summary>
    /// Färdig text att skriva i Indata, t.ex. <c>quantity=5</c>. Värdet kommer från specens example, första enum-värdet eller
    /// pattern, annars ett enkelt värde som passar typen och gränserna (min/max). Tomt för lista/objekt.
    /// </summary>
    public static string InputExample(string name, OpenApiSchema s)
    {
        var value = ExampleValue(s);
        return value is null ? "" : $"{name}={value}";
    }

    private static string? ExampleValue(OpenApiSchema s)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var t = SchemaUtil.TypeOf(s) ?? "string";
        if (t is "array" or "object") return null;
        if (s.Example is not null) return Quote(ResponseStore.ToText(SchemaUtil.ToNode(s.Example)), t);
        if (s.Enum.Count > 0) return Quote(ResponseStore.ToText(SchemaUtil.ToNode(s.Enum[0])), t);
        switch (t)
        {
            case "integer": return Math.Round(Clamp(5, s)).ToString("0", inv);
            case "number": return Clamp(10.5, s).ToString("0.##", inv);
            case "boolean": return "true";
        }
        if (!string.IsNullOrEmpty(s.Pattern))
        {
            try
            {
                var g = PatternGenerator.Generate(s.Pattern, new Bogus.Randomizer(1));
                if (System.Text.RegularExpressions.Regex.IsMatch(g, s.Pattern)) return Quote(g, t);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or IndexOutOfRangeException) { /* ingen färdig text */ }
            return null;
        }
        return s.Format switch
        {
            "date" => "2025-01-31",
            "date-time" => "2025-01-31T12:00:00Z",
            "email" => "anna@example.se",
            "uuid" => "123e4567-e89b-12d3-a456-426614174000",
            "uri" or "url" => "https://example.se",
            _ => "Exempel",
        };
    }

    private static double Clamp(double v, OpenApiSchema s)
    {
        if (s.Minimum is { } mn) { var m = (double)mn + (s.ExclusiveMinimum == true ? 1 : 0); if (v < m) v = m; }
        if (s.Maximum is { } mx) { var m = (double)mx - (s.ExclusiveMaximum == true ? 1 : 0); if (v > m) v = m; }
        return v;
    }

    /// <summary>Text som annars skulle tolkas som tal/true/null, eller innehåller ; eller ", skrivs inom citattecken.</summary>
    private static string Quote(string text, string type)
    {
        if (type != "string") return text;
        var plain = !text.Contains(';') && !text.Contains('"') &&
                    FieldList.ParseValue(text) is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var str) && str == text.Trim();
        return plain ? text : "\"" + text.Replace("\"", "\"\"") + "\"";
    }
}
