using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using TestGen.Browser.Probe;
using TestGen.Core.Views;

namespace TestGen.Browser.Tests;

/// <summary>Sonderingen. Sammanslagning och crawl testas utan webbläsare; ett par tester kör en riktig Chromium mot en liten app.</summary>
public class ProbeTests
{
    private const string Origin = "http://localhost:5000";

    // --- hjälpare ---

    private static View ViewOf(string route, string? title = null, IReadOnlyList<Form>? forms = null, IReadOnlyList<Link>? links = null) =>
        new(ViewId.Of(route), route, ViewKind.Blazor, "Pages/X.razor", title, null, [], forms ?? [], links ?? [], [], false);

    private static string Json(params View[] views) => ViewsJson.Serialize(ViewsFile.Of([new AppViews("App", views, [])]));

    private static string Json(ProbeResult result) => Json([.. result.Views]);

    private static View[] CatalogOf(params View[] views) => views;
    private static DomField Dom(string? name, string? label = null, string type = "text", string tag = "input", string? testId = null,
        bool required = false, int? minLength = null, int? maxLength = null, string? min = null, string? max = null, string? pattern = null,
        string? value = null, string? id = null, params string[] options) =>
        new(tag, tag == "input" ? type : null, name, id, label, value, testId, required, minLength, maxLength, min, max, pattern, options);

    private static PageSnapshot Snapshot(string? title = null, string? heading = null, DomLink[]? links = null, params DomForm[] forms) =>
        new(title, heading, links ?? [], forms);

    private static DomForm FormOf(string? submit, params DomField[] fields) => new(fields, submit, null);

    private static Form StaticForm(string id, params Field[] fields) => new(id, null, new Locator(LocatorKind.Css, "button[type=submit]"), fields, null);

    private static Field Css(string name, params Rule[] rules) => new(name, null, FieldType.Text, new Locator(LocatorKind.Css, "#" + name), rules, []);

    private static ProbeResult Merge(View view, PageSnapshot snapshot) =>
        Probe(CatalogOf(view), new FakeSource().Page(RouteExamples.Path(view.Route), snapshot));

    private static ProbeResult Probe(View[] views, FakeSource source, ProbeOptions? options = null) =>
        ViewProber.ProbeAsync(views, Origin, options ?? new ProbeOptions(), source, CancellationToken.None).GetAwaiter().GetResult();

    private sealed class FakeSource : IPageSource
    {
        private readonly Dictionary<string, PageResult> _pages = new(StringComparer.Ordinal);
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _opened = new();
        private int _active;
        /// <summary>Högsta antalet sidor som var öppna samtidigt.</summary>
        public int MaxActive { get; private set; }
        public TimeSpan Delay { get; init; }
        /// <summary>De öppnade sidorna, sorterade: ordningen inom en nivå är inte given när sidor öppnas samtidigt.</summary>
        public List<string> Opened => _opened.Order(StringComparer.Ordinal).ToList();

        public FakeSource Page(string path, PageSnapshot snapshot) { _pages[path] = new PageResult(200, path, snapshot); return this; }
        public FakeSource Result(string path, PageResult result) { _pages[path] = result; return this; }

        public async Task<PageResult> OpenAsync(string path, CancellationToken cancellationToken)
        {
            _opened.Enqueue(path);
            var active = Interlocked.Increment(ref _active);
            lock (_opened) MaxActive = Math.Max(MaxActive, active);
            try
            {
                if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
                return _pages.GetValueOrDefault(path) ?? new PageResult(404, path, null);
            }
            finally { Interlocked.Decrement(ref _active); }
        }
    }

    private static DomLink Link(string path, string? text = null) => new(Origin + path, text);

    // --- sammanslagning (utan webbläsare) ---

