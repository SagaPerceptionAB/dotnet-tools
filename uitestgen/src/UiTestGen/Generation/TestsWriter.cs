using UiTestGen.Model;

namespace UiTestGen.Generation;

/// <summary>Skriver testklassen för en vy. Varje slags test är en egen metod, så att nya slag läggs till på ett ställe.</summary>
internal static class TestsWriter
{
    /// <summary>Exempelvärdet för en parameter i adressen, som C#-kod. Värdet kommer från <see cref="RouteExamples"/>, så att sonderingen öppnar samma adress.</summary>
    private static string Example(RouteParameter p)
    {
        var value = RouteExamples.Value(p);
        return RouteTemplate.KindOf(p) switch
        {
            ParameterKind.Int or ParameterKind.Long or ParameterKind.Bool => value,
            ParameterKind.Guid => $"new Guid({CSharp.Literal(value)})",
            _ => CSharp.Literal(value),
        };
    }

    /// <summary>Anropet som bygger vyns adress med exempelvärden: <c>ProductPage.Path(1)</c>.</summary>
    private static string PathCall(View view) =>
        $"{PageObjectWriter.ClassName(view)}.Path({string.Join(", ", view.Parameters.Select(Example))})";

    public static string ClassName(View view) => CSharp.Identifier(view.Id, "View") + "Tests";

    public static string Write(View view, string ns, GenerateOptions options, ViewCatalog catalog)
    {
        var w = new CodeWriter();
        w.Line(CSharp.Header);
        w.Line();
        w.Line("using System.Text.RegularExpressions;");
        w.Line("using Microsoft.Playwright;");
        w.Line("using NUnit.Framework;");
        w.Line($"using {ns}.Pages;");
        w.Line($"using {ns}.Support;");
        w.Line();
        w.Line($"namespace {ns}.Tests;");
        w.Line();
        w.Line("[Parallelizable(ParallelScope.Self)]");
        using (w.Block($"public partial class {ClassName(view)} : UiTestBase"))
        {
            // Här läggs fler slags test till, en metod per slag.
            SmokeTest(w, view);
            FormTests(w, view, options);
            NavigationTests(w, view, catalog);
            VisualTest(w, view, options);
        }
        return w.ToString();
    }

    private static void SmokeTest(CodeWriter w, View view)
    {
        w.Line("[Test]");
        w.Line("[Category(\"Smoke\")]");
        if (view.RequiresAuth)
        {
            w.Line("[Category(\"Inloggning\")]");
            using (w.Block("public Task Opens_without_errors()"))
            {
                w.Line("Assert.Ignore(\"Vyn kräver inloggning, som inte stöds än.\");");
                w.Line("return Task.CompletedTask;");
            }
            return;
        }
        var page = PageObjectWriter.ClassName(view);
        using (w.Block("public async Task Opens_without_errors()"))
        {
            w.Line($"await GotoAsync({PathCall(view)});");
            if (view.Title is not null || view.Heading is not null) w.Line($"var view = new {page}(Page);");
            if (view.Title is { } title) w.Line($"await Expect(Page).ToHaveTitleAsync({CSharp.Literal(title)});");
            if (view.Heading is not null) w.Line("await Expect(view.Heading).ToBeVisibleAsync();");
            w.Line("await AssertNoErrorsAsync();");
        }
    }

    // --- Formulärtester ---

