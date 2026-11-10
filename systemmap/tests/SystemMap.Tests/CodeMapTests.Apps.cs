namespace SystemMap.Tests;

/// <summary>Minimal API, lokala variabler, fakta per app och sorter av ingångar (kod i minnet).</summary>
public partial class CodeMapTests
{
    private const string WebUsings = """
        using Microsoft.AspNetCore.Builder;
        using Microsoft.AspNetCore.Http;
        using Microsoft.AspNetCore.Mvc;
        using Microsoft.AspNetCore.Routing;
        using Microsoft.Extensions.Configuration;
        using Microsoft.Extensions.DependencyInjection;
        using Microsoft.Extensions.Options;
        """;

    private static Proj Web(string name, string code, params string[] references) => new(name, WebUsings + "\n" + code, references);

    // --- Minimal API -------------------------------------------------------------------------

    [Test]
    public async Task Minimal_API_gives_entries_with_prefixes_from_groups_and_callers()
    {
        var map = await MapAsync(Build(true, Web("Api", """
            public record Hej(string Text);
            public record NyttHej(string Text);
            public interface IHejTjanst { Hej Hej(string? namn); }
            public class HejTjanst : IHejTjanst { public Hej Hej(string? n) { System.IO.File.ReadAllText("hej.txt"); return new Hej("x"); } }
            public static class HejEndpoints
            {
                public static IEndpointRouteBuilder MapHej(this IEndpointRouteBuilder app)
                {
                    app.MapGet("/api/hej", (IHejTjanst t, string? namn) => t.Hej(namn)).WithName("Hej");
                    return app;
                }
                public static void MapMer(RouteGroupBuilder g)
                {
                    g.MapPost("/", (NyttHej body) => Results.Ok(new Hej(body.Text)));
                    g.MapMethods("/{id:int}", ["PUT", "PATCH"], Uppdatera);
                }
                static IResult Uppdatera(int id, NyttHej body) => Results.NoContent();
            }
            public static class Start
            {
                public static void Run(string[] args)
                {
                    var builder = WebApplication.CreateBuilder(args);
                    builder.Services.AddScoped<IHejTjanst, HejTjanst>();
                    var app = builder.Build();
                    app.MapHej();
                    var v1 = app.MapGroup("/api").MapGroup("v1").RequireAuthorization();
                    HejEndpoints.MapMer(v1);
                    app.MapDelete("/ta-bort/{id}", Tabort);
                    app.Run();
                }
                static IResult Tabort(int id) => Results.NoContent();
            }
            """)));

        Assert.That(map.EntryPoints.Select(e => $"{e.Kind} {e.Method} {e.Path} {e.Handler}"), Is.EqualTo(new[]
        {
            "MinimalApi GET /api/hej HejEndpoints.MapHej",
            "MinimalApi POST /api/v1 HejEndpoints.MapMer",
            "MinimalApi PUT /api/v1/{id} HejEndpoints.Uppdatera",
            "MinimalApi PATCH /api/v1/{id} HejEndpoints.Uppdatera",
            "MinimalApi DELETE /ta-bort/{id} Start.Tabort",
        }));
        var get = map.EntryPoints.First();
        Assert.That(Uses(get), Is.EqualTo(new[] { "file | läsa hej.txt" }), "anropen följs från lambdan genom tjänsten");
        Assert.That(get.Steps.Single().Member, Is.EqualTo("HejTjanst.Hej"));
        var post = map.EntryPoints.Single(e => e.Method == "POST");
        Assert.That((post.Input?.Type, post.Output?.Type), Is.EqualTo(("NyttHej", "Hej")));
        Assert.That(map.EntryPoints.Select(e => e.File), Has.All.Not.Empty);
    }