    [Test]
    public void Fills_in_title_heading_and_html_rules_but_keeps_messages_from_the_code()
    {
        var view = ViewOf("/products/create", forms: [StaticForm("Product",
            Css("Name", new Rule(RuleKind.Required, null, "Namn krävs"), new Rule(RuleKind.MaxLength, "10", "För långt")))]);
        var dom = FormOf("Spara", Dom("Name", "Namn", required: true, maxLength: 50, minLength: 2, pattern: "[a-z]+"));

        var merged = Merge(view, Snapshot("Ny produkt", "Skapa produkt", null, dom)).Views.Single();

        Assert.That(merged.Title, Is.EqualTo("Ny produkt"));
        Assert.That(merged.Heading, Is.EqualTo("Skapa produkt"));
        var field = merged.Forms.Single().Fields.Single();
        Assert.That(field.Label, Is.EqualTo("Namn"));
        Assert.That(field.Rules, Does.Contain(new Rule(RuleKind.Required, null, "Namn krävs")));
        Assert.That(field.Rules, Does.Contain(new Rule(RuleKind.MaxLength, "10", "För långt")), "Regeln med meddelande vinner över HTML:s maxlength");
        Assert.That(field.Rules, Does.Contain(new Rule(RuleKind.MinLength, "2")));
        Assert.That(field.Rules, Does.Contain(new Rule(RuleKind.Pattern, "[a-z]+")));
        Assert.That(field.Rules.Count(r => r.Kind == RuleKind.MaxLength), Is.EqualTo(1));
    }

    [Test]
    public void Does_not_overwrite_title_or_heading_from_the_code()
    {
        var view = ViewOf("/", "Från koden") with { Heading = "Rubrik i koden" };
        var merged = Merge(view, Snapshot("Annan titel", "Annan rubrik")).Views.Single();
        Assert.That((merged.Title, merged.Heading), Is.EqualTo(("Från koden", "Rubrik i koden")));
    }

    [Test]
    public void Replaces_css_locator_with_label_when_the_label_is_unique_and_with_testid_when_present()
    {
        var view = ViewOf("/x", forms: [StaticForm("F", Css("Name"), Css("Email"), Css("Phone"))]);
        var dom = FormOf("Skicka",
            Dom("Name", "Namn"),
            Dom("Email", "E-post", testId: "email-input"),
            Dom("Phone", "Namn"));   // samma etikett som Name: ingen av dem är unik

        var fields = Merge(view, Snapshot(null, null, null, dom)).Views.Single().Forms.Single().Fields;

        Assert.That(fields[0].Locator.Kind, Is.EqualTo(LocatorKind.Css), "Etiketten 'Namn' finns två gånger");
        Assert.That(fields[1].Locator, Is.EqualTo(new Locator(LocatorKind.TestId, "email-input")));
        Assert.That(fields[2].Locator.Kind, Is.EqualTo(LocatorKind.Css));

        var unique = Merge(view, Snapshot(null, null, null, FormOf("Skicka", Dom("Name", "Namn"), Dom("Email", "E-post"), Dom("Phone", "Telefon"))))
            .Views.Single().Forms.Single().Fields;
        Assert.That(unique.Select(f => f.Locator), Is.EqualTo(new[]
        {
            new Locator(LocatorKind.Label, "Namn"), new Locator(LocatorKind.Label, "E-post"), new Locator(LocatorKind.Label, "Telefon"),
        }));
    }

    [Test]
    public void Keeps_a_non_css_static_locator()
    {
        var field = new Field("Name", "Namn", FieldType.Text, new Locator(LocatorKind.Label, "Namn"), [], []);
        var view = ViewOf("/x", forms: [StaticForm("F", field)]);
        var merged = Merge(view, Snapshot(null, null, null, FormOf(null, Dom("Name", "Namn", testId: "t")))).Views.Single();
        Assert.That(merged.Forms.Single().Fields.Single().Locator, Is.EqualTo(field.Locator));
    }

