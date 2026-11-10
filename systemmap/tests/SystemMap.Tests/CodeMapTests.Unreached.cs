using System.Text.Json;
using Microsoft.Playwright;

namespace SystemMap.Tests;

/// <summary>Det som ingen ingång når: vilka projekt, klasser och metoder som räknas, och hur det visas (kod i minnet).</summary>
public partial class CodeMapTests
{
    /// <summary>Delarna som ingen ingång når, som id.</summary>
    private static List<string> UnreachedIds(CodeMapModel map) => CodeMapOverview.Unreached(CodeMapOverview.Build(map)).Select(n => n.Id).ToList();

    private static List<string> UnreachedClasses(CodeMapModel map) =>
        CodeMapOverview.Unreached(CodeMapOverview.Build(map)).Where(n => n.Kind == OverviewKind.Class).Select(n => n.Name).ToList();

    /// <summary>Alla metoder som ingen når, också i klasser som kartan inte ritar (hjälpklasser): ur inventeringen och inte översikten.</summary>
    private static List<string> UnreachedMethods(CodeMapModel map) =>
        map.Code!.Classes.SelectMany(c => c.Methods.Where(m => !m.Reached).Select(m => $"{c.Name}.{m.Name}")).ToList();

    // --- Vilka klasser som räknas ------------------------------------------------------------------

    [TestCase("class Legacy { public void Old() { } }", "Legacy", TestName = "A_class_with_a_method_body_that_nobody_calls_is_unreached")]
    [TestCase("class Dto { public int A { get; set; } public string B = \"\"; }", "", TestName = "A_class_with_only_properties_and_fields_is_not_listed")]
    [TestCase("enum Status { A, B }", "", TestName = "An_enum_is_not_listed")]
    [TestCase("interface IPort { void Go(); }", "", TestName = "An_interface_is_not_listed")]
    [TestCase("record Moved(int A, string B);", "", TestName = "A_record_with_only_properties_is_not_listed")]
    [TestCase("record Twice(int A) { public int Double() => A * 2; }", "Twice", TestName = "A_record_with_a_method_body_is_unreached")]
    [TestCase("class OnlyConstructor { public OnlyConstructor() { System.Console.WriteLine(); } }", "", TestName = "A_class_with_only_a_constructor_body_is_not_listed")]
    [TestCase("static class Reg { public static Microsoft.Extensions.DependencyInjection.IServiceCollection Add(this Microsoft.Extensions.DependencyInjection.IServiceCollection s) => s; }", "",
        TestName = "A_service_registration_extension_is_startup_and_not_listed")]
    [TestCase("static class Wire { public static void Register(Microsoft.AspNetCore.Builder.WebApplication app) { } }", "", TestName = "A_method_that_takes_a_WebApplication_is_startup_and_not_listed")]
    [TestCase("class Disp : IDisposable { public void Dispose() { } }", "", TestName = "A_class_whose_only_method_implements_a_framework_interface_is_not_listed")]
    [TestCase("class Named { public override string ToString() => \"x\"; }", "", TestName = "A_class_whose_only_method_overrides_a_framework_method_is_not_listed")]
    [TestCase("class Mw { public Task InvokeAsync(Microsoft.AspNetCore.Http.HttpContext c) => Task.CompletedTask; }", "", TestName = "Middleware_is_called_by_the_framework_and_not_listed")]
    [TestCase("class PlaceHandler : MediatR.IRequestHandler<int, int> { public Task<int> Handle(int r, CancellationToken ct) => Task.FromResult(r); }", "", TestName = "A_MediatR_handler_is_called_by_the_framework_and_not_listed")]
    [TestCase("interface IPort { void Go(); } class Adapter : IPort { public void Go() { } }", "Adapter", TestName = "A_class_implementing_an_interface_in_the_solution_that_nobody_uses_is_unreached")]
    [TestCase("[System.CodeDom.Compiler.GeneratedCode(\"t\", \"1\")] class Gen { public void M() { } }", "", TestName = "Generated_code_is_not_listed")]
    public async Task Which_classes_count_as_unreached(string code, string expected)
    {
        var map = await MapAsync(Controller("", "") + "\n" + code);
        Assert.That(UnreachedClasses(map), Is.EqualTo(expected.Length == 0 ? new string[0] : expected.Split(',')));
    }

