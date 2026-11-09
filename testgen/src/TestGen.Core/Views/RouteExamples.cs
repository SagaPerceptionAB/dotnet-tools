namespace TestGen.Core.Views;

/// <summary>Exempelvärden för ruttparametrar. Samma värden överallt, så att testerna öppnar samma adresser.</summary>
public static class RouteExamples
{
    public const string ExampleGuid = "00000000-0000-0000-0000-000000000001";

    /// <summary><c>int</c> och <c>long</c> blir 1, <c>guid</c> en fast Guid, <c>bool</c> true, allt annat <c>exempel</c>.</summary>
    public static string Value(RouteParameter p) => RouteTemplate.KindOf(p) switch
    {
        ParameterKind.Int or ParameterKind.Long => "1",
        ParameterKind.Guid => ExampleGuid,
        ParameterKind.Bool => "true",
        _ => "exempel",
    };

    /// <summary>
    /// Adressen för en rutt med exempelvärden i stället för parametrarna, de valfria med. Börjar alltid med <c>/</c>.
    /// </summary>
    public static string Path(string route) => RouteTemplate.Parse(route).ExamplePath();
}
