using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using TestGen.Core;
using TestGen.Core.Run;

namespace TestGen.Mock.Tests;

/// <summary>Mocken mot en riktig Kestrel-server på port 0, med exemplet Shop.</summary>
public class MockServerTests
{
    private static readonly string ShopSpec = FindSpec("shop.openapi.yaml");

    private MockServer _mock = null!;
    private HttpClient _http = null!;
    private readonly List<string> _tempDirs = [];

    [SetUp]
    public async Task Up()
    {
        _http = new HttpClient();
        _mock = await MockServer.StartAsync(ShopSpec);
    }

    [TearDown]
    public async Task Down()
    {
        _http.Dispose();
        await _mock.DisposeAsync();
        foreach (var dir in _tempDirs) Directory.Delete(dir, true);
        _tempDirs.Clear();
    }

    private static string FindSpec(string name)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "samples", name))) return Path.Combine(d.FullName, "samples", name);
        throw new FileNotFoundException(name);
    }

    private string WriteSpec(string json)
    {
        var dir = Directory.CreateTempSubdirectory("testgen-mock-").FullName;
        _tempDirs.Add(dir);
        var path = Path.Combine(dir, "spec.openapi.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private Task<(HttpStatusCode Status, string ContentType, JsonNode? Body)> Send(HttpMethod method, string path, string? json = null) =>
        SendTo(_mock, method, path, json);

    private async Task<(HttpStatusCode Status, string ContentType, JsonNode? Body)> SendTo(MockServer server, HttpMethod method, string path, string? json = null)
    {
        using var response = await _http.SendAsync(new HttpRequestMessage(method, server.Url + path) { Content = json is null ? null : Json(json) });
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, response.Content.Headers.ContentType?.MediaType ?? "", text.Length == 0 ? null : JsonNode.Parse(text));
    }

    private static List<string> Messages(JsonNode? problem) =>
        problem!["errors"]!.AsArray().Select(e => $"{e!["field"]}: {e["message"]}").ToList();

    [Test]
    public void The_address_is_on_loopback_with_a_free_port()
    {
        Assert.That(_mock.Url, Does.Match(@"^http://127\.0\.0\.1:\d+$"));
        Assert.That(_mock.Name, Is.EqualTo("Shop API"));
    }

    [Test]
    public async Task A_valid_createProduct_gives_201_with_the_values_from_the_call_and_a_body_that_follows_the_schema()
    {
        var (status, type, body) = await Send(HttpMethod.Post, "/products", """{"name":"Kaffe","sku":"ABC-1234","price":49.5}""");

        Assert.That(status, Is.EqualTo(HttpStatusCode.Created));
        Assert.That(type, Is.EqualTo("application/json"));
        Assert.That((string?)body!["name"], Is.EqualTo("Kaffe"));
        Assert.That((decimal)body["price"]!, Is.EqualTo(49.5m));
        var schema = OpenApiLoader.Load(ShopSpec).Single(o => o.OperationId == "createProduct").ResponseSchemas![201];
        Assert.That(ResponseSchemaCheck.Check(body, schema).Errors, Is.Empty);
    }

    [Test]
    public async Task A_missing_required_field_gives_400_with_an_explanation()
    {
        var (status, type, body) = await Send(HttpMethod.Post, "/products", """{"price":10}""");

        Assert.That(status, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(type, Is.EqualTo("application/problem+json"));
        Assert.That((string?)body!["title"], Is.EqualTo("Anropet stämmer inte med specen"));
        Assert.That(Messages(body), Is.EqualTo(new[] { "name: saknas (obligatoriskt enligt specen)" }));
    }

    [Test]
    public async Task A_value_below_the_minimum_gives_400()
    {
        var (status, _, body) = await Send(HttpMethod.Post, "/products", """{"name":"Kaffe","price":0}""");

        Assert.That(status, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(Messages(body), Is.EqualTo(new[] { "price: är 0 men ska vara minst 0.01" }));
    }

    [Test]
    public async Task A_body_that_is_not_json_gives_400()
    {
        var (status, _, body) = await Send(HttpMethod.Post, "/products", "{inte json");

        Assert.That(status, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(Messages(body), Is.EqualTo(new[] { "kropp: är inte giltig JSON" }));
    }

    [Test]
    public async Task An_unknown_path_gives_404_with_the_closest_operation()
    {
        var (status, type, body) = await Send(HttpMethod.Post, "/product", """{"name":"x","price":1}""");

        Assert.That(status, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(type, Is.EqualTo("application/problem+json"));
        Assert.That((string?)body!["title"], Is.EqualTo("Finns inte i specen"));
        Assert.That((string?)body["detail"], Does.Contain("Närmast: /products"));
    }

    [Test]
    public async Task A_known_path_with_another_method_gives_404_that_names_the_methods()
    {
        var (status, _, body) = await Send(HttpMethod.Delete, "/products");

        Assert.That(status, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((string?)body!["detail"], Does.Contain("ingen DELETE för /products").And.Contain("POST"));
    }

    [Test]
    public async Task A_path_parameter_is_checked_and_used_in_the_response()
    {
        var (ok, _, order) = await Send(HttpMethod.Get, "/orders/42");
        Assert.That(ok, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((int)order!["id"]!, Is.EqualTo(42));

        var (bad, _, problem) = await Send(HttpMethod.Get, "/orders/abc");
        Assert.That(bad, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(Messages(problem), Is.EqualTo(new[] { "id: är text men ska vara heltal" }));
    }

    [Test]
    public async Task A_path_prefix_before_the_template_is_accepted()
    {
        var (status, _, _) = await Send(HttpMethod.Get, "/api/v1/orders/7");
        Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Two_specs_run_side_by_side_on_different_ports()
    {
        await using var other = await MockServer.StartAsync(WriteSpec("""
            {"openapi":"3.0.3","info":{"title":"Ping","version":"1"},
             "paths":{"/ping":{"get":{"operationId":"ping","responses":{"200":{"description":"OK","content":{"application/json":{"schema":{"type":"object","properties":{"ok":{"type":"boolean"}}}}}}}}}}}
            """));

        Assert.That(other.Url, Is.Not.EqualTo(_mock.Url));
        Assert.That(other.Name, Is.EqualTo("Ping"));
        var (ok, _, _) = await SendTo(other, HttpMethod.Get, "/ping");
        var (missing, _, _) = await Send(HttpMethod.Get, "/ping");
        Assert.That(ok, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(missing, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(other.Calls.Select(c => c.OperationId), Is.EqualTo(new[] { "ping" }));
        Assert.That(_mock.Calls.Select(c => c.Status), Is.EqualTo(new[] { 404 }));
    }

    [Test]
    public async Task Respond_changes_the_status_of_an_operation_and_Reset_restores_it()
    {
        _mock.Respond("createProduct", 500, """{"title":"Något gick fel"}""");
        var (status, _, body) = await Send(HttpMethod.Post, "/products", """{"name":"Kaffe","price":5}""");
        Assert.That(status, Is.EqualTo(HttpStatusCode.InternalServerError));
        Assert.That((string?)body!["title"], Is.EqualTo("Något gick fel"));

        _mock.Reset();
        Assert.That(_mock.Calls, Is.Empty, "Reset tömmer loggen");
        var (after, _, _) = await Send(HttpMethod.Post, "/products", """{"name":"Kaffe","price":5}""");
        Assert.That(after, Is.EqualTo(HttpStatusCode.Created));
    }

    [Test]
    public async Task Respond_still_checks_the_call_against_the_spec()
    {
        _mock.Respond("createProduct", 500);
        var (status, _, _) = await Send(HttpMethod.Post, "/products", """{"price":5}""");
        Assert.That(status, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public void Respond_rejects_an_unknown_operation()
    {
        var e = Assert.Throws<ArgumentException>(() => _mock.Respond("nope", 500))!;
        Assert.That(e.Message, Does.Contain("nope").And.Contain("createProduct"));
    }

    [Test]
    public async Task The_log_has_every_call_in_order_with_operation_status_and_problems()
    {
        await Send(HttpMethod.Post, "/products", """{"name":"Kaffe","price":5}""");
        await Send(HttpMethod.Post, "/products", """{"price":5}""");
        await Send(HttpMethod.Get, "/nothing?x=1");

        var calls = _mock.Calls;
        Assert.That(calls.Select(c => (c.Method, c.Path, c.OperationId, c.Status)), Is.EqualTo(new (string, string, string?, int)[]
        {
            ("POST", "/products", "createProduct", 201),
            ("POST", "/products", "createProduct", 400),
            ("GET", "/nothing", null, 404),
        }));
        Assert.That(calls[1].Problems, Is.EqualTo(new[] { "name: saknas (obligatoriskt enligt specen)" }));
        Assert.That(calls[0].Body, Does.Contain("Kaffe"));
        Assert.That(calls[2].Query, Is.EqualTo("x=1"));
        Assert.That(calls.Select(c => c.TimeMs), Is.Ordered);
    }

    [Test]
    public async Task Secrets_in_the_logged_body_and_query_are_masked()
    {
        await Send(HttpMethod.Post, "/products?api_key=hemligt123", """{"name":"Kaffe","price":5,"password":"hemligt123","nested":{"token":"abc"}}""");

        var call = _mock.Calls.Single();
        Assert.That(call.Body, Does.Not.Contain("hemligt123").And.Not.Contain("abc").And.Contain("Kaffe"));
        Assert.That(call.Query, Is.EqualTo("api_key=***"));
    }

    [Test]
    public async Task Concurrent_calls_are_all_logged()
    {
        await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => Send(HttpMethod.Get, "/orders/1")));
        Assert.That(_mock.Calls, Has.Count.EqualTo(40));
    }

    [Test]
    public async Task The_same_seed_gives_the_same_response_and_another_seed_another()
    {
        var (_, _, first) = await Send(HttpMethod.Get, "/orders/1");
        var (_, _, second) = await Send(HttpMethod.Get, "/orders/1");
        Assert.That(second!.ToJsonString(), Is.EqualTo(first!.ToJsonString()));

        await using var sameSeed = await MockServer.StartAsync(ShopSpec);
        var (_, _, again) = await SendTo(sameSeed, HttpMethod.Get, "/orders/1");
        Assert.That(again!.ToJsonString(), Is.EqualTo(first.ToJsonString()));

        await using var otherSeed = await MockServer.StartAsync(ShopSpec, new MockOptions { Seed = 7 });
        var (_, _, different) = await SendTo(otherSeed, HttpMethod.Get, "/orders/1");
        Assert.That(different!.ToJsonString(), Is.Not.EqualTo(first.ToJsonString()));
    }

    [Test]
    public async Task A_body_over_the_limit_is_refused_with_413()
    {
        await using var small = await MockServer.StartAsync(ShopSpec, new MockOptions { MaxBodyBytes = 512 });
        var big = $$"""{"name":"{{new string('x', 4000)}}","price":5}""";

        var (status, type, body) = await SendTo(small, HttpMethod.Post, "/products", big);

        Assert.That(status, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
        Assert.That(type, Is.EqualTo("application/problem+json"));
        Assert.That((string?)body!["title"], Is.EqualTo("Kroppen är för stor"));
        Assert.That(small.Calls.Single().Status, Is.EqualTo(413));
    }

    [Test]
    public async Task Disposing_stops_the_server()
    {
        var url = _mock.Url;
        await _mock.DisposeAsync();

        await Assert.ThatAsync(() => _http.GetAsync(url + "/orders/1"), Throws.InstanceOf<HttpRequestException>());
        _mock = await MockServer.StartAsync(ShopSpec);   // TearDown tar bort den nya
    }

    [Test]
    public async Task Query_and_header_parameters_are_checked()
    {
        await using var server = await MockServer.StartAsync(WriteSpec("""
            {"openapi":"3.0.3","info":{"title":"List","version":"1"},
             "paths":{"/items":{"get":{"operationId":"listItems",
               "parameters":[
                 {"name":"limit","in":"query","required":true,"schema":{"type":"integer","minimum":1,"maximum":50}},
                 {"name":"X-Tenant","in":"header","required":true,"schema":{"type":"string"}}],
               "responses":{"200":{"description":"OK"}}}}}}
            """));

        using var missing = await _http.GetAsync(server.Url + "/items");
        using var bad = await _http.GetAsync(server.Url + "/items?limit=100");
        using var ok = await _http.SendAsync(new HttpRequestMessage(HttpMethod.Get, server.Url + "/items?limit=5") { Headers = { { "X-Tenant", "a" } } });

        Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(server.Calls[0].Problems, Is.EquivalentTo(new[] { "limit: saknas (obligatoriskt enligt specen)", "X-Tenant: saknas (obligatoriskt enligt specen)" }));
        Assert.That(bad.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(server.Calls[1].Problems[0], Does.StartWith("limit: är 100 men ska vara högst 50"));
        Assert.That(ok.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task A_form_post_is_checked_against_the_form_schema()
    {
        await using var server = await MockServer.StartAsync(WriteSpec("""
            {"openapi":"3.0.3","info":{"title":"Form","version":"1"},
             "paths":{"/login":{"post":{"operationId":"login",
               "requestBody":{"content":{"application/x-www-form-urlencoded":{"schema":{"type":"object","required":["user","age"],
                 "properties":{"user":{"type":"string"},"age":{"type":"integer","minimum":18}}}}}},
               "responses":{"204":{"description":"OK"}}}}}}
            """));

        using var ok = await _http.PostAsync(server.Url + "/login", new FormUrlEncodedContent([new("user", "eva"), new("age", "30")]));
        using var bad = await _http.PostAsync(server.Url + "/login", new FormUrlEncodedContent([new("user", "eva"), new("age", "12")]));

        Assert.That(ok.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(bad.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(server.Calls[1].Problems[0], Does.StartWith("age: är 12 men ska vara minst 18"));
    }

    [Test]
    public async Task A_writeOnly_field_is_allowed_in_the_call_and_a_readOnly_field_is_not_required()
    {
        await using var server = await MockServer.StartAsync(WriteSpec("""
            {"openapi":"3.0.3","info":{"title":"Users","version":"1"},
             "paths":{"/users":{"post":{"operationId":"createUser",
               "requestBody":{"content":{"application/json":{"schema":{"type":"object","required":["id","name","password"],
                 "properties":{"id":{"type":"integer","readOnly":true},"name":{"type":"string"},"password":{"type":"string","writeOnly":true}}}}}},
               "responses":{"201":{"description":"OK","content":{"application/json":{"schema":{"type":"object","required":["id","name"],
                 "properties":{"id":{"type":"integer"},"name":{"type":"string"}}}}}}}}}}}
            """));

        var (status, _, body) = await SendTo(server, HttpMethod.Post, "/users", """{"name":"Eva","password":"hemligt"}""");

        Assert.That(status, Is.EqualTo(HttpStatusCode.Created));
        Assert.That((string?)body!["name"], Is.EqualTo("Eva"));
        Assert.That(server.Calls.Single().Body, Does.Not.Contain("hemligt"));
    }
}
