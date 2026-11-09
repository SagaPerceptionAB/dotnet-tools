using TestGen.Core.Views;

namespace TestGen.Views.Tests;

/// <summary>Ruttmallen: tolkning, matchning, regex och exempeladresser.</summary>
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

    [Test]
    public void Unique_names_get_a_number_after_the_separator()
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        Assert.That(new[] { Names.Unique(used, "Name"), Names.Unique(used, "Name"), Names.Unique(used, "Name") }, Is.EqualTo(new[] { "Name", "Name2", "Name3" }));
        Assert.That(new[] { Names.Unique(used, "Test", "_"), Names.Unique(used, "Test", "_") }, Is.EqualTo(new[] { "Test", "Test_2" }));
    }
}
