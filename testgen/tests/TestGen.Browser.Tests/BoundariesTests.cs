using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using TestGen.Browser.Forms;
using TestGen.Core.Views;
using static TestGen.Browser.Tests.FormFixtures;

namespace TestGen.Browser.Tests;

public class BoundariesTests
{
    private static IReadOnlyList<BoundaryCase> Find(Form form, IOpenApiSchema body) =>
        Boundaries.Find(FormPlan.Build(ViewOf(form), form, seed: 1, body), body);

    [Test]
    public void Customer_name_without_max_length_gives_a_case_with_101_characters()
    {
        var form = FormOf(TextField("Name", "Namn", new Rule(RuleKind.Required)));
        var cases = Find(form, Body(null, ("name", Str(max: 100))));

        Assert.That(cases, Has.Count.EqualTo(1));
        Assert.That(cases[0].Kind, Is.EqualTo(BoundaryKind.MaxLength));
        Assert.That(cases[0].Value.Text, Has.Length.EqualTo(101));
        Assert.That(cases[0].Explanation, Is.EqualTo("Formuläret tillåter name med 101 tecken, men API:et tar högst 100 (createCustomer)"));
    }

    [Test]
    public void Stricter_form_gives_no_case()
    {
        var form = FormOf(TextField("Name", "Namn", new Rule(RuleKind.MaxLength, "50"), new Rule(RuleKind.MinLength, "5"), new Rule(RuleKind.Required)));
        Assert.That(Find(form, Body(["name"], ("name", Str(max: 100, min: 3)))), Is.Empty);
    }

    [Test]
    public void Min_length_gives_a_value_one_character_short()
    {
        var form = FormOf(TextField("Name", "Namn"));
        var cases = Find(form, Body(null, ("name", Str(min: 3))));

        Assert.That(cases.Single().Kind, Is.EqualTo(BoundaryKind.MinLength));
        Assert.That(cases.Single().Value.Text, Has.Length.EqualTo(2));
        Assert.That(cases.Single().Explanation, Does.Contain("minst 3"));
    }

    [Test]
    public void Maximum_gives_a_value_one_above_and_exclusive_maximum_gives_the_bound_itself()
    {
        var form = FormOf(TextField("Price", "Pris") with { Type = FieldType.Number });
        var inclusive = Find(form, Body(null, ("price", new OpenApiSchema { Type = JsonSchemaType.Integer, Maximum = "1000" })));
        var exclusive = Find(form, Body(null, ("price", new OpenApiSchema { Type = JsonSchemaType.Integer, ExclusiveMaximum = "1000" })));

        Assert.That(inclusive.Single().Value.Text, Is.EqualTo("1001"));
        Assert.That(inclusive.Single().Kind, Is.EqualTo(BoundaryKind.Maximum));
        Assert.That(exclusive.Single().Value.Text, Is.EqualTo("1000"));
        Assert.That(exclusive.Single().Explanation, Does.Contain("lägre än 1000"));
    }

    [Test]
    public void Minimum_gives_a_value_one_below_and_exclusive_minimum_gives_the_bound_itself()
    {
        var form = FormOf(TextField("Price", "Pris") with { Type = FieldType.Number });
        var inclusive = Find(form, Body(null, ("price", new OpenApiSchema { Type = JsonSchemaType.Integer, Minimum = "1" })));
        var exclusive = Find(form, Body(null, ("price", new OpenApiSchema { Type = JsonSchemaType.Integer, ExclusiveMinimum = "1" })));

        Assert.That(inclusive.Single().Value.Text, Is.EqualTo("0"));
        Assert.That(inclusive.Single().Kind, Is.EqualTo(BoundaryKind.Minimum));
        Assert.That(exclusive.Single().Value.Text, Is.EqualTo("1"));
    }

    [Test]
    public void Number_form_that_is_stricter_gives_no_case()
    {
        var field = TextField("Price", "Pris", new Rule(RuleKind.Min, "5"), new Rule(RuleKind.Max, "500")) with { Type = FieldType.Number };
        var form = FormOf(field);
        Assert.That(Find(form, Body(null, ("price", new OpenApiSchema { Type = JsonSchemaType.Integer, Minimum = "1", Maximum = "1000" }))), Is.Empty);
    }

