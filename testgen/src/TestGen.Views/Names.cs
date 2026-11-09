using System.Text;
using System.Text.RegularExpressions;
using TestGen.Core.Views;

namespace TestGen.Views;

/// <summary>Namn: ruttparametrar, PascalCase och unika namn.</summary>
internal static partial class Names
{
    /// <summary><c>product-list</c>, <c>product_list</c> och <c>productList</c> blir <c>ProductList</c>.</summary>
    public static string Pascal(string text)
    {
        var ascii = text.Replace('å', 'a').Replace('ä', 'a').Replace('ö', 'o').Replace('Å', 'A').Replace('Ä', 'A').Replace('Ö', 'O');
        var sb = new StringBuilder();
        foreach (var word in NonWord().Split(ascii).Where(w => w.Length > 0))
            sb.Append(char.ToUpperInvariant(word[0])).Append(word.AsSpan(1));
        return sb.ToString();
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

    [GeneratedRegex(@"[^A-Za-z0-9]+")]
    private static partial Regex NonWord();
}
