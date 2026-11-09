var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Som en app som gör varje undantag till 500: utan ThrowOnBadRequest=false blir fel typ i kroppen 500 i Development.
app.UseExceptionHandler(handler => handler.Run(context => { context.Response.StatusCode = 500; return Task.CompletedTask; }));

// Som en app som kontrollerar sin konfiguration vid start: utan nyckeln startar den inte.
var key = app.Configuration["App:RequiredKey"];
if (string.IsNullOrEmpty(key)) throw new InvalidOperationException("App:RequiredKey is not configured");

app.MapGet("/ping", () => new { pong = true });
app.MapGet("/echo", () => Results.Json(new { error = key }, statusCode: 500));

app.MapGet("/env", () => Results.Json(new { error = app.Environment.EnvironmentName }, statusCode: 500));

app.MapPost("/typed", (Typed body) => new { body.Name });

app.Run();

public sealed record Typed(string Name);
