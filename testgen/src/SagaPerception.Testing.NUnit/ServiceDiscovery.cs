using System.Collections;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SagaPerception.Testing.NUnit;

/// <summary>Ett konfigurationsvärde som pekar på en tjänst. <paramref name="Host"/> är värden eller kontot (aldrig hela värdet, som kan innehålla lösenord).</summary>
internal sealed record ServiceValue(ServiceKind Kind, string Key, bool Local, string Host);

/// <summary>Tjänsterna appen vill ha. <paramref name="Unmapped"/> är en EF-databas vars anslutningssträng inte står under någon känd nyckel.</summary>
internal sealed record ServiceFindings(IReadOnlyList<ServiceValue> Values, ServiceKind? Unmapped)
{
    public IEnumerable<ServiceValue> Local => Values.Where(v => v.Local);

    public ServiceValue? Real => Values.FirstOrDefault(v => !v.Local);

    public IReadOnlyList<ServiceKind> LocalKinds => Local.Select(v => v.Kind).Distinct().Order().ToList();
}

/// <summary>
/// Hittar de tjänster appen vill ha i konfigurationen från <see cref="SagaPerception.Testing.ApiFactory.BuildHost"/> (ingen appkod efter Build() har körts):
/// anslutningssträngar, Azure Storage och Redis, samt databasen i EF Cores <c>DbContextOptions</c>. EF Core läses med reflektion, så bara appen behöver paketet.
/// </summary>
internal static partial class ServiceDiscovery
{
    private const string ConnectionStrings = "ConnectionStrings:";

    private static Type? OptionsType => Type.GetType("Microsoft.EntityFrameworkCore.DbContextOptions, Microsoft.EntityFrameworkCore", throwOnError: false);

    public static ServiceFindings Find(IServiceProvider services, IConfiguration configuration)
    {
        var found = new SortedDictionary<string, ServiceValue>(StringComparer.Ordinal);
        foreach (var (key, value) in configuration.AsEnumerable())
            if (!string.IsNullOrWhiteSpace(value) && Classify(key, value) is { } service) found[key] = service;

        ServiceKind? unmapped = null;
        var database = DatabaseOf(services);
        if (database.Provider is { } provider)
        {
            var key = FindConfigKey(configuration, database.ConnectionString);
            if (key is null) unmapped = provider;
            else
            {
                var value = configuration[key];
                var host = string.IsNullOrWhiteSpace(value) ? "" : ConnectionHost(value) ?? "";
                found[key] = new ServiceValue(provider, key, host.Length == 0 || IsLocalHost(host), host);
            }
        }
        return new ServiceFindings(found.Values.ToList(), unmapped);
    }

    /// <summary>Tjänsten ett konfigurationsvärde pekar på, eller null om det inte ser ut som någon av dem.</summary>
    public static ServiceValue? Classify(string key, string value)
    {
        if (value.Contains("UseDevelopmentStorage=true", StringComparison.OrdinalIgnoreCase))
            return new ServiceValue(ServiceKind.AzureStorage, key, true, "");
        if (AzureShape().IsMatch(value))
        {
            var local = value.Contains("devstoreaccount1", StringComparison.OrdinalIgnoreCase) || LocalEndpoint().IsMatch(value);
            return new ServiceValue(ServiceKind.AzureStorage, key, local, EndpointHost(value) ?? Part(value, "AccountName") ?? "");
        }
        if (key.StartsWith(ConnectionStrings, StringComparison.OrdinalIgnoreCase))
        {
            if (HostPort().Match(value) is { Success: true } redis)
                return new ServiceValue(ServiceKind.Redis, key, IsLocalHost(redis.Groups[1].Value), redis.Groups[1].Value);
            return ClassifyDatabase(key, value);
        }
        if (key.Contains("redis", StringComparison.OrdinalIgnoreCase) && HostPort().Match(value) is { Success: true } other)
            return new ServiceValue(ServiceKind.Redis, key, IsLocalHost(other.Groups[1].Value), other.Groups[1].Value);
        return null;
    }

    private static ServiceValue? ClassifyDatabase(string key, string value)
    {
        var host = Part(value, "Host", "Server", "Address", "Addr", "Network Address", "Data Source");
        if (host is null) return null;
        var postgres = Part(value, "Host", "Port", "Username") is not null;
        if (!postgres)
        {
            if (Part(value, "Data Source") is { } source && host == source && IsFileDatabase(source)) return null;
            if (Part(value, "Initial Catalog", "Database", "Integrated Security", "Trusted_Connection", "User Id", "UID", "Password", "TrustServerCertificate") is null) return null;
        }
        var name = ConnectionHost(value) ?? host;
        return new ServiceValue(postgres ? ServiceKind.PostgreSql : ServiceKind.SqlServer, key, IsLocalHost(name), name);
    }

