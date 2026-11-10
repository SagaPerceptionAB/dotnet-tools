namespace TestGen.Browser;

/// <summary>Chromium är inte installerat. Meddelandet säger hur det installeras.</summary>
public sealed class ChromiumMissingException(string message, Exception? inner = null) : Exception(message, inner)
{
    public static ChromiumMissingException Create(Exception? inner = null) => new(
        "Chromium är inte installerat. Kör testgen init, som installerar det (eller: pwsh "
        + Path.Combine(AppContext.BaseDirectory, "playwright.ps1") + " install chromium)", inner);
}
