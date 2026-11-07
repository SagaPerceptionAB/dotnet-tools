using System.Text.RegularExpressions;

namespace SystemMap;

/// <summary>Route-mallar från controllers: hopslagning som ASP.NET gör och jämförelse med specens sökvägar.</summary>
internal static partial class RouteTemplate
{
    /// <summary>
    /// Slår ihop klassens och actionens mallar. <c>[controller]</c> och <c>[action]</c> ersätts, en mall som börjar med
    /// <c>/</c> eller <c>~/</c> ersätter klassens, och parametrar skrivs utan villkor: <c>{id:int}</c>, <c>{id?}</c> och
    /// <c>{*rest}</c> blir <c>{id}</c> och <c>{rest}</c>.
    /// </summary>
    public static string Combine(string? prefix, string? template, string controller, string action)
    {
        string path;
        if (template is not null && (template.StartsWith('/') || template.StartsWith("~/")))
            path = template.TrimStart('~');
        else
            path = "/" + string.Join('/', new[] { prefix, template }.Where(s => !string.IsNullOrEmpty(s)).Select(s => s!.Trim('/', '~')));
        path = path.Replace("[controller]", controller, StringComparison.OrdinalIgnoreCase)
                   .Replace("[action]", action, StringComparison.OrdinalIgnoreCase);
        path = Parameter().Replace(path, "{$1}");
        path = Slashes().Replace(path, "/");
        return path.Length > 1 ? path.TrimEnd('/') : path;
    }

    /// <summary>Nyckel för att jämföra sökvägar: gemener, utan avslutande snedstreck och <c>{}</c> för varje parameter.</summary>
    public static string Key(string path) => AnyParameter().Replace(path.ToLowerInvariant().TrimEnd('/'), "{}");

    [GeneratedRegex(@"\{\*{0,2}([^}:=?]+)[^}]*\}")]
    private static partial Regex Parameter();

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex AnyParameter();

    [GeneratedRegex("/{2,}")]
    private static partial Regex Slashes();
}
