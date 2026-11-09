using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using TestGen.Browser.Forms;
using TestGen.Core.Views;
using static TestGen.Browser.Tests.FormFixtures;

namespace TestGen.Browser.Tests;

public class FormPlanTests
{
    [Test]
    public void Valid_value_breaks_none_of_the_rules_and_is_the_same_every_time()
    {
        var form = FormOf(TextField("Name", "Namn", new Rule(RuleKind.Required), new Rule(RuleKind.MaxLength, "20")));
        var first = FormPlan.Build(ViewOf(form), form, seed: 7);
        var second = FormPlan.Build(ViewOf(form), form, seed: 7);

        Assert.That(first.Fields[0].Valid, Is.Not.Null);
        Assert.That(FormPlan.Violations(form.Fields[0], first.Fields[0].Valid!, null), Is.Empty);
        Assert.That(second.Fields[0].Valid, Is.EqualTo(first.Fields[0].Valid));
    }

    [Test]
    public void Valid_value_comes_from_the_spec_field_when_the_form_accepts_it()
    {
        var form = FormOf(TextField("Status", "Status"));
        var schema = new OpenApiSchema { Type = JsonSchemaType.String, Enum = [JsonValue.Create("active"), JsonValue.Create("closed")] };
        var plan = FormPlan.Build(ViewOf(form), form, seed: 3, Body(null, ("status", schema)));

        Assert.That(plan.Fields[0].Valid!.Text, Is.AnyOf("active", "closed"));
    }

    [Test]
    public void Valid_value_falls_back_to_the_form_rules_when_the_spec_value_breaks_them()
    {
        var form = FormOf(TextField("Status", "Status", new Rule(RuleKind.Pattern, "^[a-z]{3}$")));
        var schema = new OpenApiSchema { Type = JsonSchemaType.String, Enum = [JsonValue.Create("ACTIVE")] };
        var plan = FormPlan.Build(ViewOf(form), form, seed: 3, Body(null, ("status", schema)));

        Assert.That(plan.Fields[0].Valid!.Text, Does.Match("^[a-z]{3}$"));
    }

    [Test]
    public void Each_rule_gets_an_invalid_value_that_breaks_that_rule()
    {
        var email = new Field("Email", "E-post", FieldType.Email, Label("E-post"), [new Rule(RuleKind.Required, null, "Obligatoriskt"), new Rule(RuleKind.Email, null, "Ogiltig e-post")], []);
        var form = FormOf(TextField("Name", "Namn", new Rule(RuleKind.Required, null, "Namn saknas"), new Rule(RuleKind.MinLength, "3", "För kort")), email);
        var plan = FormPlan.Build(ViewOf(form), form, seed: 1);

        Assert.That(plan.Warning, Is.Null);
        var kinds = plan.Cases.Select(c => (c.Field.Name, c.Rule.Kind)).ToList();
        Assert.That(kinds, Is.EquivalentTo(new[]
        {
            ("Name", RuleKind.Required), ("Name", RuleKind.MinLength), ("Email", RuleKind.Required), ("Email", RuleKind.Email),
        }));
        foreach (var c in plan.Cases)
        {
            Assert.That(FormPlan.Violations(c.Field, c.Value, null), Does.Contain(c.Rule.Kind), $"{c.Field.Name} {c.Rule.Kind}");
            Assert.That(c.Checker, Is.EqualTo(RuleChecker.App));
        }
    }

    [Test]
    public void Rule_without_message_is_checked_by_the_browser_except_max_length()
    {
        Assert.That(FormPlan.CheckedBy(new Rule(RuleKind.Required)), Is.EqualTo(RuleChecker.Browser));
        Assert.That(FormPlan.CheckedBy(new Rule(RuleKind.MaxLength, "5")), Is.Null);
        Assert.That(FormPlan.CheckedBy(new Rule(RuleKind.MaxLength, "5", "För lång")), Is.EqualTo(RuleChecker.App));
    }

    [Test]
    public void Select_and_checkbox_get_valid_values_from_their_options()
    {
        var select = new Field("Kind", "Typ", FieldType.Select, Label("Typ"), [], ["a", "b"]);
        var box = new Field("Agree", "Godkänn", FieldType.Checkbox, Label("Godkänn"), [new Rule(RuleKind.Required)], []);
        var form = FormOf(select, box);
        var plan = FormPlan.Build(ViewOf(form), form, seed: 1);

        Assert.That(plan.Fields[0].Valid!.Text, Is.EqualTo("a"));
        Assert.That(plan.Fields[1].Valid!.Checked, Is.True);
    }
}
