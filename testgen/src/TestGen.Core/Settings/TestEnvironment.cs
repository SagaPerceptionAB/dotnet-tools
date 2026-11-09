using TestGen.Core.Run;

namespace TestGen.Core.Settings;

/// <summary>Vad som görs med ett anrop som appen under test gör till ett annat system.</summary>
public enum HostAction
{
    /// <summary>Anropet stoppas med ett tydligt fel (standard).</summary>
    Block,

    /// <summary>Anropet går till det riktiga systemet.</summary>
    Allow,

    /// <summary>Anropet besvaras av en mock.</summary>
    Mock,
}

/// <summary>Värdpolicyn ur <c>hosts</c> i testgen.yaml: en standard och undantag per värdnamn.</summary>
public sealed class HostPolicy
{
    public const string DefaultKey = "default";

    private readonly Dictionary<string, HostAction> _hosts;

    private HostPolicy(HostAction defaultAction, Dictionary<string, HostAction> hosts)
    {
        Default = defaultAction;
        _hosts = hosts;
    }

    public HostAction Default { get; }

    /// <summary>Värdarna som har en egen regel.</summary>
    public IReadOnlyDictionary<string, HostAction> Hosts => _hosts;

    /// <summary>Policyn för värden (skiftlägesokänsligt, bara värdnamnet): den egna regeln, annars standard.</summary>
    public HostAction For(string host) => _hosts.GetValueOrDefault(host.Trim().ToLowerInvariant(), Default);

    /// <summary>Tolkar ett värde ur filen: block, allow eller mock. Null om det är något annat.</summary>
    public static HostAction? Parse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "block" => HostAction.Block,
        "allow" => HostAction.Allow,
        "mock" => HostAction.Mock,
        _ => null,
    };

    internal static HostPolicy From(Dictionary<string, string>? hosts)
    {
        var defaultAction = HostAction.Block;
        var perHost = new Dictionary<string, HostAction>(StringComparer.OrdinalIgnoreCase);
        foreach (var (host, text) in hosts ?? [])
        {
            if (Parse(text) is not { } action) continue;
            if (host.Equals(DefaultKey, StringComparison.OrdinalIgnoreCase)) defaultAction = action;
            else perHost[host.Trim().ToLowerInvariant()] = action;
        }
        return new HostPolicy(defaultAction, perHost);
    }
}

/// <summary>Ett projekts inställningar i en miljö. Hemligheterna är redan lästa ur miljövariablerna.</summary>
/// <param name="Url">Adressen, eller null när miljön inte anger någon (lokalt läses den då ur launchSettings.json).</param>
/// <param name="SecretHeaderNames">Headers vars värde kom från <c>${env:NAMN}</c>; de maskas i utskrifter.</param>
public sealed record Endpoint(string? Url, IReadOnlyDictionary<string, string> Headers, IReadOnlySet<string> SecretHeaderNames, string? Token, int TimeoutSeconds);

/// <summary>
/// En vald miljö: skrivskydd och per projekt (API eller app) adress, headers, token och tidsgräns.
/// Miljövariablerna för ett projekt läses först när projektet frågas efter, så att en saknad variabel för ett projekt inte stoppar de andra.
/// </summary>
public sealed class TestEnvironment
{
    private readonly EnvironmentSettings _settings;
    private readonly Func<string, string?> _getEnv;

    internal TestEnvironment(string name, EnvironmentSettings settings, Func<string, string?> getEnv)
    {
        Name = name;
        _settings = settings;
        _getEnv = getEnv;
    }

    public string Name { get; }

    /// <summary>Bara läsande anrop (GET, HEAD, OPTIONS) får skickas.</summary>
    public bool ReadOnly => _settings.ReadOnly;

    /// <summary>Inställningarna för API-projektet. Projekt som miljön inte nämner får tomma inställningar.</summary>
    public Endpoint Api(string project) => Resolve("apis", project, _settings.Apis.Named(project));

    /// <summary>Inställningarna för webbappen.</summary>
    public Endpoint App(string project) => Resolve("apps", project, _settings.Apps.Named(project));

    /// <summary>Projektnamnen under apis som miljön nämner.</summary>
    public IReadOnlyList<string> ApiNames => _settings.Apis?.Keys.ToList() ?? [];

    private Endpoint Resolve(string section, string project, EndpointSettings? settings)
    {
        var where = $"miljön {Name}, {section}.{project}";
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var secret = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in settings?.Headers ?? [])
        {
            var expanded = EnvVars.Expand(value, $"{where}, headers.{name}", _getEnv);
            headers[name] = expanded;
            if (expanded != value) secret.Add(name);
        }
        var url = settings?.Url is { } u ? EnvVars.Expand(u, $"{where}, url", _getEnv).Trim() : null;
        var token = settings?.Token is { } t ? EnvVars.Expand(t, $"{where}, token", _getEnv).Trim() : null;
        return new Endpoint(url, headers, secret, token, settings?.Timeout ?? RunSettings.DefaultTimeoutSeconds);
    }
}
