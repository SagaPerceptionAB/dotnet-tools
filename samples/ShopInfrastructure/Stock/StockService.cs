using System.ServiceModel;
using System.ServiceModel.Channels;
using Microsoft.Extensions.Logging;
using ShopApplication;

namespace ShopInfrastructure.Stock;

/// <summary>Lagersystemets SOAP-tjänst (WCF).</summary>
[ServiceContract(Namespace = "urn:lagersystem")]
public interface ILagerService
{
    [OperationContract]
    Task<bool> ReserveraAsync(string artikel, int antal);
}

/// <summary>WCF-klienten, som den "Lägg till tjänstreferens" brukar skapa.</summary>
public sealed class LagerServiceClient(Binding binding, EndpointAddress address)
    : ClientBase<ILagerService>(binding, address), ILagerService
{
    public Task<bool> ReserveraAsync(string artikel, int antal) => Channel.ReserveraAsync(artikel, antal);
}

public sealed record StockOptions(string Url, bool Local);

public sealed class StockService(StockOptions options, ILogger<StockService> log) : IStockService
{
    public async Task ReserveAsync(string article, int quantity, CancellationToken ct)
    {
        if (options.Local)
        {
            log.LogInformation("Lokalt läge: Lagersystemet anropas inte (reservera {Quantity} st {Article}).", quantity, article);
            return;
        }

        var client = new LagerServiceClient(new BasicHttpBinding(BasicHttpSecurityMode.Transport), new EndpointAddress(options.Url));
        try
        {
            if (!await client.ReserveraAsync(article, quantity))
                throw new InvalidOperationException($"Lagersystemet kunde inte reservera {quantity} st {article}.");
            await client.CloseAsync();
        }
        catch
        {
            client.Abort();
            throw;
        }
    }
}
