using TestGen.Core.Views;
using TestGen.Views.Discovery;
using TestGen.Views.Discovery.Razor;

namespace TestGen.Views.Tests;

public class DiscoveryTests
{
    // ---------- Blazor: rutter, titel, rubrik, länkar ----------

    [Test]
    public void Blazor_page_with_several_routes_gives_one_view_per_route()
    {
        using var p = new TempProject();
        p.Write("Pages/Orders.razor", """
            @page "/orders"
            @page "/orders/{id:int}/{slug?}"
            <PageTitle>Beställningar</PageTitle>
            <h1>Beställningar</h1>
            """);
        p.Write("Pages/Plain.razor", "<h1>Ingen rutt</h1>");

        var catalog = p.Run(new BlazorDiscoverer());

        Assert.That(catalog.Views.Select(v => (v.Id, v.Route, v.Kind, v.Source)), Is.EqualTo(new[]
        {
            ("ui:/orders", "/orders", ViewKind.Blazor, "Pages/Orders.razor"),
            ("ui:/orders/{id:int}/{slug?}", "/orders/{id:int}/{slug?}", ViewKind.Blazor, "Pages/Orders.razor"),
        }));
        Assert.That(catalog.Views[1].Parameters, Is.EqualTo(new[] { new RouteParameter("id", "int", false), new RouteParameter("slug", null, true) }));
        Assert.That(catalog.Views[0].Title, Is.EqualTo("Beställningar"));
        Assert.That(catalog.Views[0].Heading, Is.EqualTo("Beställningar"));
    }

    [Test]
    public void Blazor_dynamic_title_and_heading_are_null()
    {
        using var p = new TempProject();
        p.Write("A.razor", """
            @page "/a"
            <PageTitle>Hej @Name</PageTitle>
            <h1>Välkommen @Name</h1>
            <h1>Fast</h1>
            @code { string Name = "x"; }
            """);

        var view = p.Run(new BlazorDiscoverer()).Views.Single();

        Assert.That(view.Title, Is.Null);
        Assert.That(view.Heading, Is.Null);
    }

    [Test]
    public void Blazor_links_keep_internal_addresses_and_turn_dynamic_parts_into_braces()
    {
        using var p = new TempProject();
        p.Write("Links.razor", """
            @page "/links"
            <a href="/products">Produkter</a>
            <NavLink href="/customers/create" Match="NavLinkMatch.All">Ny kund</NavLink>
            <a href="/products/@p.Id">@p.Name</a>
            <a href="@($"/orders/{o.Id}/lines")">Rader</a>
            <a href="@($"/x/{(a ? "b" : "c")}")">Villkor</a>
            <a href="about">Om</a>
            <a href="https://example.com">Extern</a>
            <a href="//cdn.example.com/x">Extern</a>
            <a href="mailto:a@b.se">Post</a>
            <a href="#top">Upp</a>
            <a href="@Url">Helt dynamisk</a>
            @code { }
            """);

        var links = p.Run(new BlazorDiscoverer()).Views.Single().Links;

        Assert.That(links.Select(l => (l.Href, l.Text)), Is.EquivalentTo(new (string, string?)[]
        {
            ("/products", "Produkter"),
            ("/customers/create", "Ny kund"),
            ("/products/{}", null),
            ("/orders/{}/lines", "Rader"),
            ("/x/{}", "Villkor"),
            ("about", "Om"),
        }));
    }

    [Test]
    public void Blazor_requires_auth_from_attribute()
    {
        using var p = new TempProject();
        p.Write("Secret.razor", "@page \"/secret\"\n@attribute [Authorize]\n<h1>Hemligt</h1>");
        p.Write("Qualified.razor", "@page \"/q\"\n@attribute [Microsoft.AspNetCore.Authorization.Authorize(Roles = \"Admin\")]\n<h1>Q</h1>");
        p.Write("Open.razor", "@page \"/open\"\n@attribute [AllowAnonymous]\n<h1>Öppet</h1>");

        var views = p.Run(new BlazorDiscoverer()).Views.ToDictionary(v => v.Route);

        Assert.That(views["/secret"].RequiresAuth, Is.True);
        Assert.That(views["/q"].RequiresAuth, Is.True);
        Assert.That(views["/open"].RequiresAuth, Is.False);
    }

    [Test]
    public void Blazor_ignores_comments_and_code_blocks_when_reading_markup()
    {
        using var p = new TempProject();
        p.Write("C.razor", """
            @page "/c"
            @* <h1>Kommenterad</h1> <a href="/gammal">x</a> *@
            <!-- <a href="/html-kommentar">y</a> -->
            <h1>Riktig</h1>
            @code {
                string Html = "<a href=\"/i-kod\">z</a> } {";
            }
            <a href="/efter-koden">Efter</a>
            """);

        var view = p.Run(new BlazorDiscoverer()).Views.Single();

        Assert.That(view.Heading, Is.EqualTo("Riktig"));
        Assert.That(view.Links.Select(l => l.Href), Is.EqualTo(new[] { "/efter-koden" }));
    }

    [Test]
    public void Blazor_warns_about_unreadable_route_and_continues()
    {
        using var p = new TempProject();
        p.Write("Konstant.razor", "@page @Routes.Start\n<h1>X</h1>");
        p.Write("Bra.razor", "@page \"/bra\"\n<h1>Bra</h1>");

        var catalog = p.Run(new BlazorDiscoverer());

        Assert.That(catalog.Views.Select(v => v.Route), Is.EqualTo(new[] { "/bra" }));
        Assert.That(catalog.Warnings, Has.Count.EqualTo(1));
        Assert.That(catalog.Warnings[0], Does.Contain("Konstant.razor").And.Contain("går inte att läsa"));
    }

    // ---------- Blazor: formulär ----------