    [Test]
    public async Task Minimal_API_input_is_the_body_and_never_a_service_or_a_type_ASP_NET_binds_from_elsewhere()
    {
        var map = await MapAsync(Build(true, Web("Api", """
            public record Svar(string Text);
            public record NyttHej(string Text);
            public class Verifierare { }
            public class Session { }
            public class Typ { public static bool TryParse(string? s, out Typ? t) { t = null; return false; } }
            public class Egen { public static ValueTask<Egen?> BindAsync(HttpContext c) => default; }
            public class Filter { public string? Q { get; set; } }
            public static class Start
            {
                public static void Run(string[] args)
                {
                    var builder = WebApplication.CreateBuilder(args);
                    builder.Services.AddScoped<Verifierare>();
                    builder.Services.AddSingleton(typeof(Session));
                    var app = builder.Build();
                    app.MapPost("/a/{p}", (Verifierare v, NyttHej body) => 1);
                    app.MapPost("/b", (Verifierare v) => 2);
                    app.MapPut("/c", (Session s) => 3);
                    app.MapPost("/e", (Typ t, Egen e) => 5);
                    app.MapPost("/f", ([AsParameters] Filter f, [FromForm] NyttHej h) => 6);
                    app.Run();
                }
            }
            """)));

        string? Input(string path) => map.EntryPoints.Single(e => e.Path == path).Input?.Type;
        Assert.That(Input("/a/{p}"), Is.EqualTo("NyttHej"), "kroppen finns kvar när en tjänst står före");
        Assert.That(new[] { "/b", "/c", "/e", "/f" }.Select(Input), Has.All.Null,
            "en tjänst, BindAsync/TryParse, [AsParameters] och [FromForm] är inte kroppen");
    }

    [Test]
    public async Task Controller_input_is_never_a_registered_service()
    {
        var map = await MapAsync(Build(true, Web("Api", """
            public class Verifierare { }
            public record NyOrder(string Text);
            [ApiController, Route("x")]
            public class XController : ControllerBase
            {
                [HttpPost("a")] public IActionResult A(Verifierare v, NyOrder o) => Ok();
                [HttpPost("b")] public IActionResult B(Verifierare v) => Ok();
            }
            public static class Start
            {
                public static void Run(string[] args)
                {
                    var builder = WebApplication.CreateBuilder(args);
                    builder.Services.AddScoped<Verifierare>();
                    builder.Build().Run();
                }
            }
            """)));

        Assert.That(map.EntryPoints.Select(e => (e.Path, e.Input?.Type)), Is.EqualTo(new (string, string?)[] { ("/x/a", "NyOrder"), ("/x/b", null) }));
    }

    [Test]
    public async Task Minimal_API_links_to_the_spec_and_an_unreadable_path_gives_a_warning()
    {
        var map = await MapAsync(Build(true, Web("Api", """
            public static class Start
            {
                public static void Run(WebApplication app, string path)
                {
                    app.MapGet("/orders/{id}", (int id) => id);
                    app.MapGet(path, () => 1);
                }
            }
            """)), [Op("getOrder", "GET", "/orders/{id}")]);
        Assert.That(map.EntryPoints.Single().OperationId, Is.EqualTo("getOrder"));
        Assert.That(map.Differences, Is.Empty);
        Assert.That(map.Warnings, Has.Some.Contains("MapGet i").And.Contain("ingen sökväg"));
    }

    // --- Lokala variabler --------------------------------------------------------------------

    [TestCase("""var baseUrl = config["Lager:BaseUrl"] is { Length: > 0 } url ? url : "http://x"; s.AddHttpClient<LagerKlient>(c => c.BaseAddress = new Uri(baseUrl));""",
        TestName = "Initierare med mönster och villkor")]
    [TestCase("""var first = config["Lager:BaseUrl"]; var second = first; s.AddHttpClient<LagerKlient>(c => c.BaseAddress = new Uri(second!));""",
        TestName = "Kedja av variabler")]
    [TestCase("""string? adress; adress = config["Lager:BaseUrl"]; s.AddHttpClient<LagerKlient>(c => c.BaseAddress = new Uri(adress!));""",
        TestName = "Tilldelning")]
    [TestCase("""if (config["Lager:BaseUrl"] is string u) { s.AddHttpClient<LagerKlient>(c => c.BaseAddress = new Uri(u)); }""",
        TestName = "Mönstervariabel")]
    public async Task Config_key_is_found_through_local_variables(string registration)
    {
        var map = await MapAsync(Build(true, Web("Api", $$"""
            public class LagerKlient(System.Net.Http.HttpClient http) { public Task<System.Net.Http.HttpResponseMessage> Hamta() => http.GetAsync("artiklar"); }
            public static class Registrering { public static void Lagg(IServiceCollection s, IConfiguration config) { {{registration}} } }
            [ApiController, Route("x")]
            public class XController(LagerKlient lager) : ControllerBase { [HttpGet] public Task Hamta() => lager.Hamta(); }
            """)));
        Assert.That(map.Integrations.Select(i => (i.Id, i.ConfigKey)), Is.EqualTo(new[] { ("http:Lager", "Lager:BaseUrl") }));
        Assert.That(Uses(map.EntryPoints.Single()), Is.EqualTo(new[] { "http:Lager | GET artiklar" }));
    }