    [Test]
    public void Pattern_gives_a_value_the_form_accepts_but_the_spec_rejects()
    {
        var form = FormOf(TextField("Code", "Kod"));
        var cases = Find(form, Body(null, ("code", Str(pattern: "^[A-Z]{3}$"))));

        Assert.That(cases.Single().Kind, Is.EqualTo(BoundaryKind.Pattern));
        Assert.That(cases.Single().Value.Text, Does.Not.Match("^[A-Z]{3}$"));
        Assert.That(cases.Single().Explanation, Does.Contain("^[A-Z]{3}$").And.Contain("(createCustomer)"));
    }

    [Test]
    public void Same_pattern_in_the_form_gives_no_case()
    {
        var form = FormOf(TextField("Code", "Kod", new Rule(RuleKind.Pattern, "^[A-Z]{3}$")));
        Assert.That(Find(form, Body(null, ("code", Str(pattern: "^[A-Z]{3}$")))), Is.Empty);
    }

    [Test]
    public void Enum_in_a_text_field_gives_a_value_outside_the_enum()
    {
        var form = FormOf(TextField("Status", "Status"));
        var schema = new OpenApiSchema { Type = JsonSchemaType.String, Enum = [JsonValue.Create("active"), JsonValue.Create("closed")] };
        var cases = Find(form, Body(null, ("status", schema)));

        Assert.That(cases.Single().Kind, Is.EqualTo(BoundaryKind.Enum));
        Assert.That(cases.Single().Value.Text, Is.Not.AnyOf("active", "closed"));
        Assert.That(cases.Single().Explanation, Does.Contain("active, closed"));
    }

    [Test]
    public void Enum_in_a_select_gives_an_option_the_spec_does_not_allow()
    {
        var field = new Field("Status", "Status", FieldType.Select, Label("Status"), [], ["active", "closed", "legacy"], "status");
        var schema = new OpenApiSchema { Type = JsonSchemaType.String, Enum = [JsonValue.Create("active"), JsonValue.Create("closed")] };
        var cases = Find(FormOf(field), Body(null, ("status", schema)));

        Assert.That(cases.Single().Value.Text, Is.EqualTo("legacy"));
    }

    [Test]
    public void Select_with_only_allowed_options_gives_no_case()
    {
        var field = new Field("Status", "Status", FieldType.Select, Label("Status"), [], ["active", "closed"], "status");
        var schema = new OpenApiSchema { Type = JsonSchemaType.String, Enum = [JsonValue.Create("active"), JsonValue.Create("closed")] };
        Assert.That(Find(FormOf(field), Body(null, ("status", schema))), Is.Empty);
    }

    [Test]
    public void Required_in_the_spec_but_not_in_the_form_gives_an_empty_value()
    {
        var form = FormOf(TextField("Name", "Namn"));
        var cases = Find(form, Body(["name"], ("name", Str())));

        Assert.That(cases.Single().Kind, Is.EqualTo(BoundaryKind.Required));
        Assert.That(cases.Single().Value.Text, Is.Empty);
        Assert.That(cases.Single().Explanation, Is.EqualTo("Formuläret tillåter tomt name, men API:et kräver det (createCustomer)"));
    }

    [Test]
    public void Required_in_the_form_gives_no_case()
    {
        var form = FormOf(TextField("Name", "Namn", new Rule(RuleKind.Required)));
        Assert.That(Find(form, Body(["name"], ("name", Str()))), Is.Empty);
    }

    [Test]
    public void Form_without_operation_or_spec_field_gives_no_case()
    {
        var body = Body(null, ("name", Str(max: 5)));
        var noOperation = FormOf(TextField("Name", "Namn")) with { OperationId = null };
        var noSpecField = FormOf(TextField("Name", "Namn") with { SpecField = null });

        Assert.That(Find(noOperation, body), Is.Empty);
        Assert.That(Find(noSpecField, body), Is.Empty);
        Assert.That(Boundaries.Find(FormPlan.Build(ViewOf(noSpecField), noSpecField, 1), null), Is.Empty);
    }

    [Test]
    public void Case_ids_are_lower_case_with_hyphens()
    {
        var field = TextField("Name", "Namn");
        Assert.That(new BoundaryCase(field, BoundaryKind.MaxLength, FieldValue.Of("x"), "").Id, Is.EqualTo("max-length"));
        Assert.That(new BoundaryCase(field, BoundaryKind.Enum, FieldValue.Of("x"), "").Id, Is.EqualTo("enum"));
    }
}
