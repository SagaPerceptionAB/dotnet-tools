using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TestGen.Core;
using UiTestGen.Generation;

namespace UiTestGen.Tests;

public class GenerationMockTests
{
    internal static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Packages.props"))) dir = dir.Parent;
        Assert.That(dir, Is.Not.Null);
        return Path.Combine([dir!.FullName, .. parts]);
    }

    private static readonly Lazy<ApiSpec> ShopSpec = new(() => OpenApiLoader.LoadSpec(RepoFile("samples", "shop.openapi.yaml")));

    private static GenerateOptions Options(bool spec = true, int seed = 12345, Backend backend = Backend.Mock) =>
        new("Shop.UiTests", backend, spec ? "../shop.openapi.yaml" : null, seed) { Spec = spec ? ShopSpec.Value : null };

    private static Dictionary<string, GeneratedFile> Files(GenerateOptions options) =>
        new PlaywrightGenerator().Generate(GenerationTests.Catalog(), options).ToDictionary(f => f.Path);

    [Test]
    public void ApiMocks_are_generated_deterministically_from_the_spec()
    {
        var first = Files(Options())["Support/ApiMocks.g.cs"].Content;
        Assert.That(Files(Options())["Support/ApiMocks.g.cs"].Content, Is.EqualTo(first));
        Assert.That(Files(Options(seed: 7))["Support/ApiMocks.g.cs"].Content, Is.Not.EqualTo(first));

        // En rad per operation, status ur första 2xx, regex för sökvägen, parametrar utan snedstreck.
        Assert.That(first, Does.Contain("new(\"GET\", \"/orders/{id}\", \"^/orders/[^/]+$\", 200, "));
        Assert.That(first, Does.Contain("new(\"POST\", \"/products\", \"^/products$\", 201, "));
        Assert.That(first, Does.Contain("new(\"POST\", \"/customers\", \"^/customers$\", 201, "));
        Assert.That(first, Does.Contain("new(\"POST\", \"/orders\", \"^/orders$\", 201, "));
        Assert.That(System.Text.RegularExpressions.Regex.Matches(first, @"^\s+new\(""", System.Text.RegularExpressions.RegexOptions.Multiline), Has.Count.EqualTo(4));
        // Kroppen kommer ur svarsschemat (Product har id, name, price och sku) som JSON-literal.
        Assert.That(first, Does.Contain("\\\"id\\\":").And.Contain("\\\"price\\\":").And.Contain("\\\"sku\\\":"));
        // Mallar utan parametrar kommer före mallar med.
        Assert.That(first.IndexOf("\"/orders\"", StringComparison.Ordinal), Is.LessThan(first.IndexOf("\"/orders/{id}\"", StringComparison.Ordinal)));
        // Den genererade koden beror inte på testgen.
        Assert.That(first, Does.Not.Contain("TestGen"));
    }

    [Test]
    public void Operations_without_a_response_schema_get_status_200_and_an_empty_body()
    {
        var spec = new ApiSpec(
        [
            new OperationInfo("ping", "GET", "/ping", [], null, [200]),
            new OperationInfo("clear", "DELETE", "/items/{id}", [], null, [204], ResponseSchemas: new Dictionary<int, Microsoft.OpenApi.Models.OpenApiSchema>()),
        ], []);
        var source = MockWriter.ApiMocks("X", spec, 1);
        Assert.That(source, Does.Contain("new(\"GET\", \"/ping\", \"^/ping$\", 200, \"\")"));
        Assert.That(source, Does.Contain("new(\"DELETE\", \"/items/{id}\", \"^/items/[^/]+$\", 204, \"\")"));
    }

    [TestCase("/orders/{id}", "^/orders/[^/]+$")]
    [TestCase("/a.b/{x}/c/{y}", @"^/a\.b/[^/]+/c/[^/]+$")]
    [TestCase("/", "^/$")]
    [TestCase("/orders/{id?}", "^/orders(?:/[^/]+)?$")]
    [TestCase("/files/{*path}", "^/files(?:/.*)?$")]
    [TestCase("/files/{name}.json", @"^/files/[^/]+\.json$")]
    public void Path_templates_become_regexes(string template, string expected) =>
        Assert.That(MockWriter.PathPattern(template), Is.EqualTo(expected));

    /// <summary>Kompilerar ApiMocks.g.cs i minnet och anropar TryMatch, så att den genererade koden prövas på riktigt.</summary>
    private static (int Status, string Body)? Match(string method, string path)
    {
        var source = Files(Options())["Support/ApiMocks.g.cs"].Content;
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create("ApiMocksTest" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source), CSharpSyntaxTree.ParseText("global using System;\nglobal using System.Collections.Generic;")], references, // som ImplicitUsings i projektet
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.That(result.Success, Is.True, string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        var type = Assembly.Load(stream.ToArray()).GetType("Shop.UiTests.Support.ApiMocks")!;
        var args = new object?[] { method, path, null };
        if (!(bool)type.GetMethod("TryMatch")!.Invoke(null, args)!) return null;
        var mock = args[2]!;
        return ((int)mock.GetType().GetProperty("Status")!.GetValue(mock)!, (string)mock.GetType().GetProperty("Body")!.GetValue(mock)!);
    }

    [TestCase("http://localhost:5090/api/orders/12?x=1", true)]
    [TestCase("http://localhost:5090/products", true)]
    [TestCase("https://api.example.test/v2/customers/", true)]
    [TestCase("http://localhost:5090/site.css", false)]
    [TestCase("http://localhost:5090/", false)]
    [TestCase("http://localhost:5090/api/orders/12/extra", false)]
    public void Url_pattern_routes_only_api_addresses(string url, bool routed) =>
        Assert.That(Regex.IsMatch(url, MockWriter.UrlPattern(MockWriter.Rows(ShopSpec.Value, 12345))), Is.EqualTo(routed));

    [TestCase("/", "http://localhost/", true)]
    [TestCase("/", "http://localhost/x", false)]
    [TestCase("/files/{*path}", "http://h/files/a/b.txt", true)]
    [TestCase("/orders/{id?}", "http://h/api/orders", true)]
    public void Url_pattern_follows_the_route_template(string template, string url, bool routed) =>
        Assert.That(Regex.IsMatch(url, MockWriter.UrlPattern([new MockWriter.Row("GET", template, "", 200, "")])), Is.EqualTo(routed));

    [TestCase("GET", "/orders/5")]
    [TestCase("GET", "/api/orders/5")]
    [TestCase("get", "/api/v2/orders/5/")]
    [TestCase("GET", "/api/orders/5?expand=lines")]
    public void TryMatch_matches_on_the_suffix_of_the_path(string method, string path)
    {
        var match = Match(method, path);
        Assert.That(match, Is.Not.Null);
        Assert.That(match!.Value.Status, Is.EqualTo(200));
        Assert.That(match.Value.Body, Does.StartWith("{").And.Contain("\"quantity\""));
    }

    [TestCase("POST", "/api/orders", 201)]
    [TestCase("POST", "/products", 201)]
    public void TryMatch_separates_methods_and_static_paths(string method, string path, int status) =>
        Assert.That(Match(method, path)?.Status, Is.EqualTo(status));

    [TestCase("GET", "/orders")]            // metoden finns inte för sökvägen
    [TestCase("GET", "/orders/5/lines")]    // slutet stämmer inte
    [TestCase("GET", "/api/orders-old/5")]
    [TestCase("DELETE", "/orders/5")]
    [TestCase("GET", "/")]
    [TestCase("GET", "")]
    public void TryMatch_rejects_unknown_calls(string method, string path) => Assert.That(Match(method, path), Is.Null);

    [Test]
    public void Stub_and_route_code_exist_only_with_a_spec()
    {
        var with = Files(Options());
        var without = Files(Options(spec: false));

        Assert.That(with.Keys, Does.Contain("Support/ApiMocks.g.cs").And.Contain("Support/ApiStub.g.cs"));
        Assert.That(without.Keys, Does.Not.Contain("Support/ApiMocks.g.cs").And.Not.Contain("Support/ApiStub.g.cs"));

        var baseWith = with["Support/UiTestBase.g.cs"].Content;
        Assert.That(baseWith, Does.Contain("page.RouteAsync(ApiPaths,").And.Contain("ApiMocks.TryMatch(").And.Contain("route.FulfillAsync(")
            .And.Contain("route.ContinueAsync()").And.Contain("is \"fetch\" or \"xhr\""));
        Assert.That(baseWith, Does.Not.Contain("\"**/*\""), "Bara API-sökvägarna ska gå via .NET, inte alla anrop.");
        Assert.That(baseWith, Does.Contain("private static readonly Regex ApiPaths = new(ApiMocks.UrlPattern);").And.Contain("using System.Text.RegularExpressions;"));
        var baseWithout = without["Support/UiTestBase.g.cs"].Content;
        Assert.That(baseWithout, Does.Not.Contain("RouteAsync").And.Not.Contain("ApiMocks"));
        Assert.That(baseWithout, Does.Contain("protected virtual Task ConfigureMocksAsync(IPage page) => Task.CompletedTask;"));

        var stub = with["Support/ApiStub.g.cs"].Content;
        Assert.That(stub, Does.Contain("[SetUpFixture]").And.Contain("UITEST_STUB_PORT").And.Contain("UITEST_STUB").And.Contain("5099")
            .And.Contain("UiTestBase.IsMockBackend").And.Contain("ApiMocks.TryMatch(").And.Contain("Status404NotFound").And.Contain("ConfigureKestrel"));
        Assert.That(stub, Does.Contain("namespace Shop.UiTests;"), "SetUpFixture ska ligga i rotnamnrymden för att gälla alla tester.");

        Assert.That(with["Shop.UiTests.csproj"].Content, Does.Contain("<FrameworkReference Include=\"Microsoft.AspNetCore.App\" />"));
        Assert.That(without["Shop.UiTests.csproj"].Content, Does.Not.Contain("FrameworkReference"));
        Assert.That(with["README.md"].Content, Does.Contain("RouteAsync").And.Contain("ShopApi__BaseUrl=http://localhost:5099").And.Contain("UITEST_STUB_PORT"));
        Assert.That(without["README.md"].Content, Does.Not.Contain("ShopApi__BaseUrl"));
    }

    [Test]
    public void Valid_form_tests_also_check_that_the_app_shows_no_error()
    {
        var tests = new PlaywrightGenerator().Generate(GenerationTests.Catalog(), Options()).Single(f => f.Path == "Tests/ProductsIdEditTests.g.cs").Content;
        Assert.That(tests, Does.Contain("await ExpectNoAlertsAsync();"));
        Assert.That(Files(Options())["Support/UiTestBase.g.cs"].Content, Does.Contain("protected Task ExpectNoAlertsAsync()").And.Contain("[role=alert]"));
    }

    [Test, Category("Slow")]
    public void Generated_project_with_spec_builds()
    {
        var dir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "uitestgen-spec-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            GeneratedFiles.Write(new PlaywrightGenerator().Generate(GenerationTests.Catalog(), Options()), dir);
            var psi = new ProcessStartInfo("dotnet", ["build", "--nologo", "-v", "q", "-warnaserror"])
            {
                WorkingDirectory = dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            psi.Environment["DOTNET_NOLOGO"] = "1";
            using var process = Process.Start(psi)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromMinutes(6)))
            {
                process.Kill(true);
                Assert.Fail("dotnet build tog för lång tid.");
            }
            Assert.That(process.ExitCode, Is.EqualTo(0), output.Result + error.Result);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { /* en kvarglömd byggprocess; mappen städas senare */ } }
    }
}