    [Test]
    public void Blazor_form_gets_model_from_code_fields_labels_rules_and_submit()
    {
        using var p = new TempProject();
        p.Write("Create.razor", """
            @page "/products/create"
            <EditForm Model="Input" FormName="Create" OnValidSubmit="Save">
                <label for="name">Namn</label>
                <InputText id="name" @bind-Value="Input.Name" />
                <label for="price">Pris</label>
                <InputNumber id="price" @bind-Value="Input.Price" />
                <button type="button">Avbryt</button>
                <button type="submit">Spara</button>
            </EditForm>
            @code {
                [SupplyParameterFromForm]
                ProductInput Input { get; set; } = new();
                public class ProductInput
                {
                    [Required(ErrorMessage = "Namn krävs")]
                    [StringLength(50, MinimumLength = 2, ErrorMessage = "2 till 50")]
                    public string Name { get; set; } = "";
                    [Range(1, 100000)]
                    public decimal Price { get; set; }
                }
            }
            """);

        var form = p.Run(new BlazorDiscoverer()).Views.Single().Forms.Single();

        Assert.That(form.Id, Is.EqualTo("Product"));
        Assert.That(form.Model, Is.EqualTo("ProductInput"));
        Assert.That(form.Submit, Is.EqualTo(new Locator(LocatorKind.Role, "button", "Spara")));
        Assert.That(form.SubmitsTo, Is.Null);
        Assert.That(form.Fields.Select(f => (f.Name, f.Label, f.Type, f.Locator)), Is.EqualTo(new[]
        {
            ("Name", (string?)"Namn", FieldType.Text, new Locator(LocatorKind.Label, "Namn")),
            ("Price", "Pris", FieldType.Number, new Locator(LocatorKind.Label, "Pris")),
        }));
        Assert.That(form.Fields[0].Rules, Is.EquivalentTo(new[]
        {
            new Rule(RuleKind.Required, null, "Namn krävs"),
            new Rule(RuleKind.MaxLength, "50", "2 till 50"),
            new Rule(RuleKind.MinLength, "2", "2 till 50"),
        }));
        Assert.That(form.Fields[1].Rules, Is.EquivalentTo(new[] { new Rule(RuleKind.Min, "1"), new Rule(RuleKind.Max, "100000") }));
    }

    [Test]
    public void Blazor_form_finds_model_in_other_file_from_bind_value_and_strips_model_suffix()
    {
        using var p = new TempProject();
        p.Write("Login.razor", """
            @page "/login"
            <EditForm Model="@model" FormName="login">
                <InputText @bind-Value="credentials.UserName" />
                <button type="submit">Logga in</button>
            </EditForm>
            @code { LoginModel credentials = new(); LoginModel model => credentials; }
            """);
        p.Write("Models/LoginModel.cs", """
            using System.ComponentModel.DataAnnotations;
            namespace App.Models;
            public class LoginModel
            {
                [Required, MaxLength(30)]
                [Display(Name = "Användarnamn")]
                public string UserName { get; set; } = "";
            }
            """);

        var form = p.Run(new BlazorDiscoverer()).Views.Single().Forms.Single();

        Assert.That(form.Model, Is.EqualTo("LoginModel"));
        Assert.That(form.Id, Is.EqualTo("Login"));
        var field = form.Fields.Single();
        Assert.That(field.Label, Is.EqualTo("Användarnamn"));
        Assert.That(field.Locator, Is.EqualTo(new Locator(LocatorKind.Label, "Användarnamn")));
        Assert.That(field.Rules.Select(r => r.Kind), Is.EquivalentTo(new[] { RuleKind.Required, RuleKind.MaxLength }));
    }

    [Test]
    public void Blazor_field_locator_falls_back_to_id_then_name()
    {
        using var p = new TempProject();
        p.Write("L.razor", """
            @page "/l"
            <EditForm Model="Input" FormName="f">
                <InputText id="first" @bind-Value="Input.First" />
                <InputText @bind-Value="Input.Second" />
                <button type="submit">Ok</button>
            </EditForm>
            @code { Data Input { get; set; } = new(); class Data { public string First { get; set; } public string Second { get; set; } } }
            """);

        var fields = p.Run(new BlazorDiscoverer()).Views.Single().Forms.Single().Fields;

        Assert.That(fields[0].Locator, Is.EqualTo(new Locator(LocatorKind.Css, "#first")));
        Assert.That(fields[1].Locator, Is.EqualTo(new Locator(LocatorKind.Css, "[name=\"Input.Second\"]")));
        Assert.That(fields.Select(f => f.Label), Is.EqualTo(new string?[] { null, null }));
    }

    [Test]
    public void Blazor_field_types_come_from_component_type_attribute_and_annotations()
    {
        using var p = new TempProject();
        p.Write("T.razor", """
            @page "/t"
            <EditForm Model="Input" FormName="f">
                <InputText @bind-Value="Input.Mail" />
                <InputText @bind-Value="Input.Phone" />
                <InputText @bind-Value="Input.Site" />
                <InputText @bind-Value="Input.Secret" />
                <InputText type="password" @bind-Value="Input.Plain" />
                <InputDate @bind-Value="Input.Born" />
                <InputDate type="datetime-local" @bind-Value="Input.When" />
                <InputTextArea @bind-Value="Input.Notes" />
                <InputCheckbox @bind-Value="Input.Agree" />
                <InputSelect @bind-Value="Input.Size">
                    <option value="">Välj</option>
                    <option value="s">Liten</option>
                    <option value="l">Stor</option>
                </InputSelect>
                <button type="submit">Ok</button>
            </EditForm>
            @code {
                Data Input { get; set; } = new();
                class Data
                {
                    [EmailAddress] public string Mail { get; set; }
                    [Phone] public string Phone { get; set; }
                    [Url] public string Site { get; set; }
                    [DataType(DataType.Password)] public string Secret { get; set; }
                    public string Plain { get; set; }
                    public DateTime Born { get; set; }
                    public DateTime When { get; set; }
                    public string Notes { get; set; }
                    public bool Agree { get; set; }
                    public string Size { get; set; }
                }
            }
            """);

        var fields = p.Run(new BlazorDiscoverer()).Views.Single().Forms.Single().Fields;

        Assert.That(fields.Select(f => (f.Name, f.Type)), Is.EqualTo(new[]
        {
            ("Mail", FieldType.Email), ("Phone", FieldType.Tel), ("Site", FieldType.Url), ("Secret", FieldType.Password),
            ("Plain", FieldType.Password), ("Born", FieldType.Date), ("When", FieldType.DateTime), ("Notes", FieldType.TextArea),
            ("Agree", FieldType.Checkbox), ("Size", FieldType.Select),
        }));
        Assert.That(fields[^1].Options, Is.EqualTo(new[] { "", "s", "l" }));
        Assert.That(fields[0].Rules, Is.EqualTo(new[] { new Rule(RuleKind.Email) }));
        Assert.That(fields[2].Rules, Is.EqualTo(new[] { new Rule(RuleKind.Url) }));
    }

