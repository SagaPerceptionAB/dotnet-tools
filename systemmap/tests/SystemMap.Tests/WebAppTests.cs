namespace SystemMap.Tests;

/// <summary>
/// Sidor och appar i riktiga projekt, lästa med MSBuild: Razor Pages och Blazor kräver Razors källgenerator, som bara körs
/// när projektet läses som vid ett bygge.
/// </summary>
public class WebAppTests
{
    private static string Describe(EntryPoint e) => $"{e.Kind} {e.Method} {e.Path} {e.Handler}";

    private static List<string> Uses(EntryPoint entry)
    {
        IEnumerable<Step> All(IEnumerable<Step> steps) => steps.SelectMany(s => new[] { s }.Concat(All(s.Steps)));
        return All(entry.Steps).Where(s => s.Integration is not null).Select(s => $"{s.Integration!.Id} | {s.Integration.Operation}").ToList();
    }

    [Test]
    public async Task Sample_finds_controllers_pages_and_the_link_from_ShopWeb_to_ShopApi()
    {
        using var loaded = await SolutionLoader.LoadAsync(TestPaths.Solution, restore: true);
        var map = await CodeMapper.AnalyzeAsync(loaded.Solution, "samples/Shop.slnx", SpecReader.Load(TestPaths.Spec), loaded.Warnings);

        Assert.That(map.EntryPoints.Where(e => e.Project == "ShopApi").Select(Describe), Is.EqualTo(new[]
        {
            "Controller POST /customers CustomersController.Create",
            "Controller POST /orders OrdersController.Create",
            "Controller GET /orders/{id} OrdersController.Get",
            "Controller POST /products ProductsController.Create",
        }));
        Assert.That(map.EntryPoints.Where(e => e.Project == "ShopWeb").Select(Describe), Is.EquivalentTo(new[]
        {
            "BlazorPage GET / Home.OnInitialized",
            "BlazorPage GET /products Products.OnInitialized",
            "BlazorPage GET /products/create ProductCreate.OnInitialized",
            "BlazorPage POST /products/create ProductCreate.Save",
            "BlazorPage GET /products/{id} ProductDetails.OnParametersSet",
            "BlazorPage GET /customers/create CustomerCreate.OnInitialized",
            "BlazorPage POST /customers/create CustomerCreate.Save",
            "RazorPage GET /about AboutModel.OnGet",
            "RazorPage POST /about AboutModel.OnPost",
        }));
        var post = map.EntryPoints.Single(e => e.Path == "/products/create" && e.Method == "POST");
        Assert.That((post.File, post.Input?.Type), Is.EqualTo(("ShopWeb/Components/Pages/ProductCreate.razor", "ProductInput")));
        Assert.That(post.Line, Is.GreaterThan(1), "raden i .razor-filen där Save står");
        Assert.That(Uses(post), Is.EqualTo(new[] { "http:ShopApi | POST products" }));
        Assert.That(Uses(map.EntryPoints.Single(e => e.Path == "/customers/create" && e.Method == "POST")), Is.EqualTo(new[] { "http:ShopApi | POST customers" }));
        var about = map.EntryPoints.Single(e => e.Path == "/about" && e.Method == "GET");
        Assert.That((about.File, about.Line), Is.EqualTo(("ShopWeb/Pages/About.cshtml", 1)), "@page-raden");
        Assert.That(map.EntryPoints.Single(e => e.Path == "/products/{id}").File, Is.EqualTo("ShopWeb/Components/Pages/ProductDetails.razor"));

        Assert.That(map.Apps.Select(a => (a.Project, string.Join("+", a.Kinds))), Is.EqualTo(new[] { ("ShopApi", "Api"), ("ShopWeb", "Web") }));
        var web = map.Apps.Single(a => a.Project == "ShopWeb");
        Assert.That(web.HttpClients, Is.EqualTo(new[] { new HttpClientFacts("ShopApiClient", "ShopApi:BaseUrl", "http:ShopApi", "ShopApi") }));
        var api = map.Apps.Single(a => a.Project == "ShopApi");
        Assert.That(api.Databases, Is.EqualTo(new[] { new DatabaseFacts("sqlite", "ShopDbContext", null) }));
        Assert.That(api.HttpClients, Is.EqualTo(new[] { new HttpClientFacts("PaymentGateway", "Betaltjanst:BaseUrl", "http:Betaltjanst", null) }));
        Assert.That(map.Integrations.Single(i => i.Id == "http:ShopApi").TargetProject, Is.EqualTo("ShopApi"));
        Assert.That(map.Differences, Is.Empty, "sidor jämförs inte med specen");
    }

    private const string Usings = """
        @using Microsoft.AspNetCore.Components
        @using Microsoft.AspNetCore.Components.Forms
        @using System.ComponentModel.DataAnnotations
        """;

