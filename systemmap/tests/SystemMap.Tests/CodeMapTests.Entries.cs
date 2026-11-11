using System.Text.Json;
using Microsoft.Playwright;

namespace SystemMap.Tests;

/// <summary>Ingångar som inte är HTTP: bakgrundstjänster, konsolprogram, meddelanden, jobb, gRPC och SignalR (kod i minnet).</summary>
public partial class CodeMapTests
{
    private const string Writer = "public class Writer { public void Write() => System.IO.File.WriteAllText(\"ut.txt\", \"x\"); }";

    private static IEnumerable<string> Keys(CodeMapModel map) => map.EntryPoints.Select(e => e.Key);

    // --- Bakgrundstjänster ---------------------------------------------------------------------

    [Test]
    public async Task A_BackgroundService_is_an_entry_that_follows_ExecuteAsync()
    {
        var map = await MapAsync(Writer + """
            public class OrderCleanupWorker(Writer writer) : Microsoft.Extensions.Hosting.BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken ct) { writer.Write(); return Task.CompletedTask; }
            }
            """);
        var entry = map.EntryPoints.Single();
        Assert.That((entry.Kind, entry.Key, entry.Handler, entry.Method, entry.Path), Is.EqualTo((EntryKind.Background, "Bakgrund: OrderCleanupWorker", "OrderCleanupWorker.ExecuteAsync", "", "")));
        Assert.That(Uses(entry), Is.EqualTo(new[] { "file | skriva ut.txt" }), "anropen följs som från en action");
        Assert.That(map.Warnings, Has.None.Contain("Hittade inga ingångar"));
    }

    [Test]
    public async Task An_IHostedService_follows_StartAsync_and_StopAsync_when_it_does_work()
    {
        var map = await MapAsync(Writer + """
            public class Flusher(Writer writer) : Microsoft.Extensions.Hosting.IHostedService
            {
                public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
                public Task StopAsync(CancellationToken ct) { writer.Write(); return Task.CompletedTask; }
            }
            """);
        var entry = map.EntryPoints.Single();
        Assert.That(entry.Key, Is.EqualTo("Bakgrund: Flusher"));
        Assert.That(entry.Handler, Is.EqualTo("Flusher.StartAsync"));
        Assert.That(Uses(entry), Is.EqualTo(new[] { "file | skriva ut.txt" }), "det StopAsync gör syns också");
    }

    [Test]
    public async Task A_background_service_in_a_base_class_in_the_solution_is_followed_and_near_misses_are_not_entries()
    {
        var map = await MapAsync(Writer + """
            public abstract class Looping(Writer writer) : Microsoft.Extensions.Hosting.BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken ct) { writer.Write(); return Task.CompletedTask; }
            }
            public class Nightly(Writer writer) : Looping(writer) { }
            // En klass som heter Worker och har ExecuteAsync men inte ärver BackgroundService är ingen bakgrundstjänst.
            public class FakeWorker { public Task ExecuteAsync(CancellationToken ct) => Task.CompletedTask; }
            public class FakeHosted { public Task StartAsync(CancellationToken ct) => Task.CompletedTask; }
            """);
        Assert.That(Keys(map), Is.EqualTo(new[] { "Bakgrund: Nightly" }), "den abstrakta basen och klasserna utan gränssnitt är inga ingångar");
        Assert.That(Uses(map.EntryPoints.Single()), Is.EqualTo(new[] { "file | skriva ut.txt" }));
    }

    // --- Konsolprogram -------------------------------------------------------------------------

    private static readonly Proj WriterLib = new("Lib", Writer);

    [Test]
    public async Task A_console_program_with_top_level_statements_is_an_entry()
    {
        var map = await MapAsync(Build(true, WriterLib, new Proj("Tool", "var w = new Lib_Writer(); w.Write();".Replace("Lib_Writer", "Writer"), "Lib") { Exe = true }));
        var entry = map.EntryPoints.Single();
        Assert.That((entry.Kind, entry.Key, entry.Project, entry.Handler), Is.EqualTo((EntryKind.Console, "Konsol: Tool", "Tool", "Program.Main")));
        Assert.That(Uses(entry), Is.EqualTo(new[] { "file | skriva ut.txt" }));
        Assert.That(entry.Line, Is.EqualTo(1), "platsen är första satsen");
    }

