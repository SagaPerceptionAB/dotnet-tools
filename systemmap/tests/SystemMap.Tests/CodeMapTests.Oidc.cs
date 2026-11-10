namespace SystemMap.Tests;

/// <summary>OIDC-discovery: att hämta discovery-dokumentet och nycklarna (JWKS) är HTTP ut ur systemet (kod i minnet).</summary>
public partial class CodeMapTests
{
    private const string OidcUsings = """
        using Microsoft.IdentityModel.Protocols;
        using Microsoft.IdentityModel.Protocols.OpenIdConnect;
        using Microsoft.Extensions.Configuration;
        """;

    private const string OidcTechnique = "HTTP (OIDC-konfiguration och nycklar)";

    private static string Oidc(string members, string body) => OidcUsings + Controller(members, body);

    [TestCase("ConfigurationManager<OpenIdConnectConfiguration> _manager = new(\"https://appleid.apple.com/.well-known/openid-configuration\", new OpenIdConnectConfigurationRetriever());",
        "await _manager.GetConfigurationAsync(CancellationToken.None);", TestName = "Fält med new(...) som anropas")]
    [TestCase("IConfigurationManager<OpenIdConnectConfiguration> _manager = new ConfigurationManager<OpenIdConnectConfiguration>(\"https://appleid.apple.com/.well-known/openid-configuration\", new OpenIdConnectConfigurationRetriever(), new HttpDocumentRetriever());",
        "await _manager.GetConfigurationAsync(CancellationToken.None);", TestName = "Fält av gränssnittet")]
    [TestCase("", "var manager = new ConfigurationManager<OpenIdConnectConfiguration>(\"https://appleid.apple.com/.well-known/openid-configuration\", new OpenIdConnectConfigurationRetriever()); manager.RequestRefresh();",
        TestName = "Lokal variabel och RequestRefresh")]
    [TestCase("const string Authority = \"https://appleid.apple.com\"; ConfigurationManager<OpenIdConnectConfiguration> _manager = new($\"{Authority}/.well-known/openid-configuration\", new OpenIdConnectConfigurationRetriever());",
        "await _manager.GetConfigurationAsync(CancellationToken.None);", TestName = "Adressen ur en konstant")]
    [TestCase("", "await OpenIdConnectConfigurationRetriever.GetAsync(\"https://appleid.apple.com/.well-known/openid-configuration\", CancellationToken.None);",
        TestName = "OpenIdConnectConfigurationRetriever.GetAsync")]
    public async Task OIDC_discovery_is_an_HTTP_integration_named_after_the_host_of_the_address(string members, string body)
    {
        var map = await MapAsync(Oidc(members, body));
        var use = All(map.EntryPoints.Single().Steps).Single(s => s.Integration is not null).Integration!;
        Assert.That((use.Id, use.Operation, use.AddressAtRuntime), Is.EqualTo(("http:appleid.apple.com", "GET https://appleid.apple.com/.well-known/openid-configuration", false)));
        var integration = map.Integrations.Single();
        Assert.That((integration.Kind, integration.Name, integration.Detail), Is.EqualTo((IntegrationKind.Http, "appleid.apple.com", OidcTechnique)),
            "att nycklarna också hämtas står som teknik, verbet är data");
    }

    [Test]
    public async Task OIDC_address_is_followed_from_a_parameter_a_constructor_and_a_factory_so_two_providers_stay_two_integrations()
    {
        // Samma form som i en riktig webb-API-solution: hanterarna skapas i en fabrik, hålls av en klass och skickas till en metod.
        var map = await MapAsync(OidcUsings + """
            using Microsoft.AspNetCore.Mvc;
            public sealed class Providers(IConfigurationManager<OpenIdConnectConfiguration> apple, IConfigurationManager<OpenIdConnectConfiguration> google)
            {
                public IConfigurationManager<OpenIdConnectConfiguration> Apple { get; } = apple;
                public IConfigurationManager<OpenIdConnectConfiguration> Google { get; } = google;
                public static Providers FromDiscovery() => new(Discover("https://appleid.apple.com"), Discover("https://accounts.google.com"));
                private static ConfigurationManager<OpenIdConnectConfiguration> Discover(string authority) =>
                    new($"{authority}/.well-known/openid-configuration", new OpenIdConnectConfigurationRetriever());
            }
            public sealed class Verifier(Providers providers)
            {
                public Task<OpenIdConnectConfiguration> AppleAsync(CancellationToken ct) => Fetch(providers.Apple, ct);
                public Task<OpenIdConnectConfiguration> GoogleAsync(CancellationToken ct) => Fetch(providers.Google, ct);
                private static Task<OpenIdConnectConfiguration> Fetch(IConfigurationManager<OpenIdConnectConfiguration> provider, CancellationToken ct) => provider.GetConfigurationAsync(ct);
            }
            [ApiController, Route("x")]
            public class XController(Verifier verifier) : ControllerBase
            {
                [HttpPost] public async Task Run() { await verifier.AppleAsync(default); await verifier.GoogleAsync(default); }
            }
            public static class Start { public static Providers Make() => Providers.FromDiscovery(); }
            """);
        Assert.That(Uses(map.EntryPoints.Single()), Is.EquivalentTo(new[]
        {
            "http:appleid.apple.com | GET https://appleid.apple.com/.well-known/openid-configuration",
            "http:accounts.google.com | GET https://accounts.google.com/.well-known/openid-configuration",
        }));
    }

