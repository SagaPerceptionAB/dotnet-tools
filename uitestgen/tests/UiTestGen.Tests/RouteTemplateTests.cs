using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using UiTestGen.Generation;
using UiTestGen.Model;
using UiTestGen.Probe;

namespace UiTestGen.Tests;

/// <summary>Den gemensamma ruttmallen och det som bygger på den: namn, matchning, regex, exempeladresser och länkkoppling.</summary>
public class RouteTemplateTests
{
    // --- Tolkning ---

    [Test]
    public void Parses_aspnet_spa_and_mixed_segments()
    {
        var template = RouteTemplate.Parse("/a/{id:int}/{slug?}/:name/:opt?/{*rest}/v{major}-{minor:int}");
        Assert.That(template.Parameters, Is.EqualTo(new[]
        {
            new RouteParameter("id", "int", false), new RouteParameter("slug", null, true), new RouteParameter("name", null, false),
            new RouteParameter("opt", null, true), new RouteParameter("rest", null, false), new RouteParameter("major", null, false),
            new RouteParameter("minor", "int", false),
        }));
        Assert.That(template.Segments.Select(s => (s.IsLiteral, s.CanBeMissing, s.IsCatchAll)), Is.EqualTo(new[]
        {
            (true, false, false), (false, false, false), (false, true, false), (false, false, false), (false, true, false), (false, true, true), (false, false, false),
        }));
    }

    [Test]
    public void Constraints_with_slashes_and_braces_stay_in_one_segment()
    {
        var template = RouteTemplate.Parse(@"/x/{code:regex(^\d{3}/a$)}/end");
        Assert.That(template.Segments, Has.Count.EqualTo(3));
        Assert.That(template.Parameters.Single(), Is.EqualTo(new RouteParameter("code", "regex", false)));
    }

    // --- Matchning ---

    [TestCase("/products/42", true)]
    [TestCase("/products/create", false)]
    [TestCase("/products/{}", true)]
    [TestCase("/products", false)]
    [TestCase("/products/42/edit", false)]
    public void Match_checks_constraints(string path, bool matches) =>
        Assert.That(RouteTemplate.Parse("/products/{id:int}").Match(path) is not null, Is.EqualTo(matches));

    [TestCase("/users/7")]
    [TestCase("/users/anna")]
    [TestCase("/users/{}")]
    public void Match_handles_spa_parameters(string path) =>
        Assert.That(RouteTemplate.Parse("/users/:id").Match(path), Is.EqualTo(new RouteMatch(1, 1)));

    [Test]
    public void Match_handles_optional_and_catch_all_segments()
    {
        var optional = RouteTemplate.Parse("/orders/{id?}");
        Assert.That(optional.Match("/orders"), Is.Not.Null);
        Assert.That(optional.Match("/orders/5"), Is.Not.Null);
        Assert.That(optional.Match("/orders/5/6"), Is.Null);

        var spaOptional = RouteTemplate.Parse("/orders/:id?");
        Assert.That(spaOptional.Match("/orders"), Is.Not.Null);

        var rest = RouteTemplate.Parse("/files/{*path}");
        Assert.That(rest.Match("/files"), Is.Not.Null);
        Assert.That(rest.Match("/files/a/b/c"), Is.EqualTo(new RouteMatch(1, 1)));
        Assert.That(rest.Match("/other/a"), Is.Null);
    }

    [Test]
    public void Match_handles_segments_that_mix_text_and_parameters()
    {
        var template = RouteTemplate.Parse("/files/{name}.json");
        Assert.That(template.Match("/files/report.json"), Is.EqualTo(new RouteMatch(1, 1)));
        Assert.That(template.Match("/files/report.xml"), Is.Null);
        Assert.That(template.Match("/files/{}.json"), Is.Not.Null);
        Assert.That(RouteTemplate.Parse("/v/{n:int}-x").Match("/v/12-x"), Is.Not.Null);
        Assert.That(RouteTemplate.Parse("/v/{n:int}-x").Match("/v/ab-x"), Is.Null);
    }

    [Test]
    public void Fixed_text_is_compared_without_case()
    {
        Assert.That(RouteTemplate.Parse("/Products/Create").Match("/products/create"), Is.EqualTo(new RouteMatch(2, 0)));
        Assert.That(RouteTemplate.Parse("/").Match("/"), Is.EqualTo(new RouteMatch(0, 0)));
    }