    [Test]
    public async Task A_console_program_with_a_Main_method_is_an_entry()
    {
        var map = await MapAsync(Build(true, WriterLib, new Proj("Tool", "public static class Start { public static void Main(string[] args) { new Writer().Write(); } }", "Lib") { Exe = true }));
        Assert.That(map.EntryPoints.Select(e => (e.Key, e.Handler)), Is.EqualTo(new[] { ("Konsol: Tool", "Start.Main") }));
        Assert.That(Uses(map.EntryPoints.Single()), Is.EqualTo(new[] { "file | skriva ut.txt" }));
    }

    [Test]
    public async Task A_console_entry_is_only_made_for_a_program_with_no_other_entries_that_is_not_a_web_app_or_a_host()
    {
        // Ett bibliotek har ingen Main.
        Assert.That((await MapAsync(Build(true, new Proj("Lib", Writer)))).EntryPoints, Is.Empty);

        // Ett program med en bakgrundstjänst: Program är uppstart, bakgrundstjänsten är ingången.
        var worker = await MapAsync(Build(true, new Proj("Worker", Writer + """
            public class Cleanup(Writer w) : Microsoft.Extensions.Hosting.BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken ct) { w.Write(); return Task.CompletedTask; }
            }
            public static class Start { public static void Main(string[] args) { Console.WriteLine("hej"); } }
            """) { Exe = true }));
        Assert.That(Keys(worker), Is.EqualTo(new[] { "Bakgrund: Cleanup" }));

        // En webbapp: Program är uppstart även utan ingångar.
        var web = await MapAsync(Build(true, Web("Api", AppStart + """
            public static class Start2 { public static void Main(string[] args) { Start.Build(args).Run(); } }
            """) with { Exe = true }));
        Assert.That(web.EntryPoints, Is.Empty);

        // Ett program som sätter upp en värd för en bakgrundstjänst som ligger i ett bibliotek.
        var host = await MapAsync(Build(true, new Proj("Lib", Writer + """
            public class Cleanup(Writer w) : Microsoft.Extensions.Hosting.BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken ct) { w.Write(); return Task.CompletedTask; }
            }
            """), new Proj("Host", """
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);
            builder.Build().Run();
            """, "Lib") { Exe = true }));
        Assert.That(Keys(host), Is.EqualTo(new[] { "Bakgrund: Cleanup" }), "värden är uppstart, inget konsolprogram");
    }

    // --- Meddelanden ---------------------------------------------------------------------------

    [Test]
    public async Task MassTransit_and_NServiceBus_handlers_are_entries_with_the_message_as_input()
    {
        var map = await MapAsync(Writer + """
            public class OrderPlaced { public int OrderId { get; set; } }
            public class OrderPlacedConsumer(Writer w) : MassTransit.IConsumer<OrderPlaced>
            {
                public Task Consume(MassTransit.ConsumeContext<OrderPlaced> context) { w.Write(); return Task.CompletedTask; }
            }
            public class OrderPlacedHandler(Writer w) : NServiceBus.IHandleMessages<OrderPlaced>
            {
                public Task Handle(OrderPlaced message, NServiceBus.IMessageHandlerContext context) { w.Write(); return Task.CompletedTask; }
            }
            """);
        Assert.That(map.EntryPoints.Select(e => (e.Kind, e.Key, e.Handler, e.Input?.Type)), Is.EquivalentTo(new[]
        {
            (EntryKind.Consumer, "Meddelande: OrderPlaced → OrderPlacedConsumer", "OrderPlacedConsumer.Consume", "OrderPlaced"),
            (EntryKind.Consumer, "Meddelande: OrderPlaced → OrderPlacedHandler", "OrderPlacedHandler.Handle", "OrderPlaced"),
        }));
        Assert.That(map.EntryPoints.Select(e => Uses(e).Single()), Is.All.EqualTo("file | skriva ut.txt"));
    }

