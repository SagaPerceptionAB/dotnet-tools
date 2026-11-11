using Microsoft.Playwright;

namespace SystemMap.Tests;

/// <summary>Sidan i en riktig webbläsare: anropen grupperade efter sökvägsområde.</summary>
public partial class CodeMapTests
{
    private const string GroupedApi = """
        public static class E
        {
            public static void Map(WebApplication app)
            {
                var v1 = app.MapGroup("/v1");
                v1.MapPost("/auth/out", () => 1);
                v1.MapPost("/auth/in", () => 2);
                v1.MapGet("/family", () => 3);
                v1.MapPost("/family", () => 4);
                v1.MapPost("/family/leave", () => 5);
                v1.MapGet("/me", () => 6);
            }
        }
        """;

    [Test]
    public async Task Page_groups_the_calls_by_path_area_in_boxes_the_list_and_the_selection()
    {
        var map = await MapAsync(Build(true, Web("Api", AppStart + GroupedApi)));
        await WithPageAsync(map, async (page, ready) =>
        {
            // Gruppen är det första segmentet efter den gemensamma början (/v1); ett ensamt anrop är ingen grupp.
            var groups = await page.EvaluateAsync<string[]>(
                "() => SystemMap.groups(JSON.parse(document.getElementById('map-data').textContent).entryPoints).map(g => (g.label || '-') + ': ' + g.entries.map(e => e.method + ' ' + e.path).join(', '))");
            Assert.That(groups, Is.EqualTo(new[]
            {
                "/v1/auth: POST /v1/auth/in, POST /v1/auth/out",
                "/v1/family: GET /v1/family, POST /v1/family, POST /v1/family/leave",
                "-: GET /v1/me",
            }));

            // Listan med anrop har en grupp per område.
            Assert.That(await GroupsAsync(page, "entry"),
                Is.EqualTo(new[] { "/v1/auth (2)", "/v1/family (3)" }));

            // Rutorna: etikett, anropen i dem och ordningen (sökväg, metod) uppifrån och ned.
            const string Boxes = "() => SystemMap.cy().nodes('.t-callgroup').map(g => g.data('label') + ': ' + g.children().sort((a, b) => a.position('y') - b.position('y')).map(n => n.data('label')).join(', '))";
            Assert.That(await page.EvaluateAsync<string[]>(Boxes), Is.EquivalentTo(new[]
            {
                "/v1/auth: POST /v1/auth/in, POST /v1/auth/out",
                "/v1/family: GET /v1/family, POST /v1/family, POST /v1/family/leave",
            }));
            Assert.That(await page.EvaluateAsync<int>("() => SystemMap.cy().nodes('.t-entry').filter(n => !n.parent().length).length"), Is.EqualTo(1), "det ensamma anropet står utan ruta");

            // Ett valt anrop dämpar rutan som inte har det.
            await PickAsync(page, "entry", "POST /v1/auth/in");
            await ready();
            Assert.That(await page.EvaluateAsync<string[]>("() => SystemMap.cy().nodes('.t-callgroup').map(g => g.data('label') + ':' + g.hasClass('dimmed'))"),
                Is.EquivalentTo(new[] { "/v1/auth:false", "/v1/family:true" }));
        });
    }

    private const string FlowApi = """
        public class AuthOptions { public string Url { get; set; } = ""; }
        public class AuthClient(System.Net.Http.HttpClient http, IOptions<AuthOptions> options)
        {
            public Task Verify() => http.PostAsync(options.Value.Url, null);
        }
        public class AuthService(AuthClient client) { public Task Check() => client.Verify(); }
        public class FamilyService(AuthClient client) { public Task Load() => client.Verify(); }
        [ApiController, Route("v1/auth")]
        public class AuthController(AuthService service) : ControllerBase
        {
            [HttpPost("in")] public Task In() => service.Check();
            [HttpPost("out")] public Task Out() => service.Check();
        }
        [ApiController, Route("v1/family")]
        public class FamilyController(FamilyService service) : ControllerBase
        {
            [HttpGet] public Task Get() => service.Load();
            [HttpPost] public Task Post() => service.Load();
        }
        [ApiController, Route("v1/me")]
        public class MeController(FamilyService service) : ControllerBase
        {
            [HttpGet] public Task Get() => service.Load();
        }
        public static class Setup
        {
            public static void Register(IServiceCollection s)
            {
                s.AddOptions<AuthOptions>().BindConfiguration("Auth");
                s.AddHttpClient<AuthClient>();
            }
        }
        """;

