using TestGen.Core.Views;
using TestGen.Views.Discovery;

namespace TestGen.Views.Tests;

public class MvcDiscoveryTests
{
    private const string Plain = "@{ ViewData[\"Title\"] = \"x\"; }\n<h1>x</h1>\n";

    [Test]
    public void Conventional_routes_follow_the_default_pattern()
    {
        using var p = new TempProject();
        p.Write("Controllers/HomeController.cs", """
            public class HomeController : Controller
            {
                public IActionResult Index() => View();
                public IActionResult About() { return View(); }
            }
            """);
        p.Write("Controllers/ProductsController.cs", """
            public class ProductsController : Controller
            {
                public IActionResult Index() { return View(); }
                public async Task<IActionResult> Details(int id) { return View(await Load(id)); }
                public IActionResult Browse(int id) { return View(); }
            }
            """);
        foreach (var path in new[] { "Home/Index", "Home/About", "Products/Index", "Products/Details", "Products/Browse" })
            p.Write($"Views/{path}.cshtml", Plain);

        var catalog = p.Run();

        Assert.That(catalog.Views.Select(v => (v.Route, v.Id, v.Kind, v.Source)), Is.EqualTo(new[]
        {
            ("/", "ui:/", ViewKind.Mvc, "Views/Home/Index.cshtml"),
            ("/Home/About", "ui:/Home/About", ViewKind.Mvc, "Views/Home/About.cshtml"),
            ("/Products", "ui:/Products", ViewKind.Mvc, "Views/Products/Index.cshtml"),
            ("/Products/Browse/{id?}", "ui:/Products/Browse/{id?}", ViewKind.Mvc, "Views/Products/Browse.cshtml"),
            ("/Products/Details/{id?}", "ui:/Products/Details/{id?}", ViewKind.Mvc, "Views/Products/Details.cshtml"),
        }));
        Assert.That(catalog.Views.Single(v => TestViews.Name(v) == "ProductsDetailsId").Parameters, Is.EqualTo(new[] { new RouteParameter("id", null, true) }));
        Assert.That(catalog.Warnings, Is.Empty);
    }

    [Test]
    public void Title_heading_and_viewbag_are_read_from_the_view()
    {
        using var p = new TempProject();
        p.Write("Controllers/HomeController.cs", "public class HomeController : Controller { public IActionResult Index() => View(); public IActionResult About() => View(); }");
        p.Write("Views/Home/Index.cshtml", "@{ ViewData[\"Title\"] = \"Start\"; }\n<h1>Välkommen</h1>");
        p.Write("Views/Home/About.cshtml", "@{ ViewBag.Title = \"Om oss\"; }\n<h1>Om @Model.Name</h1>");

        var catalog = p.Run();

        Assert.That(catalog.Views.Single(v => v.Route == "/").Title, Is.EqualTo("Start"));
        Assert.That(catalog.Views.Single(v => v.Route == "/").Heading, Is.EqualTo("Välkommen"));
        Assert.That(catalog.Views.Single(v => v.Route == "/Home/About").Title, Is.EqualTo("Om oss"));
        Assert.That(catalog.Views.Single(v => v.Route == "/Home/About").Heading, Is.Null);
    }

    [Test]
    public void Attribute_routes_replace_controller_and_action_tokens()
    {
        using var p = new TempProject();
        p.Write("Controllers/OrdersController.cs", """
            [Route("[controller]")]
            public class OrdersController : Controller
            {
                [HttpGet("")] public IActionResult Index() => View();
                [HttpGet("{id:int}")] public IActionResult Details(int id) => View();
                [Route("[action]")] public IActionResult Archive() => View();
                [HttpGet("~/shop/cart")] public IActionResult Cart() => View();
                [HttpGet("/legacy"), HttpGet("old/{id}")] public IActionResult Legacy(int id) => View();
            }
            """);
        foreach (var name in new[] { "Index", "Details", "Archive", "Cart", "Legacy" }) p.Write($"Views/Orders/{name}.cshtml", Plain);

        var catalog = p.Run();

        Assert.That(catalog.Views.Select(v => v.Route), Is.EqualTo(new[]
        {
            "/Orders", "/Orders/Archive", "/Orders/old/{id}", "/Orders/{id:int}", "/legacy", "/shop/cart",
        }));
        Assert.That(catalog.Views.Single(v => v.Route == "/Orders/{id:int}").Parameters, Is.EqualTo(new[] { new RouteParameter("id", "int", false) }));
    }