    [Test]
    public async Task MapMethods_reads_HttpMethods_fields_and_warns_when_the_methods_cannot_be_read()
    {
        var map = await MapAsync(Build(true, Web("Api", """
            public static class Start
            {
                public static void Run(WebApplication app, string[] unknown)
                {
                    var photos = app.MapGroup("/photos");
                    photos.MapMethods("/{name}", [HttpMethods.Head], (string name) => 1);
                    photos.MapMethods("/", new[] { HttpMethods.Get, HttpMethods.Post, "put" }, () => 2);
                    app.MapMethods("/other", unknown, () => 3);
                }
            }
            """)));
        Assert.That(map.EntryPoints.Select(e => $"{e.Method} {e.Path}"), Is.EqualTo(new[] { "GET /photos", "POST /photos", "PUT /photos", "HEAD /photos/{name}" }));
        Assert.That(map.Warnings, Has.Some.Contains("MapMethods i").And.Contain("inga metoder"));
    }

    // --- Adresser ur options-klasser ---------------------------------------------------------

    private const string PartnerCode = """
        public class PartnerOptions
        {
            public const string SectionName = "Partner";
            public string BaseUrl { get; set; } = "https://default.partner.test";
            public string TokenEndpoint { get; set; } = "https://auth.partner.test/token";
            public string ApiKey { get; set; } = "https://hidden.partner.test/key";
            public string Empty { get; set; } = "";
        }
        public class PartnerClient(System.Net.Http.HttpClient http, IOptions<PartnerOptions> options)
        {
            public Task A() => Send("/a");
            public Task B() => Send("/b");
            public Task C() => Send("/c");
            public Task Token() => http.PostAsync(options.Value.TokenEndpoint, null);
            public Task Key() => http.GetAsync(options.Value.ApiKey);
            public Task Unset() => http.GetAsync(options.Value.Empty);
            private Task Send(string path)
            {
                var settings = options.Value;
                using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, new Uri(new Uri(settings.BaseUrl), path));
                return http.SendAsync(request);
            }
        }
        public static class R
        {
            public static void Lagg(IServiceCollection s)
            {
                s.AddOptions<PartnerOptions>().BindConfiguration(PartnerOptions.SectionName);
                s.AddHttpClient<PartnerClient>();
            }
        }
        [ApiController, Route("p")]
        public class PController(PartnerClient c) : ControllerBase
        {
            [HttpGet("ab")] public async Task Ab() { await c.A(); await c.B(); }
            [HttpGet("c")] public Task C() => c.C();
            [HttpGet("token")] public async Task Token() { await c.Token(); await c.Key(); await c.Unset(); }
        }
        """;

    /// <summary>Kör <paramref name="test"/> med en appsettings.json i projektets katalog (annars finns ingen).</summary>
    private static async Task WithAppSettings(string project, string? json, Func<Task> test)
    {
        var dir = Path.Combine(Root, project);
        Directory.CreateDirectory(dir);
        if (json is not null) File.WriteAllText(Path.Combine(dir, "appsettings.json"), json);
        try { await test(); }
        finally { Directory.Delete(dir, true); }
    }

