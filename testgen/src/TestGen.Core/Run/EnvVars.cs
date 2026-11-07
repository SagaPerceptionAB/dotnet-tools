using System.Text.RegularExpressions;

namespace TestGen.Core.Run;

/// <summary>Ersätter <c>${env:NAMN}</c> med miljövariabeln NAMN, så att hemligheter inte behöver stå i Excel eller skalhistoriken.</summary>
public static partial class EnvVars
{
    [GeneratedRegex(@"\$\{env:([^}]*)\}")]
    private static partial Regex Placeholder();

    /// <summary>Namnen på miljövariablerna som nämns i texten.</summary>
    public static IEnumerable<string> Names(string text) => Placeholder().Matches(text).Select(m => m.Groups[1].Value.Trim()).Where(n => n.Length > 0);

    public static bool HasPlaceholder(string text) => Placeholder().IsMatch(text);

    /// <param name="where">Var värdet kommer ifrån (för felmeddelandet), t.ex. "--token".</param>
    public static string Expand(string text, string where, Func<string, string?>? getEnv = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;
        return Placeholder().Replace(text, m =>
        {
            var name = m.Groups[1].Value.Trim();
            if (name.Length == 0) throw new ArgumentException($"{where}: ${{env:}} saknar variabelnamn. Skriv t.ex. ${{env:API_TOKEN}}.");
            var value = getEnv(name);
            if (string.IsNullOrEmpty(value))
                throw new ArgumentException($"Miljövariabeln {name} är inte satt (används i {where}). Sätt den innan du kör, t.ex. export {name}=...");
            return value;
        });
    }
}