    [Test]
    public void Named_views_and_shared_views_are_found()
    {
        using var p = new TempProject();
        p.Write("Controllers/PagesController.cs", """
            public class PagesController : Controller
            {
                public IActionResult One() => View("Other");
                public IActionResult Two() => View("Common");
                public IActionResult Three() => View("~/Views/Special/Page.cshtml");
                public IActionResult Four() => View("Views/Special/Page2.cshtml");
                public IActionResult Five() => View();
                public IActionResult Six(Model m) { if (m is null) return View("NotFound"); return View(m); }
            }
            """);
        p.Write("Views/Pages/Other.cshtml", Plain);
        p.Write("Views/Shared/Common.cshtml", Plain);
        p.Write("Views/Special/Page.cshtml", Plain);
        p.Write("Views/Special/Page2.cshtml", Plain);
        p.Write("Views/Shared/Five.cshtml", Plain);
        p.Write("Views/Pages/Six.cshtml", Plain);

        var catalog = p.Run();

        Assert.That(catalog.Views.ToDictionary(v => v.Route, v => v.Source), Is.EqualTo(new Dictionary<string, string>
        {
            ["/Pages/One"] = "Views/Pages/Other.cshtml",
            ["/Pages/Two"] = "Views/Shared/Common.cshtml",
            ["/Pages/Three"] = "Views/Special/Page.cshtml",
            ["/Pages/Four"] = "Views/Special/Page2.cshtml",
            ["/Pages/Five"] = "Views/Shared/Five.cshtml",
            ["/Pages/Six"] = "Views/Pages/Six.cshtml",
        }));
        Assert.That(catalog.Warnings, Is.Empty);
    }

    [Test]
    public void Missing_view_file_gives_a_view_without_forms_and_a_warning()
    {
        using var p = new TempProject();
        p.Write("Controllers/GhostController.cs", "public class GhostController : Controller { public IActionResult Index() => View(); }");

        var catalog = p.Run();

        var view = catalog.Views.Single();
        Assert.That((view.Route, view.Source), Is.EqualTo(("/Ghost", "Controllers/GhostController.cs")));
        Assert.That(view.Forms, Is.Empty);
        Assert.That(catalog.Warnings, Has.Count.EqualTo(1));
        Assert.That(catalog.Warnings[0], Does.Contain("Ghost.Index").And.Contain("vyfil"));
    }

    [Test]
    public void Api_controllers_post_actions_and_non_view_actions_are_left_out()
    {
        using var p = new TempProject();
        p.Write("Controllers/ApiThingsController.cs", """
            [ApiController]
            [Route("api/things")]
            public class ApiThingsController : ControllerBase
            {
                [HttpGet] public IActionResult List() => Ok();
            }
            """);
        p.Write("Controllers/MarkedApiController.cs", """
            [ApiController]
            public class MarkedApiController : Controller
            {
                public IActionResult Index() => View();
            }
            """);
        p.Write("Controllers/ThingsController.cs", """
            public class ThingsController : Controller
            {
                public IActionResult Index() => View();
                [HttpGet] public IActionResult Edit(int id) => View();
                [HttpPost] public IActionResult Edit(int id, Thing t) { return View(t); }
                [HttpPut] public IActionResult Put() => View();
                [HttpDelete] public IActionResult Remove() => View();
                [NonAction] public IActionResult Helper() => View();
                public IActionResult Gone() => RedirectToAction("Index");
                public string Text() => "x";
                IActionResult Hidden() => View();
                public static IActionResult Stat() => View();
            }
            """);
        p.Write("Views/Things/Index.cshtml", Plain);
        p.Write("Views/Things/Edit.cshtml", Plain);

        var catalog = p.Run();

        Assert.That(catalog.Views.Select(v => v.Route), Is.EqualTo(new[] { "/Things", "/Things/Edit/{id?}" }));
    }