    [TestCase("""{ "Partner": { "BaseUrl": "https://api.partner.test/", "ApiKey": "https://hidden.partner.test" } }""", "https://api.partner.test",
        TestName = "Adressen ur appsettings.json")]
    [TestCase(null, "https://default.partner.test", TestName = "Adressen ur egenskapens startvärde")]
    [TestCase("""{ "Partner": { "BaseUrl": "not an address" } }""", "https://default.partner.test", TestName = "Ett värde som inte är en adress ersätts av startvärdet")]
    public async Task Options_address_gives_the_config_key_and_the_address_in_the_operation(string? appsettings, string address)
    {
        await WithAppSettings("OptionsApp", appsettings, async () =>
        {
            var map = await MapAsync(Build(true, Web("OptionsApp", AppStart + PartnerCode)));

            Assert.That(map.Integrations.Single().Id, Is.EqualTo("http:PartnerClient"));
            Assert.That(map.Integrations.Single().ConfigKey, Is.EqualTo("Partner:BaseUrl"));
            Assert.That(map.Apps.Single().HttpClients.Single().ConfigKey, Is.EqualTo("Partner:BaseUrl"), "appens klient får nyckeln som anropen hittade");
            var uses = map.EntryPoints.ToDictionary(e => e.Path, Uses);
            Assert.That(uses["/p/ab"], Is.EqualTo(new[] { $"http:PartnerClient | POST {address}/a", $"http:PartnerClient | POST {address}/b" }), "två anrop med olika sökvägar i samma metod");
            Assert.That(uses["/p/c"], Is.EqualTo(new[] { $"http:PartnerClient | POST {address}/c" }));
        });
    }

    [Test]
    public async Task Address_shows_endpoints_and_never_a_value_for_a_key_that_looks_like_a_secret()
    {
        await WithAppSettings("SecretApp", """{ "Partner": { "ApiKey": "https://hidden.partner.test", "Empty": "" } }""", async () =>
        {
            var map = await MapAsync(Build(true, Web("SecretApp", AppStart + PartnerCode)));

            Assert.That(Uses(map.EntryPoints.Single(e => e.Path == "/p/token")), Is.EqualTo(new[]
            {
                "http:PartnerClient | POST https://auth.partner.test/token",   // TokenEndpoint är en adress, inte en hemlighet
                "http:PartnerClient | GET {Partner:ApiKey}",
                "http:PartnerClient | GET {Partner:Empty}",
            }));
            Assert.That(string.Join("\n", CodeMapText.Lines(map)) + CodeMapJson.Serialize(map) + CodeMapHtml.Write(map, new HashSet<string>(), showUnreached: true), Does.Not.Contain("hidden.partner.test"));
        });
    }

    [TestCase("""s.AddOptions<XOptions>().Bind(config.GetSection("X"));""", "X:Url", TestName = "Bind med GetSection")]
    [TestCase("""s.Configure<XOptions>(config.GetSection(XOptions.Section));""", "X:Url", TestName = "Configure med GetSection")]
    [TestCase("""s.Configure<XOptions>(config.GetSection("Outer").GetSection("X"));""", "Outer:X:Url", TestName = "Nästlad sektion")]
    [TestCase("""s.AddOptions<XOptions>().BindConfiguration("Outer:X");""", "Outer:X:Url", TestName = "BindConfiguration med kolon")]
    public async Task Options_section_is_found_from_the_registration(string registration, string key)
    {
        var map = await MapAsync(Build(true, Web("Api", $$"""
            {{AppStart}}
            public class XOptions { public const string Section = "X"; public string Url { get; set; } = ""; }
            public class XKlient(System.Net.Http.HttpClient http, IOptionsSnapshot<XOptions> options) { public Task Hamta() { var u = options.Value; return http.GetAsync(u.Url); } }
            public static class R { public static void Lagg(IServiceCollection s, IConfiguration config) { {{registration}} s.AddHttpClient<XKlient>(); } }
            [ApiController, Route("x")] public class XController(XKlient k) : ControllerBase { [HttpGet] public Task Hamta() => k.Hamta(); }
            """)));
        Assert.That(map.Integrations.Single().ConfigKey, Is.EqualTo(key));
        Assert.That(Uses(map.EntryPoints.Single()), Is.EqualTo(new[] { $"http:XKlient | GET {{{key}}}" }));
    }