    [Test]
    public void Blazor_forms_without_model_suffix_or_model_get_numbered_ids()
    {
        using var p = new TempProject();
        p.Write("Two.razor", """
            @page "/two"
            <EditForm Model="Search" FormName="a"><InputText @bind-Value="Search.Text" /><button type="submit">Sök</button></EditForm>
            <EditForm EditContext="Context" FormName="b"><button type="submit">Skicka</button></EditForm>
            @code { Search Search { get; set; } = new(); class Search { public string Text { get; set; } } }
            """);

        var forms = p.Run(new BlazorDiscoverer()).Views.Single().Forms;

        Assert.That(forms.Select(f => f.Id), Is.EqualTo(new[] { "Search", "Form2" }));
        Assert.That(forms[1].Model, Is.Null);
    }

    [Test]
    public void Blazor_warns_when_model_class_is_missing()
    {
        using var p = new TempProject();
        p.Write("M.razor", """
            @page "/m"
            <EditForm Model="Input" FormName="f"><InputText @bind-Value="Input.A" /><button type="submit">Ok</button></EditForm>
            @code { Saknad Input { get; set; } = new(); }
            """);

        var catalog = p.Run(new BlazorDiscoverer());

        Assert.That(catalog.Warnings, Has.Count.EqualTo(1));
        Assert.That(catalog.Warnings[0], Does.Contain("Saknad").And.Contain("hittades inte"));
        var field = catalog.Views.Single().Forms.Single().Fields.Single();
        Assert.That(field.Name, Is.EqualTo("A"));
        Assert.That(field.Rules, Is.Empty);
    }

    // ---------- Regler ----------

    [Test]
    public void Every_annotation_gives_its_rules_with_error_messages()
    {
        using var p = new TempProject();
        p.Write("R.razor", """
            @page "/r"
            <EditForm Model="Input" FormName="f">
                <InputText @bind-Value="Input.A" />
                <InputText @bind-Value="Input.B" />
                <InputText @bind-Value="Input.C" />
                <InputNumber @bind-Value="Input.D" />
                <InputText @bind-Value="Input.E" />
                <InputText @bind-Value="Input.F" />
                <InputText @bind-Value="Input.G" />
                <button type="submit">Ok</button>
            </EditForm>
            @code {
                Data Input { get; set; } = new();
                class Data
                {
                    [StringLength(10)] public string A { get; set; }
                    [MinLength(3, ErrorMessage = "Min tre")] [MaxLength(8, ErrorMessage = "Max åtta")] public string B { get; set; }
                    [RegularExpression(@"^\d{3}$", ErrorMessage = "Tre siffror")] public string C { get; set; }
                    [Range(-5, 5.5)] public double D { get; set; }
                    [Compare(nameof(A), ErrorMessage = "Lika")] public string E { get; set; }
                    [Required][EmailAddress(ErrorMessage = "Ingen e-post")] public string F { get; set; }
                    [Range(typeof(decimal), "1.5", "9.5")] public decimal G { get; set; }
                }
            }
            """);

        var fields = p.Run(new BlazorDiscoverer()).Views.Single().Forms.Single().Fields.ToDictionary(f => f.Name);

        Assert.That(fields["A"].Rules, Is.EqualTo(new[] { new Rule(RuleKind.MaxLength, "10") }));
        Assert.That(fields["B"].Rules, Is.EqualTo(new[] { new Rule(RuleKind.MinLength, "3", "Min tre"), new Rule(RuleKind.MaxLength, "8", "Max åtta") }));
        Assert.That(fields["C"].Rules, Is.EqualTo(new[] { new Rule(RuleKind.Pattern, @"^\d{3}$", "Tre siffror") }));
        Assert.That(fields["D"].Rules, Is.EqualTo(new[] { new Rule(RuleKind.Min, "-5"), new Rule(RuleKind.Max, "5.5") }));
        Assert.That(fields["E"].Rules, Is.EqualTo(new[] { new Rule(RuleKind.Compare, "A", "Lika") }));
        Assert.That(fields["F"].Rules, Is.EqualTo(new[] { new Rule(RuleKind.Required), new Rule(RuleKind.Email, null, "Ingen e-post") }));
        Assert.That(fields["G"].Rules, Is.EqualTo(new[] { new Rule(RuleKind.Min, "1.5"), new Rule(RuleKind.Max, "9.5") }));
    }

    // ---------- Razor Pages ----------

    [Test]
    public void Razor_pages_routes_follow_the_path_under_Pages()
    {
        using var p = new TempProject();
        p.Write("Pages/Index.cshtml", "@page\n<h1>Start</h1>");
        p.Write("Pages/Products/Index.cshtml", "@page\n<h1>Produkter</h1>");
        p.Write("Pages/Products/Details.cshtml", "@page \"{id:int}\"\n<h1>Detaljer</h1>");
        p.Write("Pages/Products/Detail2/Index.cshtml", "@page \"{slug}\"\n<h1>D2</h1>");
        p.Write("Pages/Absolute.cshtml", "@page \"/abs/{x?}\"\n<h1>Abs</h1>");
        p.Write("Pages/Areas.cshtml", "@page\n<h1>X</h1>");
        p.Write("Areas/Admin/Pages/Users.cshtml", "@page\n<h1>Användare</h1>");
        p.Write("Pages/_Layout.cshtml", "@page\n<h1>Layout</h1>");
        p.Write("Pages/Shared/_Partial.cshtml", "@page\n<h1>Delvy</h1>");
        p.Write("Pages/_ViewImports.cshtml", "@using X");
        p.Write("Pages/NoPage.cshtml", "<h1>Ingen @page</h1>");
        p.Write("Views/Home/Index.cshtml", "@page\n<h1>Utanför Pages</h1>");

        var catalog = p.Run(new RazorPagesDiscoverer());

        Assert.That(catalog.Views.Select(v => (v.Route, v.Kind, v.Source)), Is.EqualTo(new[]
        {
            ("/", ViewKind.RazorPage, "Pages/Index.cshtml"),
            ("/Admin/Users", ViewKind.RazorPage, "Areas/Admin/Pages/Users.cshtml"),
            ("/Areas", ViewKind.RazorPage, "Pages/Areas.cshtml"),
            ("/Products", ViewKind.RazorPage, "Pages/Products/Index.cshtml"),
            ("/Products/Detail2/{slug}", ViewKind.RazorPage, "Pages/Products/Detail2/Index.cshtml"),
            ("/Products/Details/{id:int}", ViewKind.RazorPage, "Pages/Products/Details.cshtml"),
            ("/abs/{x?}", ViewKind.RazorPage, "Pages/Absolute.cshtml"),
        }));
        Assert.That(catalog.Views.Single(v => v.Route == "/").Id, Is.EqualTo("ui:/"));
        Assert.That(catalog.Views.Single(v => v.Route == "/Products/Details/{id:int}").Parameters, Is.EqualTo(new[] { new RouteParameter("id", "int", false) }));
    }

