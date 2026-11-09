using Azure.Storage.Blobs;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Som en app som använder sina tjänster vid start: utan databasen och lagringen startar den inte.
var database = new NpgsqlConnectionStringBuilder(app.Configuration["ConnectionStrings:Database"]);
await using (var connection = new NpgsqlConnection(database.ConnectionString))
{
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand("select 1", connection);
    await command.ExecuteScalarAsync();
}

var storage = new BlobServiceClient(app.Configuration["Storage:ConnectionString"]);
await storage.GetBlobContainerClient("photos").CreateIfNotExistsAsync();

app.MapGet("/ping", () => new { pong = true });
app.MapGet("/where", () => new { environment = app.Environment.EnvironmentName, database = $"{database.Host}:{database.Port}", storage = storage.Uri.Authority });

app.Run();
