using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("authapp-signing-key-authapp-signing-key"));
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<Once>();
builder.Services.AddSingleton<Sessions>();
builder.Services.AddHttpClient();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.MapInboundClaims = false;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        IssuerSigningKey = key,
        ValidateIssuer = false,
        ValidateAudience = false,
        NameClaimType = "name",
        RoleClaimType = "role",
    };
    // En utloggad session gäller inte längre: token nekas med 401.
    options.Events = new JwtBearerEvents
    {
        OnTokenValidated = context =>
        {
            if (context.HttpContext.RequestServices.GetRequiredService<Sessions>().IsRevoked(context.Principal?.FindFirstValue("jti"))) context.Fail("utloggad");
            return Task.CompletedTask;
        },
    };
});
// En app som slår upp användaren själv: bara det här id:t är en känd användare.
builder.Services.AddAuthorizationBuilder().AddPolicy("known", p => p.RequireAssertion(c => c.User.FindFirstValue("sub") == "known-user"));
var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/open", () => new { open = true });
app.MapGet("/me", [Authorize] (ClaimsPrincipal user) => new { name = user.Identity?.Name, email = user.FindFirstValue("email") ?? user.FindFirstValue(ClaimTypes.Email) ?? "" });
app.MapGet("/admin", [Authorize(Roles = "Admin")] () => new { admin = true });
app.MapGet("/named", [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)] () => new { named = true });
app.MapGet("/strict", [Authorize(Policy = "known")] () => new { known = true });
app.MapPost("/login", (LoginRequest request, Sessions sessions) =>
{
    if (string.IsNullOrEmpty(request.UserName)) return Results.BadRequest();
    sessions.CountLogin();
    var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Subject = new ClaimsIdentity([new Claim("sub", "known-user"), new Claim("jti", Guid.NewGuid().ToString()), new Claim("name", request.UserName!), new Claim("role", "Admin")]),
        SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
    });
    return Results.Ok(new { accessToken = token });
});
app.MapPost("/logout", [Authorize] (ClaimsPrincipal user, Sessions sessions) =>
{
    sessions.Revoke(user.FindFirstValue("jti"));
    return Results.NoContent();
});
app.MapGet("/logins", (Sessions sessions) => sessions.Logins);
app.MapGet("/nope", () => Results.Unauthorized());
app.MapPost("/echo", [Authorize(Policy = "known")] (EchoRequest request) => string.IsNullOrEmpty(request.Text) ? Results.BadRequest() : Results.Ok(new { text = request.Text }));
// Anropar en annan tjänst; testgens värdpolicy stoppar anropet, och appen svarar då 503.
app.MapGet("/dependent", async (IHttpClientFactory clients) =>
{
    using var response = await clients.CreateClient().GetAsync("http://payments.example.com/status");
    return response.IsSuccessStatusCode ? Results.Ok(new { ok = true }) : Results.StatusCode(503);
});
// 429 med och utan Retry-After: en gång, alltid, utan huvud och med för lång väntan.
app.MapGet("/once", (Once once, HttpContext http) => TooMany(http, once.First() ? "1" : null, new { ok = true }));
app.MapGet("/always", (HttpContext http) => TooMany(http, "1", null));
app.MapGet("/never", (HttpContext http) => TooMany(http, null, null));
app.MapGet("/slow", (HttpContext http) => TooMany(http, "60", null));

app.Run();

static IResult TooMany(HttpContext http, string? retryAfter, object? ok)
{
    if (retryAfter is null && ok is not null) return Results.Ok(ok);
    if (retryAfter is not null) http.Response.Headers.RetryAfter = retryAfter;
    return Results.StatusCode(429);
}

sealed record LoginRequest(string? UserName);

sealed record EchoRequest(string? Text);

/// <summary>Inloggningarna hittills och de utloggade sessionerna.</summary>
sealed class Sessions
{
    private readonly HashSet<string> _revoked = [];

    private int _logins;

    public int Logins => _logins;

    public void CountLogin() => Interlocked.Increment(ref _logins);

    public void Revoke(string? id)
    {
        if (id is not null) lock (_revoked) _revoked.Add(id);
    }

    public bool IsRevoked(string? id)
    {
        lock (_revoked) return id is not null && _revoked.Contains(id);
    }
}

/// <summary>Första anropet får 429 med Retry-After, sedan går det bra.</summary>
sealed class Once
{
    private int _calls;

    public bool First() => Interlocked.Increment(ref _calls) == 1;
}
