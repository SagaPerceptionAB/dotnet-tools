using Microsoft.AspNetCore.Mvc;
using ShopApi.Validation;
using ShopApplication;

namespace ShopApi.Controllers;

[ApiController]
[Route("[controller]")]
public sealed class CustomersController(ICustomerService customers) : ControllerBase
{
    /// <summary>createCustomer: skapa kund. E-posten är unik per kund (en dubblett ger 409).</summary>
    [HttpPost]
    public async Task<ActionResult<CustomerResponse>> Create(
        [ModelBinder(typeof(ShopJsonBinder))] CreateCustomerRequest request, CancellationToken ct) =>
        StatusCode(StatusCodes.Status201Created, await customers.CreateAsync(request, ct));
}
