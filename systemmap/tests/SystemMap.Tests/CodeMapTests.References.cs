using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SystemMap.Tests;

/// <summary>Projektreferenserna (csproj) mellan systemets projekt och hur de används: anrop, bara uppstart, annat i koden eller inte alls (kod i minnet).</summary>
public partial class CodeMapTests
{
    private const string ApiCode = """
        using Microsoft.AspNetCore.Mvc;
        using Microsoft.Extensions.DependencyInjection;
        using Infra;
        public static class Start
        {
            public static void Registrera(IServiceCollection s)
            {
                s.AddInfra();
                s.AddScoped<App.IOrderService, App.OrderService>();
            }
        }
        [ApiController, Route("api/[controller]")]
        public class OrdersController(App.IOrderService orders) : ControllerBase
        {
            [HttpPost] public Task Skapa(App.NyOrder o) { var nu = Shared.Clock.Now(); return orders.LaggAsync(o); }
        }
        """;

    /// <summary>Api refererar Application (anrop), Infrastructure (bara uppstart), Shared (en statisk hjälpare) och Legacy (inget). Infrastructure refererar Application (implementerar ett gränssnitt).</summary>
    private static Proj[] RefProjects(string apiCode = ApiCode) =>
    [
        new("Shared", "namespace Shared; public static class Clock { public static DateTime Now() => DateTime.UtcNow; }"),
        new("Legacy", "namespace Legacy; public class Old { public int Go() => 1; }"),
        new("Application", """
            namespace App;
            public record NyOrder(int KundId);
            public interface IOrderRepo { Task SparaAsync(NyOrder o); }
            public interface IOrderService { Task LaggAsync(NyOrder o); }
            public class OrderService(IOrderRepo repo) : IOrderService
            {
                public async Task LaggAsync(NyOrder o) { await repo.SparaAsync(o); }
            }
            """),
        new("Infrastructure", """
            using Microsoft.Extensions.DependencyInjection;
            namespace Infra;
            public class SqlOrderRepo : App.IOrderRepo { public Task SparaAsync(App.NyOrder o) { System.IO.File.WriteAllText("order.txt", "x"); return Task.CompletedTask; } }
            public static class InfraRegistration { public static IServiceCollection AddInfra(this IServiceCollection s) => s.AddScoped<App.IOrderRepo, SqlOrderRepo>(); }
            """, "Application"),
        new("Api", apiCode, "Application", "Infrastructure", "Shared", "Legacy"),
    ];

    private static async Task<CodeMapModel> MapRefsAsync(string apiCode = ApiCode) => await MapAsync(Build(false, RefProjects(apiCode)));

    private static ProjectReference Ref(CodeMapModel map, string from, string to) => map.References.Single(r => r.From == from && r.To == to);

    [Test]
    public async Task Every_reference_between_system_projects_is_classified()
    {
        var map = await MapRefsAsync();
        Assert.That(map.References.Select(r => $"{r.From}>{r.To}:{r.Use}"), Is.EqualTo(new[]
        {
            "Api>Application:Calls", "Api>Infrastructure:Startup", "Api>Legacy:Unused", "Api>Shared:Code", "Infrastructure>Application:Code",
        }), "sorterade på från och till");
    }

    [Test]
    public async Task A_reference_with_calls_counts_the_entries_that_go_through_it()
    {
        var map = await MapRefsAsync();
        var api = Ref(map, "Api", "Application");
        Assert.That((api.Calls, api.CallsBack), Is.EqualTo((1, false)));
        Assert.That(api.Examples, Is.Not.Empty.And.Count.LessThanOrEqualTo(3));
        Assert.That(api.File, Is.EqualTo("Api/Fil1.cs"));
        Assert.That(api.Line, Is.GreaterThan(0));
    }

