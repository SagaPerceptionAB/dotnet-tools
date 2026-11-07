namespace SystemMap.Tests;

/// <summary>Exemplet i repots <c>samples</c>-mapp, som testerna ritar kartan för.</summary>
internal static class TestPaths
{
    public static string Samples { get; } = Find();
    public static string Solution => Path.Combine(Samples, "Shop.slnx");
    public static string Spec => Path.Combine(Samples, "shop.openapi.yaml");

    private static string Find()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "samples", "Shop.slnx"))) return Path.Combine(d.FullName, "samples");
        throw new FileNotFoundException("samples/Shop.slnx");
    }
}
