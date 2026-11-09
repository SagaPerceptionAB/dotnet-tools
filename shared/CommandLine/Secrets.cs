namespace CommandLine;

/// <summary>Vad som är en hemlighet, och kommandoraden med hemligheterna maskade.</summary>
public static class Secrets
{
    public const string Hidden = "***";

    /// <summary>Ord i ett namn som visar att värdet är en hemlighet (skiftlägesokänsligt).</summary>
    private static readonly string[] SecretWords = ["key", "token", "secret", "auth", "pass", "cookie", "signature", "credential", "session"];

    /// <summary>Ord i en flagga vars värde maskas: <c>--token</c>, <c>--api-key</c>, <c>--client-secret</c>, <c>--password</c>.</summary>
    private static readonly string[] SecretFlagWords = ["token", "secret", "password", "key"];

    /// <summary>Sant om namnet tyder på en hemlighet, t.ex. <c>X-Api-Key</c>, <c>X-Auth-Token</c> eller <c>Session-Id</c>.</summary>
    public static bool LooksSecret(string name) =>
        SecretWords.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Kommandoraden med hemligheterna maskade: värdet efter <c>--token</c> och efter varje flagga vars namn innehåller
    /// token, secret, password eller key, samt värdet i <c>--header "Namn: värde"</c> när namnet ser hemligt ut.
    /// </summary>
    public static string RedactCommandLine(IReadOnlyList<string> args)
    {
        var shown = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                var eq = a.IndexOf('=');
                var name = eq > 0 ? a[2..eq] : a[2..];
                var inline = eq > 0;
                if (name == "header")
                {
                    if (inline) shown.Add("--header=" + Quote(RedactHeader(a[(eq + 1)..])));
                    else
                    {
                        shown.Add(a);
                        if (i + 1 < args.Count) shown.Add(Quote(RedactHeader(args[++i])));
                    }
                    continue;
                }
                if (SecretFlagWords.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase)))
                {
                    if (inline) shown.Add($"--{name}={Hidden}");
                    else
                    {
                        shown.Add(a);
                        if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) { shown.Add(Hidden); i++; }
                    }
                    continue;
                }
            }
            shown.Add(Quote(a));
        }
        return string.Join(' ', shown);
    }

    private static string RedactHeader(string header)
    {
        var colon = header.IndexOf(':');
        return colon > 0 && LooksSecret(header[..colon]) ? header[..colon] + ": " + Hidden : header;
    }

    private static string Quote(string s) => s.Length > 0 && !s.Any(c => char.IsWhiteSpace(c) || c == '"') ? s : "\"" + s.Replace("\"", "\\\"") + "\"";
}