    [Test]
    public async Task A_reference_that_is_only_used_at_startup_is_startup_with_the_call_as_example_and_its_place()
    {
        var map = await MapRefsAsync();
        var infra = Ref(map, "Api", "Infrastructure");
        Assert.That(infra.Calls, Is.Zero, "anropen går via Application");
        Assert.That(infra.Examples, Is.EqualTo(new[] { "anropar InfraRegistration.AddInfra" }));
        var lines = ApiCode.Split('\n');
        Assert.That((infra.File, infra.Line), Is.EqualTo(("Api/Fil1.cs", Array.FindIndex(lines, l => l.Contains("s.AddInfra()")) + 1)));
    }

    [Test]
    public async Task A_reference_used_as_a_type_or_a_static_helper_is_code()
    {
        var map = await MapRefsAsync();
        var shared = Ref(map, "Api", "Shared");
        Assert.That((shared.Use, shared.Calls), Is.EqualTo((ReferenceUse.Code, 0)));
        Assert.That(shared.Examples, Is.EqualTo(new[] { "anropar Clock.Now" }));
    }

    [Test]
    public async Task A_reference_that_nothing_uses_is_unused_without_examples()
    {
        var legacy = Ref(await MapRefsAsync(), "Api", "Legacy");
        Assert.That((legacy.Use, legacy.Calls, legacy.CallsBack, legacy.Examples, legacy.File, legacy.Line), Is.EqualTo((ReferenceUse.Unused, 0, false, (IReadOnlyList<string>)[], (string?)null, (int?)null)));
    }

    [Test]
    public async Task Implementing_an_interface_counts_as_use_and_calls_that_go_the_other_way_are_flagged()
    {
        var map = await MapRefsAsync();
        var infra = Ref(map, "Infrastructure", "Application");
        Assert.That((infra.Use, infra.Calls, infra.CallsBack), Is.EqualTo((ReferenceUse.Code, 0, true)), "Application anropar Infrastructure, inte tvärtom");
        Assert.That(infra.Examples[0], Is.EqualTo("implementerar IOrderRepo"));
        Assert.That(infra.File, Is.EqualTo("Infrastructure/Fil1.cs"));
        Assert.That(Ref(map, "Api", "Application").CallsBack, Is.False);
    }

    // Vad koden i Api gör med det som Shared deklarerar, och exemplet det ger.
    [TestCase("class P : Shared.IPort { }", "implementerar IPort", TestName = "Reference_use_implementing_an_interface")]
    [TestCase("class P : Shared.Basis { }", "ärver Basis", TestName = "Reference_use_inheriting_a_class")]
    [TestCase("int x = Shared.Help.F();", "anropar Help.F", TestName = "Reference_use_calling_a_static_method")]
    [TestCase("int x = \"a\".Ext();", "anropar Help.Ext", TestName = "Reference_use_calling_an_extension_method")]
    [TestCase("const int Y = Shared.Help.K;", "använder konstanten Help.K", TestName = "Reference_use_a_constant")]
    [TestCase("int x = new Shared.Dto().A;", "använder Dto", TestName = "Reference_use_creating_a_type")]
    [TestCase("[Shared.Mark] class P { }", "använder attributet MarkAttribute", TestName = "Reference_use_an_attribute")]
    [TestCase("System.Collections.Generic.List<Shared.Dto> l = new();", "använder Dto", TestName = "Reference_use_a_generic_argument")]
    [TestCase("string n = nameof(Shared.Dto);", "använder Dto", TestName = "Reference_use_nameof")]
    [TestCase("Type t = typeof(Shared.Dto);", "använder Dto", TestName = "Reference_use_typeof")]
    [TestCase("int x = Shared.Help.Prop;", "använder Help.Prop", TestName = "Reference_use_a_property")]
    public async Task What_the_code_uses_becomes_the_example(string member, string expected)
    {
        var map = await MapAsync(Build(false,
            new Proj("Shared", """
                namespace Shared;
                public interface IPort { }
                public class Basis { }
                public class Dto { public int A; }
                public class MarkAttribute : Attribute { }
                public static class Help { public static int F() => 1; public static int Ext(this string s) => 1; public const int K = 1; public static int Prop => 1; }
                """),
            new Proj("Api", $"using Shared;\npublic class Holder {{ {member} }}", "Shared")));
        var shared = Ref(map, "Api", "Shared");
        Assert.That((shared.Use, shared.Examples[0]), Is.EqualTo((ReferenceUse.Code, expected)));
    }

