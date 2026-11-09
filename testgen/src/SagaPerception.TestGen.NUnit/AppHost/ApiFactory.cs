using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SagaPerception.TestGen.NUnit;

/// <summary>
/// WebApplicationFactory för appen under test: miljön Testing (ändras med WithEnvironment), telemetriexport avstängd
/// (tom OTLP-adress, även om OTEL_EXPORTER_OTLP_ENDPOINT är satt på maskinen) och loggarna fångade i <see cref="Logs"/>.
/// Anropa WithSetting/WithServices innan första Services/Server. Värdena sätts med UseSetting, så appen ser dem också innan den byggts.
/// </summary>
internal class ApiFactory<TProgram> : WebApplicationFactory<TProgram>, IApiFactory where TProgram : class
{
    // Konfiguration i minnet vinner över miljövariabeln på maskinen.
    private readonly Dictionary<string, string?> _settings = new(ApiFactory.DefaultSettings);
    private readonly List<Action<IServiceCollection>> _services = [];
    private bool _started;
    private bool _kestrel;
    private string _environment = ApiFactory.DefaultEnvironment;

    private readonly LogCapture _logs = new();

    /// <summary>Loggposter från appen, i den ordning de skrevs.</summary>
    public IReadOnlyList<CapturedLog> Logs => _logs.Logs;

    /// <summary>Appens miljö (standard Testing). Anropa före första Services/Server.</summary>
    public ApiFactory<TProgram> WithEnvironment(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        EnsureNotStarted();
        _environment = name;
        return this;
    }

    /// <summary>Sätter ett konfigurationsvärde (t.ex. "ConnectionStrings:Db"). Skriver över standardvärdena.</summary>
    public ApiFactory<TProgram> WithSetting(string key, string? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        EnsureNotStarted();
        _settings[key] = value;
        return this;
    }

    /// <summary>
    /// Startar appen på Kestrel, port 0, och ger adressen (<c>http://127.0.0.1:port</c>), för tester som behöver en riktig port, t.ex. en webbläsare.
    /// Anropa efter WithSetting/WithServices. <see cref="CreateHandler"/> och <c>Server</c> fungerar inte på Kestrel; använd adressen.
    /// </summary>
    public string StartOnKestrel()
    {
        EnsureNotStarted();
        _kestrel = true;
        StartServer();
        return Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    /// <summary>Hanteraren som skickar anrop till appen i TestServer.</summary>
    public HttpMessageHandler CreateHandler() => Server.CreateHandler();

    IApiFactory IApiFactory.WithEnvironment(string name) => WithEnvironment(name);

    IApiFactory IApiFactory.WithSetting(string key, string? value) => WithSetting(key, value);

    IApiFactory IApiFactory.WithServices(Action<IServiceCollection> configure) => WithServices(configure);

    /// <summary>Ändrar tjänsteregistreringarna, t.ex. för att byta ut ett beroende mot en fake.</summary>
    public ApiFactory<TProgram> WithServices(Action<IServiceCollection> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        EnsureNotStarted();
        _services.Add(configure);
        return this;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        if (_kestrel) UseKestrel(0);
        // UseSetting når också konfiguration som appen läser före Build() (t.ex. en adress i Program.cs); ConfigureAppConfiguration kommer för sent.
        foreach (var (key, value) in _settings)
            builder.UseSetting(key, value);
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(_settings));
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<ILoggerProvider>(_logs);
            foreach (var configure in _services)
                configure(services);
        });
    }

    private void EnsureNotStarted()
    {
        // Server skapas lazy; efter första Services/Server ignoreras ändringar, så tala om det tydligt.
        if (_started)
            throw new InvalidOperationException("WithSetting/WithServices måste anropas innan fabriken startats (första Services eller Server).");
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        _started = true;
        return base.CreateHost(builder);
    }
}