    [Test]
    public void Razor_page_title_from_ViewData_and_unreadable_route_warns()
    {
        using var p = new TempProject();
        p.Write("Pages/A.cshtml", "@page\n@{ ViewData[\"Title\"] = \"Titel A\"; }\n<title>Annan</title>\n<h1>A</h1>");
        p.Write("Pages/B.cshtml", "@page\n<html><head><title>Titel B</title></head><body><h1>B</h1></body></html>");
        p.Write("Pages/C.cshtml", "@page @Routes.C\n<h1>C</h1>");

        var catalog = p.Run(new RazorPagesDiscoverer());

        Assert.That(catalog.Views.Select(v => (v.Route, v.Title)), Is.EqualTo(new[] { ("/A", (string?)"Titel A"), ("/B", "Titel B") }));
        Assert.That(catalog.Warnings, Has.Count.EqualTo(1));
        Assert.That(catalog.Warnings[0], Does.Contain("C.cshtml").And.Contain("går inte att läsa"));
    }

    [Test]
    public void Razor_page_form_reads_asp_for_labels_rules_and_submit()
    {
        using var p = new TempProject();
        p.Write("Pages/Contact.cshtml", """
            @page
            @model ContactModel
            <h1>Kontakt</h1>
            <form method="post">
                <label asp-for="Input.Email"></label>
                <input asp-for="Input.Email" />
                <label asp-for="Input.Name"></label>
                <input asp-for="Input.Name" />
                <label asp-for="Input.Message">Ditt meddelande</label>
                <textarea asp-for="Input.Message"></textarea>
                <select asp-for="Input.Topic"><option value="a">A</option><option value="b">B</option></select>
                <input asp-for="Input.Age" />
                <input asp-for="Input.Hidden" />
                <input type="submit" value="Skicka" />
            </form>
            <form method="get"><input name="q" /></form>
            """);
        p.Write("Pages/Contact.cshtml.cs", """
            using System.ComponentModel.DataAnnotations;
            public class ContactModel : PageModel
            {
                [BindProperty] public ContactInput Input { get; set; } = new();
                public class ContactInput
                {
                    [Required(ErrorMessage = "Ange e-post"), EmailAddress]
                    [Display(Name = "E-postadress")]
                    public string Email { get; set; } = "";
                    [StringLength(40, MinimumLength = 3)] public string Name { get; set; } = "";
                    [MaxLength(200)] public string Message { get; set; } = "";
                    public string Topic { get; set; } = "";
                    [Range(18, 99)] public int Age { get; set; }
                    public string Hidden { get; set; } = "";
                }
            }
            """);

        var view = p.Run(new RazorPagesDiscoverer()).Views.Single();
        var form = view.Forms.Single();

        Assert.That(form.Id, Is.EqualTo("Contact"));
        Assert.That(form.Model, Is.EqualTo("ContactInput"));
        Assert.That(form.Submit, Is.EqualTo(new Locator(LocatorKind.Role, "button", "Skicka")));
        Assert.That(form.Fields.Select(f => (f.Name, f.Label, f.Type)), Is.EqualTo(new[]
        {
            ("Email", (string?)"E-postadress", FieldType.Email),
            ("Name", "Name", FieldType.Text),
            ("Message", "Ditt meddelande", FieldType.TextArea),
            ("Topic", null, FieldType.Select),
            ("Age", null, FieldType.Number),
            ("Hidden", null, FieldType.Text),
        }));
        Assert.That(form.Fields[0].Locator, Is.EqualTo(new Locator(LocatorKind.Label, "E-postadress")));
        Assert.That(form.Fields[3].Locator, Is.EqualTo(new Locator(LocatorKind.Css, "#Input_Topic")));
        Assert.That(form.Fields[3].Options, Is.EqualTo(new[] { "a", "b" }));
        Assert.That(form.Fields[0].Rules, Is.EquivalentTo(new[] { new Rule(RuleKind.Required, null, "Ange e-post"), new Rule(RuleKind.Email) }));
        Assert.That(form.Fields[1].Rules, Is.EquivalentTo(new[] { new Rule(RuleKind.MaxLength, "40"), new Rule(RuleKind.MinLength, "3") }));
        Assert.That(form.Fields[4].Rules, Is.EquivalentTo(new[] { new Rule(RuleKind.Min, "18"), new Rule(RuleKind.Max, "99") }));
    }

    [Test]
    public void Razor_page_with_button_submit_and_properties_directly_on_the_page_model()
    {
        using var p = new TempProject();
        p.Write("Pages/Subscribe.cshtml", """
            @page
            @model SubscribeModel
            <form method="post">
                <label for="mail">Din e-post</label>
                <input id="mail" asp-for="Email" />
                <button type="submit">Prenumerera</button>
            </form>
            """);
        p.Write("Pages/Subscribe.cshtml.cs", """
            [Authorize]
            public class SubscribeModel : PageModel
            {
                [BindProperty, Required] public string Email { get; set; } = "";
            }
            """);

        var view = p.Run(new RazorPagesDiscoverer()).Views.Single();
        var form = view.Forms.Single();

        Assert.That(view.RequiresAuth, Is.True);
        Assert.That(form.Id, Is.EqualTo("Subscribe"));
        Assert.That(form.Model, Is.EqualTo("SubscribeModel"));
        Assert.That(form.Submit, Is.EqualTo(new Locator(LocatorKind.Role, "button", "Prenumerera")));
        Assert.That(form.Fields.Single().Locator, Is.EqualTo(new Locator(LocatorKind.Label, "Din e-post")));
        Assert.That(form.Fields.Single().Rules, Is.EqualTo(new[] { new Rule(RuleKind.Required) }));
    }

