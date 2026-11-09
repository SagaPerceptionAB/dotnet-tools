using Testcontainers.Azurite;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace SagaPerception.Testing.NUnit;

/// <summary>Slagen av lokala tjänster testgen ersätter med containrar.</summary>
internal enum ServiceKind
{
    PostgreSql,
    SqlServer,
    AzureStorage,
    Redis,
}

/// <summary>Vad testgen vet om varje slag: namn, standardavbildning och namnen en compose-fil använder för det.</summary>
internal static class ServiceCatalog
{
    public static string NameOf(ServiceKind kind) => kind switch
    {
        ServiceKind.PostgreSql => "PostgreSQL",
        ServiceKind.SqlServer => "SQL Server",
        ServiceKind.AzureStorage => "Azure Storage",
        _ => "Redis",
    };

    public static string DefaultImage(ServiceKind kind) => kind switch
    {
        ServiceKind.PostgreSql => "postgres:16-alpine",
        ServiceKind.SqlServer => "mcr.microsoft.com/mssql/server:2022-latest",
        ServiceKind.AzureStorage => "mcr.microsoft.com/azure-storage/azurite:latest",
        _ => "redis:7-alpine",
    };

    private static string[] ComposeNames(ServiceKind kind) => kind switch
    {
        ServiceKind.PostgreSql => ["postgres"],
        ServiceKind.SqlServer => ["mcr.microsoft.com/mssql/server"],
        ServiceKind.AzureStorage => ["mcr.microsoft.com/azure-storage/azurite"],
        _ => ["redis"],
    };

    /// <summary>Avbildningen: repots compose-fil om den har en tjänst av samma slag, annars standardavbildningen.</summary>
    public static string ImageFor(ServiceKind kind, IEnumerable<string> composeImages) =>
        TestGen.Core.Projects.ComposeImages.Match(composeImages, ComposeNames(kind)) ?? DefaultImage(kind);
}

/// <summary>Tjänsterna appen behöver, i containrar (Testcontainers), startade parallellt en gång per fixture.</summary>
internal sealed class ServiceContainers : IAsyncDisposable
{
    private readonly List<(ServiceKind Kind, string Image, string Value, IAsyncDisposable Container)> _started;

    private ServiceContainers(List<(ServiceKind, string, string, IAsyncDisposable)> started) => _started = started;

    /// <summary>Värdet appen ska ha i stället för det lokala, i samma form: en anslutningssträng, eller värd:port för Redis.</summary>
    public string ValueOf(ServiceKind kind) => _started.First(s => s.Kind == kind).Value;

    /// <summary>Avbildningen som startades.</summary>
    public string ImageOf(ServiceKind kind) => _started.First(s => s.Kind == kind).Image;

    /// <summary>Startar en container per slag. Kastar om Docker inte går att nå; anroparen översätter det till Inconclusive.</summary>
    public static async Task<ServiceContainers> StartAsync(IEnumerable<ServiceKind> kinds, IEnumerable<string> composeImages, CancellationToken ct = default)
    {
        var images = composeImages.ToList();
        var tasks = kinds.Distinct().Select(kind => StartOneAsync(kind, ServiceCatalog.ImageFor(kind, images), ct)).ToList();
        try { await Task.WhenAll(tasks); }
        catch
        {
            foreach (var task in tasks.Where(t => t.IsCompletedSuccessfully)) await task.Result.Container.DisposeAsync();
            await Task.WhenAll(tasks.Select(t => t.ContinueWith(_ => { }, TaskScheduler.Default)));
            throw;
        }
        return new ServiceContainers(tasks.Select(t => t.Result).ToList());
    }

    private static async Task<(ServiceKind Kind, string Image, string Value, IAsyncDisposable Container)> StartOneAsync(ServiceKind kind, string image, CancellationToken ct)
    {
        switch (kind)
        {
            case ServiceKind.PostgreSql:
                var postgres = new PostgreSqlBuilder(image).Build();
                await postgres.StartAsync(ct);
                return (kind, image, postgres.GetConnectionString(), postgres);
            case ServiceKind.SqlServer:
                var sql = new MsSqlBuilder(image).Build();
                await sql.StartAsync(ct);
                return (kind, image, sql.GetConnectionString(), sql);
            case ServiceKind.AzureStorage:
                // Azure SDK ligger ofta före emulatorn i API-version; utan flaggorna avvisas anropen.
                var azurite = new AzuriteBuilder(image).WithCommand("--skipApiVersionCheck", "--loose").Build();
                await azurite.StartAsync(ct);
                return (kind, image, azurite.GetConnectionString(), azurite);
            default:
                var redis = new RedisBuilder(image).Build();
                await redis.StartAsync(ct);
                return (kind, image, redis.GetConnectionString(), redis);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var started in _started) await started.Container.DisposeAsync();
    }
}
