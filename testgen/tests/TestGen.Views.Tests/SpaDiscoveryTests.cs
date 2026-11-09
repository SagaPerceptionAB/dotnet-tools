using TestGen.Core.Views;
using TestGen.Views.Discovery;
using TestGen.Views.Discovery.Spa;

namespace TestGen.Views.Tests;

public class SpaDiscoveryTests
{
    // ---------- React ----------

    [Test]
    public void React_routes_come_from_both_object_form_and_jsx()
    {
        var catalog = Discover("react");

        Assert.That(catalog.Views.Select(v => (v.Route, v.Source)), Is.EqualTo(new[]
        {
            ("/", "src/pages/Home.tsx"),
            ("/about", "src/pages/About.tsx"),
            ("/contact", "src/pages/Contact.tsx"),
            ("/help", "src/pages/Help.tsx"),
            ("/help/faq", "src/pages/Faq.tsx"),
            ("/login", "src/pages/Login.tsx"),
            ("/products", "src/pages/Products.tsx"),
            ("/products/:id", "src/pages/ProductDetail.tsx"),
            ("/products/new", "src/pages/ProductNew.tsx"),
            ("/reports", "src/pages/Reports.tsx"),
            ("/settings", "src/pages/Settings.tsx"),
        }));
        Assert.That(catalog.Views.All(v => v.Kind == ViewKind.Spa), Is.True);
    }

    [Test]
    public void React_wildcard_layout_and_unreadable_routes_are_skipped_with_a_warning()
    {
        var catalog = Discover("react");

        Assert.That(catalog.Views.Select(v => v.Route), Does.Not.Contain("/*").And.Not.Contain("/gamla-sidan"));
        Assert.That(catalog.Warnings, Has.Count.EqualTo(1));
        Assert.That(catalog.Warnings[0], Does.Contain("src/main.tsx").And.Contain("LEGACY_PATH"));
    }

    [Test]
    public void React_parameters_ids_titles_and_headings()
    {
        var views = Discover("react").Views.ToDictionary(v => v.Route);

        Assert.That(views["/products/:id"].Id, Is.EqualTo("ui:/products/:id"));
        Assert.That(views["/products/:id"].Parameters, Is.EqualTo(new[] { new RouteParameter("id", null, false) }));
        Assert.That(views["/products/:id"].Heading, Is.Null, "rubriken är {product?.name}");
        Assert.That(views["/"].Id, Is.EqualTo("ui:/"));
        Assert.That(views["/"].Title, Is.EqualTo("Butiken"));
        Assert.That(views["/"].Heading, Is.EqualTo("Välkommen till butiken"));
        Assert.That(views["/about"].Title, Is.EqualTo("Om butiken"), "<title> i markupen");
        Assert.That(views["/login"].Title, Is.EqualTo("Logga in"));
    }

    [Test]
    public void React_links_keep_internal_addresses_and_turn_template_holes_into_braces()
    {
        var views = Discover("react").Views.ToDictionary(v => v.Route);

        Assert.That(views["/"].Links, Is.EqualTo(new[]
        {
            new Link("/about", "Om butiken"), new Link("/products", "Se alla produkter"), new Link("/reports", "Rapporter"),
        }), "den externa länken ska vara bortvald");
        Assert.That(views["/products"].Links, Is.EqualTo(new[] { new Link("/products/new", "Ny produkt"), new Link("/products/{}", null) }));
    }