    [Test]
    public void Razor_pages_with_same_model_name_use_the_class_next_to_each_page()
    {
        using var p = new TempProject();
        foreach (var (folder, rule) in new[] { ("A", "[MaxLength(5)]"), ("B", "[MaxLength(9)]") })
        {
            p.Write($"Pages/{folder}/Index.cshtml", "@page\n@model IndexModel\n<form method=\"post\"><input asp-for=\"Name\" /><button>Spara</button></form>");
            p.Write($"Pages/{folder}/Index.cshtml.cs", $"public class IndexModel : PageModel {{ [BindProperty] {rule} public string Name {{ get; set; }} }}");
        }

        var views = p.Run(new RazorPagesDiscoverer()).Views;

        Assert.That(views.Select(v => v.Forms.Single().Fields.Single().Rules.Single().Value), Is.EqualTo(new[] { "5", "9" }));
    }

    [Test]
    public void Razor_page_warns_when_model_class_is_missing()
    {
        using var p = new TempProject();
        p.Write("Pages/X.cshtml", "@page\n@model XModel\n<form method=\"post\"><input asp-for=\"Name\" /><button>Ok</button></form>");

        var catalog = p.Run(new RazorPagesDiscoverer());

        Assert.That(catalog.Warnings, Has.Count.EqualTo(1));
        Assert.That(catalog.Warnings[0], Does.Contain("XModel").And.Contain("hittades inte"));
        Assert.That(catalog.Views.Single().Forms.Single().Fields.Single().Name, Is.EqualTo("Name"));
    }

    // ---------- Länkar i delade layouter ----------

    private const string Menu = """
        @inherits LayoutComponentBase
        <nav><NavLink href="/">Start</NavLink> <a href="/about">Om oss</a></nav>
        <main>@Body</main>
        """;

    private static string[] Hrefs(ViewCatalog catalog, string id) => catalog.Views.Single(v => TestViews.Name(v) == id).Links.Select(l => l.Href).ToArray();

    [Test]
    public void Blazor_default_layout_from_RouteView_adds_its_links_to_every_page()
    {
        using var p = new TempProject();
        p.Write("Components/Routes.razor", "<Router AppAssembly=\"typeof(Program).Assembly\"><Found Context=\"r\"><RouteView RouteData=\"r\" DefaultLayout=\"typeof(Layout.MainLayout)\" /></Found></Router>");
        p.Write("Components/Layout/MainLayout.razor", Menu);
        p.Write("Components/Pages/A.razor", "@page \"/a\"\n<a href=\"/b\">B</a>");
        p.Write("Components/Pages/B.razor", "@page \"/b\"\n<h1>B</h1>");

        var catalog = p.Run(new BlazorDiscoverer());

        // Sidans egen länk först, sedan menyn; en länk som redan finns blir en.
        Assert.That(Hrefs(catalog, "A"), Is.EqualTo(new[] { "/", "/about", "/b" }));
        Assert.That(Hrefs(catalog, "B"), Is.EqualTo(new[] { "/", "/about" }));
        Assert.That(catalog.Views.Single(v => TestViews.Name(v) == "B").Links.Single(l => l.Href == "/").Text, Is.EqualTo("Start"));
    }

    [Test]
    public void Blazor_layout_links_come_from_components_one_step_down_only()
    {
        using var p = new TempProject();
        p.Write("Routes.razor", "<RouteView RouteData=\"r\" DefaultLayout=\"typeof(MainLayout)\" />");
        p.Write("MainLayout.razor", "@inherits LayoutComponentBase\n<NavMenu />\n@Body");
        p.Write("NavMenu.razor", "<NavLink href=\"/menu\">Meny</NavLink>\n<Deep />");
        p.Write("Deep.razor", "<a href=\"/too-deep\">Djupt</a>");
        p.Write("A.razor", "@page \"/a\"");

        Assert.That(Hrefs(p.Run(new BlazorDiscoverer()), "A"), Is.EqualTo(new[] { "/menu" }));
    }

    [Test]
    public void Blazor_page_layout_wins_over_imports_and_imports_win_over_the_default()
    {
        using var p = new TempProject();
        p.Write("Routes.razor", "<RouteView RouteData=\"r\" DefaultLayout=\"typeof(MainLayout)\" />");
        p.Write("MainLayout.razor", "@inherits LayoutComponentBase\n<a href=\"/main\">Main</a>\n@Body");
        p.Write("AdminLayout.razor", "@inherits LayoutComponentBase\n<a href=\"/admin-menu\">Admin</a>\n@Body");
        p.Write("OtherLayout.razor", "@inherits LayoutComponentBase\n<a href=\"/other\">Annan</a>\n@Body");
        p.Write("Pages/Plain.razor", "@page \"/plain\"");
        p.Write("Pages/Admin/_Imports.razor", "@layout AdminLayout");
        p.Write("Pages/Admin/Users.razor", "@page \"/admin/users\"");
        p.Write("Pages/Admin/Own.razor", "@page \"/admin/own\"\n@layout OtherLayout");
        p.Write("Pages/Admin/Bare.razor", "@page \"/admin/bare\"\n@layout null");
        p.Write("Pages/Admin/Deep/Page.razor", "@page \"/admin/deep\"");

        var catalog = p.Run(new BlazorDiscoverer());

        Assert.That(Hrefs(catalog, "Plain"), Is.EqualTo(new[] { "/main" }));
        Assert.That(Hrefs(catalog, "AdminUsers"), Is.EqualTo(new[] { "/admin-menu" }));
        Assert.That(Hrefs(catalog, "AdminOwn"), Is.EqualTo(new[] { "/other" }));
        Assert.That(Hrefs(catalog, "AdminBare"), Is.Empty);
        Assert.That(Hrefs(catalog, "AdminDeep"), Is.EqualTo(new[] { "/admin-menu" }));
    }

    [Test]
    public void Blazor_without_a_default_layout_gives_no_layout_links()
    {
        using var p = new TempProject();
        p.Write("MainLayout.razor", Menu);
        p.Write("A.razor", "@page \"/a\"");

        Assert.That(Hrefs(p.Run(new BlazorDiscoverer()), "A"), Is.Empty);
    }