    [Test]
    public async Task Nested_option_properties_give_nested_keys()
    {
        var map = await MapAsync(Build(true, Web("Api", $$"""
            {{AppStart}}
            public class Inner { public string Url { get; set; } = ""; }
            public class YOptions { public Inner Remote { get; set; } = new(); }
            public class YKlient(System.Net.Http.HttpClient http, IOptionsMonitor<YOptions> options) { public Task Hamta() => http.GetAsync(options.CurrentValue.Remote.Url); }
            public static class R { public static void Lagg(IServiceCollection s) { s.AddOptions<YOptions>().BindConfiguration("Y"); s.AddHttpClient<YKlient>(); } }
            [ApiController, Route("y")] public class YController(YKlient k) : ControllerBase { [HttpGet] public Task Hamta() => k.Hamta(); }
            """)));
        Assert.That(map.Integrations.Single().ConfigKey, Is.EqualTo("Y:Remote:Url"));
    }

    // --- Parametrar följs till anroparen -----------------------------------------------------

    [Test]
    public async Task A_static_helper_called_by_two_typed_clients_gives_two_integrations_each_with_its_own_key()
    {
        var map = await MapAsync(Build(true, Web("Api", $$"""
            {{AppStart}}
            public class AOptions { public string Url { get; set; } = ""; }
            public class BOptions { public string Url { get; set; } = ""; }
            internal static class Helper
            {
                public static Task<System.Net.Http.HttpResponseMessage> Post(System.Net.Http.HttpClient http, string url, string path) =>
                    http.PostAsync(new Uri(new Uri(url), path), null);
            }
            public class AKlient(System.Net.Http.HttpClient http, IOptions<AOptions> options) { public Task Go() => Helper.Post(http, options.Value.Url, "/a"); }
            public class BKlient(System.Net.Http.HttpClient http, IOptions<BOptions> options) { public Task Go() => Helper.Post(http, options.Value.Url, "/b"); }
            public static class R
            {
                public static void Lagg(IServiceCollection s)
                {
                    s.AddOptions<AOptions>().BindConfiguration("A");
                    s.AddOptions<BOptions>().BindConfiguration("B");
                    s.AddHttpClient<AKlient>();
                    s.AddHttpClient<BKlient>();
                }
            }
            [ApiController, Route("z")]
            public class ZController(AKlient a, BKlient b) : ControllerBase
            {
                [HttpGet("a")] public Task A() => a.Go();
                [HttpGet("b")] public Task B() => b.Go();
                [HttpGet("both")] public async Task Both() { await a.Go(); await b.Go(); }
            }
            """)));
        Assert.That(map.Integrations.Select(i => (i.Id, i.ConfigKey)), Is.EqualTo(new[] { ("http:AKlient", "A:Url"), ("http:BKlient", "B:Url") }));
        var uses = map.EntryPoints.ToDictionary(e => e.Path, Uses);
        Assert.That(uses["/z/a"], Is.EqualTo(new[] { "http:AKlient | POST {A:Url}/a" }));
        Assert.That(uses["/z/b"], Is.EqualTo(new[] { "http:BKlient | POST {B:Url}/b" }));
        Assert.That(uses["/z/both"], Is.EqualTo(new[] { "http:AKlient | POST {A:Url}/a", "http:BKlient | POST {B:Url}/b" }));
        Assert.That(All(map.EntryPoints.Single(e => e.Path == "/z/a").Steps).Select(s => s.Member), Does.Contain("Helper.Post"), "hjälpmetoden visas i vägen");
        Assert.That(map.Apps.Single().HttpClients.Select(c => (c.Client, c.ConfigKey)), Is.EqualTo(new[] { ("AKlient", "A:Url"), ("BKlient", "B:Url") }));
    }