    [Test]
    public void Matches_razor_pages_style_names_and_adds_fields_only_the_dom_has()
    {
        var view = ViewOf("/about", forms: [StaticForm("Input", Css("Name"))]);
        var dom = FormOf("Skicka", Dom("Input.Name", "Namn"), Dom("Input.Comment", "Kommentar", tag: "textarea"));

        var form = Merge(view, Snapshot(null, null, null, dom)).Views.Single().Forms.Single();

        Assert.That(form.Id, Is.EqualTo("Input"));
        Assert.That(form.Fields.Select(f => f.Name), Is.EqualTo(new[] { "Name", "Input.Comment" }));
        Assert.That(form.Fields[1].Type, Is.EqualTo(FieldType.TextArea));
        Assert.That(form.Submit, Is.EqualTo(new Locator(LocatorKind.Role, "button", "Skicka")));
    }

    [Test]
    public void Adds_forms_only_the_dom_has_as_Form1_and_up()
    {
        var view = ViewOf("/x", forms: [StaticForm("Form1", Css("A"))]);
        var snapshot = Snapshot(null, null, null,
            FormOf("A", Dom("A", "A-fältet")),
            FormOf("Sök", Dom("q", "Sök", type: "search")),
            FormOf("Gå", Dom("kind", null, tag: "select", options: ["a", "b"]), Dom("size", null, type: "radio", value: "s"), Dom("size", null, type: "radio", value: "l")));

        var forms = Merge(view, snapshot).Views.Single().Forms;

        Assert.That(forms.Select(f => f.Id), Is.EqualTo(new[] { "Form1", "Form2", "Form3" }));
        Assert.That(forms[1].Fields.Single().Locator, Is.EqualTo(new Locator(LocatorKind.Label, "Sök")));
        Assert.That(forms[2].Fields.Select(f => (f.Name, f.Type)), Is.EqualTo(new[] { ("kind", FieldType.Select), ("size", FieldType.Radio) }));
        Assert.That(forms[2].Fields[0].Options, Is.EqualTo(new[] { "a", "b" }));
        Assert.That(forms[2].Fields[1].Options, Is.EqualTo(new[] { "s", "l" }));
    }

    [Test]
    public void Adds_links_and_normalizes_paths_that_look_like_a_known_route()
    {
        var view = ViewOf("/", links: [new Link("/products/{}", null)]);
        var items = ViewOf("/products/{id:int}");
        var create = ViewOf("/products/create");
        var snapshot = Snapshot(null, null, [Link("/products/42", "Kaffe"), Link("/products/create", "Ny"), Link("/products/7"), Link("/about#x", "Om")]);

        var links = Probe(CatalogOf(view, items, create), new FakeSource().Page("/", snapshot)).Views.Single(v => v.Route == "/").Links;

        Assert.That(links.Select(l => l.Href).Order(StringComparer.Ordinal), Is.EqualTo(new[] { "/about", "/products/create", "/products/{}" }));
        Assert.That(links.Single(l => l.Href == "/products/{}").Text, Is.EqualTo("Kaffe"), "Texten fylls i på en länk som saknade text");
    }

    // --- crawl (utan webbläsare) ---

    [Test]
    public void Crawl_follows_unknown_same_origin_links_but_not_query_fragment_files_logout_or_other_origins()
    {
        var home = ViewOf("/");
        var snapshot = Snapshot("Start", null, [
            Link("/orphan", "Okänd"), Link("/logout"), Link("/Account/Logga-ut"), Link("/signout"), Link("/list?page=2"),
            Link("/rapport.pdf"), Link("/data.zip"), new DomLink("https://example.com/extern", null), new DomLink("mailto:a@b.se", null), Link("/orphan#del")]);
        var source = new FakeSource().Page("/", snapshot).Page("/orphan", Snapshot("Okänd sida", "Okänd"));

        var catalog = Probe(CatalogOf(home), source);

        var orphan = catalog.Views.Single(v => v.Route == "/orphan");
        Assert.That((orphan.Kind, orphan.Source, orphan.Id, orphan.Title), Is.EqualTo((ViewKind.Probed, "(probe)", "ui:/orphan", "Okänd sida")));
        Assert.That(source.Opened, Is.EqualTo(new[] { "/", "/orphan" }));
        Assert.That(catalog.Views.Single(v => v.Route == "/").Links.Select(l => l.Href), Is.EqualTo(new[] { "/orphan" }));
        Assert.That(catalog.Warnings, Is.Empty);
    }