    [Test]
    public void React_form_has_fields_labels_rules_options_and_submit()
    {
        var form = Discover("react").Views.Single(v => v.Route == "/products/new").Forms.Single();

        Assert.That(form.Submit, Is.EqualTo(new Locator(LocatorKind.Role, "button", "Spara produkt")));
        Assert.That(form.Fields.Select(f => (f.Name, f.Label, f.Type)), Is.EqualTo(new[]
        {
            ("name", "Namn", FieldType.Text), ("price", "Pris", FieldType.Number),
            ("category", "Kategori", FieldType.Select), ("description", "Beskrivning", FieldType.TextArea),
        }));
        Assert.That(form.Fields[0].Locator, Is.EqualTo(new Locator(LocatorKind.Label, "Namn")));
        Assert.That(form.Fields[0].Rules, Is.EqualTo(new[]
        {
            new Rule(RuleKind.Required), new Rule(RuleKind.MinLength, "3"), new Rule(RuleKind.MaxLength, "50"),
        }));
        Assert.That(form.Fields[1].Rules, Is.EqualTo(new[] { new Rule(RuleKind.Required), new Rule(RuleKind.Min, "0"), new Rule(RuleKind.Max, "10000") }));
        Assert.That(form.Fields[2].Options, Is.EqualTo(new[] { "books", "games" }), "tomt värde hoppas över");
        Assert.That(form.Fields[3].Label, Is.EqualTo("Beskrivning"), "etikett som omsluter fältet");
        Assert.That(form.Fields[3].Rules, Is.EqualTo(new[] { new Rule(RuleKind.MaxLength, "500") }));
        Assert.That(form.SubmitsTo, Is.EqualTo(new ApiCall("POST", "/api/products")));
    }

    [Test]
    public void React_api_calls_get_method_from_the_call_and_holes_become_braces()
    {
        var views = Discover("react").Views.ToDictionary(v => v.Route);

        Assert.That(views["/products"].ApiCalls, Is.EqualTo(new[] { new ApiCall("GET", "/api/products") }));
        Assert.That(views["/products/:id"].ApiCalls, Is.EqualTo(new[] { new ApiCall("DELETE", "/api/products/{}"), new ApiCall("GET", "/api/products/{}") }));
        Assert.That(views["/products/new"].ApiCalls, Is.EqualTo(new[] { new ApiCall("POST", "/api/products") }));
        Assert.That(views["/reports"].ApiCalls, Is.EqualTo(new[] { new ApiCall("GET", "/api/reports/sales") }), "frågesträngen tas bort");
    }

    [Test]
    public void React_auth_wrapper_makes_the_route_require_auth_and_lazy_components_are_followed()
    {
        var views = Discover("react").Views.ToDictionary(v => v.Route);

        Assert.That(views.Values.Where(v => v.RequiresAuth).Select(v => v.Route), Is.EqualTo(new[] { "/products/new" }));
        Assert.That(views["/reports"].Source, Is.EqualTo("src/pages/Reports.tsx"), "React.lazy via const");
        Assert.That(views["/settings"].Source, Is.EqualTo("src/pages/Settings.tsx"), "lazy: () => import(...)");
    }

    // ---------- Angular ----------

    [Test]
    public void Angular_routes_follow_loadChildren_and_skip_redirects_and_wildcards()
    {
        var catalog = Discover("angular");

        Assert.That(catalog.Views.Select(v => (v.Route, v.Source)), Is.EqualTo(new[]
        {
            ("/", "src/app/home.component.ts"),
            ("/account", "src/app/account/profile.component.ts"),
            ("/account/settings", "src/app/account/settings.component.ts"),
            ("/login", "src/app/account/login.component.ts"),
            ("/products", "src/app/products/product-list.component.ts"),
            ("/products/:id", "src/app/products/product-detail.component.ts"),
            ("/products/:id/edit", "src/app/products/product-form.component.ts"),
            ("/products/new", "src/app/products/product-form.component.ts"),
        }));
        Assert.That(catalog.Warnings, Is.Empty);
        Assert.That(catalog.Views.Single(v => v.Route == "/products/:id/edit").Parameters, Is.EqualTo(new[] { new RouteParameter("id", null, false) }));
    }

    [Test]
    public void Angular_canActivate_is_inherited_by_children_and_applies_to_single_routes()
    {
        var views = Discover("angular").Views;

        Assert.That(views.Where(v => v.RequiresAuth).Select(v => v.Route),
            Is.EqualTo(new[] { "/account", "/account/settings", "/products/:id/edit", "/products/new" }));
    }

