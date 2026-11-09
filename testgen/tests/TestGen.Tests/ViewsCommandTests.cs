using System.Text.Json.Nodes;
using TestGen.Cli;
using TestGen.Core.Projects;
using TestGen.Core.Views;
using TestGen.Views;

namespace TestGen.Tests;

/// <summary>testgen views mot Shop-exemplet: filen, kontraktet (schemat) och utskriften. Läser solutionen med MSBuild.</summary>
public class ViewsCommandTests
{
    private string _dir = "";

    [SetUp] public void Up() => _dir = Directory.CreateTempSubdirectory("testgen-views-").FullName;
    [TearDown] public void Down() => Directory.Delete(_dir, true);

    private static (int Exit, string Out, string Err) Cli(params string[] args)
    {
        var (o, e) = (Console.Out, Console.Error);
        var (so, se) = (new StringWriter { NewLine = "\n" }, new StringWriter { NewLine = "\n" });
        Console.SetOut(so); Console.SetError(se);
        try { return (Program.Main(args), so.ToString(), se.ToString()); }
        finally { Console.SetOut(o); Console.SetError(e); }
    }

    private static string SchemaPath()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var f = Path.Combine(d.FullName, "testgen", "docs", "views.schema.json");
            if (File.Exists(f)) return f;
        }
        throw new FileNotFoundException("testgen/docs/views.schema.json");
    }

    // En körning delas av testerna: MSBuild-inläsningen tar några sekunder.
    private static readonly Lazy<(int Exit, string Out, string Err, string Json, string Path)> Shop = new(() =>
    {
        // Under testets utdatakatalog, inte i temp: källsökvägarna i views.json är relativa filen, och på Windows i CI ligger
        // temp på en annan enhet (C:) än repot (D:), så att ingen relativ sökväg finns.
        var dir = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "views-shop-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        var path = Path.Combine(dir, "tests", "Shop.Tests", "views.json");
        var (exit, o, e) = Cli("views", "--solution", ShopSolution.Path, "--out", path, "--no-restore");
        return (exit, o, e, File.Exists(path) ? File.ReadAllText(path) : "", path);
    });

    [Test]
    public void Shop_gives_the_six_views_the_api_key_and_the_form_to_createProduct()
    {
        var (exit, o, e, json, _) = Shop.Value;
        Assert.That(exit, Is.EqualTo(0), e);
        var file = ViewsJson.TryRead(WriteTemp(json))!;
        var app = file.Apps.Single();
        Assert.That(app.Project, Is.EqualTo("ShopWeb"));
        Assert.That(app.Views.Select(v => v.Id), Is.EqualTo(new[]
        {
            "ui:/", "ui:/about", "ui:/customers/create", "ui:/products", "ui:/products/create", "ui:/products/{id:int}",
        }));
        Assert.That(app.Views.Count(v => v.Kind == ViewKind.Blazor), Is.EqualTo(5));
        Assert.That(app.Apis, Is.EqualTo(new[] { new ViewsApi("ShopApi", "ShopApi:BaseUrl") }));

        var create = app.Views.Single(v => v.Id == "ui:/products/create").Forms.Single();
        Assert.That((create.OperationId, create.SubmitsTo), Is.EqualTo(("createProduct", new ApiCall("POST", "/products"))));
        Assert.That(create.Fields.Select(f => (f.Name, f.SpecField)), Is.EqualTo(new[] { ("Name", "name"), ("Sku", "sku"), ("Price", "price") }));
        Assert.That(app.Views.Single(v => v.Id == "ui:/customers/create").Forms.Single().OperationId, Is.EqualTo("createCustomer"));
        Assert.That(app.Views.Single(v => v.Id == "ui:/about").Forms.Single().OperationId, Is.Null, "kontaktformuläret anropar inget API");

        Assert.That(o, Does.Contain("ShopWeb   anropar ShopApi via ShopApi:BaseUrl\n").And.Contain("ui:/products/create").And.Contain("createProduct"));
        Assert.That(e, Does.Contain("(vyer: 6, appar: 1)").And.Contain("Skrev "));
    }

    [Test]
    public void The_file_follows_the_schema_in_key_order_with_no_nulls_and_no_absolute_paths()
    {
        var json = Shop.Value.Json;
        var node = JsonNode.Parse(json)!;
        var schema = (JsonObject)JsonNode.Parse(File.ReadAllText(SchemaPath()))!;
        Assert.That(JsonSchemaCheck.Errors(node, schema), Is.Empty);
        Assert.That(node.AsObject().First().Key, Is.EqualTo("schemaVersion"));

        var viewKeys = node["apps"]![0]!["views"]![0]!.AsObject().Select(kv => kv.Key).ToList();
        var defs = schema["$defs"]!;
        var order = defs["view"]!["properties"]!.AsObject().Select(kv => kv.Key).ToList();
        Assert.That(viewKeys, Is.EqualTo(order.Where(viewKeys.Contains).ToList()), "nycklarna i schemats ordning");
        Assert.That(json, Does.Not.Contain("null").And.Not.Contain("\\\\").And.Not.Contain("\r"));
        var ids = node["apps"]![0]!["views"]!.AsArray().Select(v => v!["id"]!.GetValue<string>()).ToList();
        Assert.That(ids, Is.Ordered.Using<string>(StringComparer.Ordinal).And.Unique);
        var outDir = Path.GetDirectoryName(Shop.Value.Path)!;
        var sources = node["apps"]![0]!["views"]!.AsArray().Select(v => v!["source"]!.GetValue<string>()).ToList();
        Assert.That(sources, Has.All.Matches<string>(s => s is not null && !Path.IsPathRooted(s) && !s.Contains('\\') && s.Contains("/samples/ShopWeb/")), "bara relativa sökvägar");
        Assert.That(sources.Select(s => File.Exists(Path.GetFullPath(Path.Combine(outDir, s)))), Has.All.True, "relativt filen");
    }

    [Test]
    public void Two_runs_give_a_byte_identical_file()
    {
        var first = Shop.Value.Json;
        var path = Shop.Value.Path;
        var (exit, _, e) = Cli("views", "--solution", ShopSolution.Path, "--out", path, "--no-restore");
        Assert.That(exit, Is.EqualTo(0), e);
        Assert.That(File.ReadAllText(path), Is.EqualTo(first));
    }

    [Test]
    public void Status_reads_the_file_the_command_wrote()
    {
        var facts = RepositoryFacts.Read(ShopSolution.Path);
        var web = facts.WebApps.Single();
        var summary = ViewsSummary.Read(WriteTemp(Shop.Value.Json), web.Name)!;
        Assert.That((summary.ViewCount, summary.Kind), Is.EqualTo((6, "blazor, razor-page")));
        Assert.That(summary.Apis, Is.EqualTo(new[] { new ViewsApi("ShopApi", "ShopApi:BaseUrl") }));
    }

    [Test]
    public void A_solution_without_a_web_app_is_a_usage_error_with_what_to_do()
    {
        File.WriteAllText(Path.Combine(_dir, "r.slnx"), "<Solution><Project Path=\"L/L.csproj\" /></Solution>");
        Directory.CreateDirectory(Path.Combine(_dir, "L"));
        File.WriteAllText(Path.Combine(_dir, "L", "L.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var (exit, o, e) = Cli("views", "--solution", Path.Combine(_dir, "r.slnx"));
        Assert.That((exit, o), Is.EqualTo((2, "")));
        Assert.That(e, Does.Contain("Hittade ingen webbapp").And.Contain("--solution"));
    }

    [Test]
    public void A_bad_max_pages_is_a_usage_error()
    {
        var (exit, o, e) = Cli("views", "--probe", "--max-pages", "0", "--solution", ShopSolution.Path);
        Assert.That((exit, o), Is.EqualTo((2, "")));
        Assert.That(e, Does.Contain("--max-pages måste vara ett positivt heltal"));
    }

    private string WriteTemp(string json)
    {
        var path = Path.Combine(_dir, "read-views.json");
        File.WriteAllText(path, json);
        return path;
    }
}