    /// <summary>Ett test som skickar formuläret med giltiga värden, och ett per regel som går att bryta.</summary>
    private static void FormTests(CodeWriter w, View view, GenerateOptions options)
    {
        if (view.Forms.Count == 0) return;
        if (view.RequiresAuth)
        {
            w.Line();
            w.Line("// Inga formulärtester: vyn kräver inloggning, som inte stöds än.");
            return;
        }
        var page = PageObjectWriter.ClassName(view);
        var path = PathCall(view);
        var names = new HashSet<string>(StringComparer.Ordinal) { "Opens_without_errors" };
        foreach (var members in PageObjectWriter.Members(view))
        {
            var plan = FormPlan.Build(view, members.Form, options);
            var formId = CSharp.Identifier(members.Form.Id, "Form");
            if (plan.Warning is not null)
            {
                w.Line();
                w.Line($"// Varning: {CSharp.Comment(plan.Warning)} Formulärtesterna för {CSharp.Comment(members.Form.Id)} hoppas över.");
                continue;
            }
            var messages = members.Form.Fields.SelectMany(f => f.Rules).Select(r => r.Message)
                .Where(m => !string.IsNullOrEmpty(m)).Select(m => m!).Distinct(StringComparer.Ordinal).ToList();

            using (FormTest(w, options, Names.Unique(names, formId + "_sends_valid_values", "_"), mutates: true))
            {
                Open(w, path, page);
                Fill(w, members, plan, null);
                w.Line($"await SubmitAsync(view.{members.Submit});");
                if (messages.Count > 0) w.Line($"await ExpectNoMessagesAsync({string.Join(", ", messages.Select(CSharp.Literal))});");
                w.Line("await ExpectNoAlertsAsync();");
                w.Line("await AssertNoErrorsAsync();");
            }
            foreach (var c in plan.Cases)
            {
                var name = Names.Unique(names, $"{formId}_{CSharp.Identifier(c.Field.Name, "Field")}_{c.Rule.Kind}_shows_error", "_");
                var locator = members.Fields[c.Index];
                using (FormTest(w, options, name, mutates: false))
                {
                    Open(w, path, page);
                    if (c.NeedsLongInput) w.Line($"await AllowTooLongAsync(view.{locator});");
                    Fill(w, members, plan, c);
                    if (c.Checker == RuleChecker.App)
                    {
                        // Webbläsarens egen kontroll stängs av, så att det är appens felmeddelande som prövas.
                        w.Line($"await SubmitAsync(view.{members.Submit}, browserValidation: false);");
                        w.Line($"await ExpectMessageAsync({CSharp.Literal(c.Rule.Message!)});");
                    }
                    else
                    {
                        w.Line($"await SubmitAsync(view.{members.Submit});");
                        w.Line($"await ExpectInvalidAsync(view.{locator});");
                    }
                }
            }
        }
    }

    // --- Navigeringstester ---

    /// <summary>
    /// Ett test per länk till en känd vy (<c>Link_to_{mål}</c>). Ett enda test för alla länkar vore kortare, men då stoppar den första
    /// trasiga länken resten och felet säger inte vilken länk det gäller. Med ett test per länk pekar testnamnet ut länken,
    /// de andra länkarna prövas ändå, och varje test börjar på startvyn utan att behöva gå tillbaka.
    /// </summary>
    private static void NavigationTests(CodeWriter w, View view, ViewCatalog catalog)
    {
        if (view.Links.Count == 0) return;
        if (view.RequiresAuth)
        {
            w.Line();
            w.Line("// Inga navigeringstester: vyn kräver inloggning, som inte stöds än.");
            return;
        }
        var path = PathCall(view);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var done = new HashSet<(string, string?)>();
        var skipped = new List<string>();
        foreach (var link in view.Links)
        {
            if (!done.Add((link.Href, link.Text))) continue;
            var target = LinkTargets.Resolve(catalog, link.Href);
            if (target is null) { skipped.Add($"{link.Href} (ingen känd vy)"); continue; }
            if (target.RequiresAuth) { skipped.Add($"{link.Href} (målet kräver inloggning, som inte stöds än)"); continue; }

            w.Line();
            w.Line("[Test]");
            w.Line("[Category(\"Navigering\")]");
            using (w.Block($"public async Task {Names.Unique(names, "Link_to_" + CSharp.Identifier(target.Id, "View"), "_")}()"))
            {
                w.Line($"await GotoAsync({path});");
                w.Line($"await {LinkTargets.Locator(catalog, link, target)}.ClickAsync();");
                w.Line("await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);");
                w.Line($"await Expect(Page).ToHaveURLAsync(new Regex({CSharp.Literal(LinkTargets.UrlPattern(target.Route))}));");
                if (target.Heading is not null) w.Line($"await Expect(new {PageObjectWriter.ClassName(target)}(Page).Heading).ToBeVisibleAsync();");
                w.Line("await AssertNoErrorsAsync();");
            }
        }
        // Länkar utan känd vy (externa adresser, relativa adresser, sidor som upptäckten inte hittade) går inte att pröva mot en rutt.
        foreach (var s in skipped) { w.Line(); w.Line($"// Inget navigeringstest för länken {CSharp.Comment(s)}."); }
    }