    [Test]
    public void Angular_titles_headings_and_links_come_from_templates()
    {
        var views = Discover("angular").Views.ToDictionary(v => v.Route);

        Assert.That(views["/"].Title, Is.EqualTo("Start"), "Title.setTitle");
        Assert.That(views["/"].Heading, Is.EqualTo("Välkommen"));
        Assert.That(views["/"].Links, Is.EqualTo(new[]
        {
            new Link("/login", "Logga in"), new Link("/products", "Alla produkter"), new Link("/products/{}", "Veckans produkt"),
        }));
        Assert.That(views["/products/:id"].Heading, Is.Null, "rubriken är {{ ... }}");
        Assert.That(views["/products/:id"].Links, Is.EqualTo(new[] { new Link("/products", "Tillbaka"), new Link("/products/{}/edit", "Redigera") }));
        Assert.That(views["/login"].Heading, Is.EqualTo("Logga in"), "inline template");
    }

    [Test]
    public void Angular_reactive_form_gets_rules_from_validators_in_the_component()
    {
        var form = Discover("angular").Views.Single(v => v.Route == "/products/new").Forms.Single();

        Assert.That(form.Submit, Is.EqualTo(new Locator(LocatorKind.Role, "button", "Spara")));
        Assert.That(form.Fields.Select(f => (f.Name, f.Label)), Is.EqualTo(new[]
        {
            ("name", "Namn"), ("sku", "Artikelnummer"), ("price", "Pris"), ("contact", "Kontakt"),
        }));
        Assert.That(form.Fields[0].Rules, Is.EqualTo(new[] { new Rule(RuleKind.Required), new Rule(RuleKind.MinLength, "3"), new Rule(RuleKind.MaxLength, "60") }));
        Assert.That(form.Fields[1].Rules, Is.EqualTo(new[] { new Rule(RuleKind.Required), new Rule(RuleKind.Pattern, @"^[A-Z]{3}-\d{4}$") }));
        Assert.That(form.Fields[2].Rules, Is.EqualTo(new[] { new Rule(RuleKind.Required), new Rule(RuleKind.Min, "0"), new Rule(RuleKind.Max, "100000") }));
        Assert.That(form.Fields[2].Type, Is.EqualTo(FieldType.Number));
        Assert.That(form.Fields[3].Rules, Is.EqualTo(new[] { new Rule(RuleKind.Email) }), "new FormControl('', [Validators.email])");
    }

    [Test]
    public void Angular_inline_template_form_and_http_calls_in_component_and_service()
    {
        var views = Discover("angular").Views.ToDictionary(v => v.Route);

        var login = views["/login"];
        Assert.That(login.Forms.Single().Fields[0].Rules, Is.EqualTo(new[] { new Rule(RuleKind.Required), new Rule(RuleKind.Email) }));
        Assert.That(login.Forms.Single().Fields[1].Rules, Is.EqualTo(new[] { new Rule(RuleKind.Required), new Rule(RuleKind.MinLength, "8") }));
        Assert.That(login.Forms.Single().SubmitsTo, Is.EqualTo(new ApiCall("POST", "/api/auth/login")));
        Assert.That(views["/products"].ApiCalls, Is.EqualTo(new[] { new ApiCall("GET", "/api/products") }), "http.get<T>(...)");
        Assert.That(views["/products/:id"].ApiCalls, Is.EqualTo(new[]
        {
            new ApiCall("GET", "/api/products"), new ApiCall("POST", "/api/products"),
            new ApiCall("DELETE", "/api/products/{}"), new ApiCall("GET", "/api/products/{}"), new ApiCall("PUT", "/api/products/{}"),
        }), "tjänsten som komponenten importerar");
    }

    // ---------- Vue ----------

    [Test]
    public void Vue_routes_merge_relative_children_and_skip_redirect_and_catch_all()
    {
        var catalog = Discover("vue");

        Assert.That(catalog.Views.Select(v => (v.Route, v.Source)), Is.EqualTo(new[]
        {
            ("/", "src/views/HomeView.vue"),
            ("/about", "src/views/AboutView.vue"),
            ("/admin", "src/views/AdminHome.vue"),
            ("/admin/users", "src/views/AdminUsers.vue"),
            ("/login", "src/views/LoginView.vue"),
            ("/products", "src/views/ProductsView.vue"),
            ("/products/:id", "src/views/ProductDetailView.vue"),
        }));
        Assert.That(catalog.Warnings, Is.Empty);
    }

