using Microsoft.AspNetCore.Mvc;
using ShopApi.Validation;
using ShopApplication;

namespace ShopApi.Controllers;

[ApiController]
[Route("[controller]")]
public sealed class ProductsController(IProductService products) : ControllerBase
{
    /// <summary>createProduct: skapa produkt.</summary>
    [HttpPost]
    public async Task<ActionResult<ProductResponse>> Create(
        [ModelBinder(typeof(ShopJsonBinder))] CreateProductRequest request, CancellationToken ct) =>
        StatusCode(StatusCodes.Status201Created, await products.CreateAsync(request, ct));
}