    [Test]
    public void Base_controller_classes_are_recognised_by_name()
    {
        using var p = new TempProject();
        p.Write("Controllers/BaseController.cs", "public abstract class AppBaseController : Controller { }");
        p.Write("Controllers/ReportsController.cs", "public class ReportsController : AppBaseController { public IActionResult Index() => View(); }");
        p.Write("Controllers/PlainController.cs", "public class PlainController { public IActionResult Index() => null; }");
        p.Write("Views/Reports/Index.cshtml", Plain);

        Assert.That(p.Run().Views.Select(v => v.Route), Is.EqualTo(new[] { "/Reports" }));
    }

    [Test]
    public void Form_gets_fields_labels_and_rules_from_the_model()
    {
        using var p = new TempProject();
        p.Write("Controllers/ProductsController.cs", """
            public class ProductsController : Controller
            {
                public IActionResult Create() => View();
                [HttpPost] public IActionResult Create(ProductInput input) => View(input);
            }
            """);
        p.Write("Models/ProductInput.cs", """
            public class ProductInput
            {
                [Required(ErrorMessage = "Namn krävs")]
                [StringLength(50, MinimumLength = 2)]
                [Display(Name = "Produktnamn")]
                public string Name { get; set; }

                [Range(1, 1000)]
                public int Price { get; set; }

                [EmailAddress]
                public string Contact { get; set; }
            }
            """);
        p.Write("Views/Products/Create.cshtml", """
            @model ProductInput
            @{ ViewData["Title"] = "Ny produkt"; }
            <h1>Ny produkt</h1>
            <form asp-action="Create" method="post">
                <label asp-for="Name"></label>
                <input asp-for="Name" />
                <label asp-for="Price"></label>
                <input asp-for="Price" />
                <label asp-for="Contact"></label>
                <input asp-for="Contact" />
                <button type="submit">Spara</button>
            </form>
            """);

        var catalog = p.Run();

        var view = catalog.Views.Single();
        Assert.That(view.Route, Is.EqualTo("/Products/Create"));
        Assert.That(view.Title, Is.EqualTo("Ny produkt"));
        var form = view.Forms.Single();
        Assert.That(form.Id, Is.EqualTo("Product"));
        Assert.That(form.Model, Is.EqualTo("ProductInput"));
        Assert.That(form.Submit, Is.EqualTo(new Locator(LocatorKind.Role, "button", "Spara")));
        Assert.That(form.Fields.Select(f => (f.Name, f.Label, f.Type)), Is.EqualTo(new[]
        {
            ("Name", (string?)"Produktnamn", FieldType.Text), ("Price", "Price", FieldType.Number), ("Contact", "Contact", FieldType.Email),
        }));
        Assert.That(form.Fields[0].Rules, Is.EqualTo(new[]
        {
            new Rule(RuleKind.Required, null, "Namn krävs"), new Rule(RuleKind.MinLength, "2"), new Rule(RuleKind.MaxLength, "50"),
        }));
        Assert.That(form.Fields[1].Rules, Is.EqualTo(new[] { new Rule(RuleKind.Min, "1"), new Rule(RuleKind.Max, "1000") }));
        Assert.That(catalog.Warnings, Is.Empty);
    }

