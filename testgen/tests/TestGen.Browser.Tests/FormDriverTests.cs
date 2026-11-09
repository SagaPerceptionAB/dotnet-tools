using Microsoft.Playwright;
using TestGen.Browser.Forms;
using TestGen.Core.Views;
using static TestGen.Browser.Tests.FormFixtures;

namespace TestGen.Browser.Tests;

public class FormDriverTests
{
    private const string FormPage = """
        <!doctype html><html lang="sv"><head><title>Formulär</title></head><body>
        <h1>Formulär</h1>
        <form id="f">
          <label for="name">Namn</label><input id="name" required>
          <label for="kind">Typ</label><select id="kind"><option>a</option><option>b</option></select>
          <label for="agree">Godkänn</label><input type="checkbox" id="agree">
          <input type="radio" name="color" value="red" aria-label="Röd"><input type="radio" name="color" value="blue" aria-label="Blå">
          <label for="short">Kort</label><input id="short" maxlength="3">
          <label for="flag">Markerad</label><input id="flag" aria-invalid="true">
          <button type="submit">Spara</button>
        </form>
        <p id="result"></p>
        <div role="alert" class="alert-success">Sparat</div><div id="err" role="alert" hidden>Fel</div>
        <script>
          document.getElementById('f').addEventListener('submit', e => {
            e.preventDefault();
            document.getElementById('result').textContent = JSON.stringify({
              name: document.getElementById('name').value,
              kind: document.getElementById('kind').value,
              agree: document.getElementById('agree').checked,
              color: (document.querySelector('input[name=color]:checked') || {}).value || ''
            });
          });
        </script></body></html>
        """;

    private static readonly Field NameField = new("Name", "Namn", FieldType.Text, Label("Namn"), [new Rule(RuleKind.Required)], []);
    private static readonly Field KindField = new("Kind", "Typ", FieldType.Select, Label("Typ"), [], ["a", "b"]);
    private static readonly Field AgreeField = new("Agree", "Godkänn", FieldType.Checkbox, Label("Godkänn"), [], []);
    private static readonly Field ColorField = new("Color", null, FieldType.Radio, new Locator(LocatorKind.Css, "input[name=color]"), [], ["red", "blue"]);
    private static readonly Form TheForm = new("F", null, new Locator(LocatorKind.Role, "button", "Spara"), [NameField, KindField, AgreeField, ColorField], null);

    private static async Task<(TestApp Site, BrowserSession Session)> OpenAsync()
    {
        var site = await TestApp.StartAsync(app => TestApp.Page(app, "/form", FormPage));
        var session = await BrowserFixture.Host.NewSessionAsync(new SessionOptions(site.Url));
        await session.OpenAsync("/form");
        return (site, session);
    }

    [Test]
    public async Task Fills_every_kind_of_field_and_submits()
    {
        var (site, session) = await OpenAsync();
        await using var _ = site;
        await using var __ = session;

        await FormDriver.FillAsync(session.Page,
        [
            (NameField, FieldValue.Of("Anna")), (KindField, FieldValue.Of("b")), (AgreeField, FieldValue.Box(true)), (ColorField, FieldValue.Of("blue")),
        ]);
        await FormDriver.SubmitAsync(session.Page, TheForm);

        await Assertions.Expect(session.Page.Locator("#result")).ToHaveTextAsync("{\"name\":\"Anna\",\"kind\":\"b\",\"agree\":true,\"color\":\"blue\"}");
    }

    [Test]
    public async Task Fills_the_valid_values_of_a_plan()
    {
        var (site, session) = await OpenAsync();
        await using var _ = site;
        await using var __ = session;
        var plan = FormPlan.Build(ViewOf(TheForm), TheForm, seed: 1);

        await FormDriver.FillValidAsync(session.Page, plan);
        await FormDriver.SubmitAsync(session.Page, TheForm);

        await Assertions.Expect(session.Page.Locator("#result")).ToContainTextAsync($"\"name\":\"{plan.Fields[0].Valid!.Text}\"");
    }

    [Test]
    public async Task Browser_validation_stops_an_empty_required_field_and_can_be_turned_off()
    {
        var (site, session) = await OpenAsync();
        await using var _ = site;
        await using var __ = session;
        var name = session.Page.Resolve(NameField.Locator);

        await FormDriver.SubmitAsync(session.Page, TheForm);
        Assert.That(await FormDriver.IsInvalidAsync(name), Is.True);
        await Assertions.Expect(session.Page.Locator("#result")).ToBeEmptyAsync();

        await FormDriver.SubmitAsync(session.Page, TheForm, browserValidation: false);
        await Assertions.Expect(session.Page.Locator("#result")).Not.ToBeEmptyAsync();
    }

    [Test]
    public async Task Valid_field_is_not_marked_invalid_and_aria_invalid_is_recognised()
    {
        var (site, session) = await OpenAsync();
        await using var _ = site;
        await using var __ = session;

        await session.Page.Resolve(NameField.Locator).FillAsync("Anna");

        Assert.That(await FormDriver.IsInvalidAsync(session.Page.Resolve(NameField.Locator), TimeSpan.FromMilliseconds(200)), Is.False);
        Assert.That(await FormDriver.IsInvalidAsync(session.Page.Locator("#flag")), Is.True);
    }

    [Test]
    public async Task Max_length_attribute_can_be_removed()
    {
        var (site, session) = await OpenAsync();
        await using var _ = site;
        await using var __ = session;
        var field = session.Page.Locator("#short");

        await FormDriver.AllowTooLongAsync(field);

        Assert.That(await field.GetAttributeAsync("maxlength"), Is.Null);
    }

    [Test]
    public async Task Error_alerts_skip_confirmations()
    {
        var (site, session) = await OpenAsync();
        await using var _ = site;
        await using var __ = session;

        var alerts = FormDriver.ErrorAlerts(session.Page);

        Assert.That(await alerts.CountAsync(), Is.EqualTo(1));
        Assert.That(await alerts.First.GetAttributeAsync("id"), Is.EqualTo("err"));
    }
}