    [Test]
    public async Task Calls_are_listed_in_groups_by_path_area_in_the_text()
    {
        var map = await MapAsync(Build(true, Web("Api", $$"""
            {{AppStart}}
            public static class E
            {
                public static void Map(WebApplication app)
                {
                    var v1 = app.MapGroup("/v1");
                    v1.MapGet("/family", () => 1);
                    v1.MapGet("/auth-info", () => 2);
                    v1.MapGet("/auth/in", () => 3);
                    v1.MapPost("/family", () => 4);
                    v1.MapGet("/me", () => 5);
                    v1.MapGet("/auth/out", () => 6);
                }
            }
            """)));
        var order = string.Join("\n", CodeMapText.Lines(map)).Split('\n').Where(l => l.Length > 0 && char.IsUpper(l[0]) && l.Contains(" /v1/")).Select(l => l.Split("  ")[0]);
        Assert.That(order, Is.EqualTo(new[] { "GET /v1/auth-info", "GET /v1/auth/in", "GET /v1/auth/out", "GET /v1/family", "POST /v1/family", "GET /v1/me" }));
    }

    // --- Fakta per app -----------------------------------------------------------------------

    private const string AppStart = """
        public static class Start
        {
            public static WebApplication Build(string[] args)
            {
                var builder = WebApplication.CreateBuilder(args);
                return builder.Build();
            }
        }
        """;

    /// <summary>En app utan ingångar listas inte, så de flesta testerna behöver en.</summary>
    private const string AppWithEntry = AppStart + "\n[ApiController, Route(\"app\")] public class AppController : ControllerBase { [HttpGet] public int Get() => 1; }";

    [Test]
    public async Task App_has_kinds_from_its_entries_and_a_library_is_not_an_app()
    {
        var map = await MapAsync(Build(true,
            Web("Bibliotek", "public class Hjalp { }"),
            Web("Api", $$"""
                {{AppStart}}
                [ApiController, Route("api/x")] public class XController : ControllerBase { [HttpGet] public int Get() => 1; }
                [Route("start")] public class StartController : Controller { [HttpGet] public IActionResult Index() => View(); }
                """, "Bibliotek"),
            Web("Rapporter", $$"""
                {{AppStart}}
                [Route("rapport")] public class RapportController : Controller { [HttpGet] public IActionResult Index() => View(); }
                """),
            Web("Tjanst", $$"""
                {{AppStart}}
                public static class E { public static void M(WebApplication app) => app.MapGet("/ping", () => "pong"); }
                """)));
        Assert.That(map.Apps.Select(a => (a.Project, string.Join("+", a.Kinds))), Is.EqualTo(new[] { ("Api", "Api+Web"), ("Rapporter", "Web"), ("Tjanst", "Api") }));
        Assert.That(map.EntryPoints.Select(e => $"{e.Path} {e.Kind}"), Is.EqualTo(new[]
        {
            "/api/x Controller", "/ping MinimalApi", "/rapport MvcView", "/start MvcView",
        }));
    }

    [Test]
    public async Task Health_paths_come_from_MapHealthChecks_with_prefix()
    {
        var map = await MapAsync(Build(true, Web("Api", $$"""
            {{AppWithEntry}}
            public static class Run
            {
                public static void Map(WebApplication app)
                {
                    app.MapHealthChecks("/healthz");
                    app.MapGroup("/ops").MapHealthChecks("/live");
                }
            }
            """)));
        Assert.That(map.Apps.Single().HealthPaths, Is.EqualTo(new[] { "/healthz", "/ops/live" }));
    }

    [TestCase("UseSqlServer", "sql-server")]
    [TestCase("UseNpgsql", "postgresql")]
    [TestCase("UseSqlite", "sqlite")]
    [TestCase("UseMySql", "mysql")]
    public async Task Database_has_provider_context_and_connection_string_key(string use, string provider)
    {
        var map = await MapAsync(Build(true, Web("Api", $$"""
            {{AppWithEntry}}
            public class ButikContext : Microsoft.EntityFrameworkCore.DbContext { }
            public static class Registrering { public static void Lagg(IServiceCollection s, IConfiguration config) => s.AddDbContext<ButikContext>(o => o.{{use}}(config.GetConnectionString("Butik"))); }
            """)));
        var database = map.Apps.Single().Databases.Single();
        Assert.That((database.Provider, database.Context, database.ConfigKey), Is.EqualTo((provider, "ButikContext", "ConnectionStrings:Butik")));
    }

