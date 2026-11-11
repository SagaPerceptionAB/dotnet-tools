using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SystemMap;

/// <summary>
/// Hur projektreferenserna (csproj) mellan systemets projekt används. För varje referens från A till B (testprojekt räknas inte):
/// <list type="bullet">
/// <item><b>anrop</b>: antalet ingångar vars väg går från kod i A till kod i B, och om någon väg går åt andra hållet;</item>
/// <item><b>användning i koden</b>: A:s syntaxträd gås igenom med semantiska modellen och varje namn som pekar på en typ, metod, egenskap, fält, konstant,
/// attribut, bastyp, gränssnitt, extension-metod, generiskt argument eller <c>nameof</c>/<c>typeof</c> som deklareras i B räknas. Varje projekts träd
/// gås igenom en gång för alla sina referenser;</item>
/// <item><b>bara uppstart</b>: all användning ligger i uppstartskod (<c>Program</c> och <c>Startup</c> i en webbapp och metoder som tar eller ger
/// <c>IServiceCollection</c>, <c>WebApplication</c> ... , samma regel som <see cref="CodeInventoryFinder"/>).</item>
/// </list>
/// Klassen blir <see cref="ReferenceUse.Calls"/> om anrop A till B finns, annars <see cref="ReferenceUse.Startup"/>, annars <see cref="ReferenceUse.Code"/>
/// och annars <see cref="ReferenceUse.Unused"/>. En referens som inte används i koden kan ändå behövas (ett paket som följer med, Razor-tillgångar, analyzers).
/// </summary>
internal static class ProjectReferenceFinder
{
    private const int MaxExamples = 3;

    /// <summary>Ett exempel på användning: texten och var den står.</summary>
    private sealed record Use(string Text, string File, int Line);

    /// <summary>Det som hittats om ett par (A, B): exempel i vanlig kod och i uppstart, var för sig.</summary>
    private sealed class Found
    {
        public readonly List<Use> Plain = [];
        public readonly List<Use> Startup = [];
        public bool Any => Plain.Count + Startup.Count > 0;
        public bool Done => Plain.Count >= MaxExamples;
    }

    public static IReadOnlyList<ProjectReference> Find(
        IReadOnlyList<Project> projects, ModelCache models, IReadOnlyList<EntryPoint> entries, string root, CancellationToken ct)
    {
        var byId = projects.ToDictionary(p => p.Id);
        var byAssembly = new Dictionary<string, Project>(StringComparer.Ordinal);
        foreach (var project in projects)
            if (models.CompilationOf(project) is { } compilation) byAssembly.TryAdd(compilation.AssemblyName ?? project.AssemblyName, project);
        var calls = Calls(entries);

        var result = new List<ProjectReference>();
        foreach (var from in projects)
        {
            // De systemprojekt A refererar, utan dubbletter.
            var targets = from.ProjectReferences.Select(r => byId.GetValueOrDefault(r.ProjectId)).OfType<Project>().DistinctBy(p => p.Id).ToList();
            if (targets.Count == 0) continue;
            var found = Scan(from, targets, byAssembly, models, root, ct);
            foreach (var to in targets)
            {
                var n = calls.GetValueOrDefault((from.Name, to.Name));
                var back = calls.GetValueOrDefault((to.Name, from.Name)) > 0;
                var f = found.GetValueOrDefault(to.Name);
                // Exemplen: det som inte är uppstart först, sedan uppstarten.
                var examples = f is null ? [] : f.Plain.Concat(f.Startup).Take(MaxExamples).ToList();
                var use = n > 0 ? ReferenceUse.Calls : f is null || !f.Any ? ReferenceUse.Unused : f.Plain.Count > 0 ? ReferenceUse.Code : ReferenceUse.Startup;
                var first = examples.FirstOrDefault();
                result.Add(new ProjectReference(from.Name, to.Name, use, n, back, examples.Select(e => e.Text).ToList(), first?.File, first?.Line));
            }
        }
        return [.. result.OrderBy(r => r.From, StringComparer.Ordinal).ThenBy(r => r.To, StringComparer.Ordinal)];
    }

    /// <summary>Antal ingångar vars väg går från kod i ett projekt till kod i ett annat, per par (från, till).</summary>
    private static Dictionary<(string, string), int> Calls(IReadOnlyList<EntryPoint> entries)
    {
        var counts = new Dictionary<(string, string), int>();
        foreach (var entry in entries)
        {
            var pairs = new HashSet<(string, string)>();
            void Walk(string project, IEnumerable<Step> steps)
            {
                foreach (var step in steps)
                {
                    if (step.Integration is not null) continue;   // ett anrop ut ur systemet är inget projekt
                    if (step.Project != project) pairs.Add((project, step.Project));
                    Walk(step.Project, step.Steps);
                }
            }
            Walk(entry.Project, entry.Steps);
            foreach (var pair in pairs) counts[pair] = counts.GetValueOrDefault(pair) + 1;
        }
        return counts;
    }

