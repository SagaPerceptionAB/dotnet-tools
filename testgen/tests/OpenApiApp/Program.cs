var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi();

var app = builder.Build();
app.MapOpenApi();

app.MapGet("/greetings/{id:int}", (int id) => new Greeting(id, "Hej"));
app.MapPost("/greetings", (GreetingInput input) => Results.Created("/greetings/1", new Greeting(1, input.Text))).Produces<Greeting>(StatusCodes.Status201Created);

app.Run();

public sealed record Greeting(int Id, string Text);

public sealed record GreetingInput(string Text, int? Priority);