    [Test]
    public async Task Examples_are_at_most_three_and_plain_use_comes_before_startup()
    {
        var map = await MapAsync(Build(false,
            new Proj("Shared", "namespace Shared; public class A { } public class B { } public class C { } public class D { } public static class Reg { public static Microsoft.Extensions.DependencyInjection.IServiceCollection AddShared(this Microsoft.Extensions.DependencyInjection.IServiceCollection s) => s; }"),
            new Proj("Api", """
                using Microsoft.Extensions.DependencyInjection;
                public static class Start { public static void Registrera(IServiceCollection s) { s.AddShared(); } }
                public class Holder { Shared.A a; Shared.B b; Shared.C c; Shared.D d; }
                """, "Shared")));
        var shared = Ref(map, "Api", "Shared");
        Assert.That(shared.Examples, Is.EqualTo(new[] { "använder A", "använder B", "använder C" }));
        Assert.That(shared.Use, Is.EqualTo(ReferenceUse.Code));
    }

    [Test]
    public async Task Extension_methods_that_set_up_the_app_are_startup_but_other_calls_in_the_same_project_are_not()
    {
        var map = await MapAsync(Build(false,
            new Proj("Shared", "namespace Shared; public static class Reg { public static Microsoft.Extensions.DependencyInjection.IServiceCollection AddShared(this Microsoft.Extensions.DependencyInjection.IServiceCollection s) => s; public static int F() => 1; }"),
            new Proj("Api", "public static class Start { public static void Registrera(Microsoft.Extensions.DependencyInjection.IServiceCollection s) { s.AddShared(); var x = Shared.Reg.F(); } }", "Shared")));
        Assert.That(Ref(map, "Api", "Shared").Use, Is.EqualTo(ReferenceUse.Startup), "också F anropas i en uppstartsmetod");
        var plain = await MapAsync(Build(false,
            new Proj("Shared", "namespace Shared; public static class Reg { public static int F() => 1; }"),
            new Proj("Api", "public class Helper { int X() => Shared.Reg.F(); }", "Shared")));
        Assert.That(Ref(plain, "Api", "Shared").Use, Is.EqualTo(ReferenceUse.Code));
    }

