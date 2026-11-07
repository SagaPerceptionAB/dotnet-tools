namespace UiTestGen.Generation;

/// <param name="Path">Relativt utdatamappen, med <c>/</c>.</param>
/// <param name="Overwrite">
/// true för genererade filer (<c>*.g.cs</c>, projektfilen): skrivs alltid om. false för filer som är tänkta att ändras för hand
/// (<c>partial</c>-klasser, README): skrivs bara om de inte redan finns.
/// </param>
public sealed record GeneratedFile(string Path, string Content, bool Overwrite);

/// <param name="ProjectName">Testprojektets namn och rotnamnrymd, t.ex. <c>Shop.UiTests</c>.</param>
/// <param name="Backend">Mockad eller riktig backend som standard i den genererade konfigurationen.</param>
/// <param name="SpecPath">OpenAPI-specen för mockade svar, relativt utdatamappen (kopieras inte). Null utan spec.</param>
/// <param name="Seed">Slumpfrö för testdata, så att samma val ger samma värden.</param>
public sealed record GenerateOptions(string ProjectName, Backend Backend, string? SpecPath = null, int Seed = 12345)
{
    /// <summary>Miljövariabeln med appens adress i de genererade testerna.</summary>
    public const string BaseUrlVariable = "UITEST_BASE_URL";
    /// <summary>Miljövariabeln som väljer backend när testerna körs: <c>mock</c> eller <c>real</c>.</summary>
    public const string BackendVariable = "UITEST_BACKEND";
    /// <summary>Miljövariabeln som slår på stub-servern med standardporten: <c>on</c>. Värdet <c>off</c> stänger av den även om porten är satt.</summary>
    public const string StubVariable = "UITEST_STUB";
    /// <summary>Miljövariabeln med stub-serverns port. Är den satt startar stubben på den porten (när backend mockas).</summary>
    public const string StubPortVariable = "UITEST_STUB_PORT";
    /// <summary>Stub-serverns port när <see cref="StubVariable"/> är <c>on</c> och ingen port är angiven.</summary>
    public const int DefaultStubPort = 5099;

    /// <summary>
    /// Den inlästa OpenAPI-specen som mockade svar skrivs ur. Null utan spec: då skrivs ingen mock-kod.
    /// Läses av anroparen (<see cref="SpecPath"/> är relativ utdatamappen och räcker inte för det).
    /// </summary>
    public TestGen.Core.ApiSpec? Spec { get; init; }
}

public enum Backend { Mock, Real }

/// <summary>Skriver de genererade filerna till disk: genererade filer skrivs om, handskrivna lämnas om de finns.</summary>
public static class GeneratedFiles
{
    /// <returns>Antal filer som skrevs.</returns>
    public static int Write(IEnumerable<GeneratedFile> files, string outputDirectory)
    {
        var written = 0;
        foreach (var f in files)
        {
            var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(outputDirectory, f.Path));
            if (!f.Overwrite && File.Exists(full)) continue;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            if (File.Exists(full) && File.ReadAllText(full) == f.Content) continue;
            File.WriteAllText(full, f.Content);
            written++;
        }
        return written;
    }
}
