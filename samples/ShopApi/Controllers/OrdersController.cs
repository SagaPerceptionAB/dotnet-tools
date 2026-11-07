using Microsoft.AspNetCore.Mvc;
using ShopApi.Validation;
using ShopApplication;

namespace ShopApi.Controllers;

[ApiController]
[Route("[controller]")]
public sealed class OrdersController(IOrderService orders) : ControllerBase
{
    /// <summary>createOrder: lägg en order. Kunden och produkten måste finnas (annars 404).</summary>
    [HttpPost]
    public async Task<ActionResult<OrderResponse>> Create(
        [ModelBinder(typeof(ShopJsonBinder))] CreateOrderRequest request, CancellationToken ct) =>
        StatusCode(StatusCodes.Status201Created, await orders.PlaceAsync(request, ct));

    /// <summary>getOrder: hämta en order. Ett id som inte är ett heltal ger 400, ett som inte finns 404.</summary>
    [HttpGet("{id}")]
    public async Task<ActionResult<OrderResponse>> Get(string id, CancellationToken ct)
    {
        if (!int.TryParse(id, out var orderId)) return ErrorResponses.Error(StatusCodes.Status400BadRequest, "ogiltigt id");
        return Ok(await orders.GetAsync(orderId, ct));
    }
}