    [Test]
    public async Task A_test_project_is_not_part_of_the_references()
    {
        var solution = Build(false, RefProjects());
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();
        var api = solution.Projects.Single(p => p.Name == "Api");
        var tests = ProjectId.CreateNewId("Tests");
        solution = solution.AddProject(ProjectInfo.Create(tests, VersionStamp.Create(), "Tests", "Tests", LanguageNames.CSharp, filePath: Path.Combine(Root, "Tests", "Tests.csproj"),
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary), metadataReferences: references, projectReferences: [new Microsoft.CodeAnalysis.ProjectReference(api.Id)]));
        var map = await MapAsync(solution);
        Assert.That(map.References.Select(r => r.From).Concat(map.References.Select(r => r.To)), Does.Not.Contain("Tests"));
        Assert.That(map.References, Has.Count.EqualTo(5));
    }

    [Test]
    public async Task A_solution_without_references_has_no_list()
    {
        var map = await MapAsync(Controller("", ""));
        Assert.That(map.References, Is.Empty);
        Assert.That(CodeMapText.Lines(map), Has.None.EqualTo("Projektreferenser:"));
        using var doc = JsonDocument.Parse(CodeMapJson.Serialize(map));
        Assert.That(doc.RootElement.GetProperty("projectReferences").GetArrayLength(), Is.Zero);
    }

    [Test]
    public async Task Json_has_schemaVersion_4_and_the_reference_fields_without_null()
    {
        using var doc = JsonDocument.Parse(CodeMapJson.Serialize(await MapRefsAsync()));
        Assert.That(doc.RootElement.GetProperty("schemaVersion").GetInt32(), Is.EqualTo(4));
        var list = doc.RootElement.GetProperty("projectReferences").EnumerateArray().ToList();
        Assert.That(list.Select(r => r.GetProperty("from").GetString() + ">" + r.GetProperty("to").GetString()), Is.EqualTo(new[]
            { "Api>Application", "Api>Infrastructure", "Api>Legacy", "Api>Shared", "Infrastructure>Application" }));
        var calls = list[0];
        Assert.That(calls.GetProperty("use").GetString(), Is.EqualTo("calls"));
        Assert.That((calls.GetProperty("calls").GetInt32(), calls.GetProperty("callsBack").GetBoolean()), Is.EqualTo((1, false)));
        Assert.That(calls.GetProperty("examples").GetArrayLength(), Is.InRange(1, 3));
        Assert.That(calls.GetProperty("file").GetString(), Is.EqualTo("Api/Fil1.cs"));
        Assert.That(list[1].GetProperty("use").GetString(), Is.EqualTo("startup"));
        Assert.That(list[3].GetProperty("use").GetString(), Is.EqualTo("code"));
        Assert.That(list[4].GetProperty("callsBack").GetBoolean(), Is.True);
        var unused = list[2];
        Assert.That(unused.GetProperty("use").GetString(), Is.EqualTo("unused"));
        Assert.That(unused.TryGetProperty("file", out _) || unused.TryGetProperty("line", out _), Is.False, "null utelämnas");
        Assert.That(unused.GetProperty("examples").GetArrayLength(), Is.Zero);
    }

    [Test]
    public async Task Text_ends_with_the_references_and_lists_the_unused_ones_last()
    {
        var lines = CodeMapText.Lines(await MapRefsAsync()).ToList();
        var from = lines.IndexOf("Projektreferenser:");
        Assert.That(from, Is.GreaterThan(0));
        Assert.That(lines.Skip(from), Is.EqualTo(new[]
        {
            "Projektreferenser:",
            "  Api → Application: används av 1 anrop",
            $"  Api → Infrastructure: bara vid uppstart (anropar InfraRegistration.AddInfra, Api/Fil1.cs:{Ref(await MapRefsAsync(), "Api", "Infrastructure").Line})",
            "  Api → Legacy: används inte",
            "  Api → Shared: används (anropar Clock.Now)",
            "  Infrastructure → Application: används (implementerar IOrderRepo …), anropen går åt andra hållet",
            "",
            "Referenser som inte används:",
            "  Api → Legacy (Api.csproj)",
        }));
    }

    [Test]
    public async Task Mermaid_at_the_level_projects_draws_references_without_calls_as_dashed_arrows()
    {
        var map = await MapRefsAsync();
        var text = CodeMapMermaid.Write(map, new MermaidOptions(MapLevel.Projects));
        Assert.That(text, Does.Contain("proj_Api -.->|\"bara uppstart\"| proj_Infrastructure"));
        Assert.That(text, Does.Contain("proj_Api -.->|\"används inte\"| proj_Legacy"));
        Assert.That(text, Does.Contain("proj_Api -.->|\"används\"| proj_Shared"));
        Assert.That(text, Does.Not.Contain("proj_Application -.->").And.Not.Contain("proj_Api -.->|\"används\"| proj_Application"), "referensen med anrop har sin vanliga pil");
        Assert.That(CodeMapMermaid.Write(map, new MermaidOptions(MapLevel.Classes)), Does.Not.Contain("-.->"));
        Assert.That(CodeMapMermaid.Write(map, new MermaidOptions(MapLevel.Projects, Entry: "POST /api/Orders")), Does.Not.Contain("-.->"), "bara för hela systemet");
    }

    [Test]
    public async Task Hiding_a_project_removes_the_references_to_and_from_it()
    {
        var map = await MapRefsAsync();
        var hidden = HiddenIn(map, "Legacy");
        var shown = CodeMapOverview.Hide(map, hidden);
        Assert.That(shown.References.Select(r => r.To), Does.Not.Contain("Legacy"));
        Assert.That(shown.References, Has.Count.EqualTo(map.References.Count - 1));
    }
}
