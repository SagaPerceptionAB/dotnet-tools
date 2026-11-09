using System.Globalization;

namespace ShopApplication;

/// <summary>Indata till createOrder (OrderCreate i specen).</summary>
public sealed record CreateOrderRequest(int CustomerId, int ProductId, int Quantity);

/// <summary>Svaret från createOrder och getOrder (Order i specen).</summary>
public sealed record OrderResponse(int Id, int CustomerId, int ProductId, int Quantity, string CreatedAt, int DiscountPercent, double TotalPrice)
{
    public static OrderResponse From(Order o) => new(o.Id, o.CustomerId, o.ProductId, o.Quantity,
        o.CreatedAt.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), o.DiscountPercent, o.TotalPrice);
}

public interface IOrderService
{
    Task<OrderResponse> PlaceAsync(CreateOrderRequest request, CancellationToken ct);
    Task<OrderResponse> GetAsync(int id, CancellationToken ct);
}

public sealed class OrderService(
    ICustomerRepository customers, IProductRepository products, IOrderRepository orders, IOrderQueries queries,
    IStockService stock, IPaymentGateway payments, IOrderEvents events, IReceiptStorage receipts, IEmailSender email) : IOrderService
{
    public async Task<OrderResponse> PlaceAsync(CreateOrderRequest request, CancellationToken ct)
    {
        var customer = await customers.GetAsync(request.CustomerId, ct) ?? throw new NotFoundException("kunden finns inte");
        var product = await products.GetAsync(request.ProductId, ct) ?? throw new NotFoundException("produkten finns inte");
        var price = Pricing.For(product.Price, request.Quantity);

        await stock.ReserveAsync(product.Sku ?? $"P{product.Id}", request.Quantity, ct);
        var payment = await payments.ChargeAsync(new PaymentRequest(customer.Id, price.Total, "SEK"), ct);

        var now = DateTime.UtcNow;
        var order = await orders.AddAsync(new Order
        {
            CustomerId = customer.Id,
            ProductId = product.Id,
            Quantity = request.Quantity,
            CreatedAt = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc),
            DiscountPercent = price.DiscountPercent,
            TotalPrice = price.Total,
            PaymentId = payment.PaymentId,
        }, ct);

        await events.PublishAsync(new OrderPlaced(order.Id, customer.Id, product.Id, order.Quantity, order.TotalPrice), ct);
        await receipts.SaveAsync(order.Id, Pricing.Receipt(order, product, price), ct);
        await email.SendAsync(customer.Email, $"Orderbekräftelse {order.Id}", $"Tack för din order! Att betala: {order.TotalPrice} kr.", ct);
        return OrderResponse.From(order);
    }

    public async Task<OrderResponse> GetAsync(int id, CancellationToken ct) =>
        await queries.GetAsync(id, ct) is { } order ? OrderResponse.From(order) : throw new NotFoundException("hittades inte");
}

/// <summary>Priset för en orderrad: 10 % rabatt när quantity är över 5.</summary>
public sealed record Price(int DiscountPercent, double Total, int UnitCents);

public static class Pricing
{
    public static Price For(double unitPrice, int quantity)
    {
        var discount = quantity > 5 ? 10 : 0;
        var total = Math.Round(unitPrice * quantity * (100 - discount) / 100, 2);

        // Medvetet fel: styckpriset i ören räknas ut som heltalsdivision på antalet.
        // quantity 0 => DivideByZeroException => 500 (borde ha gett 400). testgen visar det som FEL (T-020).
        var unitCents = (int)(total * 100) / quantity;
        return new Price(discount, total, unitCents);
    }

    public static string Receipt(Order order, Product product, Price price) => string.Join('\n',
        $"Order {order.Id}",
        $"{order.Quantity} st {product.Name} à {price.UnitCents / 100.0:0.00} kr",
        price.DiscountPercent > 0 ? $"Rabatt {price.DiscountPercent} %" : "Ingen rabatt",
        $"Att betala {order.TotalPrice:0.00} kr");
}

public interface IOrderRepository
{
    Task<Order> AddAsync(Order order, CancellationToken ct);
}

/// <summary>Läsfrågor mot orderdatabasen (skrivs med SQL, inte via EF).</summary>
public interface IOrderQueries
{
    Task<Order?> GetAsync(int id, CancellationToken ct);
}
