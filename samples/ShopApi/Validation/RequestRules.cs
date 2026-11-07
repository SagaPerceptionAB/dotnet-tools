using System.Text.Json.Nodes;
using ShopApplication;

namespace ShopApi.Validation;

/// <summary>Reglerna per indatatyp. De kontrolleras i ordning och det första felet blir svaret.</summary>
public static class RequestRules
{
    private static readonly (string Field, Rule Rule)[] Customer =
    [
        ("name", Rule.Str(required: true, min: 1, max: 100)),
        ("email", Rule.Str(required: true, email: true)),   // format: email ur specen
        ("phone", Rule.Str()),
        ("personnummer", Rule.Str()),
        ("status", Rule.Str(oneOf: ["active", "inactive"])),
    ];

    private static readonly (string Field, Rule Rule)[] Product =
    [
        ("name", Rule.Str(required: true, min: 1, max: 100)),
        ("sku", Rule.Str(pattern: "^[A-Z]{3}-[0-9]{4}$")),   // pattern ur specen
        ("price", Rule.Num(required: true, min: 0.01, max: 100000)),
    ];

    private static readonly (string Field, Rule Rule)[] Order =
    [
        ("customerId", Rule.Int(required: true)),
        ("productId", Rule.Int(required: true)),
        // OBS: quantity 0 släpps igenom med flit (specen säger minimum 1) och ger 500 i Pricing.For.
        ("quantity", Rule.Int(required: true, min: 0, max: 1000)),
    ];

    /// <summary>Första felet i <paramref name="body"/> som text (t.ex. "email saknas"), eller null om allt stämmer.</summary>
    public static string? Check(Type type, JsonObject body)
    {
        foreach (var (name, rule) in For(type))
        {
            var value = body[name];
            if (value is null)
            {
                if (rule.Required) return $"{name} saknas";
                continue;
            }
            if (rule.Check(value) is { } problem) return $"{name}: {problem}";
        }
        return null;
    }

    private static (string Field, Rule Rule)[] For(Type type) =>
        type == typeof(CreateCustomerRequest) ? Customer
        : type == typeof(CreateProductRequest) ? Product
        : type == typeof(CreateOrderRequest) ? Order
        : throw new ArgumentException($"Inga regler för {type.Name}.", nameof(type));
}
