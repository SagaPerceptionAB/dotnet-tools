using System.Net;
using System.Text;
using TestGen.Core;
using TestGen.Core.Run;

namespace TestGen.Tests;

/// <summary>Specar i 3.0 och 3.1, som YAML och JSON, från fil och webbadress. Och felet när appen har Microsoft.OpenApi 1.x.</summary>
public class SpecFormatTests
{
    private string _dir = "";

    [SetUp] public void Up() => _dir = Directory.CreateTempSubdirectory("testgen-specformat-").FullName;
    [TearDown] public void Down() => Directory.Delete(_dir, true);

    private const string Yaml30 = """
        openapi: 3.0.1
        info: { title: Demo, version: '1' }
        paths:
          /items/{id}:
            get:
              operationId: getItem
              parameters: [ { name: id, in: path, required: true, schema: { type: integer, minimum: 1, exclusiveMinimum: true } } ]
              responses: { '200': { description: ok, content: { application/json: { schema: { type: object, properties: { name: { type: string, nullable: true } } } } } } }
        """;

    private const string Yaml31 = """
        openapi: 3.1.0
        info: { title: Demo, version: '1' }
        paths:
          /items/{id}:
            get:
              operationId: getItem
              parameters: [ { name: id, in: path, required: true, schema: { type: [integer, string], exclusiveMinimum: 1 } } ]
              responses: { '200': { description: ok, content: { application/json: { schema: { type: object, properties: { name: { type: [string, 'null'] } } } } } } }
        """;

    private static string ToJson(string yaml) =>
        new YamlDotNet.Serialization.SerializerBuilder().JsonCompatible().Build().Serialize(new YamlDotNet.Serialization.DeserializerBuilder().Build().Deserialize(new StringReader(yaml))!);

    private static IEnumerable<TestCaseData> Variants()
    {
        foreach (var (version, yaml) in new[] { ("3.0", Yaml30), ("3.1", Yaml31) })
        {
            yield return new TestCaseData(yaml, ".yaml").SetName($"{version} som YAML");
            yield return new TestCaseData(ToJson(yaml), ".json").SetName($"{version} som JSON");
        }
    }

    [TestCaseSource(nameof(Variants))]
    public void A_spec_is_read_from_a_file(string text, string extension)
    {
        var path = Path.Combine(_dir, "spec" + extension);
        File.WriteAllText(path, text);

        AssertItem(OpenApiLoader.LoadSpec(path));
    }

    [TestCaseSource(nameof(Variants))]
    public async Task A_spec_is_read_from_a_url(string text, string extension)
    {
        var port = Ports.Free();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        var serve = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            var bytes = Encoding.UTF8.GetBytes(text);
            context.Response.ContentType = extension == ".json" ? "application/json" : "application/yaml";
            context.Response.OutputStream.Write(bytes);
            context.Response.Close();
        });

        var spec = await Task.Run(() => OpenApiLoader.LoadSpec($"http://localhost:{port}/spec{extension}"));
        await serve;

        AssertItem(spec);
    }

    private static void AssertItem(ApiSpec spec)
    {
        var op = spec.Operations.Single();
        Assert.That((op.OperationId, op.Method, op.Path), Is.EqualTo(("getItem", "GET", "/items/{id}")));
        var id = op.Parameters.Single();
        Assert.That(id.Schema.TypeName(), Is.EqualTo("integer"));
        Assert.That(id.Schema.Lower(), Is.EqualTo((1m, true)));
        Assert.That(SchemaUtil.IsNullable(op.ResponseSchemas![200].Props()["name"]), Is.True);
    }

    // --- Appen har Microsoft.OpenApi 1.x ----------------------------------------------------

    private static IEnumerable<TestCaseData> Conflicts()
    {
        const string type = "Microsoft.OpenApi.Models.OpenApiDocument";
        yield return new TestCaseData(new TypeLoadException($"Could not load type '{type}' from assembly 'Microsoft.OpenApi, Version=1.6.0.0'.")).SetName("TypeLoadException");
        yield return new TestCaseData(new MissingMethodException($"Method not found: 'Void {type}.SerializeAsV3()'.")).SetName("MissingMethodException");
        yield return new TestCaseData(new FileLoadException("Could not load file or assembly", "Microsoft.OpenApi, Version=1.6.0.0")).SetName("FileLoadException");
        yield return new TestCaseData(new InvalidOperationException("Appen kunde inte startas",
            new System.Reflection.TargetInvocationException(new TypeInitializationException("X", new TypeLoadException($"Could not load type '{type}'."))))).SetName("djupt i felkedjan");
        yield return new TestCaseData(new AggregateException(new TypeLoadException($"Could not load type '{type}'."))).SetName("i AggregateException");
    }

    [TestCaseSource(nameof(Conflicts))]
    public void A_Microsoft_OpenApi_version_conflict_gives_what_happened_and_what_to_do(Exception e)
    {
        var t = FriendlyErrors.Translate(e);

        Assert.That(FriendlyErrors.IsOpenApiVersionConflict(e), Is.True);
        Assert.That(t.What, Does.Contain("olika huvudversioner av Microsoft.OpenApi"));
        Assert.That(t.Action, Does.Contain("AddOpenApi()").And.Contain("Swashbuckle 10"));
        Assert.That(t.Technical, Does.Contain("Microsoft.OpenApi"));
    }

    [Test]
    public void Other_load_errors_are_not_blamed_on_Microsoft_OpenApi()
    {
        var e = new TypeLoadException("Could not load type 'Other.Thing' from assembly 'Other, Version=1.0.0.0'.");

        Assert.That(FriendlyErrors.IsOpenApiVersionConflict(e), Is.False);
        Assert.That(FriendlyErrors.Translate(e).What, Does.Not.Contain("Microsoft.OpenApi"));
    }
}
