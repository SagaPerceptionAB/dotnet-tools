using SystemMap;
using TestGen.Core.Projects;
using TestGen.Core.Views;

namespace TestGen.Views.Tests;

/// <summary>apis ur kartan och kopplingen formulär till operation, utan MSBuild: kartan byggs för hand.</summary>
public class ViewsBuilderTests
{
    private string _dir = "";

    [SetUp] public void Up() => _dir = Directory.CreateTempSubdirectory("views-builder-").FullName;
    [TearDown] public void Down() => Directory.Delete(_dir, true);

    private const string Spec = """
        openapi: 3.0.1
        info: { title: Api, version: '1' }
        paths:
          /products:
            post:
              operationId: createProduct
              requestBody:
                content:
                  application/json:
                    schema: { $ref: '#/components/schemas/ProductCreate' }
              responses: { '201': { description: ok } }
          /products/{productId}:
            put:
              operationId: updateProduct
              requestBody:
                content:
                  application/json:
                    schema: { $ref: '#/components/schemas/ProductCreate' }
              responses: { '200': { description: ok } }
        components:
          schemas:
            ProductCreate:
              type: object
              properties:
                name: { type: string }
                price: { type: number }
        """;

    private const string CreatePage = """
        @page "/products/create"
        <PageTitle>Ny</PageTitle>
        <EditForm Model="Input" FormName="Create" OnValidSubmit="Save">
            <label for="name">Namn</label>
            <InputText id="name" @bind-Value="Input.Name" />
            <label for="price">Pris</label>
            <InputNumber id="price" @bind-Value="Input.Price" />
            <label for="note">Anteckning</label>
            <InputText id="note" @bind-Value="Input.Note" />
            <button type="submit">Spara</button>
        </EditForm>
        @code {
            public sealed class ProductInput { public string Name { get; set; } = ""; public decimal Price { get; set; } public string Note { get; set; } = ""; }
            private ProductInput Input { get; set; } = new();
            private void Save() { }
        }
        """;