    [Test]
    public async Task Handlers_that_are_called_in_process_or_only_look_like_consumers_are_not_entries()
    {
        var map = await MapAsync(Writer + """
            public class OrderPlaced { public int OrderId { get; set; } }
            // Heter Consumer men ärver inget gränssnitt.
            public class OrderPlacedConsumer { public Task Consume(OrderPlaced m) => Task.CompletedTask; }
            // Och en med eget gränssnitt som heter likadant men ligger i en annan namnrymd.
            public interface IConsumer<T> { Task Consume(T message); }
            public class OwnConsumer : IConsumer<OrderPlaced> { public Task Consume(OrderPlaced m) => Task.CompletedTask; }
            // MediatR anropas inom processen och nås från den som anropar dem.
            public class PlacedHandler : MediatR.INotificationHandler<OrderPlaced> { public Task Handle(OrderPlaced n, CancellationToken ct) => Task.CompletedTask; }
            public class PlaceHandler : MediatR.IRequestHandler<OrderPlaced, int> { public Task<int> Handle(OrderPlaced r, CancellationToken ct) => Task.FromResult(1); }
            """);
        Assert.That(map.EntryPoints, Is.Empty);
    }

    [Test]
    public async Task A_Service_Bus_processor_handler_is_an_entry_named_after_the_queue()
    {
        var map = await MapAsync(Writer + """
            public class Receiver(Writer w)
            {
                public void Start()
                {
                    var processor = new Azure.Messaging.ServiceBus.ServiceBusClient().CreateProcessor("order-lagd");
                    processor.ProcessMessageAsync += HandleAsync;
                }
                private Task HandleAsync(Azure.Messaging.ServiceBus.ProcessMessageEventArgs args) { w.Write(); return Task.CompletedTask; }
            }
            // En egen händelse med samma namn är ingen Service Bus.
            public class Own { public event Func<int, Task>? ProcessMessageAsync; public void Start(Own other) { other.ProcessMessageAsync += x => Task.CompletedTask; } }
            """);
        var entry = map.EntryPoints.Single();
        Assert.That((entry.Kind, entry.Key, entry.Handler), Is.EqualTo((EntryKind.Consumer, "Meddelande: order-lagd → Receiver.HandleAsync", "Receiver.HandleAsync")));
        Assert.That(Uses(entry), Is.EqualTo(new[] { "file | skriva ut.txt" }));
    }

    // --- Jobb ----------------------------------------------------------------------------------

    [Test]
    public async Task Hangfire_jobs_are_entries_and_a_job_through_an_interface_follows_the_implementation()
    {
        var map = await MapAsync(Writer + """
            public interface IReportService { Task RunAsync(); }
            public class ReportService(Writer w) : IReportService { public Task RunAsync() { w.Write(); return Task.CompletedTask; } }
            public static class Cleanup { public static Task RunAsync() { new Writer().Write(); return Task.CompletedTask; } }
            public class Schedule
            {
                public void Register()
                {
                    Hangfire.RecurringJob.AddOrUpdate<IReportService>("nattrapport", x => x.RunAsync(), "0 3 * * *");
                    Hangfire.BackgroundJob.Enqueue(() => Cleanup.RunAsync());
                }
            }
            // Ett eget anrop som heter Enqueue är inget Hangfire-jobb.
            public static class Jobs { public static void Enqueue(System.Linq.Expressions.Expression<Func<Task>> job) { } }
            public static class Misc { public static Task Tick() => Task.CompletedTask; }
            public class Other { public void Go() => Jobs.Enqueue(() => Misc.Tick()); }
            """);
        Assert.That(map.EntryPoints.Select(e => (e.Kind, e.Key, e.Handler)), Is.EquivalentTo(new[]
        {
            (EntryKind.Job, "Jobb: nattrapport → IReportService.RunAsync", "IReportService.RunAsync"),
            (EntryKind.Job, "Jobb: Cleanup.RunAsync", "Cleanup.RunAsync"),
        }));
        Assert.That(map.EntryPoints.Select(e => Uses(e).Single()), Is.All.EqualTo("file | skriva ut.txt"), "också genom gränssnittet");
    }

    [Test]
    public async Task A_Quartz_job_is_an_entry_and_a_class_named_Job_without_the_interface_is_not()
    {
        var map = await MapAsync(Writer + """
            public class NightlyReportJob(Writer w) : Quartz.IJob { public Task Execute(Quartz.IJobExecutionContext c) { w.Write(); return Task.CompletedTask; } }
            public class CleanupJob { public Task Execute(object c) => Task.CompletedTask; }
            """);
        Assert.That(map.EntryPoints.Select(e => (e.Kind, e.Key)), Is.EqualTo(new[] { (EntryKind.Job, "Jobb: NightlyReportJob") }));
        Assert.That(Uses(map.EntryPoints.Single()), Is.EqualTo(new[] { "file | skriva ut.txt" }));
    }