    [Test]
    public void Form_without_a_known_model_warns_and_has_no_rules()
    {
        using var p = new TempProject();
        p.Write("Controllers/HomeController.cs", "public class HomeController : Controller { public IActionResult Index() => View(); }");
        p.Write("Views/Home/Index.cshtml", """
            @model MissingModel
            <form method="post"><input asp-for="Name" /><button>Skicka</button></form>
            """);

        var catalog = p.Run();

        Assert.That(catalog.Views.Single().Forms.Single().Fields.Single().Rules, Is.Empty);
        Assert.That(catalog.Warnings.Single(), Does.Contain("MissingModel"));
    }

    [Test]
    public void Links_via_tag_helpers_use_the_same_route_format()
    {
        using var p = new TempProject();
        p.Write("Controllers/HomeController.cs", "public class HomeController : Controller { public IActionResult Index() => View(); }");
        p.Write("Controllers/ProductsController.cs", """
            public class ProductsController : Controller
            {
                public IActionResult Index() => View();
                public IActionResult Details(int id) => View();
                public IActionResult Create() => View();
            }
            [Route("shop/orders")]
            public class OrdersController : Controller
            {
                [HttpGet("{id}")] public IActionResult Show(int id) => View();
            }
            """);
        p.Write("Views/Home/Index.cshtml", Plain);
        p.Write("Views/Orders/Show.cshtml", Plain);
        p.Write("Views/Products/Details.cshtml", Plain);
        p.Write("Views/Products/Create.cshtml", Plain);
        p.Write("Views/Products/Index.cshtml", """
            <a asp-controller="Home" asp-action="Index">Hem</a>
            <a asp-action="Create">Ny</a>
            <a asp-action="Details" asp-route-id="@p.Id">Visa</a>
            <a asp-controller="Orders" asp-action="Show" asp-route-id="7">Order</a>
            <a asp-controller="Orders" asp-action="Show">Order utan id</a>
            <a asp-action="Index">Lista</a>
            <a href="/about">Om</a>
            <a href="https://example.com">Extern</a>
            <a asp-controller="Reports" asp-action="Monthly" asp-route-id="3">Rapport</a>
            """);

        var links = p.Run().Views.Single(v => v.Route == "/Products").Links;

        Assert.That(links.Select(l => (l.Href, l.Text)), Is.EqualTo(new (string, string?)[]
        {
            ("/", "Hem"),
            ("/Products", "Lista"),
            ("/Products/Create", "Ny"),
            ("/Products/Details/{}", "Visa"),
            ("/Reports/Monthly/{}", "Rapport"),
            ("/about", "Om"),
            ("/shop/orders/{}", "Order"),
        }));
    }

    [Test]
    public void Authorize_on_class_or_action_is_honoured_and_allow_anonymous_wins()
    {
        using var p = new TempProject();
        p.Write("Controllers/OpenController.cs", """
            public class OpenController : Controller
            {
                public IActionResult Index() => View();
                [Authorize] public IActionResult Secret() => View();
            }
            [Authorize]
            public class AdminController : Controller
            {
                public IActionResult Index() => View();
                [AllowAnonymous] public IActionResult Login() => View();
            }
            """);
        foreach (var path in new[] { "Open/Index", "Open/Secret", "Admin/Index", "Admin/Login" }) p.Write($"Views/{path}.cshtml", Plain);

        var auth = p.Run().Views.ToDictionary(v => v.Route, v => v.RequiresAuth);

        Assert.That(auth, Is.EqualTo(new Dictionary<string, bool>
        {
            ["/Open"] = false,
            ["/Open/Secret"] = true,
            ["/Admin"] = true,
            ["/Admin/Login"] = false,
        }));
    }