    [Test]
    public async Task Razor_Pages_and_Blazor_pages_give_routes_handlers_and_calls()
    {
        var dir = Directory.CreateTempSubdirectory("systemmap-web-").FullName;
        try
        {
            TestPaths.PinSdk(dir);
            void Write(string path, string text)
            {
                var full = Path.Combine(dir, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, text);
            }
            const string Project = "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>";
            Write("Fixture.slnx", "<Solution><Project Path=\"Web/Web.csproj\" /><Project Path=\"Backend/Backend.csproj\" /></Solution>");
            Write("Backend/Backend.csproj", Project);
            Write("Backend/Program.cs", "var app = WebApplication.CreateBuilder(args).Build();\napp.MapGet(\"/ping\", () => \"pong\");\napp.Run();\n");
            Write("Backend/Properties/launchSettings.json", """{ "profiles": { "Backend": { "applicationUrl": "http://localhost:6501" } } }""");
            Write("Web/Web.csproj", Project);
            Write("Web/appsettings.json", """{ "Partner": { "BaseUrl": "http://localhost:6501" } }""");
            Write("Web/Program.cs", """
                var builder = WebApplication.CreateBuilder(args);
                builder.Services.AddRazorComponents();
                builder.Services.AddRazorPages();
                builder.Services.AddHttpClient<Web.PartnerClient>(c => c.BaseAddress = new Uri(builder.Configuration["Partner:BaseUrl"]!));
                var app = builder.Build();
                app.MapRazorPages();
                app.MapRazorComponents<Web.Components.App>();
                app.Run();
                namespace Web
                {
                    public class PartnerClient(HttpClient http) { public Task<HttpResponseMessage> Send() => http.PostAsync("meddelanden", null); }
                }
                """);
            Write("Web/Components/_Imports.razor", Usings + "\n@using Web\n");
            Write("Web/Components/App.razor", "<h1>App</h1>\n");
            Write("Web/Components/Pages/Hello.razor", """
                @page "/blazor"
                @page "/blazor/{Id:int}"
                <h1>Hej</h1>
                @code {
                    [Parameter] public int Id { get; set; }
                    protected override void OnInitialized() { System.IO.File.ReadAllText("c.txt"); }
                }
                """);
            Write("Web/Components/Pages/Form.razor", """
                @page "/form"
                @inject PartnerClient Partner
                <EditForm Model="Input" FormName="F" OnValidSubmit="Save"><InputText @bind-Value="Input.Name" /><button type="submit">Spara</button></EditForm>
                @code {
                    [SupplyParameterFromForm] private FormInput Input { get; set; } = new();
                    private async Task Save() { await Partner.Send(); }
                    public sealed class FormInput { [Required] public string Name { get; set; } = ""; }
                }
                """);
            Write("Web/Pages/_ViewImports.cshtml", "@namespace Web.Pages\n@addTagHelper *, Microsoft.AspNetCore.Mvc.TagHelpers\n");
            Write("Web/Pages/Index.cshtml", "@page\n@model IndexModel\n<h1>Start</h1>\n");
            Write("Web/Pages/Index.cshtml.cs", """
                using Microsoft.AspNetCore.Mvc.RazorPages;
                namespace Web.Pages;
                public class IndexModel : PageModel
                {
                    public void OnGet() { System.IO.File.ReadAllText("a.txt"); }
                    public Task OnPostSaveAsync() => Task.CompletedTask;
                }
                """);
            Write("Web/Pages/Orders/Details.cshtml", "@page \"{id:int}\"\n@model DetailsModel\n<h1>Order</h1>\n");
            Write("Web/Pages/Orders/Details.cshtml.cs", "using Microsoft.AspNetCore.Mvc.RazorPages;\nnamespace Web.Pages.Orders;\npublic class DetailsModel : PageModel { }\n");
            Write("Web/Pages/Hello.cshtml", "@page \"/hej\"\n<h1>Hej</h1>\n@functions {\n    public void OnPostSkicka() { System.IO.File.WriteAllText(\"b.txt\", \"x\"); }\n}\n");

            using var loaded = await SolutionLoader.LoadAsync(Path.Combine(dir, "Fixture.slnx"), restore: true);
            var map = await CodeMapper.AnalyzeAsync(loaded.Solution, Path.Combine(dir, "Fixture.slnx"), null, loaded.Warnings);

            Assert.That(map.EntryPoints.Select(Describe), Is.EquivalentTo(new[]
            {
                "MinimalApi GET /ping Program.Main",
                "RazorPage GET / IndexModel.OnGet",
                "RazorPage POST /?handler=Save IndexModel.OnPostSaveAsync",
                "RazorPage GET /Orders/Details/{id} DetailsModel.OnGet",
                "RazorPage GET /hej HelloModel.OnGet",
                "RazorPage POST /hej?handler=Skicka HelloModel.OnPostSkicka",
                "BlazorPage GET /blazor Hello.OnInitialized",
                "BlazorPage GET /blazor/{Id} Hello.OnInitialized",
                "BlazorPage GET /form Form.OnInitialized",
                "BlazorPage POST /form Form.Save",
            }));
            EntryPoint Find(string method, string path) => map.EntryPoints.Single(e => e.Method == method && e.Path == path);
            Assert.That(Uses(Find("GET", "/")), Is.EqualTo(new[] { "file | läsa a.txt" }));
            Assert.That(Uses(Find("POST", "/hej?handler=Skicka")), Is.EqualTo(new[] { "file | skriva b.txt" }));
            Assert.That(Uses(Find("GET", "/blazor")), Is.EqualTo(new[] { "file | läsa c.txt" }));
            Assert.That(Uses(Find("POST", "/form")), Is.EqualTo(new[] { "http:Partner | POST meddelanden" }));
            Assert.That(Find("POST", "/form").Input?.Type, Is.EqualTo("FormInput"));
            Assert.That((Find("GET", "/blazor").File, Find("GET", "/blazor").Line), Is.EqualTo(("Web/Components/Pages/Hello.razor", 1)));
            Assert.That(Find("POST", "/form").File, Is.EqualTo("Web/Components/Pages/Form.razor"));
            Assert.That((Find("GET", "/Orders/Details/{id}").File, Find("GET", "/hej").File), Is.EqualTo(("Web/Pages/Orders/Details.cshtml", "Web/Pages/Hello.cshtml")));
            Assert.That(Find("GET", "/").File, Is.EqualTo("Web/Pages/Index.cshtml.cs"));

            Assert.That(map.Apps.Select(a => (a.Project, string.Join("+", a.Kinds))), Is.EqualTo(new[] { ("Backend", "Api"), ("Web", "Web") }));
            Assert.That(map.Apps.Single(a => a.Project == "Web").HttpClients, Is.EqualTo(new[] { new HttpClientFacts("PartnerClient", "Partner:BaseUrl", "http:Partner", "Backend") }),
                "ingen projekt heter Partner, men Backend lyssnar på adressens port");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { /* MSBuild kan ha en fil öppen en stund till */ }
        }
    }

