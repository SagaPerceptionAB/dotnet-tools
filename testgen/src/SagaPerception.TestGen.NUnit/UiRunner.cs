using Microsoft.Playwright;
using TestGen.Browser;
using TestGen.Browser.Forms;
using TestGen.Core.Run;
using TestGen.Core.Settings;
using TestGen.Core.Views;
using TestGen.Mock;

namespace SagaPerception.TestGen.NUnit;

/// <summary>Vad ett UI-test kom fram till, innan det blir ett NUnit-utfall.</summary>
internal sealed record UiOutcome(Verdict Verdict, string Message, FailureCause? Cause = null)
{
    public static readonly UiOutcome Pass = new(Verdict.Pass, "");

    public static UiOutcome Fail(string message) => new(Verdict.Fail, message);

    public static UiOutcome Inconclusive(string message) => new(Verdict.Inconclusive, message, FailureCause.Tool);

    public static UiOutcome Ignore(string message, FailureCause cause = FailureCause.ReadOnly) => new(Verdict.Ignore, message, cause);
}

/// <summary>Det ett UI-test behöver av sessionen: mockarna per API, värdpolicyn och om miljön tillåter att formulär skickas.</summary>
internal sealed record UiContext(ViewPlan Plan, IReadOnlyDictionary<ApiBinding, MockServer> Mocks, HostGate Gate, bool ReadOnly, string ScreenshotDirectory, string DiffDirectory);

/// <summary>Kör ett UI-test i en webbläsarsession mot appen. Varje testsort har sin metod.</summary>
internal sealed class UiRunner(BrowserSession browser, UiContext context)
{
    private const string ReadOnlyText = "Miljön är skrivskyddad, så inga formulär skickas.";

    private IPage Page => browser.Page;

    public async Task<UiOutcome> RunAsync(UiCase c)
    {
        if (c.View.RequiresAuth) return UiOutcome.Ignore("Sidan kräver inloggning, som UI-testerna inte stöder än.", FailureCause.Auth);
        try
        {
            return c.Kind switch
            {
                UiKind.Page => await PageAsync(c),
                UiKind.Links => await LinksAsync(c),
                UiKind.Form => await FormAsync(c),
                UiKind.Boundary => await BoundaryAsync(c),
                UiKind.Invalid => await InvalidAsync(c),
                UiKind.ServerError => await ServerErrorAsync(c),
                UiKind.Accessibility => await AccessibilityAsync(c),
                UiKind.Screenshot => await ScreenshotAsync(c),
                _ => UiOutcome.Inconclusive($"Okänd testsort {c.Kind}."),
            };
        }
        catch (PlaywrightException e)
        {
            return UiOutcome.Fail("Webbläsaren stoppade testet: " + e.Message.Split('\n')[0].Trim());
        }
    }

    // --- Sidan och länkarna ---

    private async Task<UiOutcome> PageAsync(UiCase c)
    {
        var path = RouteExamples.Path(c.View.Route);
        var blockedBefore = context.Gate.Calls.Count;
        var problems = new List<string>();
        var response = await browser.OpenAsync(path);
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        if (!response.Ok) problems.Add($"{path} svarade {response.Status}.");
        problems.AddRange(browser.Errors.All.Select(e => e.Message));
        problems.AddRange(context.Gate.Calls.Skip(blockedBefore).Where(x => x.Action != HostAction.Allow && x.Status >= 400).Select(x => "Appens anrop stoppades: " + x.Text));
        if (c.View.Title is { } title && await Page.TitleAsync() is var actualTitle && actualTitle != title)
            problems.Add($"Sidans titel är '{actualTitle}', men views.json har '{title}'. Kör testgen views om sidan ändrats.");
        if (c.View.Heading is { } heading)
        {
            var actual = (await Page.Locator("h1").First.InnerTextAsync()).Trim();
            if (actual != heading) problems.Add($"Rubriken är '{actual}', men views.json har '{heading}'. Kör testgen views om sidan ändrats.");
        }
        return problems.Count == 0 ? UiOutcome.Pass : UiOutcome.Fail($"Sidan {path} laddas inte felfritt:{Lines(problems)}");
    }

