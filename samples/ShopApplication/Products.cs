namespace ShopApplication;

/// <summary>Indata till createProduct (ProductCreate i specen).</summary>
public sealed record CreateProductRequest(string Name, string? Sku, double Price);

/// <summary>Svaret från createProduct (Product i specen).</summary>
public sealed record ProductResponse(string Name, string? Sku, double Price, int Id)
{
    public static ProductResponse From(Product p) => new(p.Name, p.Sku, p.Price, p.Id);
}

public interface IProductService
{
    Task<ProductResponse> CreateAsync(CreateProductRequest request, CancellationToken ct);
}

public sealed class ProductService(IProductRepository products) : IProductService
{
    public async Task<ProductResponse> CreateAsync(CreateProductRequest request, CancellationToken ct)
    {
        var product = await products.AddAsync(new Product { Name = request.Name, Sku = request.Sku, Price = request.Price }, ct);
        return ProductResponse.From(product);
    }
}

public interface IProductRepository
{
    Task<Product> AddAsync(Product product, CancellationToken ct);
    Task<Product?> GetAsync(int id, CancellationToken ct);
}
