using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using SystemMap;

namespace SystemMap.Tests;

/// <summary>
/// Systemkartan byggd ur källkod i minnet (AdhocWorkspace, ingen MSBuild). Paket som inte finns i testmiljön
/// (EF Core, Dapper, Service Bus ...) deklareras som stubbar med samma namnrymd: reglerna jämför typernas namn.
/// </summary>
public partial class CodeMapTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "systemmap-tests");

    private const string Usings = """
        global using System;
        global using System.Collections.Generic;
        global using System.Linq;
        global using System.Threading;
        global using System.Threading.Tasks;
        """;

    private const string Stubs = """
        namespace Microsoft.EntityFrameworkCore
        {
            using System.Collections;
            using System.Linq.Expressions;
            public class DbContext
            {
                public int SaveChanges() => 0;
                public Task<int> SaveChangesAsync(CancellationToken ct = default) => Task.FromResult(0);
                public DbSet<T> Set<T>() where T : class => null!;
            }
            public abstract class DbSet<T> : IQueryable<T> where T : class
            {
                public abstract void Add(T entity);
                public abstract void Remove(T entity);
                public abstract ValueTask<T?> FindAsync(params object[] keys);
                public abstract Type ElementType { get; }
                public abstract Expression Expression { get; }
                public abstract IQueryProvider Provider { get; }
                public abstract IEnumerator<T> GetEnumerator();
                IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
            }
            public class DbContextOptionsBuilder
            {
                public DbContextOptionsBuilder UseSqlServer(string? connection) => this;
                public DbContextOptionsBuilder UseNpgsql(string? connection) => this;
                public DbContextOptionsBuilder UseSqlite(string? connection) => this;
                public DbContextOptionsBuilder UseInMemoryDatabase(string name) => this;
                public DbContextOptionsBuilder UseMySql(string? connection) => this;
                public DbContextOptionsBuilder UseCosmos(string? connection, string key, string database) => this;
            }
            public static class EntityFrameworkQueryableExtensions
            {
                public static Task<bool> AnyAsync<T>(this IQueryable<T> q, Expression<Func<T, bool>> p, CancellationToken ct = default) => null!;
                public static Task<List<T>> ToListAsync<T>(this IQueryable<T> q, CancellationToken ct = default) => null!;
            }
        }
        namespace Microsoft.Extensions.DependencyInjection
        {
            public static class EntityFrameworkStubs
            {
                public static IServiceCollection AddDbContext<T>(this IServiceCollection s, Action<Microsoft.EntityFrameworkCore.DbContextOptionsBuilder> o)
                    where T : Microsoft.EntityFrameworkCore.DbContext => s;
                public static IServiceCollection AddDbContext<T>(this IServiceCollection s, Action<IServiceProvider, Microsoft.EntityFrameworkCore.DbContextOptionsBuilder> o)
                    where T : Microsoft.EntityFrameworkCore.DbContext => s;
            }
        }
        namespace Dapper
        {
            public static class SqlMapper
            {
                public static Task<IEnumerable<T>> QueryAsync<T>(this System.Data.IDbConnection c, string sql, object? param = null) => null!;
            }
        }
        namespace Azure.Messaging.ServiceBus
        {
            public class ServiceBusClient { public ServiceBusSender CreateSender(string queue) => null!; public ServiceBusProcessor CreateProcessor(string queue) => null!; }
            public class ProcessMessageEventArgs { }
            public class ServiceBusProcessor { public event Func<ProcessMessageEventArgs, Task>? ProcessMessageAsync; }
            public class ServiceBusSender { public Task SendMessageAsync(ServiceBusMessage m, CancellationToken ct = default) => null!; }
            public class ServiceBusMessage { public ServiceBusMessage(string body) { } }
        }
        namespace Azure.Storage.Blobs
        {
            public class BlobServiceClient { public BlobContainerClient GetBlobContainerClient(string name) => null!; }
            public class BlobContainerClient { public Task UploadBlobAsync(string name, object data) => null!; }
        }
        namespace MassTransit
        {
            public interface IPublishEndpoint { Task Publish<T>(T message, CancellationToken ct = default) where T : class; }
            public interface ConsumeContext<T> where T : class { T Message { get; } }
            public interface IConsumer<in TMessage> where TMessage : class { Task Consume(ConsumeContext<TMessage> context); }
        }
        namespace NServiceBus
        {
            public interface IMessageHandlerContext { }
            public interface IHandleMessages<T> { Task Handle(T message, IMessageHandlerContext context); }
        }
        namespace MediatR
        {
            public interface IRequestHandler<TRequest, TResponse> { Task<TResponse> Handle(TRequest request, CancellationToken ct); }
            public interface INotificationHandler<T> { Task Handle(T notification, CancellationToken ct); }
        }
        namespace Quartz
        {
            public interface IJobExecutionContext { }
            public interface IJob { Task Execute(IJobExecutionContext context); }
        }
        namespace Hangfire
        {
            public static class RecurringJob
            {
                public static void AddOrUpdate(string id, System.Linq.Expressions.Expression<Func<Task>> job, string cron) { }
                public static void AddOrUpdate<T>(string id, System.Linq.Expressions.Expression<Func<T, Task>> job, string cron) { }
            }
            public static class BackgroundJob
            {
                public static string Enqueue(System.Linq.Expressions.Expression<Func<Task>> job) => "";
            }
        }
        namespace Grpc.Core
        {
            [AttributeUsage(AttributeTargets.Class)] public sealed class BindServiceMethodAttribute(Type service, string method) : Attribute { }
            public class ServerCallContext { }
        }
        namespace Microsoft.AspNetCore.Builder
        {
            public static class GrpcStubs { public static void MapGrpcService<T>(this Microsoft.AspNetCore.Routing.IEndpointRouteBuilder app) where T : class { } }
        }
        namespace Confluent.Kafka { public interface IProducer<TKey, TValue> { Task<object> ProduceAsync(string topic, object message); } }
        namespace RabbitMQ.Client { public interface IChannel { ValueTask BasicPublishAsync(string exchange, string routingKey, byte[] body); } }
        namespace MailKit { public interface IMailTransport { Task SendAsync(object message); } }
        namespace Refit
        {
            public sealed class GetAttribute(string path) : Attribute { public string Path { get; } = path; }
            public sealed class PostAttribute(string path) : Attribute { public string Path { get; } = path; }
        }
        namespace System.ServiceModel
        {
            [AttributeUsage(AttributeTargets.Interface)] public sealed class ServiceContractAttribute : Attribute { }
            public class ClientBase<T> where T : class { protected T Channel => null!; public void Abort() { } }
        }
        namespace System.Web.Http
        {
            public class ApiController { }
            public sealed class RoutePrefixAttribute(string prefix) : Attribute { }
            public sealed class RouteAttribute(string template) : Attribute { }
            public sealed class HttpGetAttribute : Attribute { }
        }
        """;

    // --- Hjälpare --------------------------------------------------------------------------

    /// <param name="Exe">Ett program som körs (konsol eller worker) i stället för ett bibliotek.</param>
    /// <param name="Generated">Kod i en genererad fil (.g.cs), som inte räknas som systemets egen.</param>
    private sealed record Proj(string Name, string Code, params string[] References)
    {
        public bool Exe { get; init; }
        public string? Generated { get; init; }
    }

    private static Solution Build(bool stubs, params Proj[] projects)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Where(p => !Path.GetFileName(p).StartsWith("nunit", StringComparison.OrdinalIgnoreCase))   // annars räknas projekten som testprojekt
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();
        var solution = new AdhocWorkspace().CurrentSolution;
        var ids = new Dictionary<string, ProjectId>();
        foreach (var p in projects)
        {
            var id = ids[p.Name] = ProjectId.CreateNewId(p.Name);
            solution = solution.AddProject(ProjectInfo.Create(id, VersionStamp.Create(), p.Name, p.Name, LanguageNames.CSharp,
                filePath: Path.Combine(Root, p.Name, p.Name + ".csproj"),
                compilationOptions: new CSharpCompilationOptions(p.Exe ? OutputKind.ConsoleApplication : OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable),
                parseOptions: new CSharpParseOptions(LanguageVersion.Latest),
                metadataReferences: references,
                projectReferences: p.References.Select(r => new Microsoft.CodeAnalysis.ProjectReference(ids[r]))));
            var files = new List<string> { Usings, p.Code };
            for (var i = 0; i < files.Count; i++)
                solution = solution.AddDocument(DocumentId.CreateNewId(id), $"Fil{i}.cs", SourceText.From(files[i]), filePath: Path.Combine(Root, p.Name, $"Fil{i}.cs"));
            if (p.Generated is not null)
                solution = solution.AddDocument(DocumentId.CreateNewId(id), "Genererad.g.cs", SourceText.From(p.Generated), filePath: Path.Combine(Root, p.Name, "Genererad.g.cs"));
            // Stubbarna i projektet längst ner, så att alla ser dem. De heter .g.cs, som genererad kod: de är paket i verkligheten och räknas inte som systemets kod.
            if (stubs && p.References.Length == 0)
                solution = solution.AddDocument(DocumentId.CreateNewId(id), "Stubs.g.cs", SourceText.From(Stubs), filePath: Path.Combine(Root, p.Name, "Stubs.g.cs"));
        }
        return solution;
    }

    private static Task<CodeMapModel> MapAsync(Solution solution, IReadOnlyList<ApiOperation>? operations = null, IEnumerable<string>? loadWarnings = null) =>
        CodeMapper.AnalyzeAsync(solution, Path.Combine(Root, "Test.sln"), operations, loadWarnings);

    private static Task<CodeMapModel> MapAsync(string code, bool stubs = true) => MapAsync(Build(stubs, new Proj("Api", code)));

    /// <summary>Alla steg i trädet, djupet först.</summary>
    private static IEnumerable<Step> All(IEnumerable<Step> steps) => steps.SelectMany(s => new[] { s }.Concat(All(s.Steps)));

    private static List<string> Uses(EntryPoint entry) =>
        All(entry.Steps).Where(s => s.Integration is not null).Select(s => $"{s.Integration!.Id} | {s.Integration.Operation}").ToList();

    private static string Controller(string members, string body) => $$"""
        using Microsoft.AspNetCore.Mvc;
        [ApiController, Route("x")]
        public class XController : ControllerBase
        {
            {{members}}
            [HttpPost] public async Task Run() { {{body}} await Task.CompletedTask; }
        }
        public class Kund { public int Id { get; set; } public string Namn { get; set; } = ""; }
        public class OrderLagd { public int OrderId { get; set; } }
        """;

    private static ApiOperation Op(string id, string method, string path) => new(id, method, path);

    // --- Integrationer -----------------------------------------------------------------------

    [TestCase("class Db : Microsoft.EntityFrameworkCore.DbContext { public Microsoft.EntityFrameworkCore.DbSet<Kund> Kunder => null!; } Db _db = new();",
        "await _db.Kunder.Where(k => k.Id > 0).ToListAsync();", "db:Db | läsa Kunder")]
    [TestCase("class Db : Microsoft.EntityFrameworkCore.DbContext { } Db _db = new();",
        "await _db.Set<Kund>().AnyAsync(k => k.Id == 1);", "db:Db | läsa Kund")]
    [TestCase("System.Data.IDbConnection _c = null!;",
        "await _c.QueryAsync<Kund>(\"SELECT * FROM dbo.[Kunder] k JOIN Ordrar o ON o.KundId = k.Id\");", "db:SQL | läsa Kunder, Ordrar")]
    [TestCase("System.Net.Http.IHttpClientFactory _f = null!;",
        "var http = _f.CreateClient(\"lager\"); var id = 1; await http.GetAsync($\"artiklar/{id}\");", "http:lager | GET artiklar/{id}")]
    [TestCase("System.Net.Http.IHttpClientFactory _f = null!; static string Hamta() => \"x\";",
        "var http = _f.CreateClient(\"oidc\"); await http.PostAsync(Hamta(), null);", "http:oidc | POST")]
    [TestCase("Azure.Messaging.ServiceBus.ServiceBusSender _s = new Azure.Messaging.ServiceBus.ServiceBusClient().CreateSender(\"order-lagd\");",
        "await _s.SendMessageAsync(new Azure.Messaging.ServiceBus.ServiceBusMessage(\"x\"));", "queue:order-lagd | skicka")]
    [TestCase("Azure.Storage.Blobs.BlobContainerClient _b = new Azure.Storage.Blobs.BlobServiceClient().GetBlobContainerClient(\"kvitton\");",
        "await _b.UploadBlobAsync(\"a.txt\", new object());", "storage:kvitton | ladda upp")]
    [TestCase("MassTransit.IPublishEndpoint _p = null!;", "await _p.Publish(new OrderLagd());", "queue:OrderLagd | publicera")]
    [TestCase("Confluent.Kafka.IProducer<string, string> _k = null!;", "await _k.ProduceAsync(\"ordrar\", \"x\");", "queue:ordrar | skicka")]
    [TestCase("RabbitMQ.Client.IChannel _ch = null!;", "await _ch.BasicPublishAsync(\"butik\", \"order.lagd\", new byte[0]);", "queue:butik/order.lagd | skicka")]
    [TestCase("", "System.IO.File.WriteAllText(\"kvitto.txt\", \"x\");", "file | skriva kvitto.txt")]
    [TestCase("", "await new System.Net.Mail.SmtpClient(\"smtp\").SendMailAsync(new System.Net.Mail.MailMessage());", "email:smtp | skicka e-post")]
    [TestCase("MailKit.IMailTransport _m = null!;", "await _m.SendAsync(new object());", "email:smtp | skicka e-post")]
    [TestCase("""
        [System.ServiceModel.ServiceContract] public interface ILager { Task<bool> ReserveraAsync(string artikel); }
        public class LagerClient : System.ServiceModel.ClientBase<ILager>, ILager { public Task<bool> ReserveraAsync(string a) => Channel.ReserveraAsync(a); }
        LagerClient _l = new();
        """, "try { await _l.ReserveraAsync(\"x\"); } finally { _l.Abort(); }", "wcf:ILager | Reservera")]
    [TestCase("""
        public interface IBetal { [Refit.Post("/betalningar")] Task<Kund> Betala(OrderLagd o); }
        IBetal _r = null!;
        """, "await _r.Betala(new OrderLagd());", "http:Betal | POST betalningar")]
    public async Task Recognizes_the_integration(string members, string body, string expected)
    {
        var map = await MapAsync(Controller(members, body));
        Assert.That(Uses(map.EntryPoints.Single()), Is.EqualTo(new[] { expected }));
    }

    private const string DecidedAtRunTime = "(adressen bestäms vid körning)";

    [Test]
    public async Task An_HTTP_call_whose_address_is_decided_at_run_time_is_data_and_text_and_Mermaid_say_it_in_Swedish()
    {
        var map = await MapAsync(Controller("System.Net.Http.HttpClient _http = new(); static string Hamta() => \"x\";", "await _http.PostAsync(Hamta(), null);"));
        var use = All(map.EntryPoints.Single().Steps).Single(s => s.Integration is not null).Integration!;
        Assert.That((use.Operation, use.AddressAtRuntime), Is.EqualTo(("POST", true)), "verbet är data, och att adressen bestäms vid körning är ett eget fält");
        var node = CodeMapOverview.Build(map).Nodes.Single(n => n.Kind == OverviewKind.Operation);
        Assert.That((node.Name, node.AddressAtRuntime), Is.EqualTo(("POST", true)));
        Assert.That(string.Join("\n", CodeMapText.Lines(map)), Does.Contain($"(HTTP): POST {DecidedAtRunTime}").And.Contain($"    POST {DecidedAtRunTime}"));
        foreach (var level in new[] { "classes", "all" })
            Assert.That(CodeMapMermaid.Write(map, MermaidOptions.Parse(level, null, null)), Does.Contain($"POST {DecidedAtRunTime}"), level);

        var known = await MapAsync(Controller("System.Net.Http.HttpClient _http = new();", "await _http.PostAsync(\"orders\", null);"));
        Assert.That(All(known.EntryPoints.Single().Steps).Single(s => s.Integration is not null).Integration!.AddressAtRuntime, Is.False, "en adress som går att läsa är som den är");
        Assert.That(string.Join("\n", CodeMapText.Lines(known)) + CodeMapMermaid.Write(known, new MermaidOptions()), Does.Not.Contain("vid körning"));
    }

    [Test]
    public async Task Json_says_address_runtime_for_a_call_whose_address_is_decided_at_run_time_and_keeps_the_verb_as_the_operation()
    {
        var map = await MapAsync(Controller("System.Net.Http.HttpClient _http = new(); static string Hamta() => \"x\";", "await _http.PostAsync(Hamta(), null);"));
        using var doc = JsonDocument.Parse(CodeMapJson.Serialize(map));
        var step = Walk(doc.RootElement.GetProperty("entryPoints")).Single(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("integration", out _)).GetProperty("integration");
        Assert.That((step.GetProperty("operation").GetString(), step.GetProperty("address").GetString()), Is.EqualTo(("POST", "runtime")));
        var row = doc.RootElement.GetProperty("overview").GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("kind").GetString() == "operation");
        Assert.That((row.GetProperty("name").GetString(), row.GetProperty("address").GetString()), Is.EqualTo(("POST", "runtime")));
        Assert.That(doc.RootElement.GetRawText(), Does.Not.Contain("vid körning"), "texten hör inte hemma i data");

        var known = await MapAsync(Controller("System.Net.Http.HttpClient _http = new();", "await _http.PostAsync(\"orders\", null);"));
        Assert.That(CodeMapJson.Serialize(known), Does.Not.Contain("\"address\""), "fältet finns bara när adressen bestäms vid körning");
    }

    [Test]
    public async Task Page_says_so_when_the_address_of_an_HTTP_call_is_decided_at_run_time()
    {
        var map = await MapAsync(Controller("System.Net.Http.HttpClient _http = new(); static string Hamta() => \"x\";", "await _http.PostAsync(Hamta(), null);"));
        await WithPageAsync(map, async (page, ready) =>
        {
            await page.ClickAsync("[data-level='all']");
            await ready();
            Assert.That(await page.EvaluateAsync<string[]>("() => SystemMap.cy().nodes('.t-operation').map(n => n.data('label'))"), Is.EqualTo(new[] { $"POST {DecidedAtRunTime}" }));
        });
    }

    [Test]
    public async Task EF_shows_the_written_table_not_SaveChanges()
    {
        var map = await MapAsync(Controller(
            "class Db : Microsoft.EntityFrameworkCore.DbContext { public Microsoft.EntityFrameworkCore.DbSet<Kund> Kunder => null!; } Db _db = new();",
            "_db.Kunder.Add(new Kund()); await _db.SaveChangesAsync();"));
        var step = All(map.EntryPoints.Single().Steps).Single(s => s.Integration is not null);
        Assert.That(step.Integration!.Operation, Is.EqualTo("lägga till i Kunder"));
        Assert.That(step.In?.Type, Is.EqualTo("Kund"), "det som skickas till databasen");
        Assert.That(map.Integrations.Single().Detail, Is.EqualTo("databas, EF Core"));
    }

    [Test]
    public async Task Missing_packages_are_recognized_by_name()
    {
        // Utan stubbar finns inte Azure-typerna (som när dotnet restore misslyckats): regeln jämför det korta namnet.
        var map = await MapAsync(Controller(
            "Azure.Messaging.ServiceBus.ServiceBusSender _s = new Azure.Messaging.ServiceBus.ServiceBusClient(\"cs\").CreateSender(\"betalningar\");",
            "await _s.SendMessageAsync(null!);"), stubs: false);
        Assert.That(Uses(map.EntryPoints.Single()), Is.EqualTo(new[] { "queue:betalningar | skicka" }));
    }

    [Test]
    public async Task Typed_HttpClient_gets_name_and_address_from_the_registration()
    {
        var map = await MapAsync(Build(true, new Proj("Api", """
            using Microsoft.AspNetCore.Mvc;
            using Microsoft.Extensions.Configuration;
            using Microsoft.Extensions.DependencyInjection;
            public interface IBetal { Task<Kvitto> BetalaAsync(Betalning b); }
            public record Betalning(int KundId, double Belopp);
            public record Kvitto(string Id);
            public class Betal(System.Net.Http.HttpClient http) : IBetal
            {
                public async Task<Kvitto> BetalaAsync(Betalning b)
                {
                    var svar = await System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(http, "betalningar", b);
                    return (await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<Kvitto>(svar.Content))!;
                }
            }
            public static class Registrering
            {
                public static void Lagg(IServiceCollection services, IConfiguration config) =>
                    services.AddHttpClient<IBetal, Betal>(c => c.BaseAddress = new Uri(config["Betaltjanst:BaseUrl"]!));
            }
            [ApiController, Route("ordrar")]
            public class OrdrarController(IBetal betal) : ControllerBase
            {
                [HttpPost] public Task<Kvitto> Skapa(Betalning b) => betal.BetalaAsync(b);
            }
            """)));
        var entry = map.EntryPoints.Single();
        var call = All(entry.Steps).Single(s => s.Integration is not null);
        Assert.That(call.Integration!.Id, Is.EqualTo("http:Betaltjanst"));
        Assert.That(call.Integration.Operation, Is.EqualTo("POST betalningar"));
        Assert.That((call.In?.Type, call.Out?.Type), Is.EqualTo(("Betalning", "Kvitto")), "skickar Betalning, får Kvitto");
        Assert.That(map.Integrations.Single().Detail, Is.EqualTo("HTTP, adressen i Betaltjanst:BaseUrl"));
        Assert.That(entry.Steps.Single().Member, Is.EqualTo("Betal.BetalaAsync"));
    }

    // --- Samma databas via EF Core och SQL ---------------------------------------------------

    /// <summary>Ett API med EF Core (skriver) och Dapper (läser) mot databasen som <paramref name="registration"/> och <paramref name="connection"/> anger.</summary>
    private static Proj Store(string registration, string connection) => new("Api", $$"""
        using Microsoft.AspNetCore.Mvc;
        using Microsoft.Extensions.Configuration;
        using Microsoft.Extensions.DependencyInjection;
        using Dapper;
        public class Kund { public int Id { get; set; } }
        public class ButikContext : Microsoft.EntityFrameworkCore.DbContext { public Microsoft.EntityFrameworkCore.DbSet<Kund> Kunder => null!; }
        public class Anslutning { public string Text => ""; public System.Data.IDbConnection Oppna() => null!; }
        public static class Registrering { public static void Lagg(IServiceCollection s, IConfiguration config) { {{registration}} } }
        [ApiController, Route("kunder")]
        public class KunderController(ButikContext db, IConfiguration config, Anslutning anslutning) : ControllerBase
        {
            [HttpPost] public async Task Skapa(Kund k) { db.Kunder.Add(k); await db.SaveChangesAsync(); }
            [HttpGet] public async Task<IEnumerable<Kund>> Lista()
            {
                System.Data.IDbConnection c = {{connection}};
                return await c.QueryAsync<Kund>("SELECT * FROM Kunder");
            }
            private static System.Data.IDbConnection Anslut(string? text) => null!;
        }
        """);

    [TestCase("s.AddDbContext<ButikContext>(o => o.UseSqlServer(config.GetConnectionString(\"Butik\")));",
        "Anslut(config.GetConnectionString(\"Butik\"))", "db:Butik", TestName = "Samma nyckel för anslutningen")]
    [TestCase("s.AddDbContext<ButikContext>((sp, o) => o.UseSqlServer(sp.GetRequiredService<Anslutning>().Text));",
        "anslutning.Oppna()", "db:Anslutning", TestName = "Samma klass skapar anslutningen")]
    public async Task Same_database_via_EF_and_SQL_is_one_integration(string registration, string connection, string id)
    {
        var map = await MapAsync(Build(true, Store(registration, connection)));
        Assert.That(map.Integrations.Select(i => (i.Id, i.Detail)), Is.EqualTo(new[] { (id, "databas, EF Core (ButikContext) och SQL") }));
        Assert.That(map.EntryPoints.SelectMany(Uses).Order(StringComparer.Ordinal),
            Is.EqualTo(new[] { $"{id} | lägga till i Kunder", $"{id} | läsa Kunder" }));
    }

    [Test]
    public async Task Different_connections_are_different_databases()
    {
        var map = await MapAsync(Build(true, Store("s.AddDbContext<ButikContext>(o => o.UseSqlServer(config.GetConnectionString(\"Butik\")));",
            "Anslut(config.GetConnectionString(\"Lager\"))")));
        Assert.That(map.Integrations.Select(i => (i.Id, i.Detail)), Is.EquivalentTo(new[]
        {
            ("db:Butik", "databas, EF Core (ButikContext)"),
            ("db:Lager", "databas, SQL"),
        }));
    }

    // --- Översikten: varje del en gång ---------------------------------------------------------

    // En databas som två klasser läser och skriver, och en kö med två meddelanden.
    private const string Kunder = """
            using Microsoft.AspNetCore.Mvc;
            public class Kund { public int Id { get; set; } }
            public class OrderLagd { public int Id { get; set; } }
            public class OrderMakulerad { public int Id { get; set; } }
            public class Db : Microsoft.EntityFrameworkCore.DbContext { public Microsoft.EntityFrameworkCore.DbSet<Kund> Kunder => null!; }
            public class Lasare(Db db) { public async Task<Kund?> HamtaAsync(int id) => await db.Kunder.FindAsync(id); }
            public class Skrivare(Db db) { public async Task SparaAsync(Kund k) { db.Kunder.Add(k); await db.SaveChangesAsync(); } }
            public class Handelser(Confluent.Kafka.IProducer<string, string> kafka)
            {
                public Task LagdAsync(OrderLagd o) => kafka.ProduceAsync("ordrar", o);
                public Task MakuleradAsync(OrderMakulerad o) => kafka.ProduceAsync("ordrar", o);
            }
            [ApiController, Route("kunder")]
            public class KunderController(Lasare lasare, Skrivare skrivare, Handelser handelser) : ControllerBase
            {
                [HttpGet("{id}")] public Task<Kund?> Hamta(int id) => lasare.HamtaAsync(id);
                [HttpPost] public async Task Spara(Kund k) { await skrivare.SparaAsync(k); await handelser.LagdAsync(new OrderLagd()); }
                [HttpDelete("{id}")] public Task Makulera(int id) => handelser.MakuleradAsync(new OrderMakulerad());
            }
            """;

    [Test]
    public async Task Overview_has_each_integration_once_with_its_operations()
    {
        var map = await MapAsync(Build(true, new Proj("Api", Kunder)));
        var overview = CodeMapOverview.Build(map);
        string[] Rows(string integration) => overview.Nodes.Where(n => n.Parent == "int:" + integration).Select(n => n.Name).ToArray();

        Assert.That(overview.Nodes.Count(n => n.Kind == OverviewKind.Integration), Is.EqualTo(2), "databasen och kön, en gång var");
        Assert.That(Rows("db:Db"), Is.EqualTo(new[] { "lägga till i Kunder", "läsa Kunder" }));
        Assert.That(Rows("queue:ordrar"), Is.EqualTo(new[] { "skicka OrderLagd", "skicka OrderMakulerad" }), "ett meddelande per rad");

        // Läsningen och skrivningen görs av var sin klass; pilarna går till rätt rad och vet vilka anrop som använder dem.
        var read = overview.Links.Single(l => l.To == "op:db:Db|läsa Kunder");
        var write = overview.Links.Single(l => l.To == "op:db:Db|lägga till i Kunder");
        Assert.That((read.From, read.Entries.Single()), Is.EqualTo(("method:Api/Lasare.HamtaAsync", "GET /kunder/{id}")));
        Assert.That((write.From, write.Entries.Single()), Is.EqualTo(("method:Api/Skrivare.SparaAsync", "POST /kunder")));
        Assert.That(read.File, Is.Not.Null.And.EndsWith("Fil1.cs"), "platsen där databasen anropas");
        Assert.That(overview.Nodes.Single(n => n.Id == "int:queue:ordrar").Entries, Is.EqualTo(new[] { "POST /kunder", "DELETE /kunder/{id}" }),
            "i ingångarnas ordning");
        Assert.That(overview.Nodes.Single(n => n.Id == "class:Api/KunderController").Parent, Is.EqualTo("proj:Api"));
    }

    // --- Mermaid ---------------------------------------------------------------------------------

    [Test]
    public async Task Mermaid_draws_each_integration_once_with_the_operations_on_the_arrow()
    {
        var text = CodeMapMermaid.Write(await MapAsync(Build(true, new Proj("Api", Kunder))), new MermaidOptions());
        var lines = text.Split('\n');

        Assert.That(lines[0], Is.EqualTo("flowchart LR"));
        Assert.That(lines.Count(l => l.TrimStart().StartsWith("int_Db[")), Is.EqualTo(1), "databasen en gång");
        Assert.That(text, Does.Contain("int_Db[(\"Db<br/>databas, EF Core\")]"), "databasen som cylinder, med vad den är");
        Assert.That(lines.Count(l => l.TrimStart().StartsWith("int_ordrar(")), Is.EqualTo(1), "kön en gång");
        Assert.That(text, Does.Contain("subgraph system[\"Systemet (Test.sln)\"]").And.Contain("subgraph proj_Api[\"Api\"]"));
        Assert.That(text, Does.Contain("in_GET_kunder_id>\"GET /kunder/{id}\"]").And.Contain("in_GET_kunder_id --> class_KunderController"));
        // Läsningarna, skrivningarna och meddelandena står på pilen, en gång per pil.
        Assert.That(text, Does.Contain("class_Lasare -->|\"läsa Kunder\"| int_Db")
            .And.Contain("class_Skrivare -->|\"lägga till i Kunder\"| int_Db")
            .And.Contain("class_Handelser -->|\"skicka OrderLagd<br/>skicka OrderMakulerad\"| int_ordrar"));
        Assert.That(text, Does.Contain("class in_POST_kunder,in_GET_kunder_id,in_DELETE_kunder_id entry"));
        Assert.That(lines[1], Is.EqualTo("  %% Systemkarta för Test.sln: vägar som är möjliga enligt koden."),
            "bara filnamnet: filen hamnar i dokumentation, och sökvägen på den här datorn hör inte hemma där");
        Assert.That(text, Does.Not.Contain("method_"), "metoderna ritas bara på nivån all");
    }

    [Test]
    public async Task Mermaid_for_one_entry_at_the_levels_all_and_projects()
    {
        var map = await MapAsync(Build(true, new Proj("Api", Kunder)));

        var path = CodeMapMermaid.Write(map, MermaidOptions.Parse("all", "tb", "post  /KUNDER"));
        Assert.That(path, Does.StartWith("flowchart TB\n").And.Contain("%% Bara vägen för POST /kunder."));
        Assert.That(path, Does.Contain("subgraph class_Skrivare[\"Skrivare\"]").And.Contain("method_Skrivare_SparaAsync[\"SparaAsync()\"]"));
        Assert.That(path, Does.Contain("subgraph int_Db[\"Db<br/>databas, EF Core\"]").And.Contain("op_Db_lagga_till_i_Kunder[\"lägga till i Kunder\"]"));
        Assert.That(path, Does.Contain("method_Skrivare_SparaAsync --> op_Db_lagga_till_i_Kunder"));
        Assert.That(path, Does.Not.Contain("Lasare").And.Not.Contain("OrderMakulerad").And.Not.Contain("GET /kunder"), "bara vägen för anropet");

        var projects = CodeMapMermaid.Write(map, MermaidOptions.Parse("projects", null, null));
        Assert.That(projects, Does.Contain("proj_Api[\"Api\"]").And.Not.Contain("subgraph proj_Api").And.Not.Contain("class_"));
        Assert.That(projects, Does.Match("proj_Api -->\\|\"[^\"]*läsa Kunder[^\"]*\"\\| int_Db"));
        Assert.That(CodeMapMermaid.Write(map, MermaidOptions.Parse("projects", null, null)), Is.EqualTo(projects), "samma karta ger samma text");

        var e = Assert.Throws<UsageException>(() => CodeMapMermaid.Write(map, new MermaidOptions(Entry: "GET /finns-inte")))!;
        Assert.That(e.Message, Is.EqualTo("Anropet 'GET /finns-inte' finns inte i kartan."));
    }

    [TestCase("hela", null, "--level måste vara projects, classes, methods eller all")]
    [TestCase(null, "RL", "--direction måste vara LR")]
    public void Mermaid_with_unknown_level_or_direction_gives_an_error(string? level, string? direction, string expected) =>
        Assert.That(Assert.Throws<UsageException>(() => MermaidOptions.Parse(level, direction, null))!.Message, Does.StartWith(expected));

    // --- Webbsidan --------------------------------------------------------------------------------

    [Test]
    public async Task Web_page_has_the_map_libraries_and_mermaid_but_not_the_path()
    {
        // En varning från MSBuild med hela sökvägen, och text som skulle avsluta en skripttagg.
        var csproj = Path.Combine(Root, "Api", "Api.csproj");
        var map = await MapAsync(Build(true, new Proj("Api", Kunder)), loadWarnings: [$"MSBuild: {csproj} saknar ett paket.", "</script><script>alert(1)</script>"]);
        Assert.That(map.Warnings, Has.Member($"MSBuild: {Path.Combine("Api", "Api.csproj")} saknar ett paket."),
            "sökvägen relativt solutionens katalog, som filerna i kartan: kartan kan delas");

        var html = CodeMapHtml.Write(map, new HashSet<string>(), showUnreached: true);
        Assert.That(html, Does.StartWith("<!DOCTYPE html>").And.Contain("<title>Systemkarta – Test.sln</title>"));
        Assert.That(html, Does.Contain("Content-Security-Policy\" content=\"default-src 'none';"), "sidan hämtar inget utifrån");
        Assert.That(html, Does.Contain("window.SystemMap = { create: create, summary: summary, groups: callGroups").And.Contain("cytoscape").And.Contain(".map-view {"));
        Assert.That(html, Does.Not.Contain("__" + "MAP__").And.Not.Contain("__" + "SCRIPTS__"), "alla delar är insatta");
        Assert.That(html.Split("</script>").Length - 1, Is.EqualTo(CodeMapAssets.Scripts.Count + 4), "varningen avslutar ingen skripttagg");
        Assert.That(html, Does.Not.Contain(Root), "bara filnamnet: sidan kan delas");

        // Mermaid för varje nivå och anrop, samma text som --mermaid. Uppifrån och ned byter sidan själv första raden.
        var start = html.IndexOf("<script type=\"application/json\" id=\"mermaid-data\">", StringComparison.Ordinal);
        var json = html[(html.IndexOf('>', start) + 1)..html.IndexOf("</script>", start, StringComparison.Ordinal)];
        var variants = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json)!;
        Assert.That(variants, Has.Count.EqualTo(MermaidOptions.Levels.Length * (1 + map.EntryPoints.Count)));
        Assert.That(variants["classes|POST /kunder"], Is.EqualTo(CodeMapMermaid.Write(map with { Solution = "Test.sln" }, MermaidOptions.Parse("classes", "LR", "POST /kunder"))));
        Assert.That(variants["projects|"], Does.StartWith("flowchart LR\n"));
        Assert.That(CodeMapHtml.Write(map, new HashSet<string>(), showUnreached: true), Is.EqualTo(html), "samma karta ger samma sida");
    }

    [Test]
    public void Map_assets_exist_with_the_right_type()
    {
        foreach (var name in CodeMapAssets.Scripts)
            Assert.That(CodeMapAssets.Get(name)?.ContentType, Is.EqualTo("text/javascript; charset=utf-8"), name);
        Assert.That(CodeMapAssets.Get("systemmap.css")?.ContentType, Is.EqualTo("text/css; charset=utf-8"));
        Assert.That(CodeMapAssets.Get("page.html"), Is.Null, "mallen för webbsidan serveras inte");
        Assert.That(CodeMapAssets.Get("annat.js"), Is.Null);
    }

    // --- Vägen genom projekten ---------------------------------------------------------------

    private static Proj[] Layers(string registration) =>
    [
        new("Application", """
            namespace App;
            public record NyOrder(int KundId);
            public interface IOrderRepo { Task SparaAsync(NyOrder o); }
            public interface IOrderService { Task LaggAsync(NyOrder o); }
            public class OrderService(IOrderRepo repo) : IOrderService
            {
                public async Task LaggAsync(NyOrder o) { Kontroll.Rimlig(o); await repo.SparaAsync(o); }
            }
            public static class Kontroll { public static void Rimlig(NyOrder o) { if (o.KundId < 0) throw new ArgumentException("x"); } }
            """),
        new("Infrastructure", """
            namespace Infra;
            public class SqlOrderRepo : App.IOrderRepo { public Task SparaAsync(App.NyOrder o) { System.IO.File.WriteAllText("order.txt", "x"); return Task.CompletedTask; } }
            public class MinnesOrderRepo : App.IOrderRepo { public Task SparaAsync(App.NyOrder o) => Task.CompletedTask; }
            """, "Application"),
        new("Api", $$"""
            using Microsoft.AspNetCore.Mvc;
            using Microsoft.Extensions.DependencyInjection;
            public static class Start { public static void Registrera(IServiceCollection s) { {{registration}} } }
            [ApiController, Route("api/[controller]")]
            public class OrdersController(App.IOrderService orders) : ControllerBase
            {
                [HttpPost] public Task Skapa(App.NyOrder o) => orders.LaggAsync(o);
            }
            """, "Application", "Infrastructure"),
    ];

    [Test]
    public async Task Follows_the_call_through_the_projects_in_call_order()
    {
        var map = await MapAsync(Build(true, Layers("s.AddScoped<App.IOrderService, App.OrderService>(); s.AddScoped<App.IOrderRepo, Infra.SqlOrderRepo>();")));
        // Infrastructure pekar på Application, men anropen når Application först.
        Assert.That(map.Projects, Is.EqualTo(new[] { "Api", "Application", "Infrastructure" }));
        var entry = map.EntryPoints.Single();
        Assert.That((entry.Method, entry.Path, entry.Handler, entry.Input?.Type), Is.EqualTo(("POST", "/api/Orders", "OrdersController.Skapa", "NyOrder")));
        var service = entry.Steps.Single();
        Assert.That((service.Member, service.Project), Is.EqualTo(("OrderService.LaggAsync", "Application")));
        // Kontroll.Rimlig är en statisk hjälpare utan integration: den visas inte.
        var repo = service.Steps.Single();
        Assert.That((repo.Member, repo.Project), Is.EqualTo(("SqlOrderRepo.SparaAsync", "Infrastructure")), "registreringen avgör, inte MinnesOrderRepo");
        Assert.That(Uses(entry), Is.EqualTo(new[] { "file | skriva order.txt" }));
        Assert.That(map.Warnings, Is.Empty);
    }

    [Test]
    public async Task Registration_with_typeof_counts()
    {
        var map = await MapAsync(Build(true, Layers("s.AddScoped(typeof(App.IOrderService), typeof(App.OrderService)); s.AddTransient(typeof(App.IOrderRepo), typeof(Infra.MinnesOrderRepo));")));
        Assert.That(map.EntryPoints.Single().Steps.Single().Steps.Single().Member, Is.EqualTo("MinnesOrderRepo.SparaAsync"));
    }

    [Test]
    public async Task Without_registration_all_classes_are_shown_with_a_warning()
    {
        var map = await MapAsync(Build(true, Layers("")));
        var service = map.EntryPoints.Single().Steps.Single();
        Assert.That(service.Member, Is.EqualTo("OrderService.LaggAsync"), "en enda klass implementerar IOrderService: den används");
        Assert.That(service.Steps.Select(s => s.Member), Is.EquivalentTo(new[] { "SqlOrderRepo.SparaAsync", "MinnesOrderRepo.SparaAsync" }));
        Assert.That(map.Warnings, Has.Some.Contains("Flera klasser implementerar IOrderRepo.SparaAsync (MinnesOrderRepo, SqlOrderRepo)"));
    }

    [Test]
    public async Task Cyclic_calls_give_a_finite_map()
    {
        var map = await MapAsync("""
            using Microsoft.AspNetCore.Mvc;
            [ApiController, Route("x")]
            public class XController : ControllerBase
            {
                [HttpGet] public void Run() => Loop.A(3);
            }
            public static class Loop
            {
                public static void A(int n) { if (n > 0) B(n - 1); System.IO.File.Delete("a"); }
                public static void B(int n) { if (n > 0) A(n - 1); }
            }
            """);
        Assert.That(Uses(map.EntryPoints.Single()), Is.EqualTo(new[] { "file | ta bort a" }));
    }

    // --- Ingångar och specen -------------------------------------------------------------------

    [Test]
    public async Task Routes_like_ASP_NET_and_Web_API_2()
    {
        var map = await MapAsync("""
            using Microsoft.AspNetCore.Mvc;
            [ApiController, Route("api/[controller]")]
            public class OrdersController : ControllerBase
            {
                [HttpGet("{id:int}")] public int Hamta(int id) => id;
                [HttpGet("~/halsa")] public string Halsa() => "ok";
                [HttpPost("[action]/{kod?}")] public void Avbryt(string? kod) { }
                [NonAction] public void Hjalp() { }
            }
            [System.Web.Http.RoutePrefix("api/gammal")]
            public class GammalController : System.Web.Http.ApiController
            {
                [System.Web.Http.HttpGet, System.Web.Http.Route("saker")] public string Saker() => "";
            }
            """);
        Assert.That(map.EntryPoints.Select(e => $"{e.Method} {e.Path}"),
            Is.EquivalentTo(new[] { "GET /api/Orders/{id}", "GET /halsa", "POST /api/Orders/Avbryt/{kod}", "GET /api/gammal/saker" }));
    }

    [Test]
    public async Task Links_to_the_spec_and_shows_differences()
    {
        var solution = Build(true, new Proj("Api", """
            using Microsoft.AspNetCore.Mvc;
            [ApiController, Route("api/v1/orders")]
            public class OrdersController : ControllerBase
            {
                [HttpGet("{orderId}")] public int Hamta(int orderId) => orderId;
                [HttpGet("/health")] public string Halsa() => "ok";
            }
            """));
        var map = await MapAsync(solution, [Op("getOrder", "GET", "/orders/{id}"), Op("deleteOrder", "DELETE", "/orders/{id}")]);
        var get = map.EntryPoints.Single(e => e.Handler == "OrdersController.Hamta");
        Assert.That((get.OperationId, get.Path, get.CodePath), Is.EqualTo(("getOrder", "/orders/{id}", "/api/v1/orders/{orderId}")), "specen utgår från bas-sökvägen /api/v1");
        Assert.That(map.Differences.Select(d => $"{d.Kind} {d.Method} {d.Path}"),
            Is.EqualTo(new[] { "OnlyInCode GET /health", "OnlyInSpec DELETE /orders/{id}" }));
    }

    [Test]
    public async Task Same_code_gives_the_same_JSON()
    {
        var solution = Build(true, Layers("s.AddScoped<App.IOrderService, App.OrderService>();"));
        var first = CodeMapJson.Serialize(await MapAsync(solution));
        var second = CodeMapJson.Serialize(await MapAsync(solution));
        Assert.That(second, Is.EqualTo(first));
        Assert.That(first, Does.Contain("\"entryPoints\"").And.Contain("\"integrations\"").And.Contain("\"overview\"").And.Contain("\"schemaVersion\": 3"));
    }

    [Test]
    public async Task No_controllers_gives_a_warning()
    {
        var map = await MapAsync("public class Tom { }");
        Assert.That(map.EntryPoints, Is.Empty);
        Assert.That(map.Warnings, Has.Some.Contains("Hittade inga ingångar"));
    }

    // --- Små delar -------------------------------------------------------------------------------

    [TestCase("api/[controller]", "{id:int}", "/api/Orders/{id}")]
    [TestCase("[controller]", null, "/Orders")]
    [TestCase("api/x", "~/abs/{*rest}", "/abs/{rest}")]
    [TestCase("v1", "[action]/{id?}", "/v1/Lista/{id}")]
    [TestCase(null, null, "/")]
    [TestCase("/api//orders/", "{id=5}", "/api/orders/{id}")]
    public void Route_templates_are_combined(string? prefix, string? template, string expected) =>
        Assert.That(RouteTemplate.Combine(prefix, template, "Orders", "Lista"), Is.EqualTo(expected));

    [TestCase("SELECT * FROM dbo.[Orders] o JOIN Customers c ON c.Id = o.CustomerId", "läsa Orders, Customers")]
    [TestCase("INSERT INTO Orders (Id) VALUES (@id)", "lägga till i Orders")]
    [TestCase("UPDATE Orders SET Total = 1", "ändra i Orders")]
    [TestCase("DELETE FROM Orders WHERE Id = 1", "ta bort ur Orders")]
    [TestCase("EXEC dbo.SparaOrder @id", "köra proceduren dbo.SparaOrder")]
    [TestCase("WITH x AS (SELECT 1 AS a) SELECT a FROM x", "läsa x")]
    public void SQL_is_described(string sql, string expected) => Assert.That(SqlText.Describe(sql), Is.EqualTo(expected));

    // --- Exemplet, inläst med MSBuild ---------------------------------------------------------------

    [Test]
    public async Task Sample_gives_the_whole_map()
    {
        using var loaded = await SolutionLoader.LoadAsync(TestPaths.Solution, restore: true);
        var map = await CodeMapper.AnalyzeAsync(loaded.Solution, "samples/Shop.slnx", SpecReader.Load(TestPaths.Spec), loaded.Warnings);

        Assert.That(map.Projects, Is.EqualTo(new[] { "ShopApi", "ShopWeb", "ShopApplication", "ShopInfrastructure" }));
        Assert.That(map.EntryPoints.Where(e => e.Kind == EntryKind.Controller).Select(e => e.OperationId), Is.EquivalentTo(new[] { "createCustomer", "createOrder", "getOrder", "createProduct" }));
        Assert.That(map.Differences, Is.Empty, "koden och specen ska ha samma anrop");
        Assert.That(map.Warnings, Is.Empty);
        Assert.That(Uses(map.EntryPoints.Single(e => e.OperationId == "createOrder")), Is.EqualTo(new[]
        {
            "db:ShopDatabase | läsa Customers",
            "db:ShopDatabase | läsa Products",
            "wcf:ILagerService | Reservera",
            "http:Betaltjanst | POST payments",
            "db:ShopDatabase | lägga till i Orders",
            "queue:order-lagd | skicka",
            "storage:kvitton | ladda upp",
            "email:smtp | skicka e-post",
        }));
        Assert.That(Uses(map.EntryPoints.Single(e => e.OperationId == "getOrder")), Is.EqualTo(new[] { "db:ShopDatabase | läsa Orders" }));

        // EF Core och Dapper går mot samma databas (ShopDatabase skapar anslutningen): den är en integration.
        Assert.That(map.Integrations, Has.Count.EqualTo(7), "de sex i ShopApi och ShopApi som ShopWeb anropar");
        Assert.That(map.Integrations.Single(i => i.Kind == IntegrationKind.Database).Detail, Is.EqualTo("databas, EF Core (ShopDbContext) och SQL"));
        var overview = CodeMapOverview.Build(map);
        Assert.That(overview.Nodes.Where(n => n.Parent == "int:db:ShopDatabase").Select(n => n.Name), Is.EqualTo(new[]
        {
            "lägga till i Customers", "lägga till i Orders", "lägga till i Products", "läsa Customers", "läsa Orders", "läsa Products",
        }));
        Assert.That(overview.Nodes.Where(n => n.Parent == "int:queue:order-lagd").Select(n => n.Name), Is.EqualTo(new[] { "skicka OrderPlaced" }));
    }
}