    [Test]
    public void Vue_meta_requiresAuth_is_inherited_by_children()
    {
        var views = Discover("vue").Views;

        Assert.That(views.Where(v => v.RequiresAuth).Select(v => v.Route), Is.EqualTo(new[] { "/admin", "/admin/users" }));
    }

    [Test]
    public void Vue_template_gives_heading_links_form_and_script_gives_title_and_api_calls()
    {
        var views = Discover("vue").Views.ToDictionary(v => v.Route);

        Assert.That(views["/"].Title, Is.EqualTo("Hem"));
        Assert.That(views["/"].Heading, Is.EqualTo("Välkommen"));
        Assert.That(views["/"].Links, Is.EqualTo(new[] { new Link("/products", "Produkter"), new Link("/products/{}", "Veckans produkt") }),
            "RouterLink, router-link med mall och sträng; :to med objekt och extern a hoppas över");
        Assert.That(views["/products"].Links, Is.EqualTo(new[] { new Link("/products/{}", null) }), "'/products/' + p.id");
        Assert.That(views["/products/:id"].Heading, Is.Null);
        Assert.That(views["/products/:id"].ApiCalls, Is.EqualTo(new[] { new ApiCall("POST", "/api/cart/{}"), new ApiCall("GET", "/api/products/{}") }));

        var form = views["/login"].Forms.Single();
        Assert.That(form.Submit, Is.EqualTo(new Locator(LocatorKind.Role, "button", "Logga in")));
        Assert.That(form.Fields.Select(f => (f.Name, f.Type)), Is.EqualTo(new[] { ("email", FieldType.Email), ("password", FieldType.Password) }));
        Assert.That(form.Fields[1].Rules, Is.EqualTo(new[] { new Rule(RuleKind.Required), new Rule(RuleKind.MinLength, "8") }));
        Assert.That(form.SubmitsTo, Is.EqualTo(new ApiCall("POST", "/api/auth/login")));
    }

    // ---------- Små källträd ----------

    [Test]
    public void Jsx_relative_paths_are_joined_with_the_parent_and_index_routes_get_the_parent_path()
    {
        using var p = new TempSpa();
        p.Write("src/App.jsx", """
            import Shell from './Shell';
            import List from './List';
            import Edit from './Edit';
            export default () => (
              <Routes>
                <Route path="/shop" element={<Shell />}>
                  <Route index element={<List />} />
                  <Route path=":sku/edit" element={<Edit />} />
                  <Route path="*" element={<List />} />
                </Route>
              </Routes>
            );
            """);
        p.Write("src/List.jsx", "export default () => <h1>Lista</h1>;");
        p.Write("src/Edit.jsx", "export default () => <h1>Ändra</h1>;");

        var views = p.Run().Views;

        Assert.That(views.Select(v => (v.Route, v.Source)), Is.EqualTo(new[] { ("/shop", "src/List.jsx"), ("/shop/:sku/edit", "src/Edit.jsx") }));
        Assert.That(views[1].Id, Is.EqualTo("ui:/shop/:sku/edit"));
    }

    [Test]
    public void Angular_module_style_loadChildren_follows_the_routing_module()
    {
        using var p = new TempSpa();
        p.Write("src/app/app-routing.module.ts", """
            const routes: Routes = [
              { path: 'shop', loadChildren: () => import('./shop/shop.module').then(m => m.ShopModule) },
            ];
            @NgModule({ imports: [RouterModule.forRoot(routes)] })
            export class AppRoutingModule {}
            """);
        p.Write("src/app/shop/shop.module.ts", """
            import { ShopRoutingModule } from './shop-routing.module';
            @NgModule({ imports: [ShopRoutingModule] })
            export class ShopModule {}
            """);
        p.Write("src/app/shop/shop-routing.module.ts", """
            import { CartComponent } from './cart.component';
            @NgModule({ imports: [RouterModule.forChild([{ path: 'cart', component: CartComponent }])] })
            export class ShopRoutingModule {}
            """);
        p.Write("src/app/shop/cart.component.ts", "@Component({ template: '<h1>Varukorg</h1>' }) export class CartComponent {}");

        var view = p.Run().Views.Single();

        Assert.That(view.Route, Is.EqualTo("/shop/cart"));
        Assert.That(view.Source, Is.EqualTo("src/app/shop/cart.component.ts"));
        Assert.That(view.Heading, Is.EqualTo("Varukorg"));
    }