    private void Write(string path, string text)
    {
        var full = Path.Combine(_dir, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    /// <summary>Solution med ett API (med eller utan spec) och en webbapp med sidan CreatePage.</summary>
    private RepositoryFacts Repository(bool spec = true, string page = CreatePage)
    {
        Write("r.slnx", "<Solution><Project Path=\"Api/Api.csproj\" /><Project Path=\"Web/Web.csproj\" /></Solution>");
        Write("Api/Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />");
        Write("Api/Controllers/Placeholder.txt", "");
        if (spec) Write("Api/api.openapi.yaml", Spec);
        Write("Web/Web.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />");
        Write("Web/Components/Pages/Create.razor", page);
        return RepositoryFacts.Read(Path.Combine(_dir, "r.slnx"));
    }

    private static HttpClientFacts Client(string? target = "Api", string? key = "Api:BaseUrl") => new("ApiClient", key, "http:Api", target);

    private static EntryPoint Post(string route, params Step[] steps) =>
        new(EntryKind.BlazorPage, "POST", route, route, null, "Web", "Create.Save", "Web/Components/Pages/Create.razor", 1, null, null, steps);

    private static Step Calls(string operation) =>
        new("Web", "ApiClient.SendAsync", "f.cs", 1, null, null, new IntegrationUse("http:Api", operation), []);

    private static CodeMapModel Map(HttpClientFacts[] clients, params EntryPoint[] entries) =>
        new("r.slnx", ["Web"], [], entries,
            [new AppFacts("Web", [AppKind.Web], [], [], clients)], [], []);

    private ViewsResult Build(RepositoryFacts facts, CodeMapModel map, string? outDirectory = null) => ViewsBuilder.Build(facts, map, outDirectory ?? _dir);

    [Test, Platform("Win")]
    public void An_out_directory_on_another_drive_than_the_solution_is_a_usage_error()
    {
        var facts = Repository();
        var other = char.ToUpperInvariant(facts.SolutionDirectory[0]) == 'Z' ? @"Y:\views" : @"Z:\views";
        var e = Assert.Throws<CommandLine.UsageException>(() => Build(facts, Map([Client()]), other))!;
        Assert.That(e.Message, Does.Contain("annan enhet än solutionen"));
    }

    [Test]
    public void Apis_are_the_http_clients_that_point_at_a_project_sorted_by_project_and_key()
    {
        var result = Build(Repository(), Map([Client("Zeta", "Z:Url"), Client("Api", "Api:BaseUrl"), Client("Api", "Api:Other"), Client(null, "Extern:Url")]));
        var app = result.File.Apps.Single();
        Assert.That(app.Apis, Is.EqualTo(new[] { new ViewsApi("Api", "Api:BaseUrl"), new ViewsApi("Api", "Api:Other"), new ViewsApi("Zeta", "Z:Url") }));
        Assert.That(result.Warnings, Has.Some.Contain("klienten ApiClient anropar Extern:Url").And.Contain("inte är ett projekt i solutionen"));
    }

    [Test]
    public void A_form_is_linked_to_the_operation_its_page_calls_and_fields_to_the_specs_fields()
    {
        var map = Map([Client()], Post("/products/create", Calls("POST products")));
        var result = Build(Repository(), map);

        var form = result.File.Apps.Single().Views.Single().Forms.Single();
        Assert.That(form.SubmitsTo, Is.EqualTo(new ApiCall("POST", "/products")));
        Assert.That(form.OperationId, Is.EqualTo("createProduct"));
        Assert.That(form.Fields.Select(f => (f.Name, f.SpecField)), Is.EqualTo(new[] { ("Name", "name"), ("Price", "price"), ("Note", (string?)null) }));
        Assert.That(result.Warnings, Is.Empty);
    }

    [Test]
    public void Path_parameters_match_whatever_they_are_called()
    {
        var map = Map([Client()], Post("/products/create", Calls("PUT products/{}")));
        Assert.That(Build(Repository(), map).File.Apps.Single().Views.Single().Forms.Single().OperationId, Is.EqualTo("updateProduct"));
    }

    [Test]
    public void A_call_that_the_spec_lacks_gives_a_warning_and_no_link()
    {
        var map = Map([Client()], Post("/products/create", Calls("DELETE products")));
        var result = Build(Repository(), map);

        var form = result.File.Apps.Single().Views.Single().Forms.Single();
        Assert.That((form.OperationId, form.Fields.Select(f => f.SpecField)), Is.EqualTo(((string?)null, new string?[] { null, null, null })));
        Assert.That(result.Warnings, Has.Some.Contain("/products/create").And.Contain("DELETE /products").And.Contain("ingen sådan operation"));
    }

    [Test]
    public void A_page_with_two_different_calls_or_none_is_not_linked()
    {
        var two = Map([Client()], Post("/products/create", Calls("POST products"), Calls("PUT products/{}")));
        Assert.That(Build(Repository(), two).File.Apps.Single().Views.Single().Forms.Single().SubmitsTo, Is.Null);

        var none = Map([Client()], Post("/products/create"));
        var result = Build(Repository(), none);
        Assert.That(result.File.Apps.Single().Views.Single().Forms.Single().OperationId, Is.Null);
        Assert.That(result.Warnings, Is.Empty, "ett formulär utan känt anrop är inget att varna för");
    }

    [Test]
    public void Reads_such_as_database_use_are_not_http_calls()
    {
        var map = Map([Client()], Post("/products/create", Calls("läsa Products"), Calls("POST products")));
        Assert.That(Build(Repository(), map).File.Apps.Single().Views.Single().Forms.Single().OperationId, Is.EqualTo("createProduct"));
    }

    [Test]
    public void Without_a_spec_the_api_is_still_listed_and_a_warning_says_what_to_do()
    {
        var result = Build(Repository(spec: false), Map([Client()], Post("/products/create", Calls("POST products"))));
        Assert.That(result.File.Apps.Single().Apis, Is.EqualTo(new[] { new ViewsApi("Api", "Api:BaseUrl") }));
        Assert.That(result.File.Apps.Single().Views.Single().Forms.Single().OperationId, Is.Null);
        Assert.That(result.Warnings, Has.Some.Contain("Api saknar spec").And.Contain("Lägg en spec"));
        Assert.That(result.Warnings, Has.None.Contain("ingen sådan operation"), "utan spec finns inget att jämföra med");
    }

    [Test]
    public void Sources_are_relative_to_the_output_folder_with_forward_slashes()
    {
        var facts = Repository();
        var map = Map([Client()]);
        Assert.That(Build(facts, map, Path.Combine(_dir, "tests", "T")).File.Apps.Single().Views.Single().Source, Is.EqualTo("../../Web/Components/Pages/Create.razor"));
        Assert.That(Build(facts, map, _dir).File.Apps.Single().Views.Single().Source, Is.EqualTo("Web/Components/Pages/Create.razor"));
    }

    [Test]
    public void An_app_without_views_is_left_out_with_a_warning_and_the_result_is_deterministic()
    {
        var facts = Repository(page: "<p>Ingen sida</p>");
        var result = Build(facts, Map([Client()]));
        Assert.That(result.File.Apps, Is.Empty);
        Assert.That(result.Warnings, Has.Some.Contain("Web: hittade inga vyer"));

        var withViews = Repository();
        var map = Map([Client()], Post("/products/create", Calls("POST products")));
        var first = ViewsJson.Serialize(Build(withViews, map).File);
        Assert.That(ViewsJson.Serialize(Build(withViews, map).File), Is.EqualTo(first));
    }
}
