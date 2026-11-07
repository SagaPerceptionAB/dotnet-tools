namespace ShopApplication;

// Gränssnitten mot systemen utanför butiken. Implementationerna finns i ShopInfrastructure.

/// <summary>Betaltjänsten drar beloppet från kunden.</summary>
public interface IPaymentGateway
{
    Task<PaymentResult> ChargeAsync(PaymentRequest request, CancellationToken ct);
}

public sealed record PaymentRequest(int CustomerId, double Amount, string Currency);
public sealed record PaymentResult(string PaymentId, string Status);

/// <summary>Lagersystemet reserverar varorna.</summary>
public interface IStockService
{
    Task ReserveAsync(string article, int quantity, CancellationToken ct);
}

/// <summary>Talar om för andra system att en order har lagts.</summary>
public interface IOrderEvents
{
    Task PublishAsync(OrderPlaced message, CancellationToken ct);
}

public sealed record OrderPlaced(int OrderId, int CustomerId, int ProductId, int Quantity, double TotalPrice);

/// <summary>Sparar kvittot för en order.</summary>
public interface IReceiptStorage
{
    Task SaveAsync(int orderId, string text, CancellationToken ct);
}

/// <summary>Skickar e-post till kunden.</summary>
public interface IEmailSender
{
    Task SendAsync(string to, string subject, string body, CancellationToken ct);
}