    // --- Regex ---

    [TestCase("/", "^$")]
    [TestCase("/products/{id}", "^/products/[^/]+$")]
    [TestCase("/products/{id?}", "^/products(?:/[^/]+)?$")]
    [TestCase("/users/:id?", "^/users(?:/[^/]+)?$")]
    [TestCase("/files/{*rest}", "^/files(?:/.*)?$")]
    [TestCase("/a.b/{name}.json", @"^/a\.b/[^/]+\.json$")]
    public void ToRegex_wraps_the_segments_in_prefix_and_suffix(string route, string expected) =>
        Assert.That(RouteTemplate.Parse(route).ToRegex("^", "$"), Is.EqualTo(expected));

    // --- Exempeladresser ---

    [TestCase("/", "/")]
    [TestCase("/products/{id:int}", "/products/1")]
    [TestCase("/p/{id:int}/{g:guid}/{s}/{o?}", "/p/1/00000000-0000-0000-0000-000000000001/exempel/exempel")]
    [TestCase("/users/:id?", "/users/exempel")]
    [TestCase("/files/{*rest}", "/files/exempel")]
    [TestCase("/v/{major:int}-{minor:long}.{ext}", "/v/1-1.exempel")]
    [TestCase("/flag/{on:bool}", "/flag/true")]
    public void Example_path_has_values_for_all_parameters(string route, string expected) =>
        Assert.That(RouteExamples.Path(route), Is.EqualTo(expected));

    [Test]
    public void Example_path_is_a_url_the_route_matches()
    {
        foreach (var route in new[] { "/p/{id:int}/{g:guid}/{s}/{o?}", "/users/:id?", "/files/{*rest}", "/v/{major:int}-{minor:long}.{ext}", "/flag/{on:bool}" })
            Assert.That(RouteTemplate.Parse(route).Match(RouteExamples.Path(route)), Is.Not.Null, route);
    }

