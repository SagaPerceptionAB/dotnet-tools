using ShopWeb.Components;
using ShopWeb.Data;

var builder = WebApplication.CreateBuilder(args);

// Statisk SSR: inga interaktiva render modes, så inget WASM behövs.
builder.Services.AddRazorComponents();
builder.Services.AddRazorPages();
builder.Services.AddSingleton<ShopStore>();

// ShopApi: adressen står i ShopApi:BaseUrl (miljövariabeln ShopApi__BaseUrl). Standard är exempel-API:ets egen port.
var apiBaseUrl = builder.Configuration["ShopApi:BaseUrl"] is { Length: > 0 } url ? url : "http://localhost:5080";
builder.Services.AddHttpClient<ShopApiClient>(client =>
{
    client.BaseAddress = new Uri(apiBaseUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(10);
});

var app = builder.Build();

if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/Error");

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorPages();
app.MapRazorComponents<App>();

app.Run();
