namespace TestGen.Core.Run;

/// <summary>
/// Inställningar för en körning efter att kommandoraden och ev. miljö (bladet Miljöer) slagits ihop.
/// Företräde: kommandoraden vinner över miljön. Alla ${env:...} är ersatta.
/// </summary>
public sealed class RunSettings
{
    public required string Api { get; init; }
    /// <summary>Globala headers (skiftlägesokänsliga namn).</summary>
    public required IReadOnlyDictionary<string, string> Headers { get; init; }
    public string? Token { get; init; }
    public int TimeoutSeconds { get; init; } = DefaultTimeoutSeconds;
    public string? EnvName { get; init; }
    /// <summary>Headers vars värde kom från ${env:...}; maskas i utskrifter.</summary>
    public IReadOnlySet<string> SecretHeaderNames { get; init; } = new HashSet<string>();
    /// <summary>Varningar som gäller den valda miljön (t.ex. token i klartext). Stoppar inte körningen.</summary>
    public IReadOnlyList<ExcelIssue> Warnings { get; init; } = Array.Empty<ExcelIssue>();

    public const int DefaultTimeoutSeconds = 30;

    public static RunSettings Resolve(string? api, IEnumerable<string> headerArgs, string? token, string? timeout,
        string? excel, string? envName, Func<string, string?>? getEnv = null)
    {
        EnvironmentConfig? env = null;
        var warnings = new List<ExcelIssue>();
        var valueCol = 3;
        if (envName is not null)
        {
            if (excel is null) throw new ArgumentException("--env kräver --excel <fil> (miljöerna finns i bladet Miljöer i Excel-filen).");
            (env, warnings, valueCol) = EnvironmentSheet.FindWithWarnings(excel, envName);
        }
        // Fel i värden som står i Excel samlas och visas med cell (Miljöer!C2 (Värde)); fel i flaggor kastas direkt.
        var problems = new List<ExcelIssue>();
        void Problem(string key, string message) =>
            problems.Add(new ExcelIssue(EnvironmentSheet.SheetName, env!.Rows.GetValueOrDefault(key, 1), valueCol, "Värde", message));
        var from = env is null ? "" : $" i miljön {env.Name}";

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var secret = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string raw, string where)
        {
            var (name, value) = ParseHeader(raw, where);
            // Skalet gör en saknad hemlighet ("$TOKEN" utan värde) till tom text: det ska inte bli ett anrop utan inloggning.
            if (where == "--header" && value.Length == 0)
                throw new UsageException($"--header \"{name}: \" har inget värde – saknas hemligheten i CI? Använd hellre --header '{name}: ${{env:{VarName(name)}}}' så får du ett tydligt fel om variabeln saknas.",
                    "Sätt hemligheten (t.ex. som en hemlighet i CI-systemet) och kör igen. Vill du verkligen skicka en tom header: skriv den i bladet Miljöer.");
            var expanded = EnvVars.Expand(value, where, getEnv);
            headers[name] = expanded;
            if (expanded != value) secret.Add(name); else secret.Remove(name);
        }
        var hi = 0;
        foreach (var h in env?.Headers ?? new())
        {
            var key = $"header#{hi++}";
            if (!TryParseHeader(h, out _, out _)) { Problem(key, HeaderFormat(h)); continue; }
            Add(h, $"header{from}");
        }
        foreach (var h in headerArgs) Add(h, "--header");   // kommandoraden vinner över miljön

        var apiRaw = Pick(api, "--api", env?.Api, $"api{from}");
        if (apiRaw.Text is null)
            throw new ArgumentException("API:ets adress saknas. Ange --api <bas-url>" + (env is null ? " eller välj en miljö med --env." : $" eller sätt api för miljön {env.Name} i bladet {EnvironmentSheet.SheetName}."));
        var apiUrl = EnvVars.Expand(apiRaw.Text, apiRaw.Where, getEnv).Trim();
        if (!Uri.TryCreate(apiUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            if (api is null && env is not null) Problem("api", $"API-adressen '{apiUrl}' är inte en giltig http/https-adress, t.ex. http://localhost:5080.");
            else throw new ArgumentException($"API-adressen '{apiUrl}' ({apiRaw.Where}) är inte en giltig http/https-adress, t.ex. http://localhost:5080.");
        }

        if (token is not null && token.Trim().Length == 0)
            throw new UsageException("--token är tom – saknas hemligheten i CI? Använd hellre --token '${env:TOKEN}' så får du ett tydligt fel om variabeln saknas.",
                "Sätt hemligheten (t.ex. som en hemlig variabel i CI-systemet) och kör igen. Behövs ingen inloggning: ta bort --token.");
        var tokenRaw = Pick(token, "--token", env?.Token, $"token{from}");
        var tokenValue = tokenRaw.Text is null ? null : EnvVars.Expand(tokenRaw.Text, tokenRaw.Where, getEnv).Trim();

        var timeoutRaw = Pick(timeout, "--timeout", env?.Timeout, $"timeout{from}");
        var seconds = DefaultTimeoutSeconds;
        if (timeoutRaw.Text is not null && !(int.TryParse(timeoutRaw.Text.Trim(), out seconds) && seconds > 0))
        {
            seconds = DefaultTimeoutSeconds;
            if (timeout is null && env is not null) Problem("timeout", $"timeout måste vara ett positivt antal sekunder, inte '{timeoutRaw.Text}'.");
            else throw new ArgumentException($"{timeoutRaw.Where}: timeout måste vara ett positivt antal sekunder, inte '{timeoutRaw.Text}'.");
        }
        if (problems.Count > 0) throw new ExcelValidationException(excel!, problems, warnings);

        return new RunSettings
        {
            Api = apiUrl, Headers = headers, Token = tokenValue, TimeoutSeconds = seconds, EnvName = env?.Name, SecretHeaderNames = secret, Warnings = warnings,
        };
    }

    /// <summary>Kommandoradens värde om det finns, annars miljöns. Where används i felmeddelanden.</summary>
    private static (string? Text, string Where) Pick(string? cli, string cliWhere, string? env, string envWhere) =>
        cli is not null ? (cli, cliWhere) : (env, envWhere);

    /// <summary>Tolkar "Namn: värde".</summary>
    public static (string Name, string Value) ParseHeader(string raw, string where)
    {
        if (!TryParseHeader(raw, out var name, out var value)) throw new ArgumentException($"{where}: {HeaderFormat(raw)}");
        return (name, value);
    }

    private static bool TryParseHeader(string raw, out string name, out string value)
    {
        var i = raw.IndexOf(':');
        name = i > 0 ? raw[..i].Trim() : "";
        value = i >= 0 ? raw[(i + 1)..].Trim() : "";
        return name.Length > 0 && !name.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));
    }

    private static string VarName(string headerName) =>
        new(headerName.ToUpperInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray());

    private static string HeaderFormat(string raw) => $"kan inte tolka header \"{raw}\". Skriv \"Namn: värde\", t.ex. \"X-Api-Key: abc\".";
}