    [TestCase("""o.UseInMemoryDatabase("butik")""", "in-memory", null)]
    [TestCase("""o.UseCosmos(config["Cosmos:Url"], "k", "db")""", "cosmos", "Cosmos:Url")]
    public async Task Database_without_a_connection_string_key_or_with_another_key(string options, string provider, string? key)
    {
        var map = await MapAsync(Build(true, Web("Api", $$"""
            {{AppWithEntry}}
            public class ButikContext : Microsoft.EntityFrameworkCore.DbContext { }
            public static class Registrering { public static void Lagg(IServiceCollection s, IConfiguration config) => s.AddDbContext<ButikContext>(o => {{options}}); }
            """)));
        var database = map.Apps.Single().Databases.Single();
        Assert.That((database.Provider, database.ConfigKey), Is.EqualTo((provider, key)));
    }

    [Test]
    public async Task Registrations_in_referenced_projects_count_for_the_app_but_not_for_others()
    {
        var map = await MapAsync(Build(true,
            Web("Infra", """
                public class ButikContext : Microsoft.EntityFrameworkCore.DbContext { }
                public static class Registrering
                {
                    public static void Lagg(IServiceCollection s, IConfiguration config)
                    {
                        s.AddDbContext<ButikContext>(o => o.UseSqlite("x"));
                        s.AddHttpClient<Betal>(c => c.BaseAddress = new Uri(config["Betal:BaseUrl"]!));
                    }
                }
                public class Betal(System.Net.Http.HttpClient http) { }
                """),
            Web("Api", AppWithEntry, "Infra"),
            Web("Ensam", AppWithEntry)));
        Assert.That(map.Apps.Single(a => a.Project == "Api").Databases.Single().Context, Is.EqualTo("ButikContext"));
        Assert.That(map.Apps.Single(a => a.Project == "Api").HttpClients.Single(), Is.EqualTo(new HttpClientFacts("Betal", "Betal:BaseUrl", "http:Betal", null)));
        Assert.That(map.Apps.Single(a => a.Project == "Ensam"), Has.Property("Databases").Empty.And.Property("HttpClients").Empty);
    }

    [Test]
    public async Task HTTP_client_is_linked_to_the_project_named_by_the_key()
    {
        var map = await MapAsync(Build(true,
            Web("Kassa", $$"""
                {{AppStart}}
                public class OrdersKlient(System.Net.Http.HttpClient http) { public Task<System.Net.Http.HttpResponseMessage> Skapa() => http.PostAsync("orders", null); }
                public static class R { public static void Lagg(IServiceCollection s, IConfiguration c) => s.AddHttpClient<OrdersKlient>(h => h.BaseAddress = new Uri(c["Orders:BaseUrl"]!)); }
                [ApiController, Route("kassa")] public class KassaController(OrdersKlient k) : ControllerBase { [HttpPost] public Task Skapa() => k.Skapa(); }
                """),
            Web("Orders", AppWithEntry)));
        Assert.That(map.Apps.Single(a => a.Project == "Kassa").HttpClients.Single(), Is.EqualTo(new HttpClientFacts("OrdersKlient", "Orders:BaseUrl", "http:Orders", "Orders")));
        var integration = map.Integrations.Single();
        Assert.That((integration.Id, integration.TargetProject, integration.Detail), Is.EqualTo(("http:Orders", "Orders", "HTTP, adressen i Orders:BaseUrl, anropar Orders")));
        Assert.That(Uses(map.EntryPoints.Single(e => e.Project == "Kassa")), Is.EqualTo(new[] { "http:Orders | POST orders" }));
    }

