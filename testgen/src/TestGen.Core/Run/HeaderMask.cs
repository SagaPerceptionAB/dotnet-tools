using System.Text.RegularExpressions;

namespace TestGen.Core.Run;

/// <summary>
/// Avgör vilka header-värden som är hemliga och maskar dem som <c>***</c> i allt som skrivs ut.
/// Alltid hemliga: Authorization, Proxy-Authorization, Cookie, Set-Cookie samt alla namn som <see cref="LooksSecret"/> (key, token, secret, auth, pass, cookie, signature, credential, session). Dessutom namn som anges vid skapandet
/// (headers från apiKey-scheman, den header --token sätter, headers vars värde kommer från ${env:...}).
/// Anropas med en vanlig header-dictionary: <c>mask.Mask(headers)</c>, <c>mask.Format(headers)</c> eller
/// <c>mask.Value(namn, värde)</c>. <see cref="Scrub"/> döljer kända hemliga värden i fri text (t.ex. ett svar som ekar token).
/// </summary>
public sealed class HeaderMask
{
    public const string Hidden = "***";

    private static readonly string[] AlwaysSecret = { "Authorization", "Proxy-Authorization", "Cookie", "Set-Cookie" };
    private readonly HashSet<string> _names = new(AlwaysSecret, StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _values = new();
    private readonly List<string> _warnings = new();

    /// <summary>Hemliga värden kortare än så här kan inte döljas säkert i fri text (se <see cref="Scrub"/>); en varning skapas.</summary>
    public const int SafeLength = 8;

    /// <summary>Varningar om hemligheter som är för korta (högst <see cref="SafeLength"/> - 1 tecken). Innehåller aldrig själva värdet.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Bara de alltid hemliga namnen.</summary>
    public static HeaderMask Default => new();

    public HeaderMask(IEnumerable<string>? secretNames = null, IEnumerable<string>? secretValues = null)
    {
        if (secretNames is not null) foreach (var n in secretNames) _names.Add(n);
        if (secretValues is not null) foreach (var v in secretValues) AddValue(v, "--token eller ett angivet hemligt värde");
    }

    /// <summary>Som konstruktorn, men värdena på de headers i <paramref name="headers"/> som är hemliga blir också kända värden.</summary>
    public static HeaderMask For(IEnumerable<KeyValuePair<string, string>> headers, IEnumerable<string>? secretNames = null,
        IEnumerable<string>? secretValues = null)
    {
        var mask = new HeaderMask(secretNames, secretValues);
        foreach (var (k, v) in headers)
            if (mask.IsSecret(k)) mask.AddValue(v, $"headern {k}");
        return mask;
    }

    /// <summary>Ord i ett headernamn som visar att värdet är en hemlighet (skiftlägesokänsligt).</summary>
    private static readonly string[] SecretWords = { "key", "token", "secret", "auth", "pass", "cookie", "signature", "credential", "session" };

    /// <summary>Sant om namnet tyder på en hemlighet, t.ex. <c>X-Api-Key</c>, <c>X-Auth-Token</c> eller <c>Session-Id</c>. Gäller även namn ingen angett som hemliga.</summary>
    public static bool LooksSecret(string headerName) =>
        SecretWords.Any(w => headerName.Contains(w, StringComparison.OrdinalIgnoreCase));

    public bool IsSecret(string headerName) => _names.Contains(headerName.Trim()) || LooksSecret(headerName);

    /// <summary>Inloggningsscheman som kan stå först i Authorization-värdet. Själva ordet är inte hemligt och behålls vid maskning.</summary>
    public static readonly string[] KnownSchemes =
        { "Bearer", "Basic", "Token", "ApiKey", "Api-Key", "Digest", "Negotiate", "NTLM", "OAuth", "Hawk", "JWT", "Mac", "AWS4-HMAC-SHA256" };

    /// <summary>Schemaordet (t.ex. <c>Basic</c>) om <paramref name="value"/> börjar med ett känt schema följt av något, annars null.</summary>
    public static string? SchemeOf(string value)
    {
        var sp = value.IndexOf(' ');
        if (sp <= 0 || value[(sp + 1)..].Trim().Length == 0) return null;
        var word = value[..sp];
        return KnownSchemes.FirstOrDefault(s => s.Equals(word, StringComparison.OrdinalIgnoreCase)) is not null ? word : null;
    }

    /// <summary>
    /// Värdet, eller <c>***</c> om headern är hemlig. För Authorization/Proxy-Authorization behålls ett känt schemaord:
    /// <c>Basic dXNlcjpwdw==</c> blir <c>Basic ***</c>, så att man ser vilken sorts inloggning som skickades.
    /// </summary>
    public string Value(string headerName, string value)
    {
        if (!IsSecret(headerName)) return value;
        var name = headerName.Trim();
        return name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
            ? (SchemeOf(value) is { } scheme ? scheme + " " + Hidden : Hidden)
            : Hidden;
    }

    public Dictionary<string, string> Mask(IEnumerable<KeyValuePair<string, string>> headers) =>
        headers.ToDictionary(kv => kv.Key, kv => Value(kv.Key, kv.Value), StringComparer.OrdinalIgnoreCase);

    /// <summary>"Namn: värde, Namn2: ***" - klart att skriva ut.</summary>
    public string Format(IEnumerable<KeyValuePair<string, string>> headers) =>
        string.Join(", ", headers.Select(kv => $"{kv.Key}: {Value(kv.Key, kv.Value)}"));

    /// <summary>
    /// Byter ut kända hemliga värden i en text (URL, body, curl, svar, förklaringar) mot <c>***</c>.
    /// Värden på minst <see cref="SafeLength"/> tecken döljs var de än förekommer. Kortare värden döljs bara som hela förekomster
    /// (inte inuti ett ord), så att en kort hemlighet inte fräter sönder vanliga ord ("saknas" blir inte "sa***nas").
    /// </summary>
    public string Scrub(string text)
    {
        foreach (var v in _values)
        {
            if (v.Length >= SafeLength) text = text.Replace(v, Hidden, StringComparison.Ordinal);
            else if (text.Contains(v, StringComparison.Ordinal))
                text = Regex.Replace(text, $@"(?<![\p{{L}}\p{{N}}_]){Regex.Escape(v)}(?![\p{{L}}\p{{N}}_])", Hidden, RegexOptions.CultureInvariant);
        }
        return text;
    }

    private void AddValue(string value, string label)
    {
        if (string.IsNullOrEmpty(value) || _values.Contains(value)) return;   // redan känt (t.ex. --token som också är en header): en varning räcker
        _values.Add(value);
        var secret = value;
        // "Bearer abc" => dölj även "abc" ensamt.
        if (SchemeOf(value) is { } scheme && value[(scheme.Length + 1)..].Trim() is { Length: > 0 } rest) { _values.Add(rest); secret = rest; }
        if (secret.Length < SafeLength)
            _warnings.Add($"Varning: hemligheten i {label} är bara {secret.Length} tecken. Korta hemligheter kan inte döljas helt i fri text i utskrifter; använd helst minst {SafeLength} tecken.");
        _values.Sort((a, b) => b.Length.CompareTo(a.Length));   // längst först så att delar inte lämnar rester
    }
}