    [Test]
    public void Razor_pages_layout_and_its_partials_give_links_to_all_pages_in_the_project()
    {
        using var p = new TempProject();
        p.Write("Pages/_ViewStart.cshtml", "@{ Layout = \"_Layout\"; }");
        p.Write("Pages/Shared/_Layout.cshtml", "<nav><a href=\"/\">Hem</a> <partial name=\"_Nav\" /></nav>\n@RenderBody()");
        p.Write("Pages/Shared/_Nav.cshtml", "<a href=\"/privacy\">Integritet</a> <partial name=\"_Deep\" />");
        p.Write("Pages/Shared/_Deep.cshtml", "<a href=\"/too-deep\">Djupt</a>");
        p.Write("Pages/Index.cshtml", "@page\n<h1>Hem</h1>");
        p.Write("Pages/Privacy.cshtml", "@page\n<h1>Integritet</h1><a href=\"/\">Tillbaka</a>");
        p.Write("Pages/Bare.cshtml", "@page\n@{ Layout = null; }\n<h1>Utan layout</h1>");

        var catalog = p.Run(new RazorPagesDiscoverer());

        Assert.That(Hrefs(catalog, "Home"), Is.EqualTo(new[] { "/", "/privacy" }));
        Assert.That(Hrefs(catalog, "Privacy"), Is.EqualTo(new[] { "/", "/privacy" }));
        Assert.That(catalog.Views.Single(v => TestViews.Name(v) == "Privacy").Links.Single(l => l.Href == "/").Text, Is.EqualTo("Tillbaka"));
        Assert.That(Hrefs(catalog, "Bare"), Is.Empty);
        Assert.That(catalog.Views.Select(v => v.Route), Does.Not.Contain("/Shared/_Layout"));
    }

    [Test]
    public void Razor_pages_without_a_ViewStart_get_no_layout_links_and_the_closest_ViewStart_wins()
    {
        using var p = new TempProject();
        p.Write("Pages/Shared/_Layout.cshtml", "<a href=\"/one\">Ett</a>");
        p.Write("Pages/Shared/_AdminLayout.cshtml", "<a href=\"/two\">Två</a>");
        p.Write("Pages/A.cshtml", "@page\n<h1>A</h1>");
        Assert.That(Hrefs(p.Run(new RazorPagesDiscoverer()), "A"), Is.Empty);

        p.Write("Pages/_ViewStart.cshtml", "@{ Layout = \"_Layout\"; }");
        p.Write("Pages/Admin/_ViewStart.cshtml", "@{ Layout = \"~/Pages/Shared/_AdminLayout.cshtml\"; }");
        p.Write("Pages/Admin/Users.cshtml", "@page\n<h1>Användare</h1>");
        var catalog = p.Run(new RazorPagesDiscoverer());
        Assert.That(Hrefs(catalog, "A"), Is.EqualTo(new[] { "/one" }));
        Assert.That(Hrefs(catalog, "AdminUsers"), Is.EqualTo(new[] { "/two" }));
    }

    [Test]
    public void Razor_pages_asp_page_links_get_the_route_of_the_page()
    {
        using var p = new TempProject();
        p.Write("Pages/_ViewStart.cshtml", "@{ Layout = \"_Layout\"; }");
        p.Write("Pages/Shared/_Layout.cshtml", """
            <nav>
              <a class="navbar-brand" asp-area="" asp-page="/Index">Hem</a>
              <a asp-page="/Privacy">Integritet</a>
              <a asp-page="/Products/Details" asp-route-id="5">Första</a>
              <a asp-page="/Products/Index">Produkter</a>
              <a asp-page="/Missing" asp-route-id="1">Saknas</a>
              <a asp-page="@Model.Target">Dynamisk</a>
            </nav>
            @RenderBody()
            """);
        p.Write("Pages/Index.cshtml", "@page\n<h1>Hem</h1>");
        p.Write("Pages/Privacy.cshtml", "@page\n<h1>Integritet</h1>");
        p.Write("Pages/Products/Index.cshtml", "@page\n<h1>Produkter</h1><a asp-page=\"Details\" asp-route-id=\"2\">Relativ</a><a asp-page=\"../Privacy\">Upp</a>");
        p.Write("Pages/Products/Details.cshtml", "@page \"{id:int}\"\n<h1>Produkt</h1>");

        var catalog = p.Run(new RazorPagesDiscoverer());

        Assert.That(Hrefs(catalog, "Home"), Is.EqualTo(new[] { "/", "/Missing/{}", "/Privacy", "/Products", "/Products/Details/{}" }));
        Assert.That(Hrefs(catalog, "Products"), Is.EqualTo(new[] { "/", "/Missing/{}", "/Privacy", "/Products", "/Products/Details/{}" }));
        Assert.That(catalog.Views.Single(v => TestViews.Name(v) == "Home").Links.Single(l => l.Href == "/").Text, Is.EqualTo("Hem"));
        Assert.That(catalog.Views.Single(v => TestViews.Name(v) == "Home").Links.Single(l => l.Href == "/Products/Details/{}").Text, Is.EqualTo("Första"));
    }

    [Test]
    public void Razor_pages_asp_area_and_unknown_pages_give_addresses_from_the_name()
    {
        using var p = new TempProject();
        p.Write("Areas/Admin/Pages/Users/Index.cshtml", "@page\n<a asp-page=\"/Settings\" asp-area=\"Admin\">Inställningar</a>");
        p.Write("Areas/Admin/Pages/Settings.cshtml", "@page\n<h1>Inställningar</h1>");

        var catalog = p.Run(new RazorPagesDiscoverer());

        Assert.That(catalog.Views.Select(v => v.Route), Is.EqualTo(new[] { "/Admin/Settings", "/Admin/Users" }));
        Assert.That(Hrefs(catalog, "AdminUsers"), Is.EqualTo(new[] { "/Admin/Settings" }));
    }

    [Test]
    public void Razor_pages_page_gets_links_from_its_own_partial_one_step_down()
    {
        using var p = new TempProject();
        p.Write("Pages/Shared/_Promo.cshtml", "<a href=\"/promo\">Erbjudande</a> <a asp-page=\"/Index\">Hem</a> <partial name=\"_Deep\" />");
        p.Write("Pages/Shared/_Deep.cshtml", "<a href=\"/too-deep\">Djupt</a>");
        p.Write("Pages/Index.cshtml", "@page\n<h1>Hem</h1><partial name=\"_Promo\" /><a href=\"/egen\">Egen</a>");

        var catalog = p.Run(new RazorPagesDiscoverer());

        // Sidans egen länk först, därefter partialen.
        Assert.That(catalog.Views.Single().Links.Select(l => l.Href), Is.EqualTo(new[] { "/", "/egen", "/promo" }));
    }

