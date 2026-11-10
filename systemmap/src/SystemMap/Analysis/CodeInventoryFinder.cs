using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SystemMap;

/// <summary>
/// Listar systemets egen kod (alla projekt utom testprojekt) och vilken av den som ingen ingång når. En klass räknas om den har minst en
/// metod med kod som kartan följer anrop till. Det som inte räknas (och inte heller kan vara onått):
/// <list type="bullet">
/// <item>klasser som bara är data (inga metoder med kod) och konstruktorer, egenskaper och lokala funktioner (ett projekt med bara sådant syns, men sägs inte vara onått);</item>
/// <item>genererad kod (<c>obj</c>, <c>.g.cs</c>, <c>[GeneratedCode]</c>, <c>[CompilerGenerated]</c>);</item>
/// <item><c>Program</c> och <c>Startup</c> i en webbapp, och Blazor-komponenter (<c>IComponent</c>, händelserna anropas från markupen).</item>
/// </list>
/// Det som ramverket eller uppstarten anropar är inte ingångar på kartan men räknas som nått, och det de anropar också: det är koden som
/// körs utan att någon ingång anropar den. Dessa <i>krokar</i> är metoder som skriver över eller implementerar något utanför solutionen
/// (<c>Dispose</c>, <c>OnModelCreating</c>, <c>IRequestHandler.Handle</c>, <c>IHealthCheck.CheckHealthAsync</c> ...), middleware (<c>Invoke</c>/<c>InvokeAsync</c>),
/// uppstart (metoder som tar eller ger en av ramverkets typer för att sätta upp appen: <c>IServiceCollection</c>, <c>IApplicationBuilder</c>,
/// <c>WebApplication</c> ...), <c>Main</c>, och allt i Blazor-komponenter. Anropen från krokarna följs med en egen <see cref="CallWalker"/>, så att
/// integrationerna kartan visar bara är de som en ingång når.
/// En metod är nådd om anropen har gått genom den (<see cref="CallWalker.Visited"/>); en klass om en av dess metoder är nådd eller om en
/// instans skapas av koden som följts (<see cref="CallWalker.Created"/>).
/// </summary>
internal static class CodeInventoryFinder
{
    /// <summary>Ramverkets typer för att sätta upp en app: en metod som tar eller ger en av dem är uppstart, som körs en gång av <c>Program</c>.</summary>
    private static readonly HashSet<string> StartupTypes = new(StringComparer.Ordinal)
    {
        "IServiceCollection", "IApplicationBuilder", "IEndpointRouteBuilder", "IEndpointConventionBuilder", "RouteGroupBuilder", "WebApplication",
        "WebApplicationBuilder", "IHostBuilder", "IHostApplicationBuilder", "IWebHostBuilder", "IHost", "IWebHost", "IConfigurationBuilder", "ILoggingBuilder",
        "IHealthChecksBuilder", "AuthenticationBuilder", "IMvcBuilder", "IMvcCoreBuilder", "IHttpClientBuilder", "ConfigureHostBuilder", "ConfigureWebHostBuilder",
    };