    /// <summary>
    /// Går igenom projektets träd en gång och samlar vad det använder i de andra projekten (<paramref name="targets"/>), per projektnamn. Slutar när
    /// alla har tre exempel i vanlig kod: mer behövs inte för att veta att referensen används och hur.
    /// </summary>
    private static Dictionary<string, Found> Scan(
        Project from, IReadOnlyList<Project> targets, Dictionary<string, Project> byAssembly, ModelCache models, string root, CancellationToken ct)
    {
        var found = targets.ToDictionary(t => t.Name, _ => new Found(), StringComparer.Ordinal);
        var wanted = targets.Select(t => models.CompilationOf(t)?.AssemblyName ?? t.AssemblyName).ToHashSet(StringComparer.Ordinal);
        var isApp = AppFinder.IsApp(from, models);
        foreach (var tree in models.Trees(from))
        {
            if (models.For(tree) is not { } model) continue;
            ct.ThrowIfCancellationRequested();
            foreach (var name in tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>())
            {
                if (name.IsVar || Symbol(model, name) is not { } symbol || symbol is INamedTypeSymbol && IsQualifier(name)) continue;
                var assembly = symbol.ContainingAssembly?.Name;
                if (assembly is null || !wanted.Contains(assembly) || !byAssembly.TryGetValue(assembly, out var to) || !found.TryGetValue(to.Name, out var f) || f.Done) continue;
                var startup = InStartup(name, model, isApp);
                var list = startup ? f.Startup : f.Plain;
                if (list.Count >= MaxExamples) continue;
                var text = Describe(name, symbol);
                if (list.Any(u => u.Text == text)) continue;
                var (file, line) = Symbols.Where(name.GetLocation(), root);
                list.Add(new Use(text, file, line));
            }
            if (found.Values.All(f => f.Done)) break;
        }
        return found;
    }

    /// <summary>Det namnet pekar på, om det är en typ eller en medlem (inte en lokal variabel, en parameter eller en namnrymd).</summary>
    private static ISymbol? Symbol(SemanticModel model, SimpleNameSyntax name)
    {
        var info = model.GetSymbolInfo(name);
        var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
        if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } constructor) symbol = constructor.ContainingType;
        if (symbol is IMethodSymbol { ReducedFrom: { } reduced }) symbol = reduced;   // extension-metoden som anropas som en instansmetod
        return symbol is INamedTypeSymbol or IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol ? symbol.OriginalDefinition : null;
    }

    /// <summary>Står namnet i uppstartskod: <c>Program</c> och <c>Startup</c> i en webbapp (också satserna på översta nivån), eller en metod som tar eller ger uppstartstyper?</summary>
    private static bool InStartup(SyntaxNode node, SemanticModel model, bool isApp)
    {
        for (var n = node; n is not null; n = n.Parent)
        {
            switch (n)
            {
                case GlobalStatementSyntax:
                    return isApp;
                case BaseMethodDeclarationSyntax when model.GetDeclaredSymbol(n) is IMethodSymbol m && CodeInventoryFinder.IsStartupMethod(m):
                    return true;
                case TypeDeclarationSyntax when model.GetDeclaredSymbol(n) is INamedTypeSymbol t && CodeInventoryFinder.IsStartupClass(t, isApp):
                    return true;
            }
        }
        return false;
    }

    /// <summary>Vad koden gör med symbolen, t.ex. "implementerar IOrderRepository" eller "anropar ServiceCollectionExtensions.AddShopInfrastructure".</summary>
    private static string Describe(SimpleNameSyntax name, ISymbol symbol)
    {
        var member = symbol.ContainingType is { } owner && symbol is not INamedTypeSymbol ? $"{owner.Name}.{symbol.Name}" : symbol.Name;
        if (symbol is INamedTypeSymbol type)
        {
            if (name.Ancestors().OfType<BaseListSyntax>().Any() && name.Parent is not TypeArgumentListSyntax && IsBase(name))
                return type.TypeKind == TypeKind.Interface ? $"implementerar {type.Name}" : $"ärver {type.Name}";
            if (name.Ancestors().OfType<AttributeSyntax>().Any(a => a.Name.Span.Contains(name.Span))) return $"använder attributet {type.Name}";
            return $"använder {type.Name}";
        }
        return symbol switch
        {
            IMethodSymbol => $"anropar {member}",
            IFieldSymbol { IsConst: true } => $"använder konstanten {member}",
            _ => $"använder {member}",
        };
    }

    /// <summary>Står typen bara före en punkt (<c>Help.F()</c>)? Medlemmen efter punkten är då det koden använder, och ger exemplet.</summary>
    private static bool IsQualifier(SimpleNameSyntax name)
    {
        SyntaxNode top = name;
        if (top.Parent is MemberAccessExpressionSyntax access && access.Name == name || top.Parent is QualifiedNameSyntax qualified && qualified.Right == name) top = top.Parent;
        return top.Parent is MemberAccessExpressionSyntax outer && outer.Expression == top;
    }

    /// <summary>Är namnet själva bastypen i en baslista (inte ett typargument i den)?</summary>
    private static bool IsBase(SimpleNameSyntax name)
    {
        SyntaxNode node = name;
        while (node.Parent is QualifiedNameSyntax or AliasQualifiedNameSyntax) node = node.Parent;
        return node.Parent is SimpleBaseTypeSyntax or PrimaryConstructorBaseTypeSyntax;
    }
}