    [Test]
    public async Task A_class_in_a_generated_file_and_Program_in_a_web_app_are_not_listed_but_Program_in_a_console_program_is_part_of_it()
    {
        var map = await MapAsync(Build(true, Web("Api", AppStart + "[ApiController, Route(\"x\")] public class XController : ControllerBase { [HttpGet] public int Get() => 1; }\npublic class Program { public static void Main() { } }") with { Generated = "public class FromFile { public void M() { } }" }));
        Assert.That(UnreachedClasses(map), Is.Empty);
    }

    // --- Vad som räknas som nått -------------------------------------------------------------------

    [Test]
    public async Task Code_that_is_run_without_a_call_from_an_entry_counts_as_reached()
    {
        var map = await MapAsync(Controller("static string Format(int x) => x.ToString();",
            "var made = new Made(); Pricing.Round(1); var all = new[] { 1 }.Select(Format).ToList(); _ = made.Level;") + """

            public class Made
            {
                public Made() { Setup(); }
                public int Level => Compute();
                void Setup() { }
                int Compute() => 1;
            }
            public static class Pricing { public static int Round(int x) => Inner(x); static int Inner(int x) => x; }
            public class Disposer : IDisposable { public void Dispose() { Cleanup.Run(); } }
            public static class Cleanup { public static void Run() { } }
            public class Pipeline { public Task InvokeAsync(Microsoft.AspNetCore.Http.HttpContext c) { Stamp.Add(); return Task.CompletedTask; } }
            public static class Stamp { public static void Add() { } }
            public static class Boot { public static void Register(Microsoft.Extensions.DependencyInjection.IServiceCollection s) { Defaults.Apply(); } }
            public static class Defaults { public static void Apply() { } }
            public class Never { public void M() { } }
            """);
        Assert.That(UnreachedClasses(map), Is.EqualTo(new[] { "Never" }));
        Assert.That(UnreachedMethods(map), Is.EqualTo(new[] { "Never.M" }),
            "Made (instans, konstruktor, egenskap), Pricing (hjälpmetod), Format (metod som skickas vidare), och det som Dispose, middleware och uppstart anropar nås; bara Never gör det inte");
    }

    [Test]
    public async Task A_method_passed_on_as_a_value_is_reached()
    {
        var map = await MapAsync(Controller("static string Format(int x) => x.ToString();", "var all = new[] { 1 }.Select(Format).ToList();") + "\npublic class Other { public static string Never(int x) => x.ToString(); }");
        Assert.That(UnreachedIds(map), Is.EquivalentTo(new[] { "class:Api/Other", "method:Api/Other.Never" }));
        Assert.That(UnreachedMethods(map), Is.EqualTo(new[] { "Other.Never" }), "Format nås, som metod som skickas vidare");
    }

    [Test]
    public async Task Methods_beyond_the_depth_the_map_follows_still_count_as_reached()
    {
        var chain = string.Join("\n", Enumerable.Range(1, 12).Select(i => $"public static int M{i}() => {(i < 12 ? $"M{i + 1}()" : "0")};"));
        var map = await MapAsync(Controller("", "Chain.M1();") + $"\npublic static class Chain {{\n{chain}\n}}\npublic class Never {{ public void M() {{ }} }}");
        Assert.That(map.Warnings, Has.Some.Contain("Kartan går högst"), "kedjan är längre än kartan följer");
        Assert.That(UnreachedMethods(map), Is.EqualTo(new[] { "Never.M" }), "men alla metoderna i kedjan nås");
    }

    // --- Projekt, klasser och metoder i översikten --------------------------------------------------

