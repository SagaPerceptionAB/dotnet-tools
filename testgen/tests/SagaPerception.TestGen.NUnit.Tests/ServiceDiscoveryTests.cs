using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SagaPerception.TestGen.NUnit.Tests;

/// <summary>Vilka tjänster appen vill ha läses ur dess konfiguration och EF Core-inställningar, utan Docker och utan att någon anslutning öppnas.</summary>
public class ServiceDiscoveryTests
{
    private sealed class ShopContext(DbContextOptions<ShopContext> options) : DbContext(options);

    private static IServiceProvider App(Action<IServiceCollection>? register = null)
    {
        var services = new ServiceCollection();
        register?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value)).Build();

    private static ServiceFindings Find(params (string Key, string? Value)[] values) => ServiceDiscovery.Find(App(), Config(values));

    [Test]
    public void An_app_without_services_needs_nothing()
    {
        var found = Find(("Logging:LogLevel:Default", "Information"), ("Urls", "http://localhost:5000"), ("Mail:Server", "smtp.example.com"));
        Assert.That(found.Values, Is.Empty);
        Assert.That(found.Unmapped, Is.Null);
    }

    [TestCase("Host=localhost;Port=5432;Database=app;Username=a;Password=b", "PostgreSql", true)]
    [TestCase("Server=127.0.0.1;Port=5432;Database=app;User Id=a;Password=b", "PostgreSql", true)]
    [TestCase("Host=db.example.com;Database=app;Username=a;Password=b", "PostgreSql", false)]
    [TestCase("Server=localhost,1433;Database=app;User Id=sa;Password=b;TrustServerCertificate=true", "SqlServer", true)]
    [TestCase("Server=(localdb)\\MSSQLLocalDB;Database=app;Trusted_Connection=true", "SqlServer", true)]
    [TestCase("Data Source=.;Initial Catalog=app;Integrated Security=true", "SqlServer", true)]
    [TestCase("Server=tcp:db.example.com,1433;Initial Catalog=app;User ID=a;Password=b", "SqlServer", false)]
    public void A_connection_string_is_told_by_its_shape_and_by_where_it_points(string value, string kind, bool local)
    {
        var found = ServiceDiscovery.Classify("ConnectionStrings:Database", value);

        Assert.That(found, Is.Not.Null);
        Assert.That((found!.Kind, found.Local), Is.EqualTo((Enum.Parse<ServiceKind>(kind), local)));
    }

    [TestCase("Data Source=app.db")]
    [TestCase("Data Source=:memory:")]
    [TestCase("Data Source=data/app.sqlite;Password=x")]
    [TestCase("not a connection string")]
    public void File_databases_and_other_values_under_ConnectionStrings_are_not_services(string value) =>
        Assert.That(ServiceDiscovery.Classify("ConnectionStrings:Database", value), Is.Null);

    [Test]
    public void The_host_of_a_real_database_is_reported_but_never_the_whole_value()
    {
        var found = ServiceDiscovery.Classify("ConnectionStrings:Database", "Host=db.example.com;Database=app;Username=a;Password=hemligt");
        Assert.That(found!.Host, Is.EqualTo("db.example.com"));
        Assert.That(found.ToString(), Does.Not.Contain("hemligt"));
    }

    [TestCase("UseDevelopmentStorage=true", true)]
    [TestCase("DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=abc;BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;", true)]
    [TestCase("DefaultEndpointsProtocol=http;AccountName=minlokala;AccountKey=abc;BlobEndpoint=http://127.0.0.1:10000/minlokala;", true)]
    [TestCase("DefaultEndpointsProtocol=https;AccountName=prodlager;AccountKey=abc;EndpointSuffix=core.windows.net", false)]
    public void Azure_storage_is_found_anywhere_and_local_means_the_emulator(string value, bool local)
    {
        var found = ServiceDiscovery.Classify("Photos:ConnectionString", value);

        Assert.That(found, Is.Not.Null);
        Assert.That((found!.Kind, found.Local), Is.EqualTo((ServiceKind.AzureStorage, local)));
    }

    [Test]
    public void The_account_of_a_real_storage_is_reported()
    {
        var found = ServiceDiscovery.Classify("Photos:ConnectionString", "DefaultEndpointsProtocol=https;AccountName=prodlager;AccountKey=abc;EndpointSuffix=core.windows.net");
        Assert.That(found!.Host, Is.EqualTo("prodlager"));
    }

    [TestCase("ConnectionStrings:Cache", "localhost:6379", true)]
    [TestCase("ConnectionStrings:Cache", "127.0.0.1:6380,abortConnect=false", true)]
    [TestCase("ConnectionStrings:Cache", "cache.example.com:6380,password=x,ssl=true", false)]
    [TestCase("Redis:Configuration", "localhost:6379", true)]
    [TestCase("Cache:Redis", "redis.example.com:6379", false)]
    public void Redis_is_host_and_port_under_ConnectionStrings_or_under_a_key_that_says_redis(string key, string value, bool local)
    {
        var found = ServiceDiscovery.Classify(key, value);

        Assert.That(found, Is.Not.Null);
        Assert.That((found!.Kind, found.Local), Is.EqualTo((ServiceKind.Redis, local)));
    }

    [TestCase("Cache:Duration", "5:30")]
    [TestCase("Api:BaseUrl", "http://localhost:5000")]
    [TestCase("Mail:Endpoint", "smtp.example.com:25")]
    public void A_host_and_port_elsewhere_is_not_redis(string key, string value) =>
        Assert.That(ServiceDiscovery.Classify(key, value), Is.Null);

    [Test]
    public void Several_services_are_found_together_in_key_order_and_the_kinds_are_listed_once()
    {
        var found = Find(("Storage:ConnectionString", "UseDevelopmentStorage=true"), ("ConnectionStrings:Database", "Host=localhost;Port=5432;Database=a"),
            ("Photos:ConnectionString", "UseDevelopmentStorage=true"));

        Assert.That(found.Values.Select(v => v.Key), Is.EqualTo(new[] { "ConnectionStrings:Database", "Photos:ConnectionString", "Storage:ConnectionString" }));
        Assert.That(found.LocalKinds, Is.EqualTo(new[] { ServiceKind.PostgreSql, ServiceKind.AzureStorage }));
        Assert.That(found.Real, Is.Null);
    }

    [Test]
    public void A_real_service_is_reported_even_when_others_are_local()
    {
        var found = Find(("ConnectionStrings:Database", "Host=localhost;Port=5432;Database=a"), ("Storage:ConnectionString", "AccountName=prod;AccountKey=x"));

        Assert.That(found.Real?.Key, Is.EqualTo("Storage:ConnectionString"));
        Assert.That(found.LocalKinds, Is.EqualTo(new[] { ServiceKind.PostgreSql }));
    }

    [Test]
    public void Empty_values_are_not_services()
    {
        var found = Find(("ConnectionStrings:Database", ""), ("Photos:ConnectionString", null));
        Assert.That(found.Values, Is.Empty);
    }

    [Test]
    public void Sqlite_needs_no_container()
    {
        var found = ServiceDiscovery.Find(App(s => s.AddDbContext<ShopContext>(o => o.UseSqlite("Data Source=:memory:"))), Config(("ConnectionStrings:Shop", "Data Source=:memory:")));
        Assert.That(found.Values, Is.Empty);
        Assert.That(found.Unmapped, Is.Null);
    }

    [Test]
    public void The_database_of_the_ef_options_is_found_through_the_key_whose_value_is_the_connection_string()
    {
        const string connection = "Server=localhost,1433;Database=Shop;User Id=sa;Password=hemligt;TrustServerCertificate=true";
        var found = ServiceDiscovery.Find(App(s => s.AddDbContext<ShopContext>(o => o.UseSqlServer(connection))), Config(("Min:Anslutning", connection)));

        Assert.That(found.Values, Is.EqualTo(new[] { new ServiceValue(ServiceKind.SqlServer, "Min:Anslutning", true, "localhost") }));
    }

    [Test]
    public void An_ef_database_on_a_real_host_is_a_real_service()
    {
        const string connection = "Server=tcp:db.example,1433;Database=Shop;User Id=sa;Password=hemligt;TrustServerCertificate=true";
        var found = ServiceDiscovery.Find(App(s => s.AddDbContext<ShopContext>(o => o.UseSqlServer(connection))), Config(("ConnectionStrings:Shop", connection)));

        Assert.That(found.Real, Is.EqualTo(new ServiceValue(ServiceKind.SqlServer, "ConnectionStrings:Shop", false, "db.example")));
    }

    [Test]
    public void An_ef_database_with_an_empty_connection_string_is_replaced_through_the_only_key_under_ConnectionStrings()
    {
        var found = ServiceDiscovery.Find(App(s => s.AddDbContext<ShopContext>(o => o.UseSqlServer(""))), Config(("ConnectionStrings:Database", ""), ("Photos:ConnectionString", "")));

        Assert.That(found.Values, Is.EqualTo(new[] { new ServiceValue(ServiceKind.SqlServer, "ConnectionStrings:Database", true, "") }));
    }

    [Test]
    public void An_ef_database_without_a_known_key_is_unmapped()
    {
        var found = ServiceDiscovery.Find(App(s => s.AddDbContext<ShopContext>(o => o.UseSqlServer("Server=localhost;Database=Shop"))), Config(("A:Annat", "x")));
        Assert.That(found.Unmapped, Is.EqualTo(ServiceKind.SqlServer));
    }

    [Test]
    public void The_options_are_read_through_a_scope_so_scoped_registrations_work()
    {
        var services = new ServiceCollection();
        services.AddDbContext<ShopContext>(o => o.UseSqlServer("Server=localhost;Database=y"));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        Assert.That(ServiceDiscovery.Find(provider, Config(("ConnectionStrings:A", "Server=localhost;Database=y"))).LocalKinds, Is.EqualTo(new[] { ServiceKind.SqlServer }));
    }

    [TestCase("SqlServerOptionsExtension", "SqlServer")]
    [TestCase("NpgsqlOptionsExtension", "PostgreSql")]
    [TestCase("SqliteOptionsExtension", null)]
    [TestCase("InMemoryOptionsExtension", null)]
    [TestCase("CoreOptionsExtension", null)]
    public void The_provider_is_told_from_the_options_extension(string typeName, string? expected) =>
        Assert.That(ServiceDiscovery.ProviderOf(typeName), Is.EqualTo(expected is null ? null : Enum.Parse<ServiceKind>(expected)));

    [Test]
    public void The_configuration_key_is_the_one_whose_value_is_the_connection_string()
    {
        var configuration = Config(("ConnectionStrings:Shop", "Server=a;Database=b"), ("ConnectionStrings:Annan", "Server=c"), ("Annat:Varde", "x"));

        Assert.That(ServiceDiscovery.FindConfigKey(configuration, "Server=c"), Is.EqualTo("ConnectionStrings:Annan"));
        Assert.That(ServiceDiscovery.FindConfigKey(configuration, "Server=a;Database=b"), Is.EqualTo("ConnectionStrings:Shop"));
    }

    [Test]
    public void An_empty_connection_string_is_replaced_through_the_only_key_under_ConnectionStrings()
    {
        var one = Config(("ConnectionStrings:Database", ""), ("Photos:ConnectionString", ""));
        var two = Config(("ConnectionStrings:A", ""), ("ConnectionStrings:B", ""));
        var none = Config(("Database:Connection", ""));

        Assert.That(ServiceDiscovery.FindConfigKey(one, ""), Is.EqualTo("ConnectionStrings:Database"));
        Assert.That(ServiceDiscovery.FindConfigKey(one, null), Is.EqualTo("ConnectionStrings:Database"));
        Assert.That(ServiceDiscovery.FindConfigKey(one, "Server=annan"), Is.EqualTo("ConnectionStrings:Database"), "ingen nyckel har värdet, men det finns bara en");
        Assert.That(ServiceDiscovery.FindConfigKey(two, ""), Is.Null, "två nycklar: användaren får peka ut den");
        Assert.That(ServiceDiscovery.FindConfigKey(none, ""), Is.Null);
    }
}