    [TestCase("Port1", "PortApi1", """{ "Partner": { "BaseUrl": "http://localhost:6123/" } }""", """{ "profiles": { "https": { "applicationUrl": "https://localhost:6124;http://localhost:6123" } } }""", "",
        TestName = "Porten i launchSettings.json")]
    [TestCase("Port2", "PortApi2", """{ "partner": { "baseurl": "http://127.0.0.1:6200" } }""", null, """app.WebHost.UseUrls("http://*:6200");""",
        TestName = "Porten i UseUrls")]
    [TestCase("Port3", "PortApi3", """{ "Partner": { "BaseUrl": "http://localhost:6300" } }""", """{ "profiles": { "p": { "applicationUrl": "http://localhost:6999" } } }""", "",
        TestName = "Annan port ger ingen koppling")]
    public async Task HTTP_client_is_linked_by_the_port_when_no_project_has_the_name(string web, string api, string appsettings, string? launch, string useUrls)
    {
        var root = Path.Combine(Path.GetTempPath(), "systemmap-tests");
        Directory.CreateDirectory(Path.Combine(root, web));
        Directory.CreateDirectory(Path.Combine(root, api, "Properties"));
        File.WriteAllText(Path.Combine(root, web, "appsettings.json"), appsettings);
        if (launch is not null) File.WriteAllText(Path.Combine(root, api, "Properties", "launchSettings.json"), launch);
        try
        {
            var map = await MapAsync(Build(true,
                Web(web, $$"""
                    {{AppWithEntry}}
                    public class PartnerKlient(System.Net.Http.HttpClient http) { }
                    public static class R { public static void Lagg(IServiceCollection s, IConfiguration c) => s.AddHttpClient<PartnerKlient>(h => h.BaseAddress = new Uri(c["Partner:BaseUrl"]!)); }
                    """),
                Web(api, $$"""
                    {{AppWithEntry}}
                    public static class U { public static void Run(WebApplication app) { {{useUrls}} } }
                    """)));
            var expected = web == "Port3" ? null : api;
            Assert.That(map.Apps.Single(a => a.Project == web).HttpClients.Single().TargetProject, Is.EqualTo(expected));
        }
        finally
        {
            Directory.Delete(Path.Combine(root, web), true);
            Directory.Delete(Path.Combine(root, api), true);
        }
    }

    [Test]
    public async Task Web_entries_and_apps_are_shown_in_text_Mermaid_and_JSON()
    {
        var map = await MapAsync(Build(true, Web("Api", $$"""
            {{AppStart}}
            [Route("start")] public class StartController : Controller { [HttpGet] public IActionResult Index() => View(); }
            [ApiController, Route("api/x")] public class XController : ControllerBase { [HttpGet] public int Get() => 1; }
            """)));
        var text = string.Join("\n", CodeMapText.Lines(map));
        Assert.That(text, Does.Contain("Appar:\n  Api: api och web").And.Contain("GET /start  StartController.Index [Api] mvc-view"));
        Assert.That(text, Does.Contain("GET /api/x  XController.Get [Api]\n"), "controllers har ingen sort efter projektet");
        Assert.That(CodeMapMermaid.Write(map, new MermaidOptions()), Does.Contain("[/\"GET /start\"/]").And.Contain(">\"GET /api/x\"]"));
        using var json = System.Text.Json.JsonDocument.Parse(CodeMapJson.Serialize(map));
        var root = json.RootElement;
        Assert.That(root.EnumerateObject().First().Name, Is.EqualTo("schemaVersion"));
        Assert.That(root.GetProperty("entryPoints").EnumerateArray().Select(e => e.GetProperty("kind").GetString()), Is.EqualTo(new[] { "controller", "mvc-view" }));
        var app = root.GetProperty("apps")[0];
        Assert.That(app.GetProperty("project").GetString(), Is.EqualTo("Api"));
        Assert.That(app.GetProperty("kinds").EnumerateArray().Select(k => k.GetString()), Is.EqualTo(new[] { "api", "web" }));
        Assert.That(CodeMapJson.Serialize(map), Does.Not.Contain(": null"), "null utelämnas");
        Assert.That(CodeMapOverview.Build(map).Nodes.Where(n => n.Kind == OverviewKind.Entry).Select(n => (n.Name, n.Ref)),
            Is.EqualTo(new[] { ("GET /api/x", "controller"), ("GET /start", "mvc-view") }));
    }
}