    [Test]
    public void Custom_conventional_route_in_program_gives_a_warning_but_default_does_not()
    {
        using var custom = new TempProject();
        custom.Write("Controllers/HomeController.cs", "public class HomeController : Controller { public IActionResult Index() => View(); }");
        custom.Write("Views/Home/Index.cshtml", Plain);
        custom.Write("Program.cs", """
            var app = builder.Build();
            app.MapControllerRoute(name: "blog", pattern: "blog/{*article}", defaults: new { controller = "Blog" });
            """);

        var warnings = custom.Run().Warnings;

        Assert.That(warnings.Single(), Does.Contain("Program.cs").And.Contain("blog/{*article}"));

        using var standard = new TempProject();
        standard.Write("Controllers/HomeController.cs", "public class HomeController : Controller { public IActionResult Index() => View(); }");
        standard.Write("Views/Home/Index.cshtml", Plain);
        standard.Write("Program.cs", """app.MapControllerRoute(name: "default", pattern: "{controller=Home}/{action=Index}/{id?}");""");

        Assert.That(standard.Run().Warnings, Is.Empty);
    }

    [Test]
    public void Two_runs_give_identical_json()
    {
        using var p = new TempProject();
        p.Write("Controllers/HomeController.cs", "public class HomeController : Controller { public IActionResult Index() => View(); }");
        p.Write("Controllers/ProductsController.cs", "public class ProductsController : Controller { public IActionResult Index() => View(); public IActionResult Details(int id) => View(); }");
        p.Write("Views/Home/Index.cshtml", "<a asp-controller=\"Products\" asp-action=\"Index\">P</a>");
        p.Write("Views/Products/Index.cshtml", Plain);

        var first = ViewsJson.Serialize(ViewsFile.Of([new AppViews("App", p.Run().Views, [])]));
        var second = ViewsJson.Serialize(ViewsFile.Of([new AppViews("App", p.Run().Views, [])]));

        Assert.That(second, Is.EqualTo(first));
    }

    [Test]
    public void Layout_links_and_partials_are_added_to_every_view_with_tag_helpers_translated_per_view()
    {
        using var p = new TempProject();
        p.Write("Controllers/HomeController.cs", "public class HomeController : Controller { public IActionResult Index() => View(); public IActionResult About() => View(); public IActionResult Raw() => View(); }");
        p.Write("Views/_ViewStart.cshtml", "@{ Layout = \"_Layout\"; }");
        p.Write("Views/Shared/_Layout.cshtml", """
            <nav>
              <a asp-controller="Home" asp-action="Index">Hem</a>
              <a asp-action="About">Om</a>
              <partial name="_Nav" />
            </nav>
            @RenderBody()
            """);
        p.Write("Views/Shared/_Nav.cshtml", "<a href=\"/privacy\">Integritet</a>");
        p.Write("Views/Home/Index.cshtml", Plain);
        p.Write("Views/Home/About.cshtml", Plain + "<a href=\"/\">Hem igen</a>");
        p.Write("Views/Home/Raw.cshtml", "@{ Layout = null; }\n<h1>Rå</h1><a href=\"/x\">X</a>");

        var catalog = p.Run();
        string[] Links(string route) => catalog.Views.Single(v => v.Route == route).Links.Select(l => l.Href).ToArray();

        Assert.That(Links("/"), Is.EqualTo(new[] { "/", "/Home/About", "/privacy" }));
        Assert.That(Links("/Home/About"), Is.EqualTo(new[] { "/", "/Home/About", "/privacy" }));
        Assert.That(Links("/Home/Raw"), Is.EqualTo(new[] { "/x" }));
    }

    [Test]
    public void Views_without_a_ViewStart_get_no_layout_links()
    {
        using var p = new TempProject();
        p.Write("Controllers/HomeController.cs", "public class HomeController : Controller { public IActionResult Index() => View(); }");
        p.Write("Views/Shared/_Layout.cshtml", "<a href=\"/privacy\">Integritet</a>");
        p.Write("Views/Home/Index.cshtml", Plain);

        Assert.That(p.Run().Views.Single().Links, Is.Empty);
    }

    private sealed class TempProject : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("views-").FullName;

        public void Write(string relativePath, string content)
        {
            var path = Path.Combine(_root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public ViewCatalog Run()
        {
            var (root, directories) = TestRoots.From(_root);
            return ViewDiscovery.Run(new DiscoveryContext(root, directories, []), [new MvcDiscoverer()]);
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