    private static Task<CodeMapModel> WithUnreached() => MapAsync(Build(true,
        new Proj("Lib", "public class Store { public int Count() => 1; }\npublic class Orphan { public void Gone() { } }"),
        Web("Api", AppStart + """
            [ApiController, Route("orders")]
            public class OrdersController(IOrderService service) : ControllerBase { [HttpGet] public int Get() => service.Used(); }
            public interface IOrderService { int Used(); }
            public class OrderService(Store store) : IOrderService
            {
                public int Used() => Pricing.Round(store.Count());
                public int Archive() => 2;
            }
            public static class Pricing { public static int Round(int x) => x; public static int Dead() => 0; }
            public class LegacyExporter { public void Export() { } public void Convert() { } }
            public class OrderDto { public int Id { get; set; } }
            """, "Lib"),
        new Proj("Tools", "public class Migrator { public void Run() { } }"),
        new Proj("Contracts", "public record Contract(int Id);\npublic enum Mode { A }")));

    [Test]
    public async Task Unreached_projects_classes_and_methods_are_in_the_overview_with_reached_false_and_no_links()
    {
        var map = await WithUnreached();
        var overview = CodeMapOverview.Build(map);
        Assert.That(CodeMapOverview.Unreached(overview).Select(n => n.Id), Is.EqualTo(new[]
        {
            "method:Api/OrderService.Archive",       // en metod i en klass som nås
            "class:Api/LegacyExporter", "method:Api/LegacyExporter.Export", "method:Api/LegacyExporter.Convert",
            "class:Lib/Orphan", "method:Lib/Orphan.Gone",
            "proj:Tools", "class:Tools/Migrator", "method:Tools/Migrator.Run",
        }), "Pricing nås via hjälpmetoden och syns inte; OrderDto, enum och record är bara data");
        var byId = overview.Nodes.ToDictionary(n => n.Id);
        Assert.That(byId["class:Api/OrderService"].Reached, Is.True);
        Assert.That(byId["proj:Api"].Reached && byId["proj:Lib"].Reached, Is.True, "ett projekt nås om något i det nås");
        Assert.That(byId.ContainsKey("proj:Contracts"), Is.True, "ett projekt med bara data syns ...");
        Assert.That(byId["proj:Contracts"].Reached, Is.True, "... men sägs inte vara onått, det går inte att veta");
        Assert.That(byId.ContainsKey("class:Api/Pricing"), Is.False, "en klass som nås via en hjälpmetod ritas inte");
        Assert.That(overview.Links.Where(l => CodeMapOverview.Unreached(overview).Any(n => n.Id == l.From || n.Id == l.To)), Is.Empty, "de har inga pilar");
        Assert.That(CodeMapOverview.Unreached(overview).SelectMany(n => n.Entries), Is.Empty);
        Assert.That(overview.Nodes.Where(n => n.Kind == OverviewKind.Project).Select(n => n.Name), Is.EqualTo(new[] { "Api", "Lib", "Contracts", "Tools" }),
            "projekten som nås först, i den ordning anropen når dem, sedan de andra efter namn");
    }

    [Test]
    public async Task Json_marks_only_unreached_nodes_with_reached_false()
    {
        var map = await WithUnreached();
        using var doc = JsonDocument.Parse(CodeMapJson.Serialize(map));
        var nodes = doc.RootElement.GetProperty("overview").GetProperty("nodes").EnumerateArray().ToList();
        var marked = nodes.Where(n => n.TryGetProperty("reached", out _)).ToList();
        Assert.That(marked.Select(n => n.GetProperty("reached").GetBoolean()), Is.All.False, "reached finns bara som false");
        Assert.That(marked.Select(n => n.GetProperty("id").GetString()), Is.EquivalentTo(UnreachedIds(map)));
        Assert.That(Walk(doc.RootElement).Count(e => e.ValueKind == JsonValueKind.Null), Is.Zero);
        Assert.That(nodes.Select(n => n.GetProperty("id").GetString()!), Is.Ordered.Using<string>(StringComparer.Ordinal));
    }