    [Test]
    public async Task Projects_only_reached_through_a_test_project_are_not_part_of_the_map()
    {
        var dir = Directory.CreateTempSubdirectory("systemmap-tests-").FullName;
        try
        {
            TestPaths.PinSdk(dir);
            void Write(string path, string text)
            {
                var full = Path.Combine(dir, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, text);
            }
            const string Props = "<TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings>";
            string Web(string references = "") => $"<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup>{Props}</PropertyGroup><ItemGroup>{references}</ItemGroup></Project>";
            const string Program = "var app = WebApplication.CreateBuilder(args).Build();\napp.MapGet(\"/{0}\", () => \"x\");\napp.Run();\n";

            // Tooling och Shared ligger utanför solutionen. Tests når Tooling; App når Shared.
            Write("Fixture.slnx", "<Solution><Project Path=\"App/App.csproj\" /><Project Path=\"Tests/Tests.csproj\" /></Solution>");
            Write("App/App.csproj", Web("<ProjectReference Include=\"../Shared/Shared.csproj\" />"));
            Write("App/Program.cs", string.Format(Program, "app"));
            Write("Shared/Shared.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>{Props}</PropertyGroup><ItemGroup><FrameworkReference Include=\"Microsoft.AspNetCore.App\" /></ItemGroup></Project>");
            Write("Shared/SharedController.cs", "using Microsoft.AspNetCore.Mvc;\n[ApiController, Route(\"shared\")] public class SharedController : ControllerBase { [HttpGet] public int Get() => 1; }\n");
            Write("Tests/Tests.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>{Props}</PropertyGroup><ItemGroup><PackageReference Include=\"NUnit\" Version=\"4.3.2\" /><ProjectReference Include=\"../Tooling/Tooling.csproj\" /></ItemGroup></Project>");
            Write("Tests/T.cs", "public class T { }\n");
            Write("Tooling/Tooling.csproj", Web());
            Write("Tooling/Program.cs", string.Format(Program, "tooling"));

            using var loaded = await SolutionLoader.LoadAsync(Path.Combine(dir, "Fixture.slnx"), restore: true);
            var map = await CodeMapper.AnalyzeAsync(loaded.Solution, "Fixture.slnx", null, loaded.Warnings);

            Assert.That(map.EntryPoints.Select(e => $"{e.Project} {e.Path}"), Is.EqualTo(new[] { "App /app", "Shared /shared" }));
            Assert.That(map.Apps.Select(a => a.Project), Is.EqualTo(new[] { "App" }));
            Assert.That(map.Projects, Is.EqualTo(new[] { "App", "Shared" }));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { /* MSBuild kan ha en fil öppen en stund till */ }
        }
    }
}