    // --- gRPC och SignalR ----------------------------------------------------------------------

    private const string GreeterCode = """
        namespace Greet
        {
            public class HelloRequest { public string Name { get; set; } = ""; }
            public class HelloReply { public string Message { get; set; } = ""; }
            public static class Greeter
            {
                [Grpc.Core.BindServiceMethod(typeof(Greeter), "BindService")]
                public abstract class GreeterBase
                {
                    public virtual Task<HelloReply> SayHello(HelloRequest request, Grpc.Core.ServerCallContext context) => throw new NotImplementedException();
                    public virtual Task<HelloReply> SayBye(HelloRequest request, Grpc.Core.ServerCallContext context) => throw new NotImplementedException();
                }
            }
            // Basen i en vanlig klass utan [BindServiceMethod]: ingen genererad gRPC-bas.
            public static class Plain { public abstract class PlainBase { public virtual Task<HelloReply> Ping(HelloRequest r) => throw new NotImplementedException(); } }
        }
        """;

    [Test]
    public async Task A_gRPC_service_gives_an_entry_per_overridden_call_and_near_misses_are_not_services()
    {
        var map = await MapAsync(Writer + GreeterCode + """
            public class GreeterService(Writer w) : Greet.Greeter.GreeterBase
            {
                public override Task<Greet.HelloReply> SayHello(Greet.HelloRequest request, Grpc.Core.ServerCallContext context) { w.Write(); return Task.FromResult(new Greet.HelloReply()); }
                public Task<int> NotAnRpc() => Task.FromResult(1);
            }
            public class PlainService : Greet.Plain.PlainBase { public override Task<Greet.HelloReply> Ping(Greet.HelloRequest r) => Task.FromResult(new Greet.HelloReply()); }
            """);
        var entry = map.EntryPoints.Single();
        Assert.That((entry.Kind, entry.Key, entry.Handler, entry.Input?.Type, entry.Output?.Type), Is.EqualTo((EntryKind.Grpc, "gRPC: Greeter/SayHello", "GreeterService.SayHello", "HelloRequest", "HelloReply")));
        Assert.That(Uses(entry), Is.EqualTo(new[] { "file | skriva ut.txt" }));
    }

    [Test]
    public async Task A_gRPC_service_mapped_with_MapGrpcService_is_found_without_the_attribute_on_the_base()
    {
        var code = GreeterCode.Replace("[Grpc.Core.BindServiceMethod(typeof(Greeter), \"BindService\")]", "") + """
            public class GreeterService : Greet.Greeter.GreeterBase
            {
                public override Task<Greet.HelloReply> SayBye(Greet.HelloRequest request, Grpc.Core.ServerCallContext context) => Task.FromResult(new Greet.HelloReply());
            }
            """;
        Assert.That((await MapAsync(Build(true, Web("Api", AppStart + code)))).EntryPoints, Is.Empty, "utan mappning och attribut är det en vanlig klass");
        var mapped = await MapAsync(Build(true, Web("Api", AppStart + code + "\npublic static class Wire { public static void Map(WebApplication app) { app.MapGrpcService<GreeterService>(); } }")));
        Assert.That(Keys(mapped), Is.EqualTo(new[] { "gRPC: Greeter/SayBye" }));
    }

    [Test]
    public async Task A_SignalR_hub_gives_an_entry_per_public_method_and_other_classes_do_not()
    {
        var map = await MapAsync(Writer + """
            public class ChatHub(Writer w) : Microsoft.AspNetCore.SignalR.Hub
            {
                public Task Send(string user, string text) { w.Write(); return Task.CompletedTask; }
                private void Hidden() { }
                protected void Guarded() { }
            }
            public interface IClient { Task Show(string text); }
            public class TypedHub : Microsoft.AspNetCore.SignalR.Hub<IClient> { public Task Ping() => Task.CompletedTask; }
            public class NotAHub { public Task Send(string user, string text) => Task.CompletedTask; }
            """);
        Assert.That(map.EntryPoints.Select(e => (e.Kind, e.Key, e.Handler)), Is.EquivalentTo(new[]
        {
            (EntryKind.SignalR, "SignalR: ChatHub.Send", "ChatHub.Send"),
            (EntryKind.SignalR, "SignalR: TypedHub.Ping", "TypedHub.Ping"),
        }));
        Assert.That(Uses(map.EntryPoints.First(e => e.Handler == "ChatHub.Send")), Is.EqualTo(new[] { "file | skriva ut.txt" }));
    }