    private static bool IsFileDatabase(string source) =>
        source.EndsWith(".db", StringComparison.OrdinalIgnoreCase) || source.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase)
        || source.EndsWith(".sqlite3", StringComparison.OrdinalIgnoreCase) || source.Contains(":memory:", StringComparison.OrdinalIgnoreCase) || source.Contains('/');

    /// <summary>Värden i en anslutningssträng: första värdet av <c>Host</c>, <c>Server</c> eller <c>Data Source</c>, utan <c>tcp:</c> och port.</summary>
    public static string? ConnectionHost(string connectionString)
    {
        var host = Part(connectionString, "Host", "Server", "Address", "Addr", "Network Address", "Data Source");
        if (host is null) return null;
        host = host.Split(',')[0].Trim();
        if (host.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)) host = host[4..];
        if (host.StartsWith('[') && host.IndexOf(']') is var close and > 0) return host[..(close + 1)];
        return host.Count(c => c == ':') == 1 ? host[..host.IndexOf(':')] : host;
    }

    public static bool IsLocalHost(string host)
    {
        var h = host.Trim().Trim('[', ']').ToLowerInvariant();
        return h is "localhost" or "127.0.0.1" or "::1" or "(local)" or "." || h.StartsWith("(localdb)", StringComparison.Ordinal);
    }

    /// <summary>Värdet för första nyckeln av <paramref name="names"/> som finns i en anslutningssträng (<c>a=b;c=d</c>), skiftlägesokänsligt.</summary>
    private static string? Part(string connectionString, params string[] names)
    {
        foreach (var name in names)
            foreach (var part in connectionString.Split(';'))
            {
                var at = part.IndexOf('=', StringComparison.Ordinal);
                if (at > 0 && part[..at].Trim().Equals(name, StringComparison.OrdinalIgnoreCase)) return part[(at + 1)..].Trim();
            }
        return null;
    }

    private static string? EndpointHost(string value) =>
        Uri.TryCreate(Part(value, "BlobEndpoint", "QueueEndpoint", "TableEndpoint"), UriKind.Absolute, out var uri) ? uri.Host : null;

    [GeneratedRegex(@"AccountName=|BlobEndpoint=|DefaultEndpointsProtocol=", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex AzureShape();

    [GeneratedRegex(@"://(127\.0\.0\.1|localhost)\b", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex LocalEndpoint();

    [GeneratedRegex(@"^\s*([A-Za-z0-9_.\-]*[A-Za-z][A-Za-z0-9_.\-]*|\d{1,3}(?:\.\d{1,3}){3}):(\d{1,5})(,.*)?$", RegexOptions.None, 1000)]
    private static partial Regex HostPort();

    // --- EF Core ---

    /// <summary>Databasen i appens DbContext-inställningar (första SQL Server- eller PostgreSQL-tillägget), annars ingen provider.</summary>
    private static (ServiceKind? Provider, string? ConnectionString) DatabaseOf(IServiceProvider services)
    {
        var optionsType = OptionsType;
        if (optionsType is null) return (null, null);

        using var scope = services.CreateScope();
        foreach (var options in scope.ServiceProvider.GetServices(optionsType))
        {
            if (options is null || optionsType.GetProperty("Extensions")?.GetValue(options) is not IEnumerable extensions) continue;
            foreach (var extension in extensions)
            {
                var type = extension.GetType();
                if (ProviderOf(type.Name) is { } provider)
                    return (provider, type.GetProperty("ConnectionString")?.GetValue(extension) as string);
            }
        }
        return (null, null);
    }

    /// <summary>Providerns tilläggsklass i EF Core (<c>SqlServerOptionsExtension</c>, <c>NpgsqlOptionsExtension</c>) till databasen, annars null.</summary>
    public static ServiceKind? ProviderOf(string optionsExtensionTypeName) => optionsExtensionTypeName switch
    {
        "SqlServerOptionsExtension" => ServiceKind.SqlServer,
        "NpgsqlOptionsExtension" => ServiceKind.PostgreSql,
        _ => null,
    };

    /// <summary>
    /// Nyckeln i konfigurationen som bytes mot containerns anslutningssträng: den vars värde är <paramref name="connectionString"/>, annars den
    /// enda nyckeln under <c>ConnectionStrings</c> (appens anslutningssträng kan vara tom). Null om ingen av dem passar.
    /// </summary>
    public static string? FindConfigKey(IConfiguration configuration, string? connectionString)
    {
        if (!string.IsNullOrEmpty(connectionString))
        {
            var byValue = configuration.AsEnumerable().Where(kv => kv.Value == connectionString).Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).FirstOrDefault();
            if (byValue is not null) return byValue;
        }
        var keys = configuration.GetSection("ConnectionStrings").GetChildren().Where(c => c.Value is not null).Select(c => c.Path).ToList();
        return keys.Count == 1 ? keys[0] : null;
    }
}
