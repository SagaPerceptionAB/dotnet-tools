using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TestGen.Browser.Tests;

/// <summary>En Chromium för alla tester i projektet.</summary>
[SetUpFixture]
public sealed class BrowserFixture
{
    public static BrowserHost Host { get; private set; } = null!;

    [OneTimeSetUp]
    public async Task StartAsync() => Host = await BrowserHost.StartAsync();

    [OneTimeTearDown]
    public async Task StopAsync() => await Host.DisposeAsync();
}

/// <summary>En liten webbserver på port 0 som testet själv fyller med sidor.</summary>
public sealed class TestApp : IAsyncDisposable
{
    private readonly WebApplication _app;

    private TestApp(WebApplication app, string url)
    {
        _app = app;
        Url = url;
    }

    public string Url { get; }

    public static async Task<TestApp> StartAsync(Action<WebApplication> map)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        map(app);
        await app.StartAsync();
        var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new TestApp(app, url);
    }

    /// <summary>En sida: HTML på angiven sökväg.</summary>
    public static void Page(WebApplication app, string path, string html) =>
        app.MapGet(path, () => Results.Content(html, "text/html; charset=utf-8"));

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}