    [Test]
    public void Blazor_page_gets_links_from_its_own_component_one_step_down()
    {
        using var p = new TempProject();
        p.Write("NavMenu.razor", "<NavLink href=\"/menu\">Meny</NavLink>\n<Deep />");
        p.Write("Deep.razor", "<a href=\"/too-deep\">Djupt</a>");
        p.Write("A.razor", "@page \"/a\"\n<NavMenu />\n<a href=\"/b\">B</a>");

        var catalog = p.Run(new BlazorDiscoverer());

        Assert.That(Hrefs(catalog, "A"), Is.EqualTo(new[] { "/b", "/menu" }));
    }

    [Test]
    public void Mvc_and_Razor_pages_share_layout_links_with_tag_helpers_resolved_per_view()
    {
        using var p = new TempProject();
        p.Write("Views/_ViewStart.cshtml", "@{ Layout = \"_Layout\"; }");
        p.Write("Views/Shared/_Layout.cshtml", "<a asp-action=\"Index\">Start</a> <a asp-page=\"/Index\">Sida</a> <partial name=\"_Menu\" />");
        p.Write("Views/Shared/_Menu.cshtml", "<a href=\"/menu\">Meny</a>");
        p.Write("Controllers/HomeController.cs", "public class HomeController : Controller { public IActionResult Index() => View(); }");
        p.Write("Controllers/ShopController.cs", "public class ShopController : Controller { public IActionResult Index() => View(); }");
        p.Write("Views/Home/Index.cshtml", "<h1>Hem</h1>");
        p.Write("Views/Shop/Index.cshtml", "<h1>Butik</h1>");

        var catalog = p.Run(new MvcDiscoverer(), new RazorPagesDiscoverer());

        // asp-action utan controller gäller vyns egen controller, asp-page hör till Razor Pages och ger ingen länk i MVC.
        Assert.That(Hrefs(catalog, "Home"), Is.EqualTo(new[] { "/", "/menu" }));
        Assert.That(Hrefs(catalog, "Shop"), Is.EqualTo(new[] { "/Shop", "/menu" }));
    }

    // ---------- Taggskanning ----------

    [Test]
    public void Markup_matches_nested_elements_with_the_same_name()
    {
        var doc = new MarkupDoc("<div id=\"a\"><div id=\"b\"><b>x</b> y</div> z</div><div id=\"c\">w</div>", RazorMarkupRules.Instance);
        var a = doc.Tags.Single(t => t.Is("div") && t["id"] == "a");
        var b = doc.Tags.Single(t => t.Is("div") && t["id"] == "b");
        var c = doc.Tags.Single(t => t.Is("div") && t["id"] == "c");

        Assert.That(doc.FixedText(a), Is.EqualTo("x y z"));
        Assert.That(doc.FixedText(b), Is.EqualTo("x y"));
        Assert.That(doc.FixedText(c), Is.EqualTo("w"));
        Assert.That(doc.Inside(a).Select(t => t.Name), Is.EqualTo(new[] { "div", "b", "b", "div" }));
        Assert.That(doc.Inside(c), Is.Empty);
        Assert.That(doc.Text[doc.CloseStart(b)..].StartsWith("</div> z", StringComparison.Ordinal), Is.True);
    }

    [Test]
    public void Markup_elements_without_an_end_tag_run_to_the_end_and_self_closing_ones_are_empty()
    {
        var doc = new MarkupDoc("<select><option value=\"1\">Ett<option>Två</select><br /><input name=\"q\">", RazorMarkupRules.Instance);
        var select = doc.Tags.Single(t => t.Is("select"));

        Assert.That(doc.Inside(select).Count(t => t.Is("option")), Is.EqualTo(2));
        Assert.That(doc.Inside(doc.Tags.Single(t => t.Is("br"))), Is.Empty);
        Assert.That(doc.CloseStart(doc.Tags.Single(t => t.Is("input"))), Is.EqualTo(doc.Text.Length));
        Assert.That(doc.TextUntilNextTag(doc.Tags.First(t => t.Is("option"))), Is.EqualTo("Ett"));
    }

    [Test]
    public void Markup_rules_decide_what_is_dynamic_and_how_values_are_read()
    {
        var razor = new MarkupDoc("<a href=\"@($\"/p/{id}\")\" title='x'>Hej @name</a><p>Fast</p>", RazorMarkupRules.Instance);
        Assert.That(razor.Tags[0]["href"], Is.EqualTo("@($\"/p/{id}\")"));
        Assert.That(razor.FixedText(razor.Tags[0]), Is.Null);
        Assert.That(razor.Fixed(razor.Tags[0], "title"), Is.EqualTo("x"));
        Assert.That(razor.Fixed(razor.Tags[0], "href"), Is.Null);

        var spa = new MarkupDoc("<!-- <b>dold</b> --><Link {...props} to={`/p/${id}`} id={'x'}>Hej {name}</Link><h1>{{ titel }}</h1><i>Fast</i>", TestGen.Views.Discovery.Spa.SpaMarkupRules.Instance);
        Assert.That(spa.Tags.Select(t => t.Name), Is.EqualTo(new[] { "Link", "Link", "h1", "h1", "i", "i" }));
        Assert.That(spa.Fixed(spa.Tags[0], "id"), Is.EqualTo("x"));
        Assert.That(spa.Fixed(spa.Tags[0], "to"), Is.Null);
        Assert.That(spa.FixedText(spa.Tags[0]), Is.Null);
        Assert.That(spa.FixedText(spa.Tags.Single(t => t.Is("h1"))), Is.Null);
        Assert.That(spa.FixedText(spa.Tags.Single(t => t.Is("i"))), Is.EqualTo("Fast"));
    }

    [Test]
    public void Markup_ignores_a_less_than_sign_that_does_not_start_a_tag()
    {
        var doc = new MarkupDoc("<p>1 < 2 och <b>fet</b></p>", RazorMarkupRules.Instance);

        Assert.That(doc.Tags.Select(t => t.Name), Is.EqualTo(new[] { "p", "b", "b", "p" }));
        Assert.That(doc.FixedText(doc.Tags[0]), Is.EqualTo("1 < 2 och fet"));
    }