    private async Task<UiOutcome> LinksAsync(UiCase c)
    {
        var problems = new List<string>();
        var start = RouteExamples.Path(c.View.Route);
        foreach (var href in c.View.Links.Select(l => l.Href).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            // Bara länkar inom appen med fast adress går att öppna; dynamiska (t.ex. /products/{}) har inget känt värde.
            if (!href.StartsWith('/') || href.StartsWith("//", StringComparison.Ordinal) || href.Contains("{}", StringComparison.Ordinal)) continue;
            browser.Errors.Clear();
            var response = await browser.OpenAsync(href);
            await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            var errors = browser.Errors.All.Select(e => e.Message).ToList();
            if (!response.Ok) errors.Insert(0, $"svarade {response.Status}");
            if (errors.Count > 0) problems.Add($"{href}: {string.Join("; ", errors)}");
        }
        return problems.Count == 0 ? UiOutcome.Pass : UiOutcome.Fail($"Länkar på {start} leder till sidor som inte laddas:{Lines(problems)}");
    }

    // --- Formulär ---

    private async Task<UiOutcome> FormAsync(UiCase c)
    {
        var form = c.Form!;
        if (form.Plan.Warning is { } warning) return UiOutcome.Inconclusive($"Formuläret {form.Form.Id} kan inte testas: {warning}");
        if (context.ReadOnly) return UiOutcome.Ignore(ReadOnlyText);
        await FillAndSubmitAsync(c, form, null, null);

        var problems = new List<string>();
        if (MockOf(form) is { } mock)
        {
            var calls = CallsTo(mock, form);
            if (calls.Count != 1) problems.Add($"Mocken fick {calls.Count} anrop till {form.Operation!.OperationId}, väntade ett.");
            foreach (var call in calls.Where(x => x.Problems.Count > 0 || x.Status >= 400))
                problems.Add($"Mocken svarade {call.Status} på {call.Method} {call.Path}: {string.Join("; ", call.Problems)}");
        }
        var alerts = await FormDriver.ErrorAlerts(Page).AllInnerTextsAsync();
        problems.AddRange(alerts.Select(a => "Sidan visar ett fel: " + a.Trim()));
        problems.AddRange(browser.Errors.All.Select(e => e.Message));
        return problems.Count == 0
            ? UiOutcome.Pass
            : UiOutcome.Fail($"Formuläret {form.Form.Id} på {c.View.Route} skickades med giltiga värden, men det gick inte igenom:{Lines(problems)}");
    }

    private async Task<UiOutcome> BoundaryAsync(UiCase c)
    {
        var form = c.Form!;
        var boundary = c.Boundary!;
        if (context.ReadOnly) return UiOutcome.Ignore(ReadOnlyText);
        await FillAndSubmitAsync(c, form, boundary.Field, boundary.Value);

        var fix = $"Gör så här: skärp regeln för {boundary.Field.Name} i formuläret så att den stämmer med specen, eller ändra specen om API:et ska ta emot värdet.";
        if (MockOf(form) is { } mock)
        {
            var calls = CallsTo(mock, form);
            if (calls.Count == 0) return UiOutcome.Pass;
            var answer = calls.Select(x => $"mocken svarade {x.Status}{(x.Problems.Count == 0 ? "" : ": " + string.Join("; ", x.Problems))}");
            return UiOutcome.Fail($"{boundary.Explanation}\nFormuläret {form.Form.Id} på {c.View.Route} skickade värdet vidare och {string.Join(" och ", answer)}.\n{fix}");
        }
        // Utan mock syns bara om formuläret själv stoppar värdet.
        return await FormDriver.IsInvalidAsync(Page.Resolve(boundary.Field.Locator), TimeSpan.FromSeconds(2)) || await FormDriver.ErrorAlerts(Page).CountAsync() > 0
            ? UiOutcome.Pass
            : UiOutcome.Fail($"{boundary.Explanation}\nFormuläret {form.Form.Id} på {c.View.Route} stoppade inte värdet.\n{fix}");
    }

