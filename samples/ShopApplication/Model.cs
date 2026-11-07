namespace ShopApplication;

public sealed class Customer
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    /// <summary>E-posten med versaler, så att unikhetskontrollen inte bryr sig om skiftläget.</summary>
    public string EmailKey { get; set; } = "";
    public string? Phone { get; set; }
    public string? Personnummer { get; set; }
    public string? Status { get; set; }
}

public sealed class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Sku { get; set; }
    public double Price { get; set; }
}

public sealed class Order
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public int ProductId { get; set; }
    public int Quantity { get; set; }
    public DateTime CreatedAt { get; set; }
    public int DiscountPercent { get; set; }
    public double TotalPrice { get; set; }
    public string? PaymentId { get; set; }
}

/// <summary>Något som efterfrågas finns inte (blir 404).</summary>
public sealed class NotFoundException(string message) : Exception(message);

/// <summary>Uppgifterna krockar med något som redan finns (blir 409).</summary>
public sealed class ConflictException(string message) : Exception(message)
{
    public static ConflictException EmailInUse(string email) => new($"e-post {email} används redan");
}
