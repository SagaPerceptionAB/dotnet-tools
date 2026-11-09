using Microsoft.Extensions.DependencyInjection;

namespace SagaPerception.TestGen.NUnit;

/// <summary>Det som går att göra med en <see cref="ApiFactory{TProgram}"/> utan att känna till appens Program-typ.</summary>
internal interface IApiFactory : IDisposable
{
    /// <summary>Loggposter från appen, i den ordning de skrevs.</summary>
    IReadOnlyList<CapturedLog> Logs { get; }

    IServiceProvider Services { get; }

    /// <summary>Appens miljö, t.ex. Development.</summary>
    IApiFactory WithEnvironment(string name);

    IApiFactory WithSetting(string key, string? value);

    IApiFactory WithServices(Action<IServiceCollection> configure);

    /// <summary>Startar appen på Kestrel, port 0, och ger adressen. Då fungerar inte <see cref="CreateHandler"/>.</summary>
    string StartOnKestrel();

    /// <summary>Hanteraren som skickar anrop till appen i TestServer, för kod som bygger sin egen HttpClient.</summary>
    HttpMessageHandler CreateHandler();
}
