namespace TestGen.Core.Views;

// views.json: kontraktet mellan testgen views (som läser källkoden) och UI-testerna (som bara läser filen).
// Allt är sorterat och utan tider eller absoluta sökvägar, så att samma källkod ger exakt samma fil.

/// <summary>Filen. <see cref="SchemaVersion"/> står först.</summary>
public sealed record ViewsFile(int SchemaVersion, IReadOnlyList<AppViews> Apps)
{
    public const int CurrentSchemaVersion = 1;

    public static ViewsFile Of(IReadOnlyList<AppViews> apps) => new(CurrentSchemaVersion, apps);
}

/// <summary>En app (ett webbprojekt): dess vyer och de API:er den anropar.</summary>
/// <param name="Project">Projektnamnet, samma nyckel som i solutionen.</param>
/// <param name="Apis">API:erna appen anropar, sorterade på projekt och nyckel.</param>
public sealed record AppViews(string Project, IReadOnlyList<View> Views, IReadOnlyList<ViewsApi> Apis);

/// <summary>Ett API som appen anropar och konfigurationsnyckeln för dess adress, t.ex. <c>ShopApi:BaseUrl</c>. Nyckeln pekas om till mocken.</summary>
public sealed record ViewsApi(string Project, string? ConfigKey);

/// <summary>En vy: en sida som går att öppna på en egen adress.</summary>
/// <param name="Id">Stabilt id: <c>ui:</c> och ruttmallen, t.ex. <c>ui:/products/create</c>. Unikt i appen.</param>
/// <param name="Route">Ruttmallen som den står i koden, t.ex. <c>/products/{id:int}</c>. Börjar alltid med <c>/</c>.</param>
/// <param name="Source">Filen vyn kommer från, relativt views.json, med <c>/</c>.</param>
/// <param name="Title">Sidans titel, om den går att läsa.</param>
/// <param name="Heading">Texten i första <c>&lt;h1&gt;</c>, om den är fast text.</param>
/// <param name="Parameters">Ruttparametrarna i den ordning de står i mallen.</param>
/// <param name="Forms">Formulären på sidan i den ordning de står.</param>
/// <param name="Links">Länkar till andra sidor inom appen, sorterade på adress.</param>
/// <param name="ApiCalls">Anrop till backend som vyn gör (SPA). Tomt om inget hittades.</param>
/// <param name="RequiresAuth">Vyn kräver inloggning.</param>
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

/// <summary><c>Probed</c>: sidan hittades bara när appen kördes (sonderingen), inte i källkoden.</summary>
public enum ViewKind { Blazor, RazorPage, Mvc, Spa, Probed }

/// <param name="Name">Namnet i mallen, t.ex. <c>id</c>.</param>
/// <param name="Constraint">Villkoret, t.ex. <c>int</c>, <c>guid</c>. Null om inget anges.</param>
/// <param name="Optional">Parametern får utelämnas (<c>{id?}</c>).</param>
public sealed record RouteParameter(string Name, string? Constraint, bool Optional);

/// <param name="Id">Stabilt namn inom vyn, t.ex. <c>Product</c> (modellens namn) eller <c>Form1</c>.</param>
/// <param name="Model">Modellens typnamn, om formuläret är bundet till en modell.</param>
/// <param name="Submit">Knappen som skickar formuläret.</param>
/// <param name="Fields">Fälten i den ordning de står på sidan.</param>
/// <param name="SubmitsTo">Backend-anropet formuläret gör, om det är känt i koden.</param>
/// <param name="OperationId">Specens operation som formuläret skickas till (matchad på metod och sökväg). Utelämnad om ingen matchar.</param>
public sealed record Form(string Id, string? Model, Locator Submit, IReadOnlyList<Field> Fields, ApiCall? SubmitsTo, string? OperationId = null);

/// <param name="Name">Fältets namn, t.ex. modellens property <c>Name</c> eller HTML-attributet <c>name</c>.</param>
/// <param name="Label">Etiketten som användaren ser, om den är känd.</param>
/// <param name="Locator">Hur fältet hittas på sidan.</param>
/// <param name="Rules">Formulärets valideringsregler, sorterade på sort.</param>
/// <param name="Options">Valen i en select eller radiogrupp, annars tomt.</param>
/// <param name="SpecField">Fältet i operationens request-schema som detta fält motsvarar. Utelämnat om formuläret saknar operation eller inget fält matchar.</param>
public sealed record Field(string Name, string? Label, FieldType Type, Locator Locator, IReadOnlyList<Rule> Rules, IReadOnlyList<string> Options, string? SpecField = null);

public enum FieldType { Text, TextArea, Email, Password, Number, Date, DateTime, Tel, Url, Select, Checkbox, Radio, Hidden }

/// <param name="Value">Längden för min/max-length, gränsen för min/max, mönstret för pattern, det andra fältets namn för compare. Null för required, email och url.</param>
/// <param name="Message">Felmeddelandet, om det står i koden.</param>
public sealed record Rule(RuleKind Kind, string? Value = null, string? Message = null);

public enum RuleKind { Required, MinLength, MaxLength, Min, Max, Pattern, Email, Url, Compare }

/// <summary>Hur ett element hittas: i första hand det användaren ser (etikett, roll och namn), sedan test-id, sist CSS.</summary>
/// <param name="Value">Etiketten, rollen (t.ex. <c>button</c>), test-id:t eller CSS-väljaren.</param>
/// <param name="Name">Med <see cref="LocatorKind.Role"/>: elementets tillgängliga namn, t.ex. knappens text.</param>
public sealed record Locator(LocatorKind Kind, string Value, string? Name = null);

public enum LocatorKind { Label, Role, TestId, Css }

/// <param name="Href">Adressen som den står; det dynamiska skrivs <c>{}</c>, t.ex. <c>/products/{}</c>.</param>
public sealed record Link(string Href, string? Text);

/// <param name="Method">HTTP-metoden med versaler.</param>
/// <param name="Path">Sökvägen med parametrar som <c>{}</c>.</param>
public sealed record ApiCall(string Method, string Path);

/// <summary>Namnet på en sorts vy, som i <c>views.json</c> (t.ex. <c>razor-page</c>).</summary>
public static class ViewKindNames
{
    public static string Name(this ViewKind kind) => System.Text.Json.JsonNamingPolicy.KebabCaseLower.ConvertName(kind.ToString());
}