    [Test]
    public void Angular_routes_inline_in_forRoot_inside_imports_are_found()
    {
        using var p = new TempSpa();
        p.Write("src/app/app.module.ts", """
            import { HomeComponent } from './home.component';
            import { AboutComponent } from './about.component';
            @NgModule({
              imports: [
                BrowserModule,
                RouterModule.forRoot([
                  { path: '', component: HomeComponent },
                  { path: 'about', component: AboutComponent, data: { tabs: [1, 2, 3] } },
                ]),
              ],
            })
            export class AppModule {}
            """);
        p.Write("src/app/home.component.ts", "@Component({ template: '<h1>Hem</h1>' }) export class HomeComponent {}");
        p.Write("src/app/about.component.ts", "@Component({ template: '<h1>Om</h1>' }) export class AboutComponent {}");

        var views = p.Run().Views;

        Assert.That(views.Select(v => v.Route), Is.EqualTo(new[] { "/", "/about" }));
    }

    [Test]
    public void Routes_in_deeply_nested_arrays_are_found()
    {
        using var p = new TempSpa();
        const int depth = 60;
        p.Write("src/router.ts", $"import A from './A';\nexport default {new string('[', depth)}{{ path: '/deep', component: A }}{new string(']', depth)};");
        p.Write("src/A.tsx", "export default () => <h1>Djupt</h1>;");

        Assert.That(p.Run().Views.Select(v => v.Route), Is.EqualTo(new[] { "/deep" }));
    }

    [Test]
    public void Unreadable_api_paths_give_a_warning_and_other_calls_are_kept()
    {
        using var p = new TempSpa();
        p.Write("src/router.ts", "const routes = [{ path: '/a', component: A }];\nimport A from './A';");
        p.Write("src/A.tsx", """
            export default function A() {
              fetch(url);
              fetch(`${BASE}/x`);
              fetch('https://other.example/api');
              fetch('/api/ok', { method: 'put' });
              return <h1>A</h1>;
            }
            """);

        var catalog = p.Run();

        Assert.That(catalog.Views.Single().ApiCalls, Is.EqualTo(new[] { new ApiCall("PUT", "/api/ok") }));
        Assert.That(catalog.Warnings, Has.Count.EqualTo(1), "fetch(url) och fetch(`${BASE}/x`) ger samma varning");
        Assert.That(catalog.Warnings[0], Does.Contain("src/A.tsx").And.Contain("fetch"));
    }

    [Test]
    public void Folders_without_routers_give_no_views()
    {
        using var p = new TempSpa();
        p.Write("src/util.ts", "export const path = '/x'; export const list = [{ path: 'a' }];");

        Assert.That(p.Run().Views, Is.Empty);
    }

    // ---------- Determinism ----------

    [TestCase("react")]
    [TestCase("angular")]
    [TestCase("vue")]
    public void Two_runs_give_identical_json(string fixture)
    {
        var first = ViewsJson.Serialize(ViewsFile.Of([new AppViews("App", Discover(fixture).Views, [])]));
        var second = ViewsJson.Serialize(ViewsFile.Of([new AppViews("App", Discover(fixture).Views, [])]));

        Assert.That(second, Is.EqualTo(first));
        Assert.That(first, Does.Not.Contain(FindRoot()), "inga absoluta sökvägar i katalogen");
    }

    // ---------- Tokeniseraren ----------

    [Test]
    public void Tokenizer_reads_single_and_double_quoted_strings_with_escapes()
    {
        var t = JsTokenizer.Tokenize("""a = 'it\'s' + "say \"hi\"";""");

        Assert.That(t.Where(x => x.Kind == TokKind.String).Select(x => x.Text), Is.EqualTo(new[] { "it's", "say \"hi\"" }));
    }

