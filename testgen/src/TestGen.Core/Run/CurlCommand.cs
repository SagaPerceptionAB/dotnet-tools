namespace TestGen.Core.Run;

/// <summary>
/// Bygger en curl-rad för POSIX-skal ur ett inspelat anrop. Enkelfnuttar runt allt, ett ' i texten skrivs '\''.
/// Hemliga headervärden är redan <c>***</c> i inspelningen. I curl-raden ersätts de av en skalvariabel
/// (<c>Authorization</c> blir <c>Bearer $TOKEN</c>, eller med det schema som maskningen behöll, t.ex. <c>Basic $TOKEN</c>; andra headers <c>$NAMN_I_VERSALER</c>) så att raden fungerar när
/// testaren satt variabeln med <c>export</c>. Själva värdet skrivs aldrig.
/// </summary>
public static class CurlCommand
{
    public static string Build(Exchange e)
    {
        var parts = new List<string> { "curl" };
        if (e.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)) parts.Add("-I");   // -X HEAD får curl att hänga
        else parts.AddRange(new[] { "-X", e.Method.ToUpperInvariant() });
        parts.Add(Quote(e.Url));
        foreach (var (name, value) in e.Headers)
            parts.AddRange(new[] { "-H", SecretVariable(name, value) is { } v ? $"\"{name}: {v.Prefix}${v.Variable}\"" : Quote($"{name}: {value}") });
        if (e.RequestBody is { Length: > 0 } body) parts.AddRange(new[] { "--data-raw", Quote(body) });
        if (e.TimeoutSeconds > 0) parts.AddRange(new[] { "--max-time", e.TimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        return string.Join(" ", parts);
    }

    /// <summary>Skalvariabeln för en maskad header (värdet är <c>***</c>), annars null.</summary>
    public static (string Prefix, string Variable)? SecretVariable(string headerName, string value)
    {
        if (headerName.Length == 0 || !headerName.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return null;
        // "Basic ***" (schemat behölls av HeaderMask) ger "Basic $TOKEN"; bara "***" ger Bearer för Authorization.
        string? scheme = null;
        if (value != HeaderMask.Hidden)
        {
            var word = value.EndsWith(" " + HeaderMask.Hidden, StringComparison.Ordinal) ? value[..^(HeaderMask.Hidden.Length + 1)] : null;
            if (word is null || HeaderMask.SchemeOf(word + " x") != word) return null;
            scheme = word;
        }
        if (headerName.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) return ((scheme ?? "Bearer") + " ", "TOKEN");
        var name = new string(headerName.Select(c => char.IsAsciiLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_').ToArray());
        return (scheme is null ? "" : scheme + " ", char.IsAsciiDigit(name[0]) ? "_" + name : name);
    }

    /// <summary>Variablerna som måste sättas före raden, t.ex. <c>TOKEN</c> (för Authorization). Tom om inget är maskat.</summary>
    public static List<(string Variable, string Header)> Variables(Exchange e) =>
        e.Headers.Select(h => (v: SecretVariable(h.Key, h.Value), h.Key)).Where(x => x.v is not null)
            .Select(x => (x.v!.Value.Variable, x.Key)).DistinctBy(x => x.Variable).ToList();

    public static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";
}
