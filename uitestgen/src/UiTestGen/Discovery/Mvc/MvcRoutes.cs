using System.Text;
using System.Text.RegularExpressions;

namespace UiTestGen.Discovery.Mvc;

/// <summary>Ruttmallar för MVC: attributrutter som ASP.NET slår ihop dem, konventionsrutten och länkar via tag helpers.</summary>
internal static partial class MvcRoutes
{
    public const string DefaultPattern = "{controller=Home}/{action=Index}/{id?}";

    /// <summary>
    /// Slår ihop klassens och actionens mallar. <c>[controller]</c>, <c>[action]</c> och <c>[area]</c> ersätts med namnen som de står i koden,
    /// och en mall som börjar med <c>/</c> eller <c>~/</c> ersätter klassens. Parametrarna lämnas som de står (<c>{id:int}</c>).
    /// </summary>
    public static string Combine(string? prefix, string? template, string controller, string action, string? area)
    {
        string path;
        if (template is not null && (template.StartsWith('/') || template.StartsWith("~/", StringComparison.Ordinal)))
            path = "/" + template.TrimStart('~').Trim('/');
        else
            path = "/" + string.Join('/', new[] { prefix, template }.Where(s => !string.IsNullOrEmpty(s)).Select(s => s!.Trim('/')));
        path = path.Replace("[controller]", controller, StringComparison.OrdinalIgnoreCase)
            .Replace("[action]", action, StringComparison.OrdinalIgnoreCase)
            .Replace("[area]", area ?? "", StringComparison.OrdinalIgnoreCase);
        path = Slashes().Replace(path, "/");
        return path.Length > 1 ? path.TrimEnd('/') : path;
    }

    /// <summary>
    /// Konventionsrutten <c>{controller=Home}/{action=Index}/{id?}</c>: Home/Index är <c>/</c>, Index är <c>/Controller</c>, annars <c>/Controller/Action</c>.
    /// Med <paramref name="id"/> behövs actionen i adressen (annars skulle id:t tolkas som action), så Index med id blir <c>/Controller/Index/{id?}</c>.
    /// </summary>
    public static string Conventional(string? area, string controller, string action, string? id)
    {
        var prefix = area is null ? "" : "/" + area;
        if (id is null && action.Equals("Index", StringComparison.OrdinalIgnoreCase))
            return area is null && controller.Equals("Home", StringComparison.OrdinalIgnoreCase) ? "/" : $"{prefix}/{controller}";
        return $"{prefix}/{controller}/{action}" + (id is null ? "" : "/" + id);
    }

    /// <summary>
    /// Adressen för en länk med <c>asp-controller</c>/<c>asp-action</c>. Är målet en hittad action används dess rutt: parametrar som får ett
    /// <c>asp-route-x</c> blir <c>{}</c>, valfria utan värde utelämnas. Annars gäller konventionsrutten.
    /// </summary>
    public static string ForLink(string? knownRoute, string? area, string controller, string action, ISet<string> routeValues) =>
        knownRoute is null
            ? Conventional(area, controller, action, routeValues.Contains("id") ? "{}" : null)
            : FillParameters(knownRoute, routeValues);

    /// <summary>
    /// Rutten som en länk till den ser ut: parametrar som får ett <c>asp-route-x</c> blir <c>{}</c>, valfria utan värde utelämnas,
    /// och andra delar av mönstret som innehåller parametrar blir <c>{}</c>.
    /// </summary>
    public static string FillParameters(string route, ISet<string> routeValues)
    {
        var segments = new List<string>();
        foreach (var segment in route.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (WholeParameter().Match(segment) is { Success: true } m)
            {
                var optional = m.Groups["rest"].Value.EndsWith('?');
                if (!optional || routeValues.Contains(m.Groups["name"].Value.ToLowerInvariant())) segments.Add("{}");
            }
            else segments.Add(AnyParameter().Replace(segment, "{}"));
        }
        return "/" + string.Join('/', segments);
    }

    /// <summary>Mönstret utan blanktecken, för att jämföra med standardmönstret.</summary>
    public static string Squash(string pattern)
    {
        var sb = new StringBuilder();
        foreach (var c in pattern) if (!char.IsWhiteSpace(c)) sb.Append(c);
        return sb.ToString();
    }

    [GeneratedRegex("/{2,}")]
    private static partial Regex Slashes();

    [GeneratedRegex(@"^\{\*{0,2}(?<name>[^}:=?]+)(?<rest>[^}]*)\}$")]
    private static partial Regex WholeParameter();

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex AnyParameter();
}
