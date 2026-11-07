using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopApplication;

namespace ShopInfrastructure.Database;

public sealed class CustomerRepository(ShopDbContext db) : ICustomerRepository
{
    public Task<bool> EmailExistsAsync(string email, CancellationToken ct)
    {
        var key = email.ToUpperInvariant();
        return db.Customers.AnyAsync(c => c.EmailKey == key, ct);
    }

    public async Task<Customer> AddAsync(Customer customer, CancellationToken ct)
    {
        customer.EmailKey = customer.Email.ToUpperInvariant();
        db.Customers.Add(customer);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            // Två anrop med samma e-post samtidigt: det unika indexet tar det som hann före kontrollen.
            throw ConflictException.EmailInUse(customer.Email);
        }
        return customer;
    }

    public async Task<Customer?> GetAsync(int id, CancellationToken ct) => await db.Customers.FindAsync([id], ct);
}

public sealed class ProductRepository(ShopDbContext db) : IProductRepository
{
    public async Task<Product> AddAsync(Product product, CancellationToken ct)
    {
        db.Products.Add(product);
        await db.SaveChangesAsync(ct);
        return product;
    }

    public async Task<Product?> GetAsync(int id, CancellationToken ct) => await db.Products.FindAsync([id], ct);
}

public sealed class OrderRepository(ShopDbContext db) : IOrderRepository
{
    public async Task<Order> AddAsync(Order order, CancellationToken ct)
    {
        db.Orders.Add(order);
        await db.SaveChangesAsync(ct);
        return order;
    }
}