    [Test]
    public void Crawl_stops_at_depth_3_and_at_max_pages()
    {
        var source = new FakeSource()
            .Page("/", Snapshot(null, null, [Link("/a")]))
            .Page("/a", Snapshot(null, null, [Link("/b")]))
            .Page("/b", Snapshot(null, null, [Link("/c")]))
            .Page("/c", Snapshot(null, null, [Link("/d")]))
            .Page("/d", Snapshot());

        var deep = Probe(CatalogOf(ViewOf("/")), source);
        Assert.That(deep.Views.Select(v => v.Route), Is.EqualTo(new[] { "/", "/a", "/b", "/c" }), "Djup 3: /d hittas men följs inte");

        var capped = Probe(CatalogOf(ViewOf("/")), source, new ProbeOptions(MaxPages: 2));
        Assert.That(capped.Views.Select(v => v.Route), Is.EqualTo(new[] { "/", "/a" }));
        Assert.That(capped.Warnings.Single(), Does.Contain("stoppade efter 2 sidor").And.Contain("--max-pages"));
    }

    [Test]
    public void Errors_give_swedish_warnings_and_keep_the_view_unchanged()
    {
        var home = ViewOf("/", "Start");
        var broken = ViewOf("/broken", "Trasig");
        var slow = ViewOf("/slow");
        var moved = ViewOf("/moved");
        var source = new FakeSource()
            .Page("/", Snapshot("Ny titel", null, [Link("/missing"), Link("/newslow")]))
            .Result("/broken", new PageResult(500, "/broken", null))
            .Result("/slow", new PageResult(0, "/slow", null) { TimedOut = true })
            .Result("/moved", new PageResult(200, "/login", Snapshot("Logga in")))
            .Result("/newslow", new PageResult(0, "/newslow", null) { TimedOut = true });

        var catalog = Probe(CatalogOf(home, broken, slow, moved), source, new ProbeOptions(TimeoutMs: 2500));

        Assert.That(Json(catalog.Views.Single(v => v.Route == "/broken")), Is.EqualTo(Json(broken)));
        Assert.That(Json(catalog.Views.Single(v => v.Route == "/slow")), Is.EqualTo(Json(slow)));
        Assert.That(Json(catalog.Views.Single(v => v.Route == "/moved")), Is.EqualTo(Json(moved)));
        Assert.That(catalog.Views.Select(v => v.Route), Does.Not.Contain("/missing").And.Not.Contain("/newslow"));
        Assert.That(catalog.Warnings, Is.EqualTo(new[]
        {
            "Sonderingen: /broken svarade med status 500. Vyn behålls oförändrad.",
            "Sonderingen: /missing (länk från /) svarade med status 404. Sidan lades inte till som vy.",
            "Sonderingen: /moved omdirigerades till /login. Vyn behålls oförändrad.",
            "Sonderingen: /newslow (länk från /) laddades inte inom 2,5 s. Sidan lades inte till som vy.",
            "Sonderingen: /slow laddades inte inom 2,5 s. Vyn behålls oförändrad.",
        }));
    }

    [Test]
    public void Opens_views_with_example_values_for_parameters()
    {
        var catalog = CatalogOf(ViewOf("/p/{id:int}/{g:guid}/{s}/{o?}"), ViewOf("/b/{n:long}"));
        var source = new FakeSource();
        Probe(catalog, source);
        // Valfria parametrar får exempelvärden de också, precis som i testerna.
        Assert.That(source.Opened, Is.EqualTo(new[] { "/b/1", "/p/1/00000000-0000-0000-0000-000000000001/exempel/exempel" }));
    }

