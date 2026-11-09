using SagaPerception.Testing;
using TestGen.Core;
using TestGen.Core.Run;

namespace SagaPerception.Testing.NUnit.Tests;

/// <summary>En app med AddOpenApi() (Microsoft.OpenApi 2.x) i testprocessen: fabriken, specen från appen och motorn läser den.</summary>
public class OpenApiAppTests
{
    private string _dir = "";

    [SetUp] public void Up() => _dir = Directory.CreateTempSubdirectory("testgen-openapiapp-").FullName;
    [TearDown] public void Down() => Directory.Delete(_dir, true);

    private async Task<(IApiFactory Factory, string Served, string Path)> ServedSpec()
    {
        var factory = ApiFactory.Create("OpenApiApp");
        var served = await SpecCheck.FetchAsync(factory.CreateHandler(), CancellationToken.None);
        Assert.That(served, Is.Not.Null, "appen ska servera /openapi/v1.json");
        var path = Path.Combine(_dir, "OpenApiApp.openapi.json");
        File.WriteAllText(path, served);
        return (factory, served!, path);
    }

    [Test]
    public async Task The_spec_from_AddOpenApi_is_read_by_the_engine()
    {
        var (factory, _, path) = await ServedSpec();
        using var _ = factory;

        var spec = OpenApiLoader.LoadSpec(path);

        Assert.That(spec.Operations.Select(o => $"{o.Method} {o.Path}"), Is.EqualTo(new[] { "GET /greetings/{id}", "POST /greetings" }));
        var post = spec.Operations.Single(o => o.Method == "POST");
        Assert.That(SchemaUtil.Flatten(post.BodySchema!).Required, Is.EqualTo(new[] { "priority", "text" }));
        Assert.That(SchemaUtil.Flatten(post.BodySchema!).Props.Keys, Is.EqualTo(new[] { "priority", "text" }));
    }

    [Test]
    public async Task The_engine_runs_its_tests_against_the_app_through_the_in_memory_handler()
    {
        var (factory, _, path) = await ServedSpec();
        using var _ = factory;

        var runner = new TestRunner(new RunOptions { Spec = path, Api = TestGenTarget.InProcessUrl, Seed = 1 },
            new Terminal(TextWriter.Null, TextWriter.Null, false), factory.CreateHandler());
        await runner.RunAsync();

        // Appen har ingen indatavalidering, så bara testerna med giltig indata förväntas gå igenom.
        var valid = runner.Outcomes.Where(o => o.Test.Description == "giltig indata").ToList();
        Assert.That(valid, Has.Count.EqualTo(2));
        Assert.That(valid.Where(o => o.Outcome != "OK").Select(o => $"{o.Id} {o.Test.Call}: {string.Join(" / ", o.TechnicalLines.Take(2))}"), Is.Empty);
    }

    [Test]
    public async Task The_spec_check_finds_no_difference_against_the_same_spec()
    {
        var (factory, served, path) = await ServedSpec();
        using var _ = factory;

        Assert.That(SpecCheck.Compare(path, served), Is.Empty);
    }
}
