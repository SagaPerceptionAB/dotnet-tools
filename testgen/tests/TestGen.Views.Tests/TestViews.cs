using System.Text.RegularExpressions;
using TestGen.Core.Views;

namespace TestGen.Views.Tests;

/// <summary>Ett kort namn för en vy att slå upp den med i testerna: <c>/</c> är Home, <c>/admin/users</c> är AdminUsers.</summary>
internal static partial class TestViews
{
    public static string Name(View view)
    {
        var parts = view.Route.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => Parameter().Match(p) is { Success: true } m ? (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) : p)
            .Select(Names.Pascal).Where(p => p.Length > 0).ToList();
        return parts.Count == 0 ? "Home" : string.Concat(parts);
    }

    [GeneratedRegex(@"^\{\*{0,2}([A-Za-z_][A-Za-z0-9_]*)|^:([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex Parameter();
}