    [Test]
    public void Opens_at_most_four_pages_at_a_time_and_gives_the_same_result_as_one_at_a_time()
    {
        var catalog = CatalogOf(ViewOf("/"), ViewOf("/a"), ViewOf("/b"), ViewOf("/c"), ViewOf("/d"), ViewOf("/e"), ViewOf("/f"), ViewOf("/g"));
        Func<FakeSource> make = () => new FakeSource { Delay = TimeSpan.FromMilliseconds(30) }
            .Page("/", Snapshot("S", null, [Link("/x"), Link("/y")]))
            .Page("/x", Snapshot("X", null, [Link("/z")])).Page("/y", Snapshot("Y")).Page("/z", Snapshot("Z"))
            .Result("/e", new PageResult(500, "/e", null));
        var source = make();
        var result = Probe(catalog, source);
        Assert.That(source.MaxActive, Is.InRange(2, ViewProber.MaxConcurrency));
        Assert.That(result.Views.Select(v => v.Route), Is.EqualTo(new[] { "/", "/a", "/b", "/c", "/d", "/e", "/f", "/g", "/x", "/y", "/z" }));
        Assert.That(result.Warnings, Has.Some.Contain("/e svarade med status 500"));
        Assert.That(Json(Probe(catalog, make())), Is.EqualTo(Json(result)));
    }

    [Test]
    public void Stops_at_max_pages_with_the_same_pages_whatever_the_concurrency()
    {
        var catalog = CatalogOf(ViewOf("/"), ViewOf("/a"), ViewOf("/b"));
        var source = new FakeSource { Delay = TimeSpan.FromMilliseconds(10) }
            .Page("/", Snapshot("S", null, [Link("/x"), Link("/y"), Link("/z")]));
        var result = Probe(catalog, source, new ProbeOptions(MaxPages: 4));
        Assert.That(source.Opened, Is.EqualTo(new[] { "/", "/a", "/b", "/x" }));
        Assert.That(result.Warnings, Has.Some.EqualTo("Sonderingen stoppade efter 4 sidor. 2 sidor sondrades inte. Höj gränsen med --max-pages."));
    }

    [Test]
    public void Probed_links_follow_the_same_constraints_as_link_targets()
    {
        // /products/create är inget int, så den hör inte till /products/{id:int} och sonderas som en egen sida.
        var catalog = CatalogOf(ViewOf("/"), ViewOf("/products/{id:int}"), ViewOf("/users/:id"));
        var source = new FakeSource()
            .Page("/", Snapshot("S", null, [Link("/products/create"), Link("/users/7")]))
            .Page("/products/create", Snapshot("Ny"));
        var result = Probe(catalog, source);
        Assert.That(result.Views.Select(v => v.Route), Does.Contain("/products/create"));
        Assert.That(source.Opened, Does.Not.Contain("/users/7"), "/users/7 hör till /users/:id och är redan känd");
    }

    [Test]
    public void Result_is_in_discovery_order_and_the_same_when_probed_twice()
    {
        var source = () => new FakeSource()
            .Page("/", Snapshot("S", null, [Link("/z"), Link("/b"), Link("/a")]))
            .Page("/a", Snapshot("A")).Page("/b", Snapshot("B")).Page("/z", Snapshot("Z"));
        var first = Json(Probe(CatalogOf(ViewOf("/")), source()));
        var second = Json(Probe(CatalogOf(ViewOf("/")), source()));
        Assert.That(second, Is.EqualTo(first));
        Assert.That(Probe(CatalogOf(ViewOf("/")), source()).Views.Select(v => v.Route), Is.EqualTo(new[] { "/", "/a", "/b", "/z" }));
    }

    [TestCase("/products/42", "/products/{}")]
    [TestCase("/products/create", "/products/create")]
    [TestCase("/files/a/b/c", "/files/{}")]
    [TestCase("/orders/3/lines", "/orders/{}/lines")]
    [TestCase("/unknown/1", "/unknown/1")]
    public void RouteMatcher_normalizes_to_the_known_routes_form(string path, string expected)
    {
        var matcher = new RouteMatcher(["/products/{id:int}", "/products/create", "/files/{*rest}", "/orders/{id}/lines"]);
        Assert.That(matcher.Normalize(path), Is.EqualTo(expected));
    }

