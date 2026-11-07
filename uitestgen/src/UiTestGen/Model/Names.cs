using System.Text;
using System.Text.RegularExpressions;

namespace UiTestGen.Model;

/// <summary>Namn och rutter: id:n till klassnamn och parametrar ur ruttmallar.</summary>
internal static partial class Names
{
    /// <summary>
    /// Ett id i PascalCase ur en rutt: <c>/</c> blir <c>Home</c>, <c>/products/{id:int}/edit</c> blir <c>ProductsIdEdit</c>.
    /// Bara bokstäver och siffror (å, ä, ö blir a, a, o) och aldrig en siffra först.
    /// </summary>
    public static string IdFromRoute(string route)
    {
        var parts = route.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => ParameterName().Match(p) is { Success: true } m ? (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) : p)
            .Select(Pascal).Where(p => p.Length > 0).ToList();
        var id = parts.Count == 0 ? "Home" : string.Concat(parts);
        return char.IsDigit(id[0]) ? "View" + id : id;
    }

    /// <summary><c>product-list</c>, <c>product_list</c> och <c>productList</c> blir <c>ProductList</c>.</summary>
    public static string Pascal(string text)
    {
        var ascii = text.Replace('å', 'a').Replace('ä', 'a').Replace('ö', 'o').Replace('Å', 'A').Replace('Ä', 'A').Replace('Ö', 'O');
        var sb = new StringBuilder();
        foreach (var word in NonWord().Split(ascii).Where(w => w.Length > 0))
            sb.Append(char.ToUpperInvariant(word[0])).Append(word.AsSpan(1));
        return sb.ToString();
    }

    /// <summary>camelCase ur ett namn: <c>product-list</c> blir <c>productList</c>. Tom sträng om namnet saknar bokstäver och siffror.</summary>
    public static string Camel(string text)
    {
        var pascal = Pascal(text);
        return pascal.Length == 0 ? "" : char.ToLowerInvariant(pascal[0]) + pascal[1..];
    }

    /// <summary>
    /// Ett namn som inte finns i <paramref name="used"/>, och som läggs till där. Är det taget läggs ett löpnummer (2, 3 ...) till
    /// efter <paramref name="separator"/>: <c>Name</c>, <c>Name2</c>, <c>Name3</c>.
    /// </summary>
    public static string Unique(HashSet<string> used, string wanted, string separator = "")
    {
        var name = wanted;
        for (var n = 2; !used.Add(name); n++) name = wanted + separator + n;
        return name;
    }

    /// <summary>Parametrarna i en ruttmall: <c>{id:int}</c>, <c>{slug?}</c>, <c>{*rest}</c>, och SPA-formen <c>:id</c>.</summary>
    public static IReadOnlyList<RouteParameter> Parameters(string route) => RouteTemplate.Parse(route).Parameters;

    [GeneratedRegex(@"^\{\*{0,2}([A-Za-z_][A-Za-z0-9_]*)|^:([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex ParameterName();

    [GeneratedRegex(@"[^A-Za-z0-9]+")]
    private static partial Regex NonWord();
}