    // Kör ett test mot sidan för en karta i en riktig webbläsare; sidan är klar när #map-view har data-ready. level är nivån som väljs
    // direkt efter att sidan öppnats (de flesta testerna utgår från klasserna): "projects" är det sidan öppnar med, null låter den välja själv (inget sparat val).
    // groupsOpen: anropsgrupperna är hopfällda från början; de flesta testerna vill se anropen i rutorna och fäller därför ut dem först.
    private static async Task WithPageAsync(CodeMapModel map, Func<IPage, Func<Task>, Task> test, IReadOnlySet<string>? hidden = null, bool showUnreached = true, ColorScheme? colors = null, string? level = "classes", bool groupsOpen = true)
    {
        var file = Path.Combine(Path.GetTempPath(), $"systemmap-page-{Guid.NewGuid():N}.html");
        await File.WriteAllTextAsync(file, CodeMapHtml.Write(map, hidden ?? new HashSet<string>(), showUnreached));
        try
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync();
            var page = await browser.NewPageAsync(new BrowserNewPageOptions { ViewportSize = new ViewportSize { Width = 1400, Height = 900 }, ReducedMotion = ReducedMotion.Reduce, ColorScheme = colors });
            await page.GotoAsync(new Uri(file).AbsoluteUri);
            Task Ready() => page.WaitForSelectorAsync("#map-view[data-ready='true']", new PageWaitForSelectorOptions { State = WaitForSelectorState.Attached });
            await Ready();
            await ChooseLevelAsync(page, Ready, level);
            if (groupsOpen) await OpenGroupsAsync(page, Ready);
            await test(page, Ready);
        }
        finally
        {
            File.Delete(file);
        }
    }

    // Väljer nivån med listorna (så som en användare sparar den). Ett sessionStorage-värde satt av ett initskript gav ostabila omladdningar.
    private static async Task ChooseLevelAsync(IPage page, Func<Task> ready, string? level)
    {
        if (level is null or "projects") return;
        await PickAsync(page, "cls", "Alla");
        if (level is "methods") await PickAsync(page, "fn", "Alla");
        else if (level is "all") await PickAsync(page, "integ", "Alla, med läsningar och skrivningar");
        await ready();
    }

    // Fäller ut alla hopfällda anropsgrupper (ett klick var på kartans nod).
    private static async Task OpenGroupsAsync(IPage page, Func<Task> ready)
    {
        await page.EvaluateAsync("() => { const cy = SystemMap.cy(); if (!cy) return; for (const id of cy.nodes('.t-calls').map(n => n.id())) cy.getElementById(id).emit('tap'); }");
        await ready();
    }

    // Mitten på varje klass (eller dess ruta) per namn, och pilarna som "källa>mål" (utan de osynliga hjälpmedlen).
    private const string Centers = "() => Object.fromEntries(SystemMap.cy().nodes('.t-class').map(n => [n.data('label').split('\\n')[0], n.position()]))";
    private const string Arrows = "() => SystemMap.cy().edges().not('.band').map(e => e.source().data('label').split('\\n')[0] + '>' + e.target().data('label').split('\\n')[0])";

    [Test]
    public async Task Page_places_the_classes_in_steps_in_both_directions_and_at_every_level()
    {
        var map = await MapAsync(Build(true, Web("Api", AppStart + FlowApi)));
        await WithPageAsync(map, async (page, ready) =>
        {
            var steps = await page.EvaluateAsync<Dictionary<string, int>>("() => Object.fromEntries(Object.entries(SystemMap.steps()).map(([id, s]) => [SystemMap.cy().getElementById(id).data('label').split('\\n')[0], s]))");
            Assert.That(steps.Where(s => s.Key.EndsWith("Controller")).Select(s => s.Value).Distinct(), Is.EqualTo(new[] { 1 }), "klasser som anropen in når är steg 1");
            Assert.That((steps["AuthService"], steps["FamilyService"], steps["AuthClient"]), Is.EqualTo((2, 2, 3)));

            async Task InOrder(string axis)
            {
                var c = await page.EvaluateAsync<Dictionary<string, Dictionary<string, double>>>(Centers);
                foreach (var chain in new[] { new[] { "AuthController", "AuthService", "AuthClient" }, new[] { "FamilyController", "FamilyService", "AuthClient" }, new[] { "MeController", "FamilyService" } })
                {
                    for (var i = 0; i + 1 < chain.Length; i++)
                        Assert.That(c[chain[i]][axis], Is.LessThan(c[chain[i + 1]][axis]), $"{chain[i]} före {chain[i + 1]} längs {axis}");
                }
            }
            await InOrder("x");
            await page.ClickAsync("[data-direction='TB']");
            await ready();
            await InOrder("y");
            await page.ClickAsync("[data-direction='LR']");
            await ready();

            // Nivå Allt: sidan ritas, metoderna ligger i sin klass och klasserna står i steg.
            await LevelAsync(page, "all");
            await ready();
            await InOrder("x");
            Assert.That(await page.EvaluateAsync<bool>("() => { const m = SystemMap.cy().nodes('.t-method'); return m.length > 0 && m.every(n => n.parent().hasClass('t-class') && n.position('x') > n.parent().boundingBox().x1 && n.position('x') < n.parent().boundingBox().x2); }"), Is.True);
        });
    }

    [Test]
    public async Task Page_draws_one_arrow_per_call_group_and_target_and_the_selected_call_keeps_its_own()
    {
        var map = await MapAsync(Build(true, Web("Api", AppStart + FlowApi)));
        await WithPageAsync(map, async (page, ready) =>
        {
            // Hela systemet: en pil per ruta och mål, det ensamma anropet behåller sin, och ingen pil går från ett anrop i en ruta.
            Assert.That(await page.EvaluateAsync<string[]>(Arrows), Is.SupersetOf(new[] { "/v1/auth>AuthController", "/v1/family>FamilyController", "GET /v1/me>MeController" }));
            Assert.That(await page.EvaluateAsync<string[]>("() => SystemMap.cy().edges().filter(e => e.source().hasClass('t-callgroup')).map(e => e.source().data('label').split('\\n')[0] + '>' + e.target().data('label').split('\\n')[0])"),
                Is.EquivalentTo(new[] { "/v1/auth>AuthController", "/v1/family>FamilyController" }));
            Assert.That(await page.EvaluateAsync<int>("() => SystemMap.cy().edges().not('.band').filter(e => e.source().parent().hasClass('t-callgroup')).length"), Is.Zero);
            Assert.That(await page.EvaluateAsync<int>("() => SystemMap.cy().edges().filter(e => e.source().hasClass('t-entry') && !e.source().parent().length).length"), Is.EqualTo(1));

            // De samlade pilarna korsar inte varandra (rakt mellan mitterna; pilar som delar en ände räknas inte).
            Assert.That(await page.EvaluateAsync<string[]>("""
                () => {
                  const es = SystemMap.cy().edges().not('.band').toArray(), bad = [];
                  const side = (a, b, c) => Math.sign((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x));
                  for (let i = 0; i < es.length; i++) for (let j = i + 1; j < es.length; j++) {
                    const p = es[i], q = es[j];
                    if (p.source().id() === q.source().id() || p.target().id() === q.target().id() || p.source().id() === q.target().id() || p.target().id() === q.source().id()) continue;
                    const a = p.source().position(), b = p.target().position(), c = q.source().position(), d = q.target().position();
                    if (side(a, b, c) * side(a, b, d) < 0 && side(c, d, a) * side(c, d, b) < 0) bad.push(p.id() + ' x ' + q.id());
                  }
                  return bad;
                }
                """), Is.Empty);

            // Ett valt anrop: dess egna pilar ritas och är markerade, och ingen ruta ritar en samlad pil.
            await PickAsync(page, "entry", "POST /v1/auth/in");
            await ready();
            Assert.That(await page.EvaluateAsync<int>("() => SystemMap.cy().edges().filter(e => e.source().hasClass('t-callgroup')).length"), Is.Zero);
            Assert.That(await page.EvaluateAsync<string[]>("() => SystemMap.cy().edges('.path').map(e => e.source().data('label').split('\\n')[0] + '>' + e.target().data('label').split('\\n')[0])"),
                Does.Contain("POST /v1/auth/in>AuthController"));
        });
    }

    [Test]
    public async Task Page_in_the_vertical_direction_lists_the_calls_of_a_group_under_each_other_in_columns()
    {
        var map = await MapAsync(Build(true, Web("Api", AppStart + FlowApi)));
        await WithPageAsync(map, async (page, ready) =>
        {
            await page.ClickAsync("[data-direction='TB']");
            await ready();
            var labels = await page.EvaluateAsync<Dictionary<string, string[]>>(Members("n.data('label').split('\\n')[0]"));
            var xs = await page.EvaluateAsync<Dictionary<string, double[]>>(Members("n.position('x')"));
            var ys = await page.EvaluateAsync<Dictionary<string, double[]>>(Members("n.position('y')"));

            // Anropen i en grupp står under varandra, i gruppens ordning och med samma mitt-x.
            Assert.That(labels["/v1/auth"], Is.EqualTo(new[] { "POST /v1/auth/in", "POST /v1/auth/out" }));
            Assert.That(labels["/v1/family"], Is.EqualTo(new[] { "GET /v1/family", "POST /v1/family" }));
            foreach (var group in labels.Keys)
            {
                Assert.That(ys[group], Is.Ordered.Ascending.And.Unique);
                Assert.That(xs[group].Max() - xs[group].Min(), Is.LessThan(2));
            }

            // Grupperna står bredvid varandra utan att överlappa.
            var spans = await page.EvaluateAsync<double[][]>("() => SystemMap.cy().nodes('.t-callgroup').map(g => [g.boundingBox().x1, g.boundingBox().x2]).sort((a, b) => a[0] - b[0])");
            Assert.That(spans, Has.Length.EqualTo(2));
            Assert.That(spans[1][0], Is.GreaterThan(spans[0][1]));

            // Endpoint-klassen står under sin grupp, med mitten inom gruppens bredd.
            var under = await page.EvaluateAsync<double[]>("""
                () => ['/v1/auth>AuthController', '/v1/family>FamilyController'].map(k => {
                  const [g, c] = k.split('>'), box = SystemMap.cy().nodes('.t-callgroup').filter(n => n.data('label') === g)[0].boundingBox();
                  const p = SystemMap.cy().nodes('.t-class').filter(n => n.data('label').split('\n')[0] === c)[0].position();
                  return p.y > box.y2 && p.x >= box.x1 && p.x <= box.x2 ? 1 : 0;
                })
                """);
            Assert.That(under, Is.EqualTo(new[] { 1.0, 1.0 }));

            // Kartan är inte en remsa: den är smalare än anropen sida vid sida.
            var widths = await page.EvaluateAsync<double[]>("() => [SystemMap.cy().elements().not('.anchor').boundingBox().w, SystemMap.cy().nodes('.t-entry').map(n => n.width()).reduce((a, b) => a + b, 0)]");
            Assert.That(widths[0], Is.LessThan(widths[1]));
        });
    }

    // Anropen i varje ruta, uppifrån och ned, med ett värde per anrop.
    private static string Members(string value) =>
        $"() => Object.fromEntries(SystemMap.cy().nodes('.t-callgroup').map(g => [g.data('label'), g.children().sort((a, b) => a.position('y') - b.position('y')).map(n => {value})]))";
}