    [Test]
    public async Task Text_ends_with_what_no_entry_reaches_short_and_per_project()
    {
        var map = await WithUnreached();
        var text = CodeMapText.Lines(map).ToList();
        var at = text.IndexOf("Nås inte från någon ingång:");
        Assert.That(at, Is.GreaterThan(0));
        Assert.That(text.Skip(at + 1), Is.EqualTo(new[]
        {
            "  Api: LegacyExporter; metoder i klasser som nås: OrderService.Archive",
            "  Lib: Orphan",
            "  Tools (hela projektet): Migrator",
        }));
        Assert.That(text.Take(at), Has.None.Contain("Migrator"), "efter kartan över vägarna");

        var many = await MapAsync(Controller("", "") + "\n" + string.Join("\n", Enumerable.Range(1, 11).Select(i => $"public class Old{i:00} {{ public void M() {{ }} }}")));
        Assert.That(CodeMapText.Lines(many).Last(), Is.EqualTo("  Api: Old01, Old02, Old03, Old04, Old05, Old06, Old07, Old08 och 3 till"), "högst åtta av varje, resten räknas");
        Assert.That(CodeMapText.Lines(await MapAsync(Controller("", ""))), Has.None.Contain("Nås inte"), "inget att säga när allt nås");
    }

    [Test]
    public async Task Mermaid_draws_unreached_parts_dashed_at_the_chosen_level_and_not_for_a_single_entry()
    {
        var map = await WithUnreached();
        var classes = CodeMapMermaid.Write(map, new MermaidOptions());
        Assert.That(classes, Does.Contain("classDef unreached fill:#f4f5f7,stroke:#8a96a5,color:#6b7785,stroke-dasharray:4 3"));
        Assert.That(classes, Does.Match(@"class [^\n]*class_LegacyExporter[^\n]* unreached").And.Match(@"class [^\n]*proj_Tools[^\n]* unreached"));
        Assert.That(classes, Does.Not.Contain("Archive"), "metoderna ritas först på nivån Funktioner");
        var methods = CodeMapMermaid.Write(map, new MermaidOptions(MapLevel.Methods));
        Assert.That(methods, Does.Contain("method_OrderService_Archive[\"Archive()\"]").And.Contain("method_LegacyExporter_Export[\"Export()\"]"));
        Assert.That(methods, Does.Match(@"class [^\n]*method_OrderService_Archive[^\n]* unreached"));
        Assert.That(methods, Does.Match(@"class [^\n]*method_OrderService_Used[^\n]* code"), "det som nås är som förut");
        var projects = CodeMapMermaid.Write(map, new MermaidOptions(MapLevel.Projects));
        Assert.That(projects, Does.Match(@"class [^\n]*proj_Tools[^\n]* unreached").And.Not.Match(@"class [^\n]*proj_Api[^\n]* unreached"));
        var one = CodeMapMermaid.Write(map, MermaidOptions.Parse("methods", null, "GET /orders"));
        Assert.That(one, Does.Not.Contain("unreached").And.Not.Contain("LegacyExporter"), "ett anrops väg har inget som inte nås");
        Assert.That(CodeMapMermaid.Write(await MapAsync(Controller("", "")), new MermaidOptions()), Does.Not.Contain("unreached"));
    }

    // --- Dölja och lämna bort -----------------------------------------------------------------------