    [Test]
    public async Task OIDC_address_from_configuration_uses_the_key_like_other_HTTP_calls()
    {
        var map = await MapAsync(Oidc(
            "IConfiguration _config = null!; ConfigurationManager<OpenIdConnectConfiguration> Manager() => new(_config[\"Apple:Authority\"] + \"/.well-known/openid-configuration\", new OpenIdConnectConfigurationRetriever());",
            "await Manager().GetConfigurationAsync(CancellationToken.None);"));
        Assert.That(Uses(map.EntryPoints.Single()), Is.EqualTo(new[] { "http:Apple | GET {Apple:Authority}/.well-known/openid-configuration" }));
        Assert.That(map.Integrations.Single().Detail, Is.EqualTo(OidcTechnique + ", adressen i Apple:Authority"));
    }

    [TestCase("ConfigurationManager<OpenIdConnectConfiguration> _manager = null!;", "await _manager.GetConfigurationAsync(CancellationToken.None);", TestName = "Hanteraren skapas inte i koden")]
    [TestCase("static string Hamta() => Environment.GetEnvironmentVariable(\"AUTH\")!; ConfigurationManager<OpenIdConnectConfiguration> _manager = new(Hamta(), new OpenIdConnectConfigurationRetriever());",
        "await _manager.GetConfigurationAsync(CancellationToken.None);", TestName = "Adressen kommer ur en metod som inte går att läsa")]
    [TestCase("", "await OpenIdConnectConfigurationRetriever.GetAsync(Environment.GetEnvironmentVariable(\"AUTH\")!, CancellationToken.None);", TestName = "GetAsync med okänd adress")]
    public async Task OIDC_discovery_with_an_address_that_cannot_be_read_is_flagged_as_decided_at_run_time(string members, string body)
    {
        var map = await MapAsync(Oidc(members, body));
        var use = All(map.EntryPoints.Single().Steps).Single(s => s.Integration is not null).Integration!;
        Assert.That((use.Id, use.Operation, use.AddressAtRuntime), Is.EqualTo(("http:XController", "GET", true)), "verbet är data, adressen bestäms vid körning");
    }

    [Test]
    public async Task OIDC_manager_that_two_paths_create_with_different_addresses_is_not_guessed()
    {
        var map = await MapAsync(OidcUsings + """
            using Microsoft.AspNetCore.Mvc;
            public sealed class Holder(ConfigurationManager<OpenIdConnectConfiguration> manager) { public Task Get() => manager.GetConfigurationAsync(default); }
            public static class A { public static Holder Make() => new(new("https://a.example/.well-known/openid-configuration", new OpenIdConnectConfigurationRetriever())); }
            public static class B { public static Holder Make() => new(new("https://b.example/.well-known/openid-configuration", new OpenIdConnectConfigurationRetriever())); }
            [ApiController, Route("x")]
            public class XController(Holder holder) : ControllerBase { [HttpPost] public Task Run() => holder.Get(); }
            """);
        Assert.That(Uses(map.EntryPoints.Single()), Is.EqualTo(new[] { "http:Holder | GET" }), "ett fel namn är värre än en adress som sägs bestämmas vid körning");
    }

    [Test]
    public async Task OIDC_near_misses_are_not_integrations()
    {
        var map = await MapAsync(OidcUsings + """
            using Microsoft.AspNetCore.Mvc;
            namespace Egen { public class ConfigurationManager<T> { public Task<T> GetConfigurationAsync(CancellationToken ct) => null!; public void RequestRefresh() { } } }
            public class Cache { public Task<string> GetConfigurationAsync(string name) => Task.FromResult(name); }
            [ApiController, Route("x")]
            public class XController : ControllerBase
            {
                Egen.ConfigurationManager<string> _own = new();
                Cache _cache = new();
                ConfigurationManager<OpenIdConnectConfiguration> _manager = new("https://appleid.apple.com/.well-known/openid-configuration", new OpenIdConnectConfigurationRetriever());
                [HttpPost] public async Task Run()
                {
                    await _own.GetConfigurationAsync(default);
                    _own.RequestRefresh();
                    await _cache.GetConfigurationAsync("x");
                    var unused = _manager;   // att hålla hanteraren är inte att hämta något
                }
            }
            """);
        Assert.That(Uses(map.EntryPoints.Single()), Is.Empty);
    }

    [Test]
    public async Task OIDC_discovery_is_recognized_by_name_when_the_package_is_missing()
    {
        var map = await MapAsync(Oidc(
            "ConfigurationManager<OpenIdConnectConfiguration> _manager = new(\"https://appleid.apple.com/.well-known/openid-configuration\", new OpenIdConnectConfigurationRetriever());",
            "await _manager.GetConfigurationAsync(CancellationToken.None);").Replace("using Microsoft.Extensions.Configuration;", ""),   // ramverket har en egen ConfigurationManager
            stubs: false);
        Assert.That(Uses(map.EntryPoints.Single()), Is.EqualTo(new[] { "http:appleid.apple.com | GET https://appleid.apple.com/.well-known/openid-configuration" }),
            "typerna går inte att läsa, men namnen räcker");
    }

    [Test]
    public async Task HttpDocumentRetriever_is_a_plain_HTTP_GET_of_the_document()
    {
        var map = await MapAsync(Oidc("", "await new HttpDocumentRetriever().GetDocumentAsync(\"https://www.googleapis.com/oauth2/v3/certs\", CancellationToken.None);"));
        Assert.That(Uses(map.EntryPoints.Single()), Is.EqualTo(new[] { "http:www.googleapis.com | GET https://www.googleapis.com/oauth2/v3/certs" }));
        Assert.That(map.Integrations.Single().Detail, Is.EqualTo("HTTP"));
    }
}
