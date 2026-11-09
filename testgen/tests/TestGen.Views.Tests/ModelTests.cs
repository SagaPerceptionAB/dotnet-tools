using TestGen.Core.Views;
using TestGen.Views.Discovery;

namespace TestGen.Views.Tests;

public class ModelTests
{
    [Test]
    public void Parameters_from_aspnet_and_spa_routes()
    {
        Assert.That(Names.Parameters("/products/{id:int}/{slug?}/{*rest}"), Is.EqualTo(new[]
        {
            new RouteParameter("id", "int", false), new RouteParameter("slug", null, true), new RouteParameter("rest", null, false),
        }));
        Assert.That(Names.Parameters("/users/:userId/posts/:postId?"), Is.EqualTo(new[]
        {
            new RouteParameter("userId", null, false), new RouteParameter("postId", null, true),
        }));
    }

    [Test]
    public void Ids_are_ui_and_the_route_and_unique()
    {
        var a = new View(ViewId.Of("/products"), "/products", ViewKind.Blazor, "B.razor", "Produkter", "Produkter", [], [], [new Link("/b", null), new Link("/a", "A")], [], false);
        var b = a with { Source = "A.razor" };
        var catalog = ViewDiscovery.Run(new DiscoveryContext("/", [], ["varning"]), [new Fixed([a, b])]);

        Assert.That(catalog.Views.Select(v => (v.Id, v.Source)), Is.EqualTo(new[] { ("ui:/products", "A.razor"), ("ui:/products#2", "B.razor") }));
        Assert.That(catalog.Views[0].Links.Select(l => l.Href), Is.EqualTo(new[] { "/a", "/b" }));
        Assert.That(catalog.Warnings, Is.EqualTo(new[] { "varning" }));
    }

    [Test]
    public void Json_is_camel_case_with_schema_version_first_enums_in_kebab_case_and_no_nulls()
    {
        var field = new Field("DueDate", null, FieldType.DateTime, new Locator(LocatorKind.Label, "Förfallodag"), [new Rule(RuleKind.MinLength, "3")], []);
        var form = new Form("Task", "TaskInput", new Locator(LocatorKind.Role, "button", "Spara"), [field], new ApiCall("POST", "/tasks"), "createTask");
        var view = new View("ui:/tasks/new", "/tasks/new", ViewKind.RazorPage, "Pages/New.cshtml", null, null, [], [form], [], [], false);
        var json = ViewsJson.Serialize(ViewsFile.Of([new AppViews("TaskWeb", [view], [new ViewsApi("TaskApi", "TaskApi:BaseUrl")])]));

        Assert.That(json, Does.StartWith("{\n  \"schemaVersion\": 1,"));
        Assert.That(json, Does.Contain("\"kind\": \"razor-page\"").And.Contain("\"type\": \"date-time\"").And.Contain("\"kind\": \"min-length\"")
            .And.Contain("\"operationId\": \"createTask\"").And.Contain("\"configKey\": \"TaskApi:BaseUrl\"").And.Contain("Förfallodag"));
        Assert.That(json, Does.Not.Contain("null").And.Not.Contain("\"title\"").And.Not.Contain("specField"));
        Assert.That(json, Does.EndWith("}\n").And.Not.Contain("\r"));
    }

    [Test]
    public void Json_round_trips_and_a_wrong_schema_version_or_broken_file_reads_as_null()
    {
        var dir = Directory.CreateTempSubdirectory("views-json").FullName;
        try
        {
            var file = ViewsFile.Of([new AppViews("W", [new View("ui:/", "/", ViewKind.Mvc, "V.cshtml", "T", null, [], [], [new Link("/", "Hem")], [], true)], [])]);
            var path = Path.Combine(dir, "views.json");
            File.WriteAllText(path, ViewsJson.Serialize(file));
            Assert.That(ViewsJson.Serialize(ViewsJson.TryRead(path)!), Is.EqualTo(ViewsJson.Serialize(file)));

            File.WriteAllText(path, ViewsJson.Serialize(file).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2"));
            Assert.That(ViewsJson.TryRead(path), Is.Null);
            File.WriteAllText(path, "{ trasig");
            Assert.That(ViewsJson.TryRead(path), Is.Null);
            Assert.That(ViewsJson.TryRead(Path.Combine(dir, "saknas.json")), Is.Null);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private sealed class Fixed(IEnumerable<View> views) : IViewDiscoverer
    {
        public IEnumerable<View> Discover(DiscoveryContext context) => views;
    }
}
