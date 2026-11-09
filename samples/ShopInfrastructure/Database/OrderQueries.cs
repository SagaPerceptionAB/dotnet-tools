using System.Globalization;
using Dapper;
using ShopApplication;

namespace ShopInfrastructure.Database;

/// <summary>Läser ordrar med SQL (Dapper) i stället för EF Core, som många system gör för läsfrågor.</summary>
public sealed class OrderQueries(ShopDatabase database) : IOrderQueries
{
    private const string Sql =
        "SELECT Id, CustomerId, ProductId, Quantity, CreatedAt, DiscountPercent, TotalPrice FROM Orders WHERE Id = @id";

    public async Task<Order?> GetAsync(int id, CancellationToken ct)
    {
        await using var connection = database.Open();
        var row = await connection.QuerySingleOrDefaultAsync<OrderRow>(new CommandDefinition(Sql, new { id }, cancellationToken: ct));
        return row?.ToOrder();
    }

    // SQLite ger heltal som long och datum som text.
    private sealed class OrderRow
    {
        public long Id { get; set; }
        public long CustomerId { get; set; }
        public long ProductId { get; set; }
        public long Quantity { get; set; }
        public string CreatedAt { get; set; } = "";
        public long DiscountPercent { get; set; }
        public double TotalPrice { get; set; }

        public Order ToOrder() => new()
        {
            Id = (int)Id,
            CustomerId = (int)CustomerId,
            ProductId = (int)ProductId,
            Quantity = (int)Quantity,
            CreatedAt = DateTime.Parse(CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
            DiscountPercent = (int)DiscountPercent,
            TotalPrice = TotalPrice,
        };
    }
}
