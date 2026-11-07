using System.Text.Json.Serialization;

namespace UiTestGen.Model;

// Vykatalogen är kontraktet mellan stegen: Hitta (Discovery) och Sondera (Probe) fyller den, Generera (Generation) läser den.
// Allt i katalogen är sorterat och utan tider eller absoluta sökvägar, så att samma källkod ger exakt samma JSON.

/// <summary>Alla vyer som hittades, sorterade på <see cref="View.Route"/> och sedan <see cref="View.Id"/>.</summary>
public sealed record ViewCatalog(IReadOnlyList<View> Views, IReadOnlyList<string> Warnings)
{
    /// <summary>Höjs när formatet ändras på ett sätt som gamla kataloger inte klarar.</summary>
    public int Version { get; init; } = 1;
}

/// <summary>En vy: en sida som går att öppna på en egen adress.</summary>
/// <param name="Id">Stabilt namn i PascalCase som blir klassnamn, t.ex. <c>ProductsCreate</c> för <c>/products/create</c>. Unikt i katalogen.</param>
/// <param name="Route">Ruttmallen som den står i koden, t.ex. <c>/products/{id:int}</c>. Börjar alltid med <c>/</c>.</param>
/// <param name="Kind">Vilken sorts vy det är.</param>
/// <param name="Source">Filen vyn kommer från, relativt roten för sökningen, med <c>/</c> som avgränsare.</param>
/// <param name="Title">Sidans titel (<c>&lt;PageTitle&gt;</c>, <c>ViewData["Title"]</c>, <c>&lt;title&gt;</c>), om den går att läsa.</param>
/// <param name="Heading">Texten i första <c>&lt;h1&gt;</c>, om den är fast text.</param>
/// <param name="Parameters">Ruttparametrarna i den ordning de står i mallen.</param>
/// <param name="Forms">Formulären på sidan.</param>
/// <param name="Links">Länkar till andra sidor inom appen (sökvägar som börjar med <c>/</c>, eller relativa).</param>
/// <param name="ApiCalls">Anrop till backend som vyn gör, för mockning. Tomt om inget hittades.</param>
/// <param name="RequiresAuth">Vyn kräver inloggning (<c>[Authorize]</c>, <c>@attribute [Authorize]</c>, route guard).</param>
public sealed record View(
    string Id,
    string Route,
    ViewKind Kind,
    string Source,
    string? Title,
    string? Heading,
    IReadOnlyList<RouteParameter> Parameters,
    IReadOnlyList<Form> Forms,
    IReadOnlyList<Link> Links,
    IReadOnlyList<ApiCall> ApiCalls,
    bool RequiresAuth);

[JsonConverter(typeof(JsonStringEnumConverter<ViewKind>))]
public enum ViewKind { Blazor, RazorPage, Mvc, Spa, Probed }

/// <param name="Name">Namnet i mallen, t.ex. <c>id</c>.</param>
/// <param name="Constraint">Villkoret, t.ex. <c>int</c>, <c>guid</c>, <c>alpha</c>. Null om inget anges.</param>
/// <param name="Optional">Parametern får utelämnas (<c>{id?}</c>).</param>
public sealed record RouteParameter(string Name, string? Constraint, bool Optional);

/// <param name="Id">Stabilt namn på formuläret inom vyn, t.ex. <c>Product</c> (modellens namn) eller <c>Form1</c>.</param>
/// <param name="Model">Modellens typnamn, t.ex. <c>ProductInput</c>, om formuläret är bundet till en modell.</param>
/// <param name="Submit">Hur formuläret skickas: knappen att klicka på.</param>
/// <param name="Fields">Fälten i den ordning de står på sidan.</param>
/// <param name="SubmitsTo">Backend-anropet som formuläret gör när det skickas, om det är känt (för mockning).</param>
public sealed record Form(string Id, string? Model, Locator Submit, IReadOnlyList<Field> Fields, ApiCall? SubmitsTo);

/// <param name="Name">Fältets namn, t.ex. modellens property <c>Name</c> eller HTML-attributet <c>name</c>.</param>
/// <param name="Label">Etiketten som användaren ser, om den är känd.</param>
/// <param name="Locator">Hur fältet hittas på sidan.</param>
/// <param name="Rules">Valideringsreglerna, sorterade på <see cref="Rule.Kind"/>.</param>
/// <param name="Options">Valen i en select eller radiogrupp (värdena), annars tomt.</param>
public sealed record Field(string Name, string? Label, FieldType Type, Locator Locator, IReadOnlyList<Rule> Rules, IReadOnlyList<string> Options);

[JsonConverter(typeof(JsonStringEnumConverter<FieldType>))]
public enum FieldType { Text, TextArea, Email, Password, Number, Date, DateTime, Tel, Url, Select, Checkbox, Radio, Hidden }

/// <summary>En valideringsregel.</summary>
/// <param name="Value">Regelns värde: längden för MinLength/MaxLength, gränsen för Min/Max, mönstret för Pattern, det andra fältets namn för Compare. Null för Required, Email och Url.</param>
/// <param name="Message">Felmeddelandet som visas, om det står i koden (<c>ErrorMessage = "..."</c>).</param>
public sealed record Rule(RuleKind Kind, string? Value = null, string? Message = null);

[JsonConverter(typeof(JsonStringEnumConverter<RuleKind>))]
public enum RuleKind { Required, MinLength, MaxLength, Min, Max, Pattern, Email, Url, Compare }

/// <summary>
/// Hur ett element hittas. I första hand det användaren ser (etikett, roll och namn), sedan <c>data-testid</c>, sist CSS.
/// Genereringen översätter till <c>GetByLabel</c>, <c>GetByRole</c>, <c>GetByTestId</c> och <c>Locator</c>.
/// </summary>
/// <param name="Value">Etiketten, rollen (t.ex. <c>button</c>), test-id:t eller CSS-väljaren.</param>
/// <param name="Name">Med <see cref="LocatorKind.Role"/>: elementets tillgängliga namn, t.ex. knappens text.</param>
public sealed record Locator(LocatorKind Kind, string Value, string? Name = null);

[JsonConverter(typeof(JsonStringEnumConverter<LocatorKind>))]
public enum LocatorKind { Label, Role, TestId, Css }

/// <param name="Href">Adressen som den står, t.ex. <c>/products/create</c> eller <c>products/@p.Id</c> (då med <c>{}</c> för det dynamiska: <c>/products/{}</c>).</param>
/// <param name="Text">Länktexten, om den är fast text.</param>
public sealed record Link(string Href, string? Text);

/// <param name="Method">HTTP-metoden med versaler, t.ex. <c>GET</c>.</param>
/// <param name="Path">Sökvägen med parametrar som <c>{}</c>, t.ex. <c>/products/{}</c>, så att den kan matchas mot specen.</param>
public sealed record ApiCall(string Method, string Path);
