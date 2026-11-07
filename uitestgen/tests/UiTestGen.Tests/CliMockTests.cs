using UiTestGen.Cli;
using UiTestGen.Model;

namespace UiTestGen.Tests;

public class CliMockTests
{
    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var oldOut = Console.Out;
        var oldErr = Console.Error;
        using var o = new StringWriter();
        using var e = new StringWriter();
        Console.SetOut(o);
        Console.SetError(e);
        try { return (Program.Main(args), o.ToString(), e.ToString()); }
        finally { Console.SetOut(oldOut); Console.SetError(oldErr); }
    }

    private static string Setup(out string catalogPath)
    {
        var dir = Path.Combine(Path.GetTempPath(), "uitestgen-clim-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        catalogPath = Path.Combine(dir, "vykatalog.json");
        ViewCatalogJson.Write(GenerationTests.Catalog(), catalogPath);
        return dir;
    }

    [Test]
    public void Spec_gives_mock_files_and_mock_as_default_backend()
    {
        var dir = Setup(out var catalog);
        try
        {
            var outDir = Path.Combine(dir, "Shop.UiTests");
            var (code, _, err) = Run("generate", "--catalog", catalog, "--out", outDir, "--spec", GenerationMockTests.RepoFile("samples", "shop.openapi.yaml"));
            Assert.That(code, Is.EqualTo(0), err);
            Assert.That(err, Does.Not.Contain("Varning"));
            Assert.That(File.Exists(Path.Combine(outDir, "Support", "ApiMocks.g.cs")), Is.True);
            Assert.That(File.Exists(Path.Combine(outDir, "Support", "ApiStub.g.cs")), Is.True);
            Assert.That(File.ReadAllText(Path.Combine(outDir, "Support", "UiTestBase.g.cs")), Does.Contain("DefaultBackend = \"mock\""));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public void Mock_without_spec_warns_and_writes_no_mocks()
    {
        var dir = Setup(out var catalog);
        try
        {
            var outDir = Path.Combine(dir, "Shop.UiTests");
            var (code, _, err) = Run("generate", "--catalog", catalog, "--out", outDir, "--backend", "mock");
            Assert.That(code, Is.EqualTo(0));
            Assert.That(err, Does.Contain("Varning").And.Contain("--spec"));
            Assert.That(File.Exists(Path.Combine(outDir, "Support", "ApiMocks.g.cs")), Is.False);
            Assert.That(File.ReadAllText(Path.Combine(outDir, "Support", "UiTestBase.g.cs")), Does.Contain("ConfigureMocksAsync(IPage page) => Task.CompletedTask;"));

            // Utan mock ingen varning.
            Assert.That(Run("generate", "--catalog", catalog, "--out", outDir, "--backend", "real").Err, Does.Not.Contain("Varning"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public void Broken_or_missing_spec_gives_a_friendly_message()
    {
        var dir = Setup(out var catalog);
        try
        {
            var outDir = Path.Combine(dir, "Shop.UiTests");
            var broken = Path.Combine(dir, "broken.yaml");
            File.WriteAllText(broken, "openapi: 3.0.3\npaths: [oops\n  - x: : :\n");
            var (code, _, err) = Run("generate", "--catalog", catalog, "--out", outDir, "--spec", broken);
            Assert.That(code, Is.EqualTo(2));
            Assert.That(err, Does.Contain("Kunde inte läsa OpenAPI-spec").And.Contain("Gör så här"));
            Assert.That(err, Does.Not.Contain("Unexpected").And.Not.Contain("Exception"));

            var (missingCode, _, missingErr) = Run("generate", "--catalog", catalog, "--out", outDir, "--spec", Path.Combine(dir, "finns-inte.yaml"));
            Assert.That(missingCode, Is.EqualTo(2));
            Assert.That(missingErr, Does.Contain("Hittar inte specen"));
            Assert.That(Directory.Exists(outDir), Is.False, "Inget skrivs när specen är fel.");
        }
        finally { Directory.Delete(dir, true); }
    }
}
