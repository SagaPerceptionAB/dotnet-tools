using Microsoft.Extensions.DependencyInjection;

namespace SagaPerception.Testing;

/// <summary>Det som går att göra med en <see cref="ApiFactory{TProgram}"/> utan att känna till appens Program-typ.</summary>
public interface IApiFactory : IDisposable, IAsyncDisposable
{
    TelemetryCapture Telemetry { get; }

    IServiceProvider Services { get; }

    /// <summary>Appens miljö, t.ex. Development.</summary>
    IApiFactory WithEnvironment(string name);

    IApiFactory WithSetting(string key, string? value);

    IApiFactory WithServices(Action<IServiceCollection> configure);

    /// <summary>En HttpClient mot appen i TestServer.</summary>
    HttpClient CreateClient();

    /// <summary>Startar appen på Kestrel, port 0, och ger adressen. Då fungerar varken <see cref="CreateHandler"/> eller <see cref="CreateClient"/>.</summary>
    string StartOnKestrel();

    /// <summary>Hanteraren som skickar anrop till appen i TestServer, för kod som bygger sin egen HttpClient.</summary>
    HttpMessageHandler CreateHandler();
}
