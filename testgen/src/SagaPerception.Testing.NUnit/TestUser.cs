using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SagaPerception.Testing.NUnit;

/// <summary>
/// Testanvändaren för en fixtur: en fiktiv inloggad användare som appen i testprocessen godtar när anropet bär körningens slumpade token.
/// Appens egen autentisering är orörd för alla andra anrop. Tjänsten byter ut <see cref="IAuthenticationHandlerProvider"/>: bär anropet
/// token ger den en hanterare som loggar in användaren, oavsett vilket schema appen frågar efter (standardschemat, ett schema som anges
/// med namn eller ett policyschema). Därför behövs ingen referens till JwtBearer eller Cookies, och appens scheman ändras inte.
/// </summary>
internal sealed class TestUser
{
    public const string Scheme = "testgen";

    private static readonly (string Type, string Long)[] LongTypes =
    [
        ("sub", ClaimTypes.NameIdentifier),
        ("name", ClaimTypes.Name),
        ("email", ClaimTypes.Email),
        ("role", ClaimTypes.Role),
    ];

    private readonly List<Claim> _claims = [];

    private TestUser(string token, IReadOnlyDictionary<string, string> claims)
    {
        Token = token;
        Claims = claims;
        foreach (var (type, value) in claims)
        {
            var values = type.Equals("role", StringComparison.OrdinalIgnoreCase) ? value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) : [value];
            foreach (var v in values)
            {
                _claims.Add(new Claim(type, v));
                if (LongTypes.FirstOrDefault(t => t.Type.Equals(type, StringComparison.OrdinalIgnoreCase)).Long is { } longType)
                    _claims.Add(new Claim(longType, v));
            }
        }
    }

    /// <summary>Körningens slumpade token. Hemlig: maskas i all utdata.</summary>
    public string Token { get; }

    /// <summary>Claims efter att <c>auth.claims</c> lagts över standardvärdena.</summary>
    public IReadOnlyDictionary<string, string> Claims { get; }

    /// <summary>Sidhuvudet som bär token.</summary>
    public KeyValuePair<string, string> Header => KeyValuePair.Create("Authorization", "Bearer " + Token);

    /// <param name="claims">Claims ur <c>auth.claims</c>; det som saknas får fiktiva värden.</param>
    public static TestUser Create(IReadOnlyDictionary<string, string> claims)
    {
        var all = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["sub"] = "5c0b8a3e-0d3f-4f6e-9a57-3f1c2b7e8d10",
            ["name"] = "Test Testsson",
            ["email"] = "test.testsson@example.com",
        };
        foreach (var (type, value) in claims) all[type] = value;
        return new TestUser(Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant(), all);
    }

    /// <summary>Bär anropet token, som <c>Authorization: Bearer token</c> eller som värdet i en annan header (t.ex. en API-nyckel)?</summary>
    public bool Carries(HttpRequest request)
    {
        foreach (var header in request.Headers)
            foreach (var value in header.Value)
                if (value is not null && (value == Token || (value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && value.AsSpan(7).Trim().SequenceEqual(Token.AsSpan()))))
                    return true;
        return false;
    }

    internal ClaimsPrincipal Principal() =>
        new(new ClaimsIdentity(_claims, Scheme, ClaimTypes.Name, ClaimTypes.Role));

    /// <summary>Registrerar testanvändaren i appens tjänster (kallas via <c>WithServices</c>).</summary>
    public void Register(IServiceCollection services)
    {
        var original = services.LastOrDefault(d => d.ServiceType == typeof(IAuthenticationHandlerProvider));
        if (original is not null) services.Remove(original);
        services.Add(new ServiceDescriptor(typeof(IAuthenticationHandlerProvider),
            sp => new Provider(Inner(sp, original), this), original?.Lifetime ?? ServiceLifetime.Transient));
    }

    private static IAuthenticationHandlerProvider Inner(IServiceProvider sp, ServiceDescriptor? original) => original switch
    {
        { ImplementationInstance: IAuthenticationHandlerProvider instance } => instance,
        { ImplementationFactory: { } factory } => (IAuthenticationHandlerProvider)factory(sp),
        { ImplementationType: { } type } => (IAuthenticationHandlerProvider)ActivatorUtilities.CreateInstance(sp, type),
        _ => ActivatorUtilities.CreateInstance<AuthenticationHandlerProvider>(sp),
    };

    private sealed class Provider(IAuthenticationHandlerProvider inner, TestUser user) : IAuthenticationHandlerProvider
    {
        public async Task<IAuthenticationHandler?> GetHandlerAsync(HttpContext context, string authenticationScheme)
        {
            if (!user.Carries(context.Request)) return await inner.GetHandlerAsync(context, authenticationScheme);
            var handler = new Handler(user);
            await handler.InitializeAsync(new AuthenticationScheme(Scheme, null, typeof(Handler)), context);
            return handler;
        }
    }

    private sealed class Handler(TestUser user) : IAuthenticationHandler
    {
        private HttpContext _context = null!;
        private AuthenticationScheme _scheme = null!;

        public Task InitializeAsync(AuthenticationScheme scheme, HttpContext context)
        {
            _scheme = scheme;
            _context = context;
            return Task.CompletedTask;
        }

        public Task<AuthenticateResult> AuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(user.Principal(), _scheme.Name)));

        public Task ChallengeAsync(AuthenticationProperties? properties)
        {
            _context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }

        public Task ForbidAsync(AuthenticationProperties? properties)
        {
            _context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }
    }
}
