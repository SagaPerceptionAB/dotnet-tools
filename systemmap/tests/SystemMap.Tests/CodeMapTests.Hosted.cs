namespace SystemMap.Tests;

/// <summary>Bakgrundstjänster: en app kör en tjänst bara om dess uppstart registrerar den (kod i minnet).</summary>
public partial class CodeMapTests
{
    private const string Cleanup = """
        public class Cleanup : Microsoft.Extensions.Hosting.BackgroundService
        {
            protected override Task ExecuteAsync(CancellationToken ct) { System.IO.File.WriteAllText("ut.txt", "x"); return Task.CompletedTask; }
        }
        """;

    private const string Other = "public class Other : Microsoft.Extensions.Hosting.BackgroundService { protected override Task ExecuteAsync(CancellationToken ct) => Task.CompletedTask; }";
    private const string XController = "[ApiController, Route(\"x\")] public class XController : ControllerBase { [HttpGet] public int Get() => 1; }";

    private const string NotRegistered = "Cleanup är en bakgrundstjänst men registreras inte med AddHostedService i någon app.";

    /// <summary>En webbapp (ett program som körs) vars uppstart är <paramref name="startup"/>, med tjänsten Cleanup.</summary>
    private static Task<CodeMapModel> HostedApp(string startup) => MapAsync(Build(true,
        Web("Api", $"var builder = WebApplication.CreateBuilder(args);\n{startup}\nvar app = builder.Build();\napp.Run();\n{Cleanup}\n{Other}\n{XController}") with { Exe = true }));

    private static string Kinds(CodeMapModel map) => string.Join("+", map.Apps.Single().Kinds);

    [TestCase("builder.Services.AddHostedService<Cleanup>();", TestName = "AddHostedService<T>")]
    [TestCase("builder.Services.AddHostedService(sp => new Cleanup());", TestName = "AddHostedService med fabrik")]
    [TestCase("builder.Services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService, Cleanup>();", TestName = "AddSingleton<IHostedService, T>")]
    [TestCase("builder.Services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(sp => new Cleanup());", TestName = "AddSingleton<IHostedService> med fabrik")]
    [TestCase("builder.Services.AddSingleton(typeof(Microsoft.Extensions.Hosting.IHostedService), typeof(Cleanup));", TestName = "AddSingleton med typeof")]
    [TestCase("builder.Services.AddSingleton<Cleanup>(); builder.Services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(sp => sp.GetRequiredService<Cleanup>());",
        TestName = "Samma instans hämtad ur tjänsterna")]
    [TestCase("Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddEnumerable(builder.Services, ServiceDescriptor.Singleton<Microsoft.Extensions.Hosting.IHostedService, Cleanup>());",
        TestName = "TryAddEnumerable med ServiceDescriptor")]
    [TestCase("Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder().ConfigureServices(s => s.AddHostedService<Cleanup>());", TestName = "ConfigureServices med lambda")]
    public async Task A_background_service_the_startup_registers_gives_the_app_the_worker_kind(string startup)
    {
        var map = await HostedApp(startup);
        Assert.That(Kinds(map), Is.EqualTo("Api+Worker"));
        Assert.That(Keys(map), Does.Contain("Bakgrund: Cleanup"));
        Assert.That(map.Warnings, Has.None.Contain("Cleanup är en bakgrundstjänst men"), "Other registreras inte, men den är en annan tjänst");
    }

    [TestCase("", TestName = "Ingen registrering")]
    [TestCase("builder.Services.AddSingleton<Cleanup>();", TestName = "Vanlig registrering är ingen bakgrundstjänst")]
    [TestCase("builder.Services.AddScoped<Cleanup, Cleanup>();", TestName = "Scoped registrering av klassen")]
    [TestCase("var unused = typeof(Cleanup); builder.Services.AddSingleton(typeof(Cleanup));", TestName = "typeof utan IHostedService")]
    public async Task A_background_service_nobody_registers_is_still_an_entry_with_a_warning_and_gives_no_app_the_worker_kind(string startup)
    {
        var map = await HostedApp(startup);
        Assert.That(Kinds(map), Is.EqualTo("Api"), "bara anropet; ingen registrerar Cleanup (eller Other)");
        Assert.That(Keys(map), Does.Contain("Bakgrund: Cleanup"), "koden finns, så den visas");
        Assert.That(map.Warnings, Does.Contain(NotRegistered));
    }

    [TestCase("builder.Services.AddHostedService<Other>();", TestName = "Other med AddHostedService")]
    [TestCase("builder.Services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService, Other>();", TestName = "Other med IHostedService")]
    [TestCase("builder.Services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(sp => new Other());", TestName = "Other med fabrik")]
    public async Task Only_the_service_that_is_registered_counts_and_the_other_is_warned_about(string startup)
    {
        var map = await HostedApp(startup);
        Assert.That(Kinds(map), Is.EqualTo("Api+Worker"), "Other är registrerad");
        Assert.That(map.Warnings.Where(w => w.Contains("bakgrundstjänst men")), Is.EqualTo(new[] { NotRegistered }), "bara Cleanup saknas");
    }

