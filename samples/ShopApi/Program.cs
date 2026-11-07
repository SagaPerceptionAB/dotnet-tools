// Exempel-API som implementerar samples/shop.openapi.yaml. Det är uppdelat i tre projekt (samples/Shop.slnx) så att
// testgen kan visa hur ett anrop går genom systemet och ut till integrationerna (testgen map --solution samples/Shop.slnx):
//
//   ShopApi             tar emot anropet och kontrollerar indata (controllers)
//   ShopApplication     affärsreglerna: kunder, produkter och ordrar (tjänster)
//   ShopInfrastructure  integrationerna: databas (EF Core och Dapper mot SQLite), Betaltjänst (HTTP),
//                       Lagersystem (WCF), kön order-lagd (Service Bus), kvitton (Blob) och e-post (SMTP)
//
// Integrationerna körs i lokalt läge (Integrationer:Lokalt, standard true): databasen ligger i minnet och övriga
// anrop kortsluts i adaptern, så inget behöver installeras. Allt nollställs när API:et startas om.
//
// Beteenden som testgen kan visa:
//   - e-post är unik per kund: dubblett ger 409
//   - orders med quantity > 5 får 10 % rabatt
//   - e-post måste se ut som en e-postadress (specens format: email); "abc" och tom text ger 400
//   - sku måste följa specens mönster (annars 400)
//   - quantity 0 ger MEDVETET 500 (ett "bugg-exempel": testgen ska visa FEL för det fallet)
//   - sätts miljövariabeln SHOPAPI_TOKEN kräver alla anrop "Authorization: Bearer <värdet>" (401 annars)
//
// Start:  dotnet run --project samples/ShopApi        (lyssnar på http://localhost:5080)
// Annan port:  dotnet run --project samples/ShopApi --urls http://localhost:6000

using System.Text.Encodings.Web;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using ShopApi.Validation;
using ShopApplication;
using ShopInfrastructure;

var builder = WebApplication.CreateBuilder(args);
if (string.IsNullOrEmpty(builder.Configuration["urls"]) && string.IsNullOrEmpty(builder.Configuration["ASPNETCORE_URLS"]))
    builder.WebHost.UseUrls("http://localhost:5080");
// Konsolen visar bara det som händer mot integrationerna (raderna "Lokalt läge: ..."), inte varje SQL-fråga och HTTP-anrop.
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning).AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);

builder.Services.AddControllers(o =>
    {
        o.Filters.Add<ShopErrorFilter>();
        // Bodyn läses alltid som JSON (ShopJsonBinder), även när Content-Type säger formulär: MVC ska inte läsa den först.
        o.ValueProviderFactories.RemoveType<FormValueProviderFactory>();
        o.ValueProviderFactories.RemoveType<FormFileValueProviderFactory>();
        o.ValueProviderFactories.RemoveType<JQueryFormValueProviderFactory>();
    })
    .ConfigureApiBehaviorOptions(o => o.InvalidModelStateResponseFactory = ErrorResponses.FromModelState)
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;   // fält som inte skickades syns inte i svaret
        o.JsonSerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;          // å, ä och ö skrivs som de är
    });
builder.Services.AddScoped<ICustomerService, CustomerService>();
builder.Services.AddScoped<IProductService, ProductService>();
builder.Services.AddScoped<IOrderService, OrderService>();
builder.Services.AddShopInfrastructure(builder.Configuration);

var app = builder.Build();
app.Services.EnsureShopDatabase();

// Valfri inloggning: bara om SHOPAPI_TOKEN är satt.
var requiredToken = Environment.GetEnvironmentVariable("SHOPAPI_TOKEN");
if (!string.IsNullOrEmpty(requiredToken))
{
    app.Use(async (ctx, next) =>
    {
        if (ctx.Request.Headers.Authorization.ToString() != $"Bearer {requiredToken}")
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsJsonAsync(new { error = "inloggning krävs" });
            return;
        }
        await next();
    });
}

// Oväntade fel blir 500 med ett kort, stabilt svar.
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (Exception)
    {
        ctx.Response.StatusCode = 500;
        await ctx.Response.WriteAsJsonAsync(new { error = "internal error" });
    }
});

app.MapControllers();
app.Run();
