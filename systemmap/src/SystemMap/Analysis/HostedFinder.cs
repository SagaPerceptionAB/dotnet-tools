using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SystemMap;

/// <summary>
/// Hittar ingångar som inte tas emot som HTTP-anrop utan sätts igång av värden: bakgrundstjänster (<c>BackgroundService</c> och
/// <c>IHostedService</c>) och konsolprogram. Ett konsolprogram är <c>Main</c> (eller satserna på översta nivån) i ett projekt som inte är en
/// webbapp, inte sätter upp en värd (<c>Host.CreateApplicationBuilder</c> ...) och inte har några andra ingångar: i en webbapp och i en worker
/// är <c>Program</c> uppstart och inget som kartan följer.
/// </summary>
internal static class HostedFinder
{
    /// <summary>Metoderna som värden anropar, med den som gör jobbet först. StopAsync och livscykelmetoderna följs bara om klassen skriver egna.</summary>
    private static readonly string[] HostedMethods = ["ExecuteAsync", "StartAsync", "StopAsync", "StartingAsync", "StartedAsync", "StoppingAsync", "StoppedAsync"];

    public static IEnumerable<EntryCandidate> Background(IEnumerable<(INamedTypeSymbol Type, string Project)> types, string root)
    {
        foreach (var (type, project) in types)
        {
            if (type is not { TypeKind: TypeKind.Class, IsAbstract: false } ||
                !Symbols.Is(type, "Microsoft.Extensions.Hosting.BackgroundService", "Microsoft.Extensions.Hosting.IHostedService")) continue;
            var roots = HostedMethods.Select(name => Declared(type, name)).OfType<IMethodSymbol>().DistinctBy(Symbols.Key).ToList();
            if (roots.Count == 0) continue;
            var (file, line) = Symbols.Where(roots[0], root);
            yield return new EntryCandidate(EntryKind.Background, roots, "", "", project, $"{type.Name}.{roots[0].Name}", file, line, null, null, "Bakgrund: " + type.Name, type);
        }
    }

    public static IEnumerable<EntryCandidate> Console(IReadOnlyList<Project> projects, ModelCache models, IReadOnlyList<EntryCandidate> found, string root)
    {
        foreach (var project in projects)
        {
            if (found.Any(e => e.Project == project.Name) || AppFinder.IsApp(project, models)) continue;
            if (models.EntryPointOf(project, CancellationToken.None) is not { } main || IsHost(project, models)) continue;
            var location = main.Locations.FirstOrDefault(l => l.IsInSource) ?? FirstStatement(models.CompilationOf(project)!)?.GetLocation();
            if (location is null) continue;
            var (file, line) = Symbols.Where(location, root);
            yield return new EntryCandidate(EntryKind.Console, [main], "", "", project.Name, $"{main.ContainingType.Name}.Main", file, line, null, null, "Konsol: " + project.Name);
        }
    }

    /// <summary>Ett program vars Main sätter upp en värd (se <see cref="StartsHost"/>).</summary>
    internal static bool IsHost(Project project, ModelCache models) =>
        models.Fact(project, "host", () => models.EntryPointOf(project, CancellationToken.None) is { } main && StartsHost(main, models));

    /// <summary>Main som sätter upp en värd (<c>Host.CreateApplicationBuilder</c>, <c>AddHostedService</c> ...) är uppstart av en worker, inget konsolprogram.</summary>
    private static bool StartsHost(IMethodSymbol main, ModelCache models)
    {
        foreach (var reference in main.DeclaringSyntaxReferences)
        {
            if (models.For(reference.SyntaxTree) is not { } model) continue;
            var nodes = reference.GetSyntax() is CompilationUnitSyntax unit
                ? unit.Members.OfType<GlobalStatementSyntax>().SelectMany(g => g.DescendantNodes())
                : reference.GetSyntax().DescendantNodes();
            foreach (var id in nodes.OfType<IdentifierNameSyntax>())
                if ((model.GetSymbolInfo(id).Symbol ?? model.GetSymbolInfo(id).CandidateSymbols.FirstOrDefault()) is { } symbol &&
                    (symbol as INamedTypeSymbol ?? symbol.ContainingType)?.ContainingNamespace?.ToDisplayString() is { } ns &&
                    (ns == "Microsoft.Extensions.Hosting" || ns.StartsWith("Microsoft.Extensions.Hosting.", StringComparison.Ordinal)))
                    return true;
        }
        return false;
    }

    private static SyntaxNode? FirstStatement(Compilation compilation) =>
        compilation.SyntaxTrees.SelectMany(t => t.GetRoot().DescendantNodes(n => n is CompilationUnitSyntax)).OfType<GlobalStatementSyntax>().FirstOrDefault();

    /// <summary>
    /// Metoden med namnet som klassen (eller en bas i solutionen) själv skriver, även som explicit gränssnittsimplementation
    /// (<c>Task IHostedService.StartAsync</c>). Den som är mest härledd gäller.
    /// </summary>
    internal static IMethodSymbol? Declared(INamedTypeSymbol type, string name)
    {
        for (var t = type; t is not null && Symbols.InSource(t); t = t.BaseType)
        {
            var method = t.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(m =>
                m is { IsAbstract: false, MethodKind: MethodKind.Ordinary or MethodKind.ExplicitInterfaceImplementation } && Symbols.InSource(m) &&
                (m.Name == name || m.ExplicitInterfaceImplementations.Any(i => i.Name == name)));
            if (method is not null) return method;
        }
        return null;
    }
}
