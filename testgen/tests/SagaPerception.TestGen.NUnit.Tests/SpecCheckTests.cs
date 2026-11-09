using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using TestGen.Core.Run;

namespace SagaPerception.TestGen.NUnit.Tests;

/// <summary>Specen som en code-first-app serverar jämförs med den incheckade.</summary>
public class SpecCheckTests
{
    private string _dir = "";

    [SetUp] public void Up() => _dir = Directory.CreateTempSubdirectory("testgen-speccheck-").FullName;
    [TearDown] public void Down() => Directory.Delete(_dir, true);

    private static async Task<WebApplication> App(string? path, string? body)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        if (path is not null) app.MapGet(path, () => Microsoft.AspNetCore.Http.Results.Text(body!, "application/json"));
        await app.StartAsync();
        return app;
    }

    private static string RepoSpec => Path.Combine(Samples.Directory, "shop.openapi.yaml");

    [Test]
    public async Task The_spec_is_fetched_from_the_openapi_path()
    {
        await using var app = await App("/openapi/v1.json", """{"openapi":"3.0.1"}""");
        Assert.That(await SpecCheck.FetchAsync(app.GetTestServer().CreateHandler(), CancellationToken.None), Is.EqualTo("""{"openapi":"3.0.1"}"""));
    }

    [Test]
    public async Task The_swagger_path_is_the_second_choice()
    {
        await using var app = await App("/swagger/v1/swagger.json", """{"swagger":"x"}""");
        Assert.That(await SpecCheck.FetchAsync(app.GetTestServer().CreateHandler(), CancellationToken.None), Is.EqualTo("""{"swagger":"x"}"""));
    }

    [Test]
    public async Task An_app_without_a_spec_gives_null()
    {
        await using var app = await App(null, null);
        Assert.That(await SpecCheck.FetchAsync(app.GetTestServer().CreateHandler(), CancellationToken.None), Is.Null);
    }

    [Test]
    public void The_same_spec_in_json_form_matches_the_one_in_the_repo()
    {
        // Samma innehåll i JSON i stället för YAML: formen spelar ingen roll, bara innehållet.
        var repo = Path.Combine(_dir, "min.openapi.yaml");
        File.WriteAllText(repo, """
            openapi: 3.0.1
            info: { title: Min, version: '1' }
            paths:
              /ping:
                get:
                  operationId: ping
                  responses:
                    '200': { description: ok }
            """);
        var served = """{"openapi":"3.0.1","info":{"title":"Min","version":"1"},"paths":{"/ping":{"get":{"operationId":"ping","responses":{"200":{"description":"ok"}}}}}}""";

        Assert.That(SpecCheck.Compare(repo, served), Is.Empty);
    }

    [Test]
    public void A_spec_with_a_missing_operation_gives_a_short_diff()
    {
        var served = """
            {"openapi":"3.0.1","info":{"title":"Shop","version":"1"},
             "paths":{"/customers":{"post":{"operationId":"createCustomer","responses":{"201":{"description":"x"}}}}}}
            """;
        var diff = SpecCheck.Compare(RepoSpec, served);

        Assert.That(diff, Is.Not.Empty);
        Assert.That(diff, Has.Some.Contain("POST /products: finns i specen i repot men inte i appen."));
    }

    [Test]
    public void A_broken_served_spec_is_reported_as_an_error_not_as_a_difference()
    {
        Assert.Throws<SourceException>(() => SpecCheck.Compare(RepoSpec, "inte en spec: [ "));
    }
}
