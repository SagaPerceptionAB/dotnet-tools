using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace SagaPerception.TestGen.NUnit.Tests;

public class AppHostTests
{
    private static HttpClient Client(IApiFactory factory) => new(factory.CreateHandler()) { BaseAddress = new Uri("http://localhost/") };

    private sealed class FailingCheck : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(HealthCheckResult.Unhealthy("databasen svarar inte"));
    }

    [Test]
    public async Task Live_and_ready_return_200()
    {
        using var factory = ApiFactory.Create("TestApi");
        using var client = Client(factory);

        var live = await client.GetAsync("/health/live");
        var ready = await client.GetAsync("/health/ready");

        Assert.That(live.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(ready.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task StartOnKestrel_serves_the_app_on_a_real_port()
    {
        using var factory = ApiFactory.Create("TestApi");
        var url = factory.StartOnKestrel();

        using var http = new HttpClient();
        var live = await http.GetAsync(url + "/health/live");

        Assert.That(url, Does.StartWith("http://").And.Not.EndWith(":0"));
        Assert.That(live.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public void StartOnKestrel_after_start_is_refused()
    {
        using var factory = ApiFactory.Create("TestApi");
        _ = factory.Services;

        Assert.Throws<InvalidOperationException>(() => factory.StartOnKestrel());
    }

    [Test]
    public async Task Failing_check_gives_503_on_ready_but_200_on_live()
    {
        using var factory = ApiFactory.Create("TestApi").WithServices(s =>
            s.AddHealthChecks().AddCheck<FailingCheck>("db"));
        using var client = Client(factory);

        var ready = await client.GetAsync("/health/ready");
        var live = await client.GetAsync("/health/live");

        Assert.That(ready.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        Assert.That(await ready.Content.ReadAsStringAsync(), Is.EqualTo("Unhealthy"));
        Assert.That(live.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task WithSetting_reaches_the_app_and_environment_is_Testing()
    {
        using var factory = ApiFactory.Create("TestApi").WithSetting("Min:Nyckel", "värde");
        using var client = Client(factory);

        Assert.That(await client.GetStringAsync("/setting/Min:Nyckel"), Is.EqualTo("värde"));
        Assert.That(factory.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostEnvironment>().EnvironmentName, Is.EqualTo("Testing"));
    }

    [Test]
    public void WithEnvironment_sets_the_environment_of_the_app_and_of_BuildHost()
    {
        using var factory = ApiFactory.Create("TestApi").WithEnvironment("Development");
        Assert.That(factory.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostEnvironment>().EnvironmentName, Is.EqualTo("Development"));

        using var host = ApiFactory.BuildHost("TestApi", environment: "Development");
        Assert.That(host.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostEnvironment>().EnvironmentName, Is.EqualTo("Development"));

        using var started = ApiFactory.Create("TestApi");
        _ = started.Services;
        Assert.Throws<InvalidOperationException>(() => started.WithEnvironment("Development"));
    }

    [Test]
    public async Task WithSetting_is_visible_to_the_app_before_it_is_built()
    {
        using var factory = ApiFactory.Create("TestApi").WithSetting("Early:Key", "tidigt");
        using var client = Client(factory);

        Assert.That(await client.GetStringAsync("/early"), Is.EqualTo("tidigt"));
    }

    [Test]
    public async Task Create_finds_the_app_by_project_name()
    {
        using var factory = ApiFactory.Create("TestApi").WithSetting("Early:Key", "via namn");
        using var client = Client(factory);

        Assert.That(await client.GetStringAsync("/early"), Is.EqualTo("via namn"));
        Assert.That(await client.GetStringAsync("/ping"), Is.EqualTo("pong"));
    }

    [Test]
    public void Create_with_an_unknown_project_says_what_to_do()
    {
        var e = Assert.Throws<InvalidOperationException>(() => ApiFactory.Create("FinnsInte"))!;
        Assert.That(e.Message, Does.Contain("ProjectReference"));
    }

    [Test]
    public void BuildHost_builds_the_services_with_the_settings_without_running_the_code_after_Build()
    {
        using var host = ApiFactory.BuildHost("TestApi", new Dictionary<string, string?> { ["Early:Key"] = "tidigt", ["Min:Nyckel"] = "värde" });

        var configuration = host.Services.GetRequiredService<IConfiguration>();
        Assert.That(configuration["Early:Key"], Is.EqualTo("tidigt"));
        Assert.That(configuration["Min:Nyckel"], Is.EqualTo("värde"));
        Assert.That(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"], Is.EqualTo(""), "samma standardvärden som fabriken");
        Assert.That(host.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostEnvironment>().EnvironmentName, Is.EqualTo("Testing"));
        Assert.That(configuration["AfterBuild:Ran"], Is.Null, "koden efter Build() ska inte köras");

        using var running = ApiFactory.Create("TestApi");
        Assert.That(running.Services.GetRequiredService<IConfiguration>()["AfterBuild:Ran"], Is.EqualTo("yes"), "kontroll: fabriken kör den");
    }

    [Test]
    public void BuildHost_with_an_unknown_project_says_what_to_do()
    {
        Assert.That(Assert.Throws<InvalidOperationException>(() => ApiFactory.BuildHost("FinnsInte"))!.Message, Does.Contain("ProjectReference"));
    }

    [Test]
    public async Task WithServices_after_start_throws()
    {
        using var factory = ApiFactory.Create("TestApi");
        using var client = Client(factory);
        Assert.Throws<InvalidOperationException>(() => factory.WithSetting("a", "b"));
        await Task.CompletedTask;
    }

    [Test]
    public async Task Logs_from_the_app_are_captured()
    {
        using var factory = ApiFactory.Create("TestApi");
        using var client = Client(factory);

        var response = await client.GetAsync("/ping");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var log = factory.Logs.Single(l => l.Message.StartsWith("Ping mottagen"));
        Assert.That(log.Level, Is.EqualTo(LogLevel.Information));
        Assert.That(log.Message, Is.EqualTo("Ping mottagen 42"));
    }
}