    // --- Visuella tester ---

    /// <summary>
    /// Jämför hela sidan med referensbilden <c>Screenshots/{Id}.png</c>. Inte för vyer som kräver inloggning.
    /// Med mockad backend kommer datan ur tabellen och bilden är stabil. Med riktig backend ändras innehållet av sig självt,
    /// så testet får kategorin Instabil och kan sorteras bort.
    /// </summary>
    private static void VisualTest(CodeWriter w, View view, GenerateOptions options)
    {
        if (view.RequiresAuth)
        {
            w.Line();
            w.Line("// Inget visuellt test: vyn kräver inloggning, som inte stöds än.");
            return;
        }
        var page = PageObjectWriter.ClassName(view);
        var id = CSharp.Identifier(view.Id, "View");
        w.Line();
        w.Line("/// <summary>Element som ska döljas i skärmdumpen utöver <c>[data-uitest-mask]</c> (klockslag, slumpvärden). Implementeras i den handskrivna filen.</summary>");
        w.Line("partial void MaskedElements(IPage page, List<ILocator> masks);");
        w.Line();
        w.Line("[Test]");
        w.Line("[Category(\"Visuellt\")]");
        if (options.Backend == Backend.Real)
        {
            w.Line("// Med riktig backend ändras datan mellan körningarna, så bilden är instabil. Kör med mock för en stabil jämförelse.");
            w.Line("[Category(\"Instabil\")]");
        }
        using (w.Block("public async Task Looks_like_the_reference()"))
        {
            w.Line($"await GotoAsync({PathCall(view)});");
            if (view.Heading is not null) w.Line($"await Expect(new {page}(Page).Heading).ToBeVisibleAsync();");
            w.Line("var masks = new List<ILocator>();");
            w.Line("MaskedElements(Page, masks);");
            w.Line($"await Screenshots.MatchReferenceAsync(Page, {CSharp.Literal(id)}, masks);");
        }
    }

    /// <summary>
    /// Attributen och rubriken för ett formulärtest. Muterar-kategorin kommer med när backend är riktig och testet skickar
    /// giltiga värden: bara de sparar något. Ogiltiga värden stoppas av valideringen.
    /// </summary>
    private static IDisposable FormTest(CodeWriter w, GenerateOptions options, string name, bool mutates)
    {
        w.Line();
        w.Line("[Test]");
        w.Line("[Category(\"Formulär\")]");
        if (mutates && options.Backend == Backend.Real)
        {
            w.Line("// Skickar data till riktig backend.");
            w.Line("[Category(\"Muterar\")]");
        }
        return w.Block($"public async Task {name}()");
    }

    private static void Open(CodeWriter w, string path, string page)
    {
        w.Line($"await GotoAsync({path});");
        w.Line($"var view = new {page}(Page);");
    }

    /// <summary>Anropet som fyller i formuläret: alla fält giltigt, utom fältet i <paramref name="invalid"/>.</summary>
    private static void Fill(CodeWriter w, PageObjectWriter.FormMembers members, FormPlan plan, InvalidCase? invalid)
    {
        if (members.Parameters.Count == 0) return;
        var args = members.Parameters.Select(p =>
        {
            var field = members.Form.Fields[p.Index];
            var value = invalid is not null && invalid.Index == p.Index ? invalid.Value : plan.Fields[p.Index].Valid!;
            return $"{p.Name}: {ValueExpression(field, value)}";
        });
        w.Line($"await view.{members.Fill}({string.Join(", ", args)});");
    }

    /// <summary>Värdet som C#-kod. Långa upprepningar (en för lång text) skrivs som <c>new string('x', n)</c>.</summary>
    internal static string ValueExpression(Field field, FieldValue value)
    {
        if (field.Type == FieldType.Checkbox) return value.Checked ? "true" : "false";
        var text = value.Text;
        if (text.Length > 40 && text.All(c => c == text[0]) && !char.IsControl(text[0]) && text[0] is not ('\'' or '\\'))
            return $"new string('{text[0]}', {text.Length})";
        return CSharp.Literal(text);
    }
}