    private async Task<UiOutcome> InvalidAsync(UiCase c)
    {
        var form = c.Form!;
        var invalid = c.Invalid!;
        if (context.ReadOnly) return UiOutcome.Ignore(ReadOnlyText);
        await browser.OpenAsync(RouteExamples.Path(c.View.Route));
        if (invalid.NeedsLongInput) await FormDriver.AllowTooLongAsync(Page.Resolve(invalid.Field.Locator));
        await FormDriver.FillAsync(Page, Values(form, invalid.Field, invalid.Value));
        await FormDriver.SubmitAsync(Page, form.Form, browserValidation: invalid.Checker == RuleChecker.Browser);

        var what = $"{invalid.Field.Name} ({invalid.Rule.Kind})";
        if (MockOf(form) is { } mock && CallsTo(mock, form).Count > 0)
            return UiOutcome.Fail($"Formuläret {form.Form.Id} på {c.View.Route} skickade ett ogiltigt värde för {what} till API:et. Formuläret ska stoppa det.");
        if (invalid.Checker == RuleChecker.App)
        {
            try { await Assertions.Expect(Page.GetByText(invalid.Rule.Message!, new() { Exact = true }).First).ToBeVisibleAsync(); }
            catch (PlaywrightException)
            {
                return UiOutcome.Fail($"Formuläret {form.Form.Id} på {c.View.Route} visar inte felet '{invalid.Rule.Message}' när {what} bryts.");
            }
        }
        else if (!await FormDriver.IsInvalidAsync(Page.Resolve(invalid.Field.Locator)))
            return UiOutcome.Fail($"Formuläret {form.Form.Id} på {c.View.Route} markerar inte {invalid.Field.Name} som ogiltigt när {invalid.Rule.Kind} bryts.");
        return UiOutcome.Pass;
    }

    private async Task<UiOutcome> ServerErrorAsync(UiCase c)
    {
        var form = c.Form!;
        if (form.Plan.Warning is not null) return UiOutcome.Inconclusive("Formuläret kan inte testas, se testet för formuläret.");
        if (context.ReadOnly) return UiOutcome.Ignore(ReadOnlyText);
        if (MockOf(form) is not { } mock) return UiOutcome.Ignore("Tjänstefel kan bara provoceras med mocken, som bara används lokalt.", FailureCause.Environment);
        mock.Respond(form.Operation!.OperationId, 500);
        await FillAndSubmitAsync(c, form, null, null);

        try { await Assertions.Expect(FormDriver.ErrorAlerts(Page).First).ToBeVisibleAsync(); }
        catch (PlaywrightException)
        {
            return UiOutcome.Fail($"API:et svarade 500 på {form.Operation.OperationId}, men sidan {c.View.Route} visar inget fel för användaren.");
        }
        var crashes = browser.Errors.All.Where(e => e.Kind is PageErrorKind.Page or PageErrorKind.Console).Select(e => e.Message).ToList();
        return crashes.Count == 0 ? UiOutcome.Pass : UiOutcome.Fail($"Sidan {c.View.Route} visar ett fel, men kraschar också:{Lines(crashes)}");
    }

    // --- Valfria kontroller ---

    private async Task<UiOutcome> AccessibilityAsync(UiCase c)
    {
        await browser.OpenAsync(RouteExamples.Path(c.View.Route));
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        var violations = await Accessibility.CheckAsync(Page);
        return violations.Count == 0
            ? UiOutcome.Pass
            : UiOutcome.Fail($"{c.View.Route} bryter mot WCAG 2.1 AA (axe-core {Accessibility.AxeVersion}):{Lines(violations.Select(v => v.Describe()))}");
    }

    private async Task<UiOutcome> ScreenshotAsync(UiCase c)
    {
        await browser.OpenAsync(RouteExamples.Path(c.View.Route));
        var result = await ScreenshotComparer.CompareAsync(Page, c.View.Route,
            new ScreenshotOptions(context.ScreenshotDirectory, context.DiffDirectory, Update: Environment.GetEnvironmentVariable("TESTGEN_UPDATE_SCREENSHOTS") == "1"));
        if (result.Passed) return UiOutcome.Pass;
        return result.IsNewReference ? UiOutcome.Inconclusive(result.Message) : UiOutcome.Fail(result.Message);
    }

    // --- Gemensamt ---

    private async Task FillAndSubmitAsync(UiCase c, FormCase form, Field? replace, FieldValue? value)
    {
        await browser.OpenAsync(RouteExamples.Path(c.View.Route));
        await FormDriver.FillAsync(Page, Values(form, replace, value));
        await FormDriver.SubmitAsync(Page, form.Form);
    }

    private static IEnumerable<(Field, FieldValue)> Values(FormCase form, Field? replace, FieldValue? value)
    {
        foreach (var plan in form.Plan.Fields)
            if ((ReferenceEquals(plan.Field, replace) ? value : plan.Valid) is { } v) yield return (plan.Field, v);
    }

    private MockServer? MockOf(FormCase form) => form.Api is { } api && context.Mocks.TryGetValue(api, out var mock) && form.Operation is not null ? mock : null;

    private static List<MockCall> CallsTo(MockServer mock, FormCase form) => mock.Calls.Where(x => x.OperationId == form.Operation!.OperationId).ToList();

    private static string Lines(IEnumerable<string> lines) => string.Concat(lines.Select(l => "\n  " + l));
}