    [Test]
    public async Task Hide_works_on_unreached_projects_and_classes_in_the_overview_the_model_and_the_text()
    {
        var map = await WithUnreached();
        var overview = CodeMapOverview.Build(map);
        var hidden = CodeMapOverview.ResolveHidden(overview, ["LegacyExporter", "projekt:Tools"]);
        Assert.That(hidden, Is.EquivalentTo(new[] { "class:Api/LegacyExporter", "proj:Tools" }));
        var after = CodeMapOverview.Hide(overview, hidden).Nodes.Select(n => n.Id).ToList();
        Assert.That(after, Does.Not.Contain("class:Api/LegacyExporter").And.Not.Contain("method:Api/LegacyExporter.Export").And.Not.Contain("proj:Tools").And.Not.Contain("class:Tools/Migrator"));
        Assert.That(after, Does.Contain("class:Lib/Orphan").And.Contain("method:Api/OrderService.Archive"));
        var text = string.Join("\n", CodeMapText.Lines(CodeMapOverview.Hide(map, hidden)));
        Assert.That(text, Does.Not.Contain("LegacyExporter").And.Not.Contain("Migrator").And.Contain("Orphan"));
        Assert.That(CodeMapOverview.HideName(overview.Nodes.Single(n => n.Id == "class:Lib/Orphan"), overview), Is.EqualTo("Lib/Orphan"));
    }

    [Test]
    public async Task WithoutUnreached_leaves_out_unreached_parts_and_keeps_the_rest_and_the_arrows()
    {
        var map = await WithUnreached();
        var overview = CodeMapOverview.Build(map);
        var kept = CodeMapOverview.WithoutUnreached(overview);
        Assert.That(kept.Nodes.Select(n => n.Id), Is.EqualTo(overview.Nodes.Where(n => n.Reached).Select(n => n.Id)));
        Assert.That(kept.Links, Is.EqualTo(overview.Links));
        Assert.That(CodeMapOverview.WithoutUnreached(CodeMapOverview.Build(await MapAsync(Controller("", "")))).Nodes.Count, Is.EqualTo(CodeMapOverview.Build(await MapAsync(Controller("", ""))).Nodes.Count));
        var text = string.Join("\n", CodeMapText.Lines(map with { Code = null }));
        Assert.That(text, Does.Not.Contain("Nås inte"));
    }

    // --- Sidan --------------------------------------------------------------------------------------

    private const string Unreached = "() => SystemMap.cy().nodes('.unreached').map(n => n.id()).sort()";

    [Test]
    public async Task Page_draws_unreached_parts_dashed_and_dimmed_with_a_legend_and_only_the_methods_on_the_level_Funktioner()
    {
        var map = await WithUnreached();
        await WithPageAsync(map, async (page, ready) =>
        {
            Assert.That(await page.IsVisibleAsync("#map-legend"), Is.True);
            Assert.That(await page.InnerTextAsync("#map-legend"), Is.EqualTo("Nås inte från någon ingång"));
            Assert.That(await page.EvaluateAsync<string[]>(Unreached), Is.EqualTo(new[] { "class:Api/LegacyExporter", "class:Lib/Orphan", "class:Tools/Migrator", "proj:Tools" }),
                "på nivån Klasser: klasserna och projektet, inte metoderna");
            var style = await page.EvaluateAsync<string[]>("() => { const n = SystemMap.cy().getElementById('class:Api/LegacyExporter'); return [n.style('border-style'), n.style('opacity')]; }");
            Assert.That(style, Is.EqualTo(new[] { "dashed", "0.6" }), "streckad och dämpad");
            Assert.That(await page.EvaluateAsync<string>("() => SystemMap.cy().getElementById('class:Api/OrderService').style('border-style')"), Is.EqualTo("solid"), "det som nås är som förut");

            // Metoderna syns när klasserna fälls ut, med nivån Funktioner.
            await page.ClickAsync("[data-level='methods']");
            await ready();
            Assert.That(await page.EvaluateAsync<string[]>("() => SystemMap.cy().nodes('.t-method.unreached').map(n => n.id()).sort()"),
                Is.EqualTo(new[] { "method:Api/LegacyExporter.Convert", "method:Api/LegacyExporter.Export", "method:Api/OrderService.Archive", "method:Lib/Orphan.Gone", "method:Tools/Migrator.Run" }));
            Assert.That(await page.EvaluateAsync<string>("() => SystemMap.cy().getElementById('method:Api/OrderService.Used').style('border-style')"), Is.EqualTo("solid"));

            // Texten: titel på kartan och kortet.
            await page.EvaluateAsync("() => SystemMap.cy().getElementById('method:Api/OrderService.Archive').emit('mouseover')");
            Assert.That(await page.GetAttributeAsync("#map-view", "title"), Is.EqualTo("Nås inte från någon ingång"));
            await page.EvaluateAsync("() => SystemMap.cy().getElementById('method:Api/OrderService.Archive').emit('mouseout')");
            Assert.That(await page.GetAttributeAsync("#map-view", "title"), Is.EqualTo(""));
            await page.EvaluateAsync("() => SystemMap.cy().getElementById('method:Api/OrderService.Archive').emit('tap')");
            Assert.That(await page.InnerTextAsync("#map-card"), Does.Contain("Status: Nås inte från någon ingång").And.Not.Contain("Används av anropen"));
            await page.EvaluateAsync("() => SystemMap.cy().getElementById('method:Api/OrderService.Used').emit('tap')");
            Assert.That(await page.InnerTextAsync("#map-card"), Does.Contain("Används av anropen: GET /orders").And.Not.Contain("Nås inte"));

            // Ett valt anrop dämpar dem som allt annat som inte är på vägen.
            await page.SelectOptionAsync("#map-entry", new SelectOptionValue { Label = "GET /orders" });
            await ready();
            Assert.That(await page.EvaluateAsync<int>("() => SystemMap.cy().nodes('.unreached').length"), Is.GreaterThan(0));
            Assert.That(await page.EvaluateAsync<int>("() => SystemMap.cy().nodes('.unreached').not('.dimmed').length"), Is.Zero, "med ett valt anrop är de dämpade som allt annat utanför vägen");
        });
    }