    public static async Task<CodeInventory> FindAsync(
        IReadOnlyList<Project> projects, ModelCache models, CallWalker walker, CallWalker hooks, IReadOnlySet<string> reachedProjects, string root, CancellationToken ct)
    {
        var found = new List<(string Project, INamedTypeSymbol Type, TypeDeclarationSyntax Declaration)>();
        var everything = new List<INamedTypeSymbol>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var roots = new List<IMethodSymbol>();
        foreach (var project in projects)
        {
            var isApp = AppFinder.IsApp(project, models);
            if (models.CompilationOf(project) is { Options.OutputKind: OutputKind.ConsoleApplication or OutputKind.WindowsApplication } compilation &&
                compilation.GetEntryPoint(ct) is { } main) roots.Add(main);
            foreach (var tree in models.Trees(project))
            {
                if (models.For(tree) is not { } model || IsGeneratedFile(tree.FilePath)) continue;
                foreach (var declaration in tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>())
                {
                    if (declaration is not (ClassDeclarationSyntax or RecordDeclarationSyntax { ClassOrStructKeyword.ValueText: "class" or "" }) ||
                        model.GetDeclaredSymbol(declaration) is not INamedTypeSymbol { TypeKind: TypeKind.Class } type ||
                        !seen.Add(project.Name + "|" + Symbols.TypeKey(type)) || IsGenerated(type)) continue;
                    everything.Add(type);
                    var component = Symbols.Is(type, "Microsoft.AspNetCore.Components.IComponent");
                    var startup = type.Name is "Program" or "Startup" && isApp;
                    foreach (var method in type.GetMembers().OfType<IMethodSymbol>().Where(m => m is { MethodKind: MethodKind.Ordinary, IsAbstract: false } && Symbols.InSource(m) && HasBody(m) && !IsGenerated(m)))
                        if (component || startup || IsHook(method, type)) roots.Add(method);
                    if (!component && !startup) found.Add((project.Name, type, declaration));
                }
            }
        }
        // Krokarna följs för att se vad de når; vad de gör syns inte på kartan. Konstruktorerna körs när en klass används och metoder som skickas
        // vidare som värden anropas av någon annan, och egenskaperna läses av den som använder klassen: allt det följs tills inget nytt dyker upp.
        bool Visited(IMethodSymbol m) => walker.Visited.Contains(Symbols.Key(m)) || hooks.Visited.Contains(Symbols.Key(m));
        bool InUse(INamedTypeSymbol t) => t.GetMembers().OfType<IMethodSymbol>().Any(Visited) || walker.Created.Contains(Symbols.TypeKey(t)) || hooks.Created.Contains(Symbols.TypeKey(t));
        var walkedMembers = new HashSet<string>(StringComparer.Ordinal);
        var pending = roots.DistinctBy(Symbols.Key).ToList();
        while (true)
        {
            if (pending.Count > 0) await hooks.StepsFromAsync(pending, ct);
            pending = [];
            foreach (var type in everything.Where(InUse))
                foreach (var member in type.InstanceConstructors.Concat(type.StaticConstructors)
                             .Concat(type.GetMembers().OfType<IPropertySymbol>().SelectMany(p => new[] { p.GetMethod, p.SetMethod }.OfType<IMethodSymbol>()))
                             .Where(c => c.DeclaringSyntaxReferences.Length > 0))
                    if (walkedMembers.Add(Symbols.Key(member))) pending.Add(member);
            foreach (var group in hooks.Referenced.Concat(walker.Referenced).Where(r => !Visited(r.Value)).DistinctBy(r => r.Key))
                if (walkedMembers.Add(group.Key)) pending.Add(group.Value);   // en gång var: loopen ska ta slut
            if (pending.Count == 0) break;
        }

        var classes = new List<CodeClass>();
        var reached = new HashSet<string>(reachedProjects, StringComparer.Ordinal);
        foreach (var (project, type, declaration) in found)
        {
            var methods = new List<CodeMethod>();
            foreach (var group in type.GetMembers().OfType<IMethodSymbol>().Where(m => IsTracked(m, type)).GroupBy(m => m.Name, StringComparer.Ordinal))
            {
                var first = group.OrderBy(m => m.Locations.First(l => l.IsInSource).SourceSpan.Start).First();
                var (methodFile, methodLine) = Symbols.Where(first.Locations.First(l => l.IsInSource), root);
                methods.Add(new CodeMethod(group.Key, methodFile, methodLine, group.Any(Visited)));
            }
            if (methods.Count == 0) continue;
            var isReached = methods.Any(m => m.Reached) || type.GetMembers().OfType<IMethodSymbol>().Any(Visited) ||
                            walker.Created.Contains(Symbols.TypeKey(type)) || hooks.Created.Contains(Symbols.TypeKey(type));
            var (file, line) = Symbols.Where(declaration.Identifier.GetLocation(), root);
            classes.Add(new CodeClass(project, type.Name, file, line, isReached, [.. methods.OrderBy(m => m.File, StringComparer.Ordinal).ThenBy(m => m.Line)]));
            if (isReached) reached.Add(project);
        }
        // Samma ordning varje gång: projekten efter namn, klasserna efter fil och rad. Ett projekt utan någon klass som räknas (bara data) sägs
        // inte vara onått: det går inte att veta.
        var withCode = classes.Select(c => c.Project).ToHashSet(StringComparer.Ordinal);
        return new CodeInventory(
            [.. projects.Select(p => p.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(p => new CodeProject(p, reached.Contains(p) || !withCode.Contains(p)))],
            [.. classes.OrderBy(c => c.Project, StringComparer.Ordinal).ThenBy(c => c.File, StringComparer.Ordinal).ThenBy(c => c.Line).ThenBy(c => c.Name, StringComparer.Ordinal)]);
    }

    /// <summary>En metod med kod som koden själv anropar (och inte ramverket eller uppstarten).</summary>
    private static bool IsTracked(IMethodSymbol method, INamedTypeSymbol type) =>
        method is { MethodKind: MethodKind.Ordinary, IsImplicitlyDeclared: false, IsAbstract: false, IsExtern: false } && Symbols.InSource(method) &&
        HasBody(method) && !IsGenerated(method) && !IsHook(method, type);

    /// <summary>En metod som ramverket eller uppstarten anropar, inte koden: se klassens beskrivning.</summary>
    private static bool IsHook(IMethodSymbol method, INamedTypeSymbol type)
    {
        for (var o = method.OverriddenMethod; o is not null; o = o.OverriddenMethod)
            if (!IsOwn(o)) return true;   // skriver över ramverkets metod (eller genererad kod, som en gRPC-bas)
        if (method.ExplicitInterfaceImplementations.Any(i => !IsOwn(i))) return true;
        foreach (var iface in type.AllInterfaces.Where(i => !IsOwn(i)))
            foreach (var member in iface.GetMembers().OfType<IMethodSymbol>())
                if (SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(member), method)) return true;
        if (method.Name is "Invoke" or "InvokeAsync" && (type.Name.EndsWith("Middleware", StringComparison.Ordinal) || method.Parameters.FirstOrDefault()?.Type.Name == "HttpContext" ||
                                                          Symbols.Is(type, "Microsoft.AspNetCore.Mvc.ViewComponent"))) return true;
        return method.Parameters.Select(p => p.Type).Append(method.ReturnType).Any(IsStartupType);
    }

    private static bool IsStartupType(ITypeSymbol type)
    {
        type = Symbols.Unwrap(type) ?? type;
        return StartupTypes.Contains(type.Name) && type.ContainingNamespace?.ToDisplayString().StartsWith("Microsoft.", StringComparison.Ordinal) == true;
    }

    /// <summary>Skriven för hand i solutionen. Genererad kod räknas som ramverk: det är inte koden som anropar den.</summary>
    private static bool IsOwn(ISymbol symbol) => Symbols.InSource(symbol) && !IsGenerated(symbol);

    private static bool HasBody(IMethodSymbol method) =>
        method.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).OfType<BaseMethodDeclarationSyntax>().Any(m => m.Body is not null || m.ExpressionBody is not null);

    private static bool IsGenerated(ISymbol symbol) => Symbols.HasAttribute(symbol, "GeneratedCode", "CompilerGenerated") ||
        symbol.DeclaringSyntaxReferences.All(r => IsGeneratedFile(r.SyntaxTree.FilePath));

    private static bool IsGeneratedFile(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase) || normalized.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase) || normalized.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase);
    }
}