    [Test]
    public async Task A_worker_template_registers_with_builder_Services_AddHostedService_and_an_unregistered_worker_is_no_app()
    {
        var registered = await MapAsync(Build(true, new Proj("Worker", $"using Microsoft.Extensions.DependencyInjection;\nvar builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);\nbuilder.Services.AddHostedService<Cleanup>();\nbuilder.Build().Run();\n{Cleanup}") { Exe = true }));
        Assert.That(registered.Apps.Select(a => (a.Project, string.Join("+", a.Kinds))), Is.EqualTo(new[] { ("Worker", "Worker") }));

        var unregistered = await MapAsync(Build(true, new Proj("Worker", $"var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);\nbuilder.Build().Run();\n{Cleanup}") { Exe = true }));
        Assert.That(unregistered.Apps, Is.Empty, "en värd som inte kör något är ingen worker");
        Assert.That(Keys(unregistered), Does.Contain("Bakgrund: Cleanup"));
        Assert.That(unregistered.Warnings, Does.Contain(NotRegistered));
    }

    [Test]
    public async Task A_registration_in_an_extension_method_counts_when_the_app_calls_it_and_not_when_it_does_not()
    {
        var lib = new Proj("Lib", $$"""
            using Microsoft.Extensions.DependencyInjection;
            public static class Jobs
            {
                public static IServiceCollection AddJobs(this IServiceCollection services) => services.AddHostedService<Cleanup>();
                public static IServiceCollection AddNothing(this IServiceCollection services) => services;
            }
            {{Cleanup}}
            """);
        Proj Api(string call) => Web("Api", $"var builder = WebApplication.CreateBuilder(args);\nbuilder.Services.{call}();\nbuilder.Build().Run();\n{XController}", "Lib") with { Exe = true };

        var called = await MapAsync(Build(true, lib, Api("AddJobs")));
        Assert.That(Kinds(called), Is.EqualTo("Api+Worker"), "extension-metoden körs av uppstarten");
        Assert.That(called.Warnings, Has.None.Contain("bakgrundstjänst men"));

        var notCalled = await MapAsync(Build(true, lib, Api("AddNothing")));
        Assert.That(Kinds(notCalled), Is.EqualTo("Api"), "biblioteket har metoden, men uppstarten anropar den inte");
        Assert.That(notCalled.Warnings, Does.Contain(NotRegistered));
    }

    [Test]
    public async Task A_registration_in_a_method_the_startup_never_reaches_does_not_count()
    {
        var map = await MapAsync(Build(true, Web("Api",
            $"var builder = WebApplication.CreateBuilder(args);\nbuilder.Build().Run();\npublic static class Unused {{ public static void Register(IServiceCollection services) => services.AddHostedService<Cleanup>(); }}\n{Cleanup}\n{XController}") with
        { Exe = true }));
        Assert.That(Kinds(map), Is.EqualTo("Api"), "koden registrerar tjänsten, men ingen kör den");
        Assert.That(map.Warnings, Does.Contain(NotRegistered));
    }

    [Test]
    public async Task Two_apps_that_use_the_same_library_only_get_the_worker_kind_if_they_register_the_service()
    {
        var lib = new Proj("Lib", Cleanup);
        const string Controller = XController;
        var map = await MapAsync(Build(true, lib,
            Web("Registers", $"var builder = WebApplication.CreateBuilder(args);\nbuilder.Services.AddHostedService<Cleanup>();\nbuilder.Build().Run();\n{Controller}", "Lib") with { Exe = true },
            Web("Only", $"var builder = WebApplication.CreateBuilder(args);\nbuilder.Build().Run();\n{Controller.Replace("XController", "YController")}", "Lib") with { Exe = true }));
        Assert.That(map.Apps.Select(a => (a.Project, string.Join("+", a.Kinds))), Is.EquivalentTo(new[] { ("Registers", "Api+Worker"), ("Only", "Api") }));
    }

    [Test]
    public async Task Startup_ConfigureServices_registers_when_UseStartup_points_at_it()
    {
        const string Startup = "public class Startup { public void ConfigureServices(IServiceCollection services) { services.AddHostedService<Cleanup>(); } }";
        var map = await MapAsync(Build(true, Web("Api", $"Microsoft.AspNetCore.Hosting.WebHostBuilderExtensions.UseStartup<Startup>(Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder(args).ConfigureWebHost(w => {{ }})).Build().Run();\n{Startup}\n{Cleanup}\n{XController}") with { Exe = true }));
        Assert.That(Kinds(map), Is.EqualTo("Api+Worker"));

        var unused = await MapAsync(Build(true, Web("Api", $"Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder(args).Build().Run();\nvar app = WebApplication.CreateBuilder(args).Build();\n{Startup}\n{Cleanup}\n{XController}") with { Exe = true }));
        Assert.That(Kinds(unused), Is.EqualTo("Api"), "en Startup som ingen pekar ut körs inte");
    }

    [Test]
    public async Task A_registration_whose_type_cannot_be_read_is_not_guessed_against()
    {
        // AddHostedService<T> i en generisk hjälpmetod: typen syns inte, så vilken tjänst som helst kan vara registrerad.
        var map = await HostedApp("Add<Cleanup>(builder.Services);\nstatic void Add<T>(IServiceCollection services) where T : class, Microsoft.Extensions.Hosting.IHostedService => services.AddHostedService<T>();");
        Assert.That(Kinds(map), Is.EqualTo("Api+Worker"));
        Assert.That(map.Warnings, Has.None.Contain("Cleanup är en bakgrundstjänst men"), "ingen varning när något inte går att läsa");
    }
}
