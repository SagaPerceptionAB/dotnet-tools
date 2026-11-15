namespace SystemMap.Tests;

/// <summary>Exemplet i repots <c>samples</c>-mapp, som testerna ritar kartan för.</summary>
internal static class TestPaths
{
    public static string Samples { get; } = Find();
    public static string Solution => Path.Combine(Samples, "Shop.slnx");
    public static string Spec => Path.Combine(Samples, "shop.openapi.yaml");

    /// <summary>
    /// Lägger repots global.json i en tillfällig solution, så att den läses med samma .NET SDK som repot (inte den senaste på datorn).
    /// Utan den läses projekten utanför repot med den nyaste SDK:n, vars Razor-generator kan vara byggd för en nyare Roslyn än kartans.
    /// </summary>
    public static void PinSdk(string dir) => File.Copy(Path.Combine(Samples, "..", "global.json"), Path.Combine(dir, "global.json"), overwrite: true);

    private static string Find()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "samples", "Shop.slnx"))) return Path.Combine(d.FullName, "samples");
        throw new FileNotFoundException("samples/Shop.slnx");
    }
}
