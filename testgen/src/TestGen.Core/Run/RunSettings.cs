using TestGen.Core.Settings;

namespace TestGen.Core.Run;

/// <summary>
/// Inställningar för en körning efter att kommandoraden och miljön (testgen.yaml) slagits ihop.
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
    /// <summary>Miljön är skrivskyddad: bara GET, HEAD och OPTIONS skickas.</summary>
    public bool ReadOnly { get; init; }
    /// <summary>Headers vars värde kom från ${env:...}; maskas i utskrifter.</summary>
    public IReadOnlySet<string> SecretHeaderNames { get; init; } = new HashSet<string>();

    public const int DefaultTimeoutSeconds = 30;

    /// <param name="project">API-projektet vars inställningar i miljön gäller, eller null när bara en adress angavs.</param>
    /// <param name="localUrl">Adressen ur launchSettings.json; används bara i miljön local när varken kommandoraden eller miljön anger någon.</param>
    public static RunSettings Resolve(string? api, IEnumerable<string> headerArgs, string? token, string? timeout,
        TestEnvironment env, string? project = null, string? localUrl = null, Func<string, string?>? getEnv = null)
    {
        var endpoint = project is null ? new Endpoint(null, new Dictionary<string, string>(), new HashSet<string>(), null, DefaultTimeoutSeconds) : env.Api(project);

        var headers = new Dictionary<string, string>(endpoint.Headers, StringComparer.OrdinalIgnoreCase);
        var secret = new HashSet<string>(endpoint.SecretHeaderNames, StringComparer.OrdinalIgnoreCase);
        foreach (var raw in headerArgs)   // kommandoraden vinner över miljön
        {
            var (name, value) = ParseHeader(raw, "--header");
            // Skalet gör en saknad hemlighet ("$TOKEN" utan värde) till tom text: det ska inte bli ett anrop utan inloggning.
            if (value.Length == 0)
                throw new UsageException($"--header \"{name}: \" har inget värde – saknas hemligheten i CI? Använd hellre --header '{name}: ${{env:{VarName(name)}}}' så får du ett tydligt fel om variabeln saknas.",
                    "Sätt hemligheten (t.ex. som en hemlighet i CI-systemet) och kör igen.");
            var expanded = EnvVars.Expand(value, "--header", getEnv);
            headers[name] = expanded;
            if (expanded != value) secret.Add(name); else secret.Remove(name);
        }

        var fromEnvironment = endpoint.Url ?? (env.Name.Equals(TestGenSettings.LocalEnvironment, StringComparison.OrdinalIgnoreCase) ? localUrl : null);
        var apiUrl = api is null ? fromEnvironment : EnvVars.Expand(api, "--api", getEnv).Trim();
        var where = api is not null ? "--api" : endpoint.Url is not null ? $"miljön {env.Name}" : "launchSettings.json";
        if (apiUrl is null)
            throw new ArgumentException(project is null
                ? "API:ets adress saknas. Ange --api <adress>."
                : $"API:ets adress saknas för {project} i miljön {env.Name}. Ange --api <adress>, eller sätt environments.{env.Name}.apis.{project}.url i {TestGenSettings.FileName}"
                  + (env.Name.Equals(TestGenSettings.LocalEnvironment, StringComparison.OrdinalIgnoreCase) ? ", eller lägg en Properties/launchSettings.json vid projektet." : "."));
        if (!Uri.TryCreate(apiUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException($"API-adressen '{apiUrl}' ({where}) är inte en giltig http/https-adress, t.ex. http://localhost:5080.");

        if (token is not null && token.Trim().Length == 0)
            throw new UsageException("--token är tom – saknas hemligheten i CI? Använd hellre --token '${env:TOKEN}' så får du ett tydligt fel om variabeln saknas.",
                "Sätt hemligheten (t.ex. som en hemlig variabel i CI-systemet) och kör igen. Behövs ingen inloggning: ta bort --token.");
        var tokenValue = token is null ? endpoint.Token : EnvVars.Expand(token, "--token", getEnv).Trim();

        var seconds = endpoint.TimeoutSeconds;
        if (timeout is not null && !(int.TryParse(timeout.Trim(), out seconds) && seconds > 0))
            throw new ArgumentException($"--timeout måste vara ett positivt antal sekunder, inte '{timeout}'.");

        return new RunSettings
        {
            Api = apiUrl,
            Headers = headers,
            Token = tokenValue,
            TimeoutSeconds = seconds,
            EnvName = env.Name,
            ReadOnly = env.ReadOnly,
            SecretHeaderNames = secret,
        };
    }

    /// <summary>Tolkar "Namn: värde".</summary>
    private static (string Name, string Value) ParseHeader(string raw, string where)
    {
        var i = raw.IndexOf(':');
        var name = i > 0 ? raw[..i].Trim() : "";
        var value = i >= 0 ? raw[(i + 1)..].Trim() : "";
        if (name.Length == 0 || name.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new ArgumentException($"{where}: kan inte tolka header \"{raw}\". Skriv \"Namn: värde\", t.ex. \"X-Api-Key: abc\".");
        return (name, value);
    }

    private static string VarName(string headerName) =>
        new(headerName.ToUpperInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray());
}
