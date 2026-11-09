namespace SagaPerception.TestGen.NUnit.Tests;

/// <summary>Var exemplen ligger, hittat uppifrån testernas katalog.</summary>
internal static class Samples
{
    public static string Directory { get; } = Find("samples");

    /// <summary>Katalogen där Shop.Tests ligger; solutionen letas uppåt härifrån.</summary>
    public static string ShopTests => Path.Combine(Directory, "Shop.Tests");

    public static string Betaltjanst => Path.Combine(Directory, "betaltjanst.openapi.yaml");

    private static string Find(string name)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, name);
            if (System.IO.Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "Shop.slnx"))) return candidate;
        }
        throw new DirectoryNotFoundException($"Hittar inte {name} uppåt från {AppContext.BaseDirectory}.");
    }
}
