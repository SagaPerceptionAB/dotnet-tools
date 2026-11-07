using System.Net;
using System.Net.Http.Json;
using ShopApplication;

namespace ShopInfrastructure.Payments;

/// <summary>Betaltjänsten anropas över HTTP. Adressen kommer från Betaltjanst:BaseUrl (se InfrastructureRegistration).</summary>
public sealed class PaymentGateway(HttpClient http) : IPaymentGateway
{
    public async Task<PaymentResult> ChargeAsync(PaymentRequest request, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("payments", request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PaymentResult>(ct)
               ?? throw new InvalidOperationException("Betaltjänsten svarade utan innehåll.");
    }
}

/// <summary>Lokalt läge: svarar i stället för den riktiga betaltjänsten. Alla betalningar godkänns.</summary>
public sealed class LocalPaymentHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = JsonContent.Create(new PaymentResult($"lokal-{Guid.NewGuid():N}", "godkänd")),
        });
}
