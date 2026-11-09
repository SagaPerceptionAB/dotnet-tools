using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();

// Läses före Build(), som en adress i många appar: bara UseSetting når hit.
var early = builder.Configuration["Early:Key"] ?? "";

var app = builder.Build();
app.Configuration["AfterBuild:Ran"] = "yes";   // sätts bara om koden efter Build() körs
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready");
app.MapGet("/ping", (ILogger<Program> logger) =>
{
    using var activity = TestApiTelemetry.Source.StartActivity("ping-work");
    logger.LogInformation("Ping mottagen {Value}", 42);
    return "pong";
});
app.MapGet("/early", () => early);
app.MapGet("/setting/{key}", (string key, IConfiguration config) => config[key] ?? "");
app.Run();

public partial class Program;

public static class TestApiTelemetry
{
    public const string SourceName = "TestApi";
    public static readonly ActivitySource Source = new(SourceName);
}
