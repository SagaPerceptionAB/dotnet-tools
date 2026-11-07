using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;
using ShopApplication;

namespace ShopInfrastructure.Receipts;

/// <summary>Sparar kvittot som en fil i blob-containern kvitton.</summary>
public sealed class BlobReceiptStorage(ILogger<BlobReceiptStorage> log, BlobServiceClient? service = null) : IReceiptStorage
{
    private readonly BlobContainerClient? _container = service?.GetBlobContainerClient("kvitton");

    public async Task SaveAsync(int orderId, string text, CancellationToken ct)
    {
        if (_container is null)
        {
            log.LogInformation("Lokalt läge: kvittot för order {OrderId} sparas inte i containern kvitton.", orderId);
            return;
        }
        await _container.UploadBlobAsync($"order-{orderId}.txt", BinaryData.FromString(text), ct);
    }
}