    [Test]
    public void Tokenizer_skips_line_and_block_comments_but_not_slashes_in_strings()
    {
        var t = JsTokenizer.Tokenize("""
            // path: '/gammal'
            const a = { path: '/ny' /* path: '/borta' */, url: 'http://x/y' }; // slut
            """);

        Assert.That(t.Where(x => x.Kind == TokKind.String).Select(x => x.Text), Is.EqualTo(new[] { "/ny", "http://x/y" }));
    }

    [Test]
    public void Tokenizer_turns_template_holes_into_braces_even_when_nested()
    {
        var t = JsTokenizer.Tokenize("x = `/api/${id}/items/${items.map(i => `${i}`).join(',')}?q=${'}'}`;");

        var template = t.Single(x => x.Kind == TokKind.Template);
        Assert.That(template.Text, Is.EqualTo("/api/{}/items/{}?q={}"));
        Assert.That(template.Holes, Is.True);
        Assert.That(t.Last().IsPunct(';'), Is.True);
    }

    [Test]
    public void Tokenizer_treats_an_apostrophe_in_jsx_text_as_punctuation_not_a_string()
    {
        var t = JsTokenizer.Tokenize("<p>Don't panic</p>\n<Route path=\"/a\" />");

        Assert.That(t.Where(x => x.Kind == TokKind.String).Select(x => x.Text), Is.EqualTo(new[] { "/a" }));
    }

    [Test]
    public void Tokenizer_recognizes_regex_literals_and_division()
    {
        var t = JsTokenizer.Tokenize("const r = /^[a-z/]+$/i; const d = a / b / c; Validators.pattern(/\\d+/)");

        Assert.That(t.Where(x => x.Kind == TokKind.Regex).Select(x => x.Text), Is.EqualTo(new[] { "^[a-z/]+$", @"\d+" }));
    }

    [Test]
    public void Parser_reads_nested_objects_arrays_shorthand_and_functions()
    {
        var tokens = JsTokenizer.Tokenize("""
            [{ path: 'a', meta: { requiresAuth: true }, children: [{ path: 'b', component: B }], load: () => import('./x').then(m => m.X, 1), 'quoted-key': "v", shorthand, ...rest }]
            """);

        var array = new JsParser(tokens).ParseArray(0, tokens.Count);

        var o = (JsObject)array.Items.Single();
        Assert.That(o.Props.Select(p => p.Key), Is.EqualTo(new[] { "path", "meta", "children", "load", "quoted-key", "shorthand" }));
        Assert.That(((JsStr)o.Get("path")!).Value, Is.EqualTo("a"));
        Assert.That(((JsObject)o.Get("meta")!).Has("requiresAuth"), Is.True);
        Assert.That(((JsArray)o.Get("children")!).Items, Has.Count.EqualTo(1));
        Assert.That(o.Get("load"), Is.TypeOf<JsRaw>());
        Assert.That(((JsStr)o.Get("quoted-key")!).Value, Is.EqualTo("v"));
        Assert.That(array.End, Is.EqualTo(tokens.Count));
    }

    [Test]
    public void Parser_survives_broken_input()
    {
        foreach (var code in new[] { "[{ path: ", "[{ a: [1, 2", "{ , , }", "[ ( ] )", "[{ path 'x' }]" })
        {
            var tokens = JsTokenizer.Tokenize(code);
            Assert.DoesNotThrow(() => new JsParser(tokens).ParseArray(0, tokens.Count), code);
        }
    }

    // ---------- Hjälpmedel ----------

    private static ViewCatalog Discover(string fixture)
    {
        var (root, directories) = TestRoots.From(Path.Combine(FindRoot(), "testgen", "tests", "spa-fixtures", fixture));
        return ViewDiscovery.Run(new DiscoveryContext(root, directories, []), [new SpaDiscoverer()]);
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "testgen", "tests", "spa-fixtures"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Hittar inte testgen/tests/spa-fixtures.");
    }

    private sealed class TempSpa : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("testgen-spa-").FullName;

        public void Write(string relativePath, string content)
        {
            var path = Path.Combine(_root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public ViewCatalog Run()
        {
            var (root, directories) = TestRoots.From(_root);
            return ViewDiscovery.Run(new DiscoveryContext(root, directories, []), [new SpaDiscoverer()]);
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