    /// <summary>
    /// Page Objectens <c>Path(...)</c> med testernas värden ger samma adress som sonderingens: kompilerar den genererade Page Objecten
    /// och anropar <c>Path</c> med exakt de uttryck som TestsWriter skriver.
    /// </summary>
    [TestCase("/p/{id:int}/{g:guid}/{s}/{o?}")]
    [TestCase("/users/:id?")]
    [TestCase("/files/{*rest}")]
    [TestCase("/v/{major:int}-{minor:long}.{ext}")]
    [TestCase("/flag/{on:bool}/{n:int?}")]
    [TestCase("/products/{id:int}")]
    [TestCase("/")]
    public void Page_object_path_with_example_values_equals_the_probe_path(string route)
    {
        var view = new View("V", route, ViewKind.Blazor, "V.razor", null, null, Names.Parameters(route), [], [], [], false);
        var page = PageObjectWriter.Write(view, "T");
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(System.IO.Path.PathSeparator)
            .Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create("PathTest" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(page), CSharpSyntaxTree.ParseText("global using System;\nglobal using System.Collections.Generic;\nglobal using System.Threading.Tasks;")], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.That(result.Success, Is.True, string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        var path = Assembly.Load(stream.ToArray()).GetType("T.Pages.VPage")!.GetMethod("Path")!;

        // Värdena som testerna skickar: RouteExamples.Value som C#-värden.
        var args = view.Parameters.Select(p => RouteTemplate.KindOf(p) switch
        {
            ParameterKind.Int => (object)1,
            ParameterKind.Long => 1L,
            ParameterKind.Bool => true,
            ParameterKind.Guid => new Guid(RouteExamples.ExampleGuid),
            _ => "exempel",
        }).ToArray();
        Assert.That(path.Invoke(null, args), Is.EqualTo(RouteExamples.Path(route)));
    }

    // --- Normalisering i sonderingen ---

    [Test]
    public void Probe_matcher_applies_constraints_and_normalizes()
    {
        var matcher = new RouteMatcher(["/products/{id:int}", "/products/create", "/users/:id", "/files/{*rest}", "/orders/{id?}", "/v/{n:int}-x"]);
        Assert.That(matcher.Match("/products/42"), Is.EqualTo("/products/{id:int}"));
        Assert.That(matcher.Match("/products/create"), Is.EqualTo("/products/create"));
        Assert.That(matcher.Match("/products/other"), Is.Null, "other är inget int");
        Assert.That(matcher.Normalize("/products/other"), Is.EqualTo("/products/other"));
        Assert.That(matcher.Normalize("/users/anna"), Is.EqualTo("/users/{}"));
        Assert.That(matcher.Normalize("/files/a/b"), Is.EqualTo("/files/{}"));
        Assert.That(matcher.Match("/orders"), Is.EqualTo("/orders/{id?}"));
        Assert.That(matcher.Normalize("/v/7-x"), Is.EqualTo("/v/{}-x"));
    }

    // --- Länkkoppling ---

    private static View V(string id, string route) =>
        new(id, route, ViewKind.Spa, id + ".tsx", null, null, Names.Parameters(route), [], [], [], false);

    [Test]
    public void Links_resolve_to_spa_routes_and_respect_constraints()
    {
        var catalog = new ViewCatalog([V("Create", "/products/create"), V("Detail", "/products/:id"), V("Number", "/orders/{id:int}"), V("Opt", "/archive/{year?}")], []);
        Assert.That(LinkTargets.Resolve(catalog, "/products/42")?.Id, Is.EqualTo("Detail"));
        Assert.That(LinkTargets.Resolve(catalog, "/products/{}")?.Id, Is.EqualTo("Detail"));
        Assert.That(LinkTargets.Resolve(catalog, "/products/create")?.Id, Is.EqualTo("Create"), "den fasta vinner över parametern");
        Assert.That(LinkTargets.Resolve(catalog, "/orders/7")?.Id, Is.EqualTo("Number"));
        Assert.That(LinkTargets.Resolve(catalog, "/orders/create"), Is.Null);
        Assert.That(LinkTargets.Resolve(catalog, "/orders/{}")?.Id, Is.EqualTo("Number"));
        Assert.That(LinkTargets.Resolve(catalog, "/archive")?.Id, Is.EqualTo("Opt"));
        Assert.That(LinkTargets.Resolve(catalog, "/archive/2024/")?.Id, Is.EqualTo("Opt"));
        Assert.That(LinkTargets.Resolve(catalog, "https://example.com/products/1"), Is.Null);
    }

    [Test]
    public void A_probed_link_is_normalized_and_resolved_with_the_same_rules()
    {
        // /orders/42 sonderas som /orders/{} och ska hamna på samma vy som länken /orders/42 gör.
        var catalog = new ViewCatalog([V("Number", "/orders/{id:int}"), V("Detail", "/products/:id")], []);
        var matcher = new RouteMatcher(catalog.Views.Select(v => v.Route));
        foreach (var path in new[] { "/orders/42", "/products/abc", "/orders/new" })
        {
            var normalized = matcher.Normalize(path);
            Assert.That(LinkTargets.Resolve(catalog, normalized)?.Id, Is.EqualTo(LinkTargets.Resolve(catalog, path)?.Id), path);
        }
        Assert.That(matcher.Match("/orders/new"), Is.Null);
    }

    [Test]
    public void Resolver_is_built_once_per_catalog_and_remembers_answers()
    {
        var catalog = new ViewCatalog([V("Detail", "/products/:id")], []);
        var first = LinkTargets.For(catalog);
        Assert.That(LinkTargets.For(catalog), Is.SameAs(first));
        var view = LinkTargets.Resolve(catalog, "/products/1");
        Assert.That(LinkTargets.Resolve(catalog, "/products/1"), Is.SameAs(view));
    }

    [Test]
    public void Orphans_are_computed_once_per_catalog()
    {
        var catalog = GenerationNavigationTests.OrphanShop();
        var first = CatalogInsights.Orphans(catalog);
        Assert.That(CatalogInsights.Orphans(catalog), Is.SameAs(first));
    }

    // --- Namn ---

    [Test]
    public void Unique_names_get_a_number_after_the_separator()
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        Assert.That(new[] { Names.Unique(used, "Name"), Names.Unique(used, "Name"), Names.Unique(used, "Name") }, Is.EqualTo(new[] { "Name", "Name2", "Name3" }));
        Assert.That(new[] { Names.Unique(used, "Test", "_"), Names.Unique(used, "Test", "_") }, Is.EqualTo(new[] { "Test", "Test_2" }));
    }

    [TestCase("product-list", "productList")]
    [TestCase("Name", "name")]
    [TestCase("åska", "aska")]
    [TestCase("---", "")]
    public void Camel_case(string text, string expected) => Assert.That(Names.Camel(text), Is.EqualTo(expected));
}
