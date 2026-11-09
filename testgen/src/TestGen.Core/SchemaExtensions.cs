using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.OpenApi;

namespace TestGen.Core;

/// <summary>Microsoft.OpenApi 2.x har nullbara listor och gränsvärden som text. Här görs de till vanliga värden.</summary>
public static class SchemaExtensions
{
    public static IDictionary<string, IOpenApiSchema> Props(this IOpenApiSchema s) => s.Properties ?? new Dictionary<string, IOpenApiSchema>();

    public static IList<IOpenApiSchema> AllOfs(this IOpenApiSchema s) => s.AllOf ?? [];

    public static IList<IOpenApiSchema> OneOfs(this IOpenApiSchema s) => s.OneOf ?? [];

    public static IList<IOpenApiSchema> AnyOfs(this IOpenApiSchema s) => s.AnyOf ?? [];

    public static ISet<string> RequiredNames(this IOpenApiSchema s) => s.Required ?? new HashSet<string>();

    /// <summary>Värdena i <c>enum</c>. JSON-null (som Microsoft.OpenApi skriver som ett särskilt textvärde) är inget värde att skicka och ingår inte.</summary>
    public static IList<JsonNode> EnumValues(this IOpenApiSchema s) => (s.Enum ?? []).Where(n => !JsonNullSentinel.IsJsonNullSentinel(n)).ToList();

    /// <summary>Exempel ur <c>example</c> (3.0) eller första ur <c>examples</c>; null om det saknas.</summary>
    public static JsonNode? ExampleNode(this IOpenApiSchema s)
    {
#pragma warning disable CS0618 // Example är föråldrad men fylls i för 3.0-specar
        return s.Example ?? s.Examples?.FirstOrDefault();
#pragma warning restore CS0618
    }

    /// <summary>Id i components om schemat är en referens, annars null.</summary>
    public static string? RefId(this IOpenApiSchema s) => (s as OpenApiSchemaReference)?.Reference?.Id;

    /// <summary>Typens namn utan null-flaggan, se <see cref="SchemaUtil.NameOf"/>.</summary>
    public static string? TypeName(this IOpenApiSchema s) => SchemaUtil.NameOf(s.Type);

    /// <summary>Nedre gräns och om den är exklusiv. Läsaren gör 3.0 (minimum + exclusiveMinimum: true) till samma form som 3.1 (exclusiveMinimum: tal).</summary>
    public static (decimal Value, bool Exclusive)? Lower(this IOpenApiSchema s) =>
        Number(s.ExclusiveMinimum) is { } ex ? (ex, true) : Number(s.Minimum) is { } v ? (v, false) : null;

    /// <summary>Övre gräns och om den är exklusiv.</summary>
    public static (decimal Value, bool Exclusive)? Upper(this IOpenApiSchema s) =>
        Number(s.ExclusiveMaximum) is { } ex ? (ex, true) : Number(s.Maximum) is { } v ? (v, false) : null;

    private static decimal? Number(string? text) =>
        decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
}