    [Test]
    public async Task Page_has_no_legend_or_toggle_when_everything_is_reached()
    {
        var map = await MapAsync(Build(true, Web("Api", AppStart + "[ApiController, Route(\"x\")] public class XController : ControllerBase { [HttpGet] public int Get() => 1; }")));
        await WithPageAsync(map, async (page, ready) =>
        {
            Assert.That(await page.IsHiddenAsync("#map-legend"), Is.True);
            await page.ClickAsync("#map-show-summary");
            Assert.That(await page.IsHiddenAsync("#map-unreached-field"), Is.True);
            Assert.That(await page.EvaluateAsync<int>("() => SystemMap.cy().nodes('.unreached').length"), Is.Zero);
        });
    }

    [Test]
    public async Task Page_panel_lists_unreached_parts_and_has_a_toggle_that_is_on_and_remembered()
    {
        var map = await WithUnreached();
        await WithPageAsync(map, async (page, ready) =>
        {
            await page.ClickAsync("#map-show-summary");
            Assert.That(await page.InnerTextAsync("#map-unreached-field"), Is.EqualTo("Visa delar som inte nås"));
            Assert.That(await page.IsCheckedAsync("#map-unreached"), Is.True, "från början ibockad");
            Assert.That(await page.EvaluateAsync<string[]>("() => [...document.querySelectorAll('#map-tree label')].map(l => l.textContent)"),
                Does.Contain("LegacyExporter · nås inte").And.Contain("Tools · nås inte").And.Contain("OrderService").And.Not.Contain("OrderService · nås inte"));

            // De går att dölja som andra delar.
            await page.UncheckAsync("input[data-part='class:Api/LegacyExporter']");
            await ready();
            Assert.That(await page.EvaluateAsync<string[]>(Unreached), Does.Not.Contain("class:Api/LegacyExporter").And.Contain("class:Lib/Orphan"));
            Assert.That(await page.InnerTextAsync("#map-hint"), Is.EqualTo("1 dolda"));
            await page.CheckAsync("input[data-part='class:Api/LegacyExporter']");
            await ready();

            // Valet tar bort allt som inte nås, och förklaringen med dem.
            await page.UncheckAsync("#map-unreached");
            await ready();
            Assert.That(await page.EvaluateAsync<int>("() => SystemMap.cy().nodes('.unreached').length"), Is.Zero);
            Assert.That(await page.IsHiddenAsync("#map-legend"), Is.True);
            Assert.That(await page.InnerTextAsync("#map-hint"), Is.EqualTo("inte nådda dolda"));
            Assert.That(await page.EvaluateAsync<string[]>("() => [...document.querySelectorAll('#map-tree input[data-part]')].filter(b => b.disabled).map(b => b.getAttribute('data-part')).sort()"),
                Is.EqualTo(new[] { "class:Api/LegacyExporter", "class:Lib/Orphan", "class:Tools/Migrator", "proj:Tools" }), "kryssrutorna för dem är avbockade och går inte att ändra");
            Assert.That(await page.EvaluateAsync<string[]>("() => SystemMap.cy().nodes('.t-class').map(n => n.id()).sort()"), Is.EqualTo(new[] { "class:Api/OrderService", "class:Api/OrdersController", "class:Lib/Store" }),
                "det som nås är kvar");

            // Det sparas i webbläsaren.
            await page.ReloadAsync();
            await ready();
            Assert.That(await page.EvaluateAsync<int>("() => SystemMap.cy().nodes('.unreached').length"), Is.Zero);
            await page.ClickAsync("#map-show-summary");
            Assert.That(await page.IsCheckedAsync("#map-unreached"), Is.False);

            // Mermaid-filen kan inte följa det som är dolt.
            await page.ClickAsync("#map-mermaid");
            Assert.That(await page.InnerTextAsync("#map-mermaid-status"), Does.Contain("Mermaid-filen kan inte följa det som är dolt").And.Contain("--no-unreached"));

            // Visa allt ger tillbaka dem.
            await page.ClickAsync("#map-show-all");
            await ready();
            Assert.That(await page.EvaluateAsync<int>("() => SystemMap.cy().nodes('.unreached').length"), Is.GreaterThan(0));
            Assert.That(await page.IsCheckedAsync("#map-unreached"), Is.True);
            await page.ReloadAsync();
            await ready();
            Assert.That(await page.EvaluateAsync<int>("() => SystemMap.cy().nodes('.unreached').length"), Is.GreaterThan(0), "också det sparas");
        });
    }

