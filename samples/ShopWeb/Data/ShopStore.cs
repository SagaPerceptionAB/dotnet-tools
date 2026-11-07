namespace ShopWeb.Data;

public sealed record Product(int Id, string Name, string Sku, decimal Price);

public sealed record Customer(int Id, string Name, string Email, string Phone);

/// <summary>Produkter och kunder i minnet. Ersätter en backend i exemplet.</summary>
public sealed class ShopStore
{
    private readonly List<Product> _products =
    [
        new(1, "Kaffebryggare", "KAF-1001", 899m),
        new(2, "Termos", "TRM-2002", 249m),
        new(3, "Kaffekvarn", "KVA-3003", 599m),
    ];

    private readonly List<Customer> _customers = [new(1, "Anna Andersson", "anna@example.com", "+46 70 123 45 67")];

    public IReadOnlyList<Product> Products { get { lock (_products) return [.. _products]; } }

    public IReadOnlyList<Customer> Customers { get { lock (_customers) return [.. _customers]; } }

    public Product? FindProduct(int id) => Products.FirstOrDefault(p => p.Id == id);

    public Product AddProduct(string name, string sku, decimal price)
    {
        lock (_products)
        {
            var product = new Product(_products.Max(p => p.Id) + 1, name, sku, price);
            _products.Add(product);
            return product;
        }
    }

    public Customer AddCustomer(string name, string email, string phone)
    {
        lock (_customers)
        {
            var customer = new Customer(_customers.Max(c => c.Id) + 1, name, email, phone);
            _customers.Add(customer);
            return customer;
        }
    }
}
