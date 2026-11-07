using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using ShopApplication;

namespace ShopInfrastructure.Messaging;

/// <summary>Lägger ett meddelande på kön order-lagd (Service Bus) när en order har lagts.</summary>
public sealed class ServiceBusOrderEvents(ILogger<ServiceBusOrderEvents> log, ServiceBusClient? client = null) : IOrderEvents
{
    private readonly ServiceBusSender? _sender = client?.CreateSender("order-lagd");

    public async Task PublishAsync(OrderPlaced message, CancellationToken ct)
    {
        if (_sender is null)
        {
            log.LogInformation("Lokalt läge: order {OrderId} läggs inte på kön order-lagd.", message.OrderId);
            return;
        }
        await _sender.SendMessageAsync(new ServiceBusMessage(BinaryData.FromObjectAsJson(message)) { Subject = nameof(OrderPlaced) }, ct);
    }
}
