using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ShopApplication;
using ShopInfrastructure.Database;
using ShopInfrastructure.Email;
using ShopInfrastructure.Messaging;
using ShopInfrastructure.Payments;
using ShopInfrastructure.Receipts;
using ShopInfrastructure.Stock;

namespace ShopInfrastructure;

public static class InfrastructureRegistration
{
    /// <summary>
    /// Registrerar adaptrarna mot integrationerna. <c>Integrationer:Lokalt</c> (standard: true) kortsluter allt som annars
    /// skulle gå till ett annat system, så att exemplet går att köra utan Azure, lagersystem eller e-postserver.
    /// Själva anropen finns ändå kvar i adaptrarna, så att testgen kan visa dem i systemkartan.
    /// </summary>
    public static IServiceCollection AddShopInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var local = config.GetValue("Integrationer:Lokalt", true);

        // Databas: SQLite i minnet. Skriver via EF Core, läser ordrar med Dapper.
        services.AddSingleton<ShopDatabase>();
        services.AddDbContext<ShopDbContext>((sp, o) => o.UseSqlite(sp.GetRequiredService<ShopDatabase>().ConnectionString));
        services.AddScoped<ICustomerRepository, CustomerRepository>();
        services.AddScoped<IProductRepository, ProductRepository>();
        services.AddScoped<IOrderRepository, OrderRepository>();
        services.AddScoped<IOrderQueries, OrderQueries>();

        // Betaltjänst (HTTP). Lokalt svarar LocalPaymentHandler i stället för den riktiga tjänsten.
        var payments = services.AddHttpClient<IPaymentGateway, PaymentGateway>(http =>
            http.BaseAddress = new Uri(config["Betaltjanst:BaseUrl"] ?? "https://betaltjanst.example/"));
        if (local) payments.ConfigurePrimaryHttpMessageHandler(() => new LocalPaymentHandler());

        // Lagersystem (WCF).
        services.AddSingleton(new StockOptions(config["Lagersystem:Url"] ?? "https://lager.example/LagerService.svc", local));
        services.AddScoped<IStockService, StockService>();

        // Kön order-lagd (Service Bus) och kvitton (Blob). Klienterna finns bara utanför lokalt läge.
        if (!local)
        {
            services.AddSingleton(new ServiceBusClient(config["ServiceBus:ConnectionString"]));
            services.AddSingleton(new BlobServiceClient(config["Blob:ConnectionString"]));
        }
        services.AddSingleton<IOrderEvents, ServiceBusOrderEvents>();
        services.AddSingleton<IReceiptStorage, BlobReceiptStorage>();

        // E-post (SMTP).
        services.AddSingleton(new SmtpOptions(config["Smtp:Host"] ?? "smtp.example", config["Smtp:Fran"] ?? "butik@example.se", local));
        services.AddSingleton<IEmailSender, SmtpEmailSender>();
        return services;
    }

    /// <summary>Skapar tabellerna. Databasen ligger i minnet, så den är tom vid varje start.</summary>
    public static void EnsureShopDatabase(this IServiceProvider services)
    {
        using var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ShopDbContext>().Database.EnsureCreated();
    }
}