    // ---------- Exemplet ShopWeb ----------

    [Test]
    public void ShopWeb_catalog_has_expected_views_forms_fields_and_rules()
    {
        var catalog = DiscoverShopWeb();

        Assert.That(catalog.Warnings, Is.Empty);
        Assert.That(catalog.Views.Select(v => (v.Id, v.Route, v.Kind)), Is.EqualTo(new[]
        {
            ("ui:/", "/", ViewKind.Blazor),
            ("ui:/about", "/about", ViewKind.RazorPage),
            ("ui:/customers/create", "/customers/create", ViewKind.Blazor),
            ("ui:/products", "/products", ViewKind.Blazor),
            ("ui:/products/create", "/products/create", ViewKind.Blazor),
            ("ui:/products/{id:int}", "/products/{id:int}", ViewKind.Blazor),
        }));

        var home = catalog.Views.Single(v => TestViews.Name(v) == "Home");
        Assert.That(home.Heading, Is.EqualTo("Välkommen till webbshopen"));
        Assert.That(home.Links.Select(l => l.Href), Is.EqualTo(new[] { "/", "/about", "/customers/create", "/products" }));

        var products = catalog.Views.Single(v => TestViews.Name(v) == "Products");
        Assert.That(products.Links.Select(l => l.Href), Is.EqualTo(new[] { "/", "/about", "/customers/create", "/products", "/products/create", "/products/{}" }));

        var details = catalog.Views.Single(v => TestViews.Name(v) == "ProductsId");
        Assert.That(details.Parameters, Is.EqualTo(new[] { new RouteParameter("id", "int", false) }));
        Assert.That(details.Source, Is.EqualTo("Components/Pages/ProductDetails.razor"));

        var create = catalog.Views.Single(v => TestViews.Name(v) == "ProductsCreate").Forms.Single();
        Assert.That(create.Id, Is.EqualTo("Product"));
        Assert.That(create.Model, Is.EqualTo("ProductInput"));
        Assert.That(create.Submit, Is.EqualTo(new Locator(LocatorKind.Role, "button", "Spara produkt")));
        Assert.That(create.Fields.Select(f => (f.Name, f.Label, f.Type)), Is.EqualTo(new[]
        {
            ("Name", (string?)"Namn", FieldType.Text), ("Sku", "Artikelkod", FieldType.Text), ("Price", "Pris", FieldType.Number),
        }));
        Assert.That(create.Fields[0].Rules.Select(r => (r.Kind, r.Value)), Is.EqualTo(new (RuleKind, string?)[]
        {
            (RuleKind.Required, null), (RuleKind.MinLength, "2"), (RuleKind.MaxLength, "50"),
        }));
        Assert.That(create.Fields[1].Rules.Select(r => r.Kind), Is.EqualTo(new[] { RuleKind.Required, RuleKind.Pattern }));
        Assert.That(create.Fields[2].Rules.Select(r => (r.Kind, r.Value)), Is.EqualTo(new (RuleKind, string?)[] { (RuleKind.Min, "1"), (RuleKind.Max, "100000") }));
        Assert.That(create.Fields[0].Rules.All(r => !string.IsNullOrEmpty(r.Message)), Is.True);

        var customer = catalog.Views.Single(v => TestViews.Name(v) == "CustomersCreate").Forms.Single();
        Assert.That(customer.Id, Is.EqualTo("Customer"));
        Assert.That(customer.Fields.Select(f => (f.Name, f.Type)), Is.EqualTo(new[]
        {
            ("Name", FieldType.Text), ("Email", FieldType.Email), ("Phone", FieldType.Tel),
        }));
        Assert.That(customer.Fields[1].Rules.Select(r => r.Kind), Is.EqualTo(new[] { RuleKind.Required, RuleKind.Email }));

        // About har Layout = null och får inga menylänkar. Menyn i MainLayout ger de andra vyerna länkar.
        var about = catalog.Views.Single(v => TestViews.Name(v) == "About");
        Assert.That(about.Links.Select(l => l.Href), Is.EqualTo(new[] { "/" }));
        Assert.That(about.Title, Is.EqualTo("Om oss"));
        Assert.That(about.Heading, Is.EqualTo("Kontakta oss"));
        var contact = about.Forms.Single();
        Assert.That(contact.Id, Is.EqualTo("Contact"));
        Assert.That(contact.Submit, Is.EqualTo(new Locator(LocatorKind.Role, "button", "Skicka meddelande")));
        Assert.That(contact.Fields.Select(f => (f.Name, f.Label, f.Type)), Is.EqualTo(new[]
        {
            ("Name", (string?)"Ditt namn", FieldType.Text), ("Email", "E-post", FieldType.Email), ("Message", "Meddelande", FieldType.TextArea),
        }));
    }

    [Test]
    public void ShopWeb_two_runs_give_identical_json()
    {
        var first = ViewsJson.Serialize(ViewsFile.Of([new AppViews("App", DiscoverShopWeb().Views, [])]));
        var second = ViewsJson.Serialize(ViewsFile.Of([new AppViews("App", DiscoverShopWeb().Views, [])]));

        Assert.That(second, Is.EqualTo(first));
        Assert.That(first, Does.Not.Contain(FindRoot()));
    }

    private static ViewCatalog DiscoverShopWeb()
    {
        var (root, directories) = TestRoots.From(Path.Combine(FindRoot(), "samples", "ShopWeb", "ShopWeb.csproj"));
        return ViewDiscovery.Run(new DiscoveryContext(root, directories, []), Discoverers.All());
    }

    // Går uppåt till mappen med .git, som systemmaps tester gör.
    private static string FindRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, ".git")) || File.Exists(Path.Combine(d.FullName, ".git"))) return d.FullName;
        throw new FileNotFoundException("Hittade inte repots rot (mappen med .git).");
    }

    /// <summary>En tillfällig mapp med källfiler som tas bort efteråt.</summary>
    private sealed class TempProject : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("views-").FullName;

        public void Write(string relativePath, string content)
        {
            var path = Path.Combine(_root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public ViewCatalog Run(params IViewDiscoverer[] discoverers)
        {
            var (root, directories) = TestRoots.From(_root);
            return ViewDiscovery.Run(new DiscoveryContext(root, directories, []), discoverers);
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