    // --- Sorter tillsammans: spec, appar, JSON, text, Mermaid ----------------------------------

    private const string MixedCode = """
        public class OrderCleanupWorker(Writer w) : Microsoft.Extensions.Hosting.BackgroundService
        {
            protected override Task ExecuteAsync(CancellationToken ct) { w.Write(); return Task.CompletedTask; }
        }
        public class OrderPlaced { public int OrderId { get; set; } }
        public class OrderPlacedConsumer(Writer w) : MassTransit.IConsumer<OrderPlaced>
        {
            public Task Consume(MassTransit.ConsumeContext<OrderPlaced> context) { w.Write(); return Task.CompletedTask; }
        }
        public class ChatHub : Microsoft.AspNetCore.SignalR.Hub { public Task Send(string text) => Task.CompletedTask; }
        [ApiController, Route("x")] public class XController(Writer w) : ControllerBase { [HttpPost] public int Run() { w.Write(); return 1; } }
        """;

    private static Task<CodeMapModel> Mixed(IReadOnlyList<ApiOperation>? operations = null) =>
        MapAsync(Build(true, Web("Api", AppStart + Writer + MixedCode)), operations);

    [Test]
    public async Task Entries_that_are_not_HTTP_are_never_reported_as_only_in_the_code_against_the_spec()
    {
        var map = await Mixed([Op("Run", "POST", "/x"), Op("Gone", "GET", "/gone")]);
        Assert.That(map.Differences.Select(d => (d.Kind, d.Method, d.Path)), Is.EqualTo(new[] { (DifferenceKind.OnlyInSpec, "GET", "/gone") }),
            "bara specens operation som koden saknar; bakgrundstjänsten, meddelandet och hubben är inga API-anrop");
        Assert.That(map.EntryPoints.Where(e => !e.Kind.IsHttp()).Select(e => e.OperationId), Is.All.Null);
        Assert.That(map.EntryPoints.Single(e => e.Kind == EntryKind.Controller).OperationId, Is.EqualTo("Run"));
    }

    [Test]
    public async Task Entries_come_in_the_order_HTTP_first_then_by_kind_and_name()
    {
        var map = await Mixed();
        Assert.That(Keys(map), Is.EqualTo(new[]
        {
            "POST /x", "Bakgrund: OrderCleanupWorker", "Meddelande: OrderPlaced → OrderPlacedConsumer", "SignalR: ChatHub.Send",
        }));
        Assert.That(CallGroups.Order(map.EntryPoints).Select(e => e.Key), Is.EqualTo(Keys(map)), "texten följer samma ordning");
    }

    [Test]
    public async Task Apps_get_worker_and_console_kinds_from_the_entries_in_their_projects_and_the_projects_they_use()
    {
        var map = await MapAsync(Build(true,
            new Proj("Lib", Writer + """
                public class Cleanup(Writer w) : Microsoft.Extensions.Hosting.BackgroundService
                {
                    protected override Task ExecuteAsync(CancellationToken ct) { w.Write(); return Task.CompletedTask; }
                }
                """),
            new Proj("Worker", """
                using Microsoft.Extensions.DependencyInjection;
                var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);
                builder.Services.AddHostedService<Cleanup>();
                builder.Build().Run();
                """, "Lib") { Exe = true },
            new Proj("Tool", "new Writer().Write();", "Lib") { Exe = true },
            Web("Api", AppStart + """
                [ApiController, Route("x")] public class XController : ControllerBase { [HttpGet] public int Get() => 1; }
                public class Hosted : Microsoft.Extensions.Hosting.BackgroundService { protected override Task ExecuteAsync(CancellationToken ct) => Task.CompletedTask; }
                public static class Registration { public static void Add(IServiceCollection services) => services.AddHostedService<Hosted>(); }
                """)));
        Assert.That(map.Apps.Select(a => (a.Project, string.Join("+", a.Kinds))), Is.EquivalentTo(new[]
        {
            ("Worker", "Worker"), ("Tool", "Console"), ("Api", "Api+Worker"),
        }), "Lib är ett bibliotek, Worker registrerar tjänsten i Lib, ett konsolprogram kör inte tjänsterna i biblioteken det använder och en webbapp kan också ha bakgrundsarbete");
        Assert.That(string.Join("\n", CodeMapText.Lines(map)), Does.Contain("  Worker: worker").And.Contain("  Tool: console").And.Contain("  Api: api och worker"));
    }