    [Test]
    public void An_app_url_that_is_not_http_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => ViewProber.Origin("localhost:5000"));
        Assert.That(ViewProber.Origin("http://127.0.0.1:5000/a/b?x=1"), Is.EqualTo("http://127.0.0.1:5000"));
    }

    // --- riktig webbläsare ---

    private static string Page(string title, string body) => $"<!doctype html><html><head><meta charset=\"utf-8\"><title>{title}</title></head><body>{body}</body></html>";

    private static readonly Dictionary<string, string> Site = new(StringComparer.Ordinal)
    {
        ["/"] = Page("Start", """
            <h1>Välkommen</h1>
            <a href="/known">Känd</a> <a href="/orphan">Okänd sida</a> <a href="/missing">Saknas</a> <a href="/logout">Logga ut</a>
            <a href="/list?page=2">Sida 2</a> <a href="/items/7">Sju</a> <a href="/rapport.pdf">PDF</a> <a href="https://example.com/">Extern</a>
            """),
        ["/known"] = Page("Kända sidan", """
            <h1>Kunduppgifter</h1>
            <form>
              <label for="Name">Namn</label><input id="Name" name="Name" required maxlength="50" />
              <label for="Mail">E-post</label><input id="Mail" name="Email" type="email" data-testid="email-input" />
              <label for="Kind">Sort</label><select id="Kind" name="Kind"><option value="">Välj</option><option value="a">A</option><option value="b">B</option></select>
              <button type="submit">Spara</button>
            </form>
            """),
        ["/orphan"] = Page("Okänd titel", """
            <h1>Sök</h1>
            <form><label for="q">Sökord</label><input id="q" name="q" minlength="3" /><button>Sök nu</button></form>
            <a href="/deep2">Djupare</a>
            """),
        ["/deep2"] = Page("Två", """<a href="/deep3">Vidare</a>"""),
        ["/deep3"] = Page("Tre", """<a href="/deep4">Vidare</a>"""),
        ["/deep4"] = Page("Fyra", "<h1>För djupt</h1>"),
        ["/items/1"] = Page("Sak", "<h1>Sak 1</h1>"),
    };

    private static View[] BrowserViews() => CatalogOf(
        ViewOf("/"),
        ViewOf("/known", forms: [new Form("Customer", null, new Locator(LocatorKind.Css, "button[type=submit]"),
            [Css("Name", new Rule(RuleKind.Required, null, "Namn krävs")), Css("Email")], null)]),
        ViewOf("/items/{id:int}"));

    /// <summary>Sonderar en liten app med sidorna i <see cref="Site"/> (en sida, /slow, svarar långsamt) i den riktiga Chromium.</summary>
    private static async Task<ProbeResult> ProbeSiteAsync(View[] views, ProbeOptions? options = null)
    {
        await using var app = await TestApp.StartAsync(web =>
        {
            web.MapFallback((HttpContext context) => Site.TryGetValue(context.Request.Path.Value!, out var html)
                ? Results.Content(html, "text/html; charset=utf-8")
                : Results.Content(Page("Saknas", "<h1>404</h1>"), "text/html; charset=utf-8", statusCode: 404));
            web.MapGet("/slow", async () =>
            {
                await Task.Delay(4000);
                return Results.Content(Page("Långsam", "<h1>Långsam</h1>"), "text/html; charset=utf-8");
            });
        });
        return await ViewProber.ProbeAsync(views, app.Url, BrowserFixture.Host, options);
    }

    [Test]
    public async Task Browser_merges_the_dom_into_the_static_views()
    {
        var result = await ProbeSiteAsync(BrowserViews());

        var known = result.Views.Single(v => v.Route == "/known");
        Assert.That(known.Title, Is.EqualTo("Kända sidan"));
        Assert.That(known.Heading, Is.EqualTo("Kunduppgifter"));
        var form = known.Forms.Single();
        Assert.That(form.Id, Is.EqualTo("Customer"));
        Assert.That(form.Submit, Is.EqualTo(new Locator(LocatorKind.Role, "button", "Spara")));
        var name = form.Fields.Single(f => f.Name == "Name");
        Assert.That(name.Label, Is.EqualTo("Namn"));
        Assert.That(name.Locator, Is.EqualTo(new Locator(LocatorKind.Label, "Namn")));
        Assert.That(name.Rules, Is.EquivalentTo(new[] { new Rule(RuleKind.Required, null, "Namn krävs"), new Rule(RuleKind.MaxLength, "50") }));
        var email = form.Fields.Single(f => f.Name == "Email");
        Assert.That(email.Locator, Is.EqualTo(new Locator(LocatorKind.TestId, "email-input")));
        Assert.That(email.Label, Is.EqualTo("E-post"));
        Assert.That(form.Fields.Select(f => f.Name), Is.EqualTo(new[] { "Name", "Email", "Kind" }));
        Assert.That(form.Fields[2], Has.Property("Type").EqualTo(FieldType.Select).And.Property("Options").EqualTo(new[] { "a", "b" }));
    }

    [Test]
    public async Task Browser_crawls_unknown_pages_within_the_limits_and_warns_about_errors()
    {
        var result = await ProbeSiteAsync(BrowserViews());

        Assert.That(result.Views.Select(v => v.Route).Order(StringComparer.Ordinal),
            Is.EqualTo(new[] { "/", "/deep2", "/deep3", "/items/{id:int}", "/known", "/orphan" }),
            "/deep4 ligger för djupt, /missing, /logout, /list?page=2, PDF och extern sida följs inte");
        var orphan = result.Views.Single(v => v.Route == "/orphan");
        Assert.That((orphan.Kind, orphan.Source, orphan.Id), Is.EqualTo((ViewKind.Probed, "(probe)", "ui:/orphan")));
        Assert.That((orphan.Title, orphan.Heading), Is.EqualTo(("Okänd titel", "Sök")));
        var search = orphan.Forms.Single();
        Assert.That(search.Id, Is.EqualTo("Form1"));
        Assert.That(search.Submit, Is.EqualTo(new Locator(LocatorKind.Role, "button", "Sök nu")));
        Assert.That(search.Fields.Single(), Has.Property("Locator").EqualTo(new Locator(LocatorKind.Label, "Sökord"))
            .And.Property("Rules").EqualTo(new[] { new Rule(RuleKind.MinLength, "3") }));
        Assert.That(result.Views.Single(v => v.Route == "/").Links.Select(l => l.Href),
            Is.EquivalentTo(new[] { "/items/{}", "/known", "/missing", "/orphan" }));
        Assert.That(result.Warnings, Is.EqualTo(new[] { "Sonderingen: /missing (länk från /) svarade med status 404. Sidan lades inte till som vy." }));
    }

    [Test]
    public async Task Browser_respects_max_pages_and_timeout()
    {
        var limited = await ProbeSiteAsync(BrowserViews(), new ProbeOptions(MaxPages: 2));
        Assert.That(limited.Warnings, Has.Some.Contain("stoppade efter 2 sidor"));

        var slow = CatalogOf(ViewOf("/known"), ViewOf("/slow"));
        var timedOut = await ProbeSiteAsync(slow, new ProbeOptions(TimeoutMs: 1000));
        Assert.That(timedOut.Warnings, Has.Some.StartWith("Sonderingen: /slow laddades inte inom 1 s. Vyn behålls oförändrad."));
        Assert.That(Json(timedOut.Views.Single(v => v.Route == "/slow")), Is.EqualTo(Json(slow.Single(v => v.Route == "/slow"))));
    }

    [Test]
    public async Task Browser_gives_the_same_result_twice()
    {
        var first = Json(await ProbeSiteAsync(BrowserViews()));
        var second = Json(await ProbeSiteAsync(BrowserViews()));
        Assert.That(second, Is.EqualTo(first));
    }
}
