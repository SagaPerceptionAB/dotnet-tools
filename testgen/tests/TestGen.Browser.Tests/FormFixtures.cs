using Microsoft.OpenApi;
using TestGen.Core.Views;

namespace TestGen.Browser.Tests;

/// <summary>Byggstenar för formulär och scheman i testerna.</summary>
internal static class FormFixtures
{
    public static Locator Label(string text) => new(LocatorKind.Label, text);

    public static Field TextField(string name, string label, params Rule[] rules) =>
        new(name, label, FieldType.Text, Label(label), rules, [], SpecField: name.ToLowerInvariant());

    public static Form FormOf(params Field[] fields) =>
        new("Customer", "CustomerInput", new Locator(LocatorKind.Role, "button", "Spara"), fields, new ApiCall("POST", "/customers"), "createCustomer");

    public static View ViewOf(Form form) =>
        new("ui:/customers/create", "/customers/create", ViewKind.Blazor, "Create.razor", "Ny kund", "Ny kund", [], [form], [], [], false);

    /// <summary>Request-schemat med de angivna fälten. De som står i <paramref name="required"/> är obligatoriska.</summary>
    public static IOpenApiSchema Body(IEnumerable<string>? required = null, params (string Name, IOpenApiSchema Schema)[] props)
    {
        return new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            Properties = props.ToDictionary(p => p.Name, p => p.Schema),
            Required = new HashSet<string>(required ?? []),
        };
    }

    public static IOpenApiSchema Str(int? max = null, int? min = null, string? pattern = null) =>
        new OpenApiSchema { Type = JsonSchemaType.String, MaxLength = max, MinLength = min, Pattern = pattern };
}