    [Test]
    public async Task Json_for_entries_that_are_not_HTTP_has_a_name_a_kind_and_no_method_or_path()
    {
        var map = await Mixed();
        using var doc = JsonDocument.Parse(CodeMapJson.Serialize(map));
        Assert.That(doc.RootElement.GetProperty("schemaVersion").GetInt32(), Is.EqualTo(4));
        var entries = doc.RootElement.GetProperty("entryPoints").EnumerateArray().ToList();
        Assert.That(entries.Select(e => e.GetProperty("kind").GetString()), Is.EqualTo(new[] { "controller", "background", "consumer", "signalr" }));
        var worker = entries[1];
        Assert.That(worker.GetProperty("name").GetString(), Is.EqualTo("Bakgrund: OrderCleanupWorker"));
        Assert.That(new[] { "method", "path", "codePath" }.Where(p => worker.TryGetProperty(p, out _)), Is.Empty, "ingen HTTP-metod eller sökväg");
        Assert.That(entries[0].TryGetProperty("name", out _), Is.False, "ett HTTP-anrop heter metod och sökväg");
        Assert.That(entries[0].GetProperty("method").GetString(), Is.EqualTo("POST"));
        Assert.That(entries[2].GetProperty("input").GetProperty("type").GetString(), Is.EqualTo("OrderPlaced"));
        var overview = doc.RootElement.GetProperty("overview").GetProperty("nodes").EnumerateArray().Where(n => n.GetProperty("kind").GetString() == "entry").ToList();
        Assert.That(overview.Select(n => (n.GetProperty("name").GetString(), n.GetProperty("ref").GetString())), Is.EquivalentTo(new[]
        {
            ("POST /x", "controller"), ("Bakgrund: OrderCleanupWorker", "background"), ("Meddelande: OrderPlaced → OrderPlacedConsumer", "consumer"), ("SignalR: ChatHub.Send", "signalr"),
        }));
        Assert.That(Walk(doc.RootElement).Count(e => e.ValueKind == JsonValueKind.Null), Is.Zero, "null utelämnas");
    }

    private static IEnumerable<JsonElement> Walk(JsonElement e)
    {
        yield return e;
        if (e.ValueKind == JsonValueKind.Object) foreach (var p in e.EnumerateObject()) foreach (var c in Walk(p.Value)) yield return c;
        else if (e.ValueKind == JsonValueKind.Array) foreach (var i in e.EnumerateArray()) foreach (var c in Walk(i)) yield return c;
    }

    [Test]
    public async Task Text_and_Mermaid_print_entries_that_are_not_HTTP_by_name()
    {
        var map = await Mixed();
        var text = string.Join("\n", CodeMapText.Lines(map));
        Assert.That(text, Does.Contain("\nBakgrund: OrderCleanupWorker  OrderCleanupWorker.ExecuteAsync [Api]\n  Writer"));
        Assert.That(text, Does.Contain("\nMeddelande: OrderPlaced → OrderPlacedConsumer  OrderPlacedConsumer.Consume [Api]\n  in: OrderPlaced {OrderId}"));
        Assert.That(text, Does.Not.Contain("background").And.Not.Contain(" consumer"), "namnet säger redan vad det är");

        var mermaid = CodeMapMermaid.Write(map, new MermaidOptions());
        Assert.That(mermaid, Does.Contain("in_Bakgrund_OrderCleanupWorker{{\"Bakgrund: OrderCleanupWorker\"}}"), "sexhörning, inte flaggan för HTTP");
        Assert.That(mermaid, Does.Contain("in_POST_x>\"POST /x\"]"));

        var only = CodeMapMermaid.Write(map, MermaidOptions.Parse(null, null, "bakgrund:  ordercleanupworker"));
        Assert.That(only, Does.Contain("%% Bara vägen för Bakgrund: OrderCleanupWorker.").And.Not.Contain("in_POST_x").And.Not.Contain("ChatHub"));
        Assert.Throws<UsageException>(() => CodeMapMermaid.Write(map, MermaidOptions.Parse(null, null, "Bakgrund: Finns inte")));
    }

