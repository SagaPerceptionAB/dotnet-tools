namespace ShopApplication;

/// <summary>Indata till createCustomer (CustomerCreate i specen).</summary>
public sealed record CreateCustomerRequest(string Name, string Email, string? Phone, string? Personnummer, string? Status);

/// <summary>Svaret från createCustomer (Customer i specen).</summary>
public sealed record CustomerResponse(string Name, string Email, string? Phone, string? Personnummer, string? Status, int Id)
{
    public static CustomerResponse From(Customer c) => new(c.Name, c.Email, c.Phone, c.Personnummer, c.Status, c.Id);
}

public interface ICustomerService
{
    Task<CustomerResponse> CreateAsync(CreateCustomerRequest request, CancellationToken ct);
}

public sealed class CustomerService(ICustomerRepository customers, IEmailSender email) : ICustomerService
{
    public async Task<CustomerResponse> CreateAsync(CreateCustomerRequest request, CancellationToken ct)
    {
        // E-posten är unik per kund oavsett skiftläge: en dubblett ger 409.
        if (await customers.EmailExistsAsync(request.Email, ct)) throw ConflictException.EmailInUse(request.Email);

        var customer = await customers.AddAsync(new Customer
        {
            Name = request.Name,
            Email = request.Email,
            Phone = request.Phone,
            Personnummer = request.Personnummer,
            Status = request.Status,
        }, ct);
        await email.SendAsync(customer.Email, "Välkommen", $"Hej {customer.Name}! Ditt kundnummer är {customer.Id}.", ct);
        return CustomerResponse.From(customer);
    }
}

public interface ICustomerRepository
{
    Task<bool> EmailExistsAsync(string email, CancellationToken ct);
    Task<Customer> AddAsync(Customer customer, CancellationToken ct);
    Task<Customer?> GetAsync(int id, CancellationToken ct);
}
