using UiTestGen.Discovery;
using UiTestGen.Model;

namespace UiTestGen.Tests;

public class ModelTests
{
    [TestCase("/", "Home")]
    [TestCase("/products", "Products")]
    [TestCase("/products/{id:int}/edit", "ProductsIdEdit")]
    [TestCase("/order-history", "OrderHistory")]
    [TestCase("/users/:userId", "UsersUserId")]
    [TestCase("/404", "View404")]
    [TestCase("/kundvagn/översikt", "KundvagnOversikt")]
    public void Id_from_route(string route, string id) => Assert.That(Names.IdFromRoute(route), Is.EqualTo(id));

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
    public void Catalog_round_trips_through_json_and_ids_are_unique()
    {
        var a = new View("Products", "/products", ViewKind.Blazor, "B.razor", "Produkter", "Produkter", [], [], [new Link("/b", null), new Link("/a", "A")], [], false);
        var b = a with { Source = "A.razor" };
        var ctx = new DiscoveryContext("/", [], ["varning"]);
        var catalog = ViewDiscovery.Run(ctx, [new Fixed([a, b])]);

        Assert.That(catalog.Views.Select(v => (v.Id, v.Source)), Is.EqualTo(new[] { ("Products", "A.razor"), ("Products2", "B.razor") }));
        Assert.That(catalog.Views[0].Links.Select(l => l.Href), Is.EqualTo(new[] { "/a", "/b" }));

        var json = ViewCatalogJson.Serialize(catalog);
        Assert.That(json, Does.Contain("\"kind\": \"Blazor\"").And.Contain("Produkter"));
        Assert.That(ViewCatalogJson.Serialize(ViewCatalogJson.Deserialize(json)), Is.EqualTo(json));
    }

    [Test]
    public void Known_views_keep_their_id_before_probed_ones()
    {
        // "/a-b" och "/a/b" ger båda id:t AB. Den sonderade sorteras först på rutt men får ändå suffixet.
        var probed = new View("AB", "/a-b", ViewKind.Probed, "(sondering)", null, null, [], [], [], [], false);
        var known = probed with { Route = "/a/b", Kind = ViewKind.Blazor, Source = "AB.razor" };
        var catalog = ViewDiscovery.Run(new DiscoveryContext("/", [], []), [new Fixed([known, probed])]);
        Assert.That(catalog.Views.Select(v => (v.Route, v.Id)), Is.EqualTo(new[] { ("/a-b", "AB2"), ("/a/b", "AB") }));
    }

    private sealed class Fixed(IEnumerable<View> views) : IViewDiscoverer
    {
        public IEnumerable<View> Discover(DiscoveryContext context) => views;
    }
}