    [Test]
    public async Task The_warning_for_no_entries_lists_every_kind_that_is_recognized()
    {
        var map = await MapAsync("public class Ingenting { }");
        Assert.That(map.Warnings.Single(), Does.Contain("Hittade inga ingångar").And.Contain("controllers").And.Contain("minimal API").And.Contain("Razor Pages").And.Contain("Blazor-sidor")
            .And.Contain("bakgrundstjänster").And.Contain("konsolprogram").And.Contain("meddelandehanterare").And.Contain("jobb").And.Contain("gRPC").And.Contain("SignalR"));
    }

    // --- Sidan ---------------------------------------------------------------------------------

    [Test]
    public async Task Page_groups_entries_that_are_not_HTTP_by_kind_after_the_HTTP_groups_and_matching_ignores_them()
    {
        var map = await MapAsync(Build(true, Web("Api", AppStart + Writer + MixedCode + """
            public class OtherJob : Quartz.IJob { public Task Execute(Quartz.IJobExecutionContext c) => Task.CompletedTask; }
            public class OtherConsumer : MassTransit.IConsumer<OrderPlaced> { public Task Consume(MassTransit.ConsumeContext<OrderPlaced> c) => Task.CompletedTask; }
            """)));
        await WithPageAsync(map, async (page, ready) =>
        {
            var groups = await page.EvaluateAsync<string[]>(
                "() => SystemMap.groups(JSON.parse(document.getElementById('map-data').textContent).entryPoints).map(g => (g.label || '-') + ': ' + g.entries.map(e => e.name || e.method + ' ' + e.path).join(', '))");
            Assert.That(groups, Is.EqualTo(new[]
            {
                "-: POST /x",
                "Bakgrundstjänster: Bakgrund: OrderCleanupWorker",
                "Meddelanden: Meddelande: OrderPlaced → OrderPlacedConsumer, Meddelande: OrderPlaced → OtherConsumer",
                "Jobb: Jobb: OtherJob",
                "SignalR: SignalR: ChatHub.Send",
            }), "HTTP först, sedan en grupp per sort med sorten som etikett, också för ett ensamt anrop");

            Assert.That(await GroupsAsync(page, "entry"),
                Is.EqualTo(new[] { "Bakgrundstjänster (1)", "Meddelanden (2)", "Jobb (1)", "SignalR (1)" }), "grupperna i listan med anrop");
            Assert.That(await OptionTextsAsync(page, "entry"),
                Is.EqualTo(new[] { "POST /x", "Bakgrund: OrderCleanupWorker", "Meddelande: OrderPlaced → OrderPlacedConsumer", "Meddelande: OrderPlaced → OtherConsumer", "Jobb: OtherJob", "SignalR: ChatHub.Send" }));

            // I kartan har bara gruppen med flera anrop en ruta; ett ensamt anrop står för sig.
            Assert.That(await page.EvaluateAsync<string[]>("() => SystemMap.cy().nodes('.t-callgroup').map(g => g.data('label'))"), Is.EqualTo(new[] { "Meddelanden" }));
            Assert.That(await page.EvaluateAsync<int>("() => SystemMap.cy().nodes('.t-entry').length"), Is.EqualTo(6));

            // testgen matchar ett test mot ett HTTP-anrop; det som inte är HTTP har ingen metod eller sökväg att matcha.
            Assert.That(await page.EvaluateAsync<string?>("() => (SystemMap.entryFor(null, 'POST', '/x') || {}).kind || null"), Is.EqualTo("controller"));
            Assert.That(await page.EvaluateAsync<string?>("() => (SystemMap.entryFor(null, 'POST', '') || SystemMap.entryFor(null, undefined, undefined) || {}).kind || null"), Is.Null);

            // Ett valt anrop som inte är HTTP visar sin väg.
            await PickAsync(page, "entry", "Bakgrund: OrderCleanupWorker");
            await ready();
            Assert.That(await page.EvaluateAsync<string[]>("() => SystemMap.cy().nodes('.path').map(n => n.data('label').split('\\n')[0]).sort()"),
                Does.Contain("Bakgrund: OrderCleanupWorker").And.Contain("Writer"));
            Assert.That(await page.EvaluateAsync<string[]>("() => SystemMap.cy().nodes('.t-entry.dimmed').map(n => n.data('label')).sort()"), Does.Contain("POST /x").And.Not.Contain("Bakgrund: OrderCleanupWorker"));
        });
    }
}
