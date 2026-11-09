using System.Net.Http.Json;

namespace ShopWeb.Data;

/// <summary>Resultatet av ett anrop till ShopApi: lyckades det, och annars ett meddelande att visa för användaren.</summary>
public sealed record ApiResult(bool Ok, string? Error = null)
{
    public static readonly ApiResult Success = new(true);
}

/// <summary>Typad HttpClient mot ShopApi (samples/ShopApi, se samples/shop.openapi.yaml). Adressen står i <c>ShopApi:BaseUrl</c>.</summary>
public sealed class ShopApiClient(HttpClient http)
{
    // Fälten som specen kräver: name och price (ProductCreate), name och email (CustomerCreate).
    // Adressen är relativ till ShopApi:BaseUrl och står i själva anropet, så att systemkartan ser den.
    public Task<ApiResult> CreateProductAsync(string name, string sku, decimal price) =>
        SendAsync(() => http.PostAsJsonAsync("products", new { name, sku = sku.Length > 0 ? sku : null, price }), "produkten");

    public Task<ApiResult> CreateCustomerAsync(string name, string email, string phone) =>
        SendAsync(() => http.PostAsJsonAsync("customers", new { name, email, phone = phone.Length > 0 ? phone : null }), "kunden");

    private async Task<ApiResult> SendAsync(Func<Task<HttpResponseMessage>> send, string what)
    {
        try
        {
            using var response = await send();
            if (response.IsSuccessStatusCode) return ApiResult.Success;
            return new ApiResult(false, response.StatusCode == System.Net.HttpStatusCode.Conflict
                ? $"Det gick inte att spara {what}: uppgifterna finns redan."
                : $"Det gick inte att spara {what}: tjänsten svarade med status {(int)response.StatusCode}.");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return new ApiResult(false, $"Det gick inte att spara {what}: tjänsten går inte att nå. Försök igen om en stund.");
        }
    }
}
