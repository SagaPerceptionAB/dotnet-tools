using TestGen.Core.Run;

namespace TestGen.Mock;

/// <summary>Inställningar för <see cref="MockServer"/>.</summary>
public sealed record MockOptions
{
    /// <summary>0 låter systemet välja en ledig port.</summary>
    public int Port { get; init; }

    /// <summary>Slumpfröet för svaren. Samma frö och samma anrop ger samma svar.</summary>
    public int Seed { get; init; } = 12345;

    /// <summary>Största tillåtna kropp i byte. Större anrop får 413.</summary>
    public int MaxBodyBytes { get; init; } = 1_000_000;

    /// <summary>Avgör vad som maskas i loggen.</summary>
    public HeaderMask Mask { get; init; } = HeaderMask.Default;
}
