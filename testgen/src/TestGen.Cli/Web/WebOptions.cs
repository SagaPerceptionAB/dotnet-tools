namespace TestGen.Cli.Web;

/// <summary>Inställningar för <c>testgen web</c>.</summary>
public sealed class WebOptions
{
    /// <summary>Port på 127.0.0.1. 0 = slumpad ledig port (används i tester).</summary>
    public int Port { get; init; } = DefaultPort;
    /// <summary>Förifyllt värde i fältet Spec (sökväg eller URL).</summary>
    public string? Spec { get; init; }
    /// <summary>Förifyllt värde i fältet Excel.</summary>
    public string? Excel { get; init; }
    /// <summary>Solutionen som systemkartan ritas för. Utan den letas solutionen upp från mappen testgen startades i.</summary>
    public string? Solution { get; init; }
    /// <summary>testgen.yaml att använda i stället för att leta upp den vid solutionen.</summary>
    public string? Config { get; init; }
    /// <summary>Tester: ger en fejkad HTTP-handler för API:et som testas, så att inget nät behövs.</summary>
    public Func<HttpMessageHandler?>? HandlerFactory { get; init; }

    public const int DefaultPort = 5100;
}