    [Test]
    public async Task Page_written_without_unreached_parts_starts_with_the_toggle_off_and_a_choice_in_the_page_beats_that()
    {
        var map = await WithUnreached();
        await WithPageAsync(map, async (page, ready) =>
        {
            Assert.That(await page.EvaluateAsync<int>("() => SystemMap.cy().nodes('.unreached').length"), Is.Zero);
            await page.ClickAsync("#map-show-summary");
            Assert.That(await page.IsCheckedAsync("#map-unreached"), Is.False);
            await page.CheckAsync("#map-unreached");
            await ready();
            Assert.That(await page.EvaluateAsync<int>("() => SystemMap.cy().nodes('.unreached').length"), Is.GreaterThan(0));
            await page.ReloadAsync();
            await ready();
            Assert.That(await page.EvaluateAsync<int>("() => SystemMap.cy().nodes('.unreached').length"), Is.GreaterThan(0), "valet i sidan gäller före det sidan skrevs med");
        }, showUnreached: false);
    }

    [Test]
    public async Task Page_dropUnreached_is_the_same_rule_as_the_C_sharp_one()
    {
        var map = await WithUnreached();
        var overview = CodeMapOverview.Build(map);
        await WithPageAsync(map, async (page, ready) =>
        {
            var ids = await page.EvaluateAsync<string[]>("() => SystemMap.dropUnreached(JSON.parse(document.getElementById('map-data').textContent).overview).nodes.map(n => n.id)");
            Assert.That(ids, Is.EqualTo(CodeMapOverview.WithoutUnreached(overview).Nodes.OrderBy(n => n.Id, StringComparer.Ordinal).Select(n => n.Id)));
        });
    }
}
