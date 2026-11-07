using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopApplication;

namespace ShopInfrastructure.Database;

/// <summary>
/// SQLite-databasen i minnet. Den finns så länge minst en anslutning är öppen, så den här klassen håller en öppen tills
/// API:et stängs. En omstart ger alltså en tom databas.
/// </summary>
public sealed class ShopDatabase : IDisposable
{
    private readonly SqliteConnection _keepAlive;

    public ShopDatabase()
    {
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = $"shop-{Guid.NewGuid():N}", Mode = SqliteOpenMode.Memory, Cache = SqliteCacheMode.Shared,
        }.ToString();
        _keepAlive = new SqliteConnection(ConnectionString);
        _keepAlive.Open();
    }

    public string ConnectionString { get; }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        return connection;
    }

    public void Dispose() => _keepAlive.Dispose();
}

public sealed class ShopDbContext(DbContextOptions<ShopDbContext> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Customer>().ToTable("Customers").HasIndex(c => c.EmailKey).IsUnique();
        model.Entity<Product>().ToTable("Products");
        model.Entity<Order>().ToTable("Orders");
    }
}
