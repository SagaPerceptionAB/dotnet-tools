using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SystemMap;

/// <summary>
/// En ingång och vägen som tar dit (en per HTTP-metod och route). <paramref name="Roots"/> är metoderna vars anrop följs:
/// en action, en lambda i ett minimal API, en sidas hanterare, en bakgrundstjänsts ExecuteAsync eller en meddelandehanterares Consume.
/// <paramref name="Name"/> är namnet på en ingång som inte är HTTP (<c>Bakgrund: OrderCleanupWorker</c>); <paramref name="Method"/> och
/// <paramref name="CodePath"/> är då tomma.
/// </summary>
internal sealed record EntryCandidate(
    EntryKind Kind, IReadOnlyList<IMethodSymbol> Roots, string Method, string CodePath, string Project, string Handler, string File, int Line,
    DataShape? Input, DataShape? Output, string? Name = null)
{
    /// <summary>Nyckel för anropen som följs (lika för ingångar som delar hanterare).</summary>
    public string RootsKey => string.Join("|", Roots.Select(Symbols.Key));
}

/// <summary>
/// Hittar ingångarna: actions i controllers med attributrouting (<c>[Route]</c>, <c>[HttpPost]</c> ...), både ASP.NET Core
/// och ASP.NET Web API 2 (<c>ApiController</c>, <c>[RoutePrefix]</c>), minimal API:er (<see cref="MinimalApiFinder"/>) samt
/// Razor Pages och Blazor-sidor (<see cref="PageFinder"/>), samt det som inte är HTTP: bakgrundstjänster och konsolprogram
/// (<see cref="HostedFinder"/>), meddelandehanterare och jobb (<see cref="MessagingFinder"/>) och gRPC-tjänster och SignalR-hubbar
/// (<see cref="RpcFinder"/>). Konventionell routing känns inte igen än.
/// </summary>
internal static class EntryPointFinder
{
    private static readonly string[] Verbs = ["Get", "Post", "Put", "Delete", "Patch", "Head", "Options"];
    private static readonly string[] ResultMethods =
        ["Ok", "Created", "CreatedAtAction", "CreatedAtRoute", "Accepted", "AcceptedAtAction", "AcceptedAtRoute", "StatusCode", "Json", "Content"];

    public static List<EntryCandidate> Find(IEnumerable<Project> projects, ModelCache models, CallSites sites, ServiceResolver services, string root, List<string> warnings)
    {
        var projectList = projects.ToList();
        var types = Types(projectList, models);
        var entries = new List<EntryCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in projectList)
            foreach (var tree in models.Trees(project))
            {
                if (models.For(tree) is not { } model) continue;
                foreach (var declaration in tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
                {
                    if (model.GetDeclaredSymbol(declaration) is not INamedTypeSymbol type || !IsController(type) || !seen.Add(Symbols.TypeKey(type))) continue;
                    entries.AddRange(Actions(type, project.Name, models, services, root, warnings));
                }
            }
        entries.AddRange(MinimalApiFinder.Find(models, sites, services, root, warnings));
        entries.AddRange(PageFinder.Find(projectList, models, root));
        entries.AddRange(HostedFinder.Background(types, root));
        entries.AddRange(MessagingFinder.Find(types, projectList, models, sites, services, root));
        entries.AddRange(RpcFinder.Find(types, sites, root));
        entries.AddRange(HostedFinder.Console(projectList, models, entries, root));
        return entries;
    }

    /// <summary>Klasserna i projekten (en gång per klass, även om den är delad i flera filer), med projektet de hör till.</summary>
    internal static List<(INamedTypeSymbol Type, string Project)> Types(IEnumerable<Project> projects, ModelCache models)
    {
        var types = new List<(INamedTypeSymbol, string)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in projects)
            foreach (var tree in models.Trees(project))
            {
                if (models.For(tree) is not { } model) continue;
                foreach (var declaration in tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
                    if (model.GetDeclaredSymbol(declaration) is INamedTypeSymbol type && seen.Add(Symbols.TypeKey(type))) types.Add((type, project.Name));
            }
        return types;
    }

    private static bool IsController(INamedTypeSymbol type) =>
        type is { TypeKind: TypeKind.Class, IsAbstract: false, DeclaredAccessibility: Accessibility.Public } &&
        !Symbols.HasAttribute(type, "NonController") &&
        (Symbols.Is(type, "Microsoft.AspNetCore.Mvc.ControllerBase", "System.Web.Http.ApiController", "System.Web.Mvc.Controller") ||
         Bases(type).Any(t => Symbols.HasAttribute(t, "ApiController")));

    private static IEnumerable<EntryCandidate> Actions(INamedTypeSymbol type, string project, ModelCache models, ServiceResolver services, string root, List<string> warnings)
    {
        var controller = type.Name.EndsWith("Controller") ? type.Name[..^"Controller".Length] : type.Name;
        var prefixes = ClassRoutes(type);
        var unrouted = false;
        foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
        {
            if (method is not { MethodKind: MethodKind.Ordinary, DeclaredAccessibility: Accessibility.Public, IsStatic: false } ||
                Symbols.HasAttribute(method, "NonAction") || !Symbols.InSource(method)) continue;

            var verbs = HttpAttributes(method);
            var routes = Templates(method, "Route");
            if (verbs.Count == 0)
            {
                if (routes.Count == 0) { unrouted = true; continue; }
                verbs = [("ANY", null)];
            }

            var action = AttributeString(method, "ActionName") ?? (method.Name.EndsWith("Async") ? method.Name[..^5] : method.Name);
            var (file, line) = Symbols.Where(method.Locations.First(l => l.IsInSource), root);
            var input = Input(method, services);
            var output = Output(method, models);
            var kind = ReturnsView(method, models) ? EntryKind.MvcView : EntryKind.Controller;
            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (verb, template) in verbs)
                foreach (var t in template is not null ? [template] : routes.Count > 0 ? routes.Cast<string?>() : [null])
                    foreach (var prefix in prefixes)
                    {
                        var path = RouteTemplate.Combine(prefix, t, controller, action);
                        if (paths.Add(verb + " " + path))
                            yield return new EntryCandidate(kind, [method], verb, path, project, $"{type.Name}.{method.Name}", file, line, input, output);
                    }
        }
        if (unrouted)
            warnings.Add($"{type.Name} har actions utan route-attribut (konventionell routing). De visas inte på kartan.");
    }

    /// <summary>Klassens route-mallar (<c>[Route]</c>, eller <c>[RoutePrefix]</c> i Web API 2), även ärvda. Ingen mall ger en tom.</summary>
    private static List<string?> ClassRoutes(INamedTypeSymbol type)
    {
        foreach (var t in Bases(type))
        {
            var routes = Templates(t, "Route").Concat(Templates(t, "RoutePrefix")).Cast<string?>().ToList();
            if (routes.Count > 0) return routes;
        }
        return [null];
    }

    private static IEnumerable<INamedTypeSymbol> Bases(INamedTypeSymbol type)
    {
        for (var t = type; t is not null && !Symbols.FullName(t).StartsWith("Microsoft.AspNetCore.Mvc.") && !Symbols.FullName(t).StartsWith("System."); t = t.BaseType)
            yield return t;
    }

    /// <summary>[HttpGet], [HttpPost("{id}")], [AcceptVerbs("GET", "POST")] ...: HTTP-metoden och mallen (om någon).</summary>
    private static List<(string Verb, string? Template)> HttpAttributes(IMethodSymbol method)
    {
        var result = new List<(string, string?)>();
        foreach (var attribute in method.GetAttributes())
        {
            var name = attribute.AttributeClass?.Name.Replace("Attribute", "") ?? "";
            if (name.StartsWith("Http") && Verbs.Contains(name[4..]))
                result.Add((name[4..].ToUpperInvariant(), attribute.ConstructorArguments.FirstOrDefault() is { Value: string t } ? t : null));
            else if (name == "AcceptVerbs")
            {
                var route = attribute.NamedArguments.FirstOrDefault(a => a.Key == "Route").Value.Value as string;
                foreach (var argument in attribute.ConstructorArguments)
                    foreach (var verb in argument.Kind == TypedConstantKind.Array ? argument.Values.Select(v => v.Value as string) : [argument.Value as string])
                        if (verb is not null) result.Add((verb.ToUpperInvariant(), route));
            }
        }
        return result;
    }

    private static List<string> Templates(ISymbol symbol, string attribute) =>
        symbol.GetAttributes()
            .Where(a => a.AttributeClass?.Name == attribute + "Attribute" || a.AttributeClass?.Name == attribute)
            .Select(a => a.ConstructorArguments.FirstOrDefault().Value as string)
            .OfType<string>()
            .ToList();

    private static string? AttributeString(ISymbol symbol, string attribute) => Templates(symbol, attribute).FirstOrDefault();

    /// <summary>En action som returnerar en vy (<c>ViewResult</c>, eller <c>return View(...)</c> i en controller med vyer).</summary>
    private static bool ReturnsView(IMethodSymbol method, ModelCache models)
    {
        if (Symbols.Is(Symbols.Unwrap(method.ReturnType), "Microsoft.AspNetCore.Mvc.ViewResult", "Microsoft.AspNetCore.Mvc.PartialViewResult")) return true;
        foreach (var reference in method.DeclaringSyntaxReferences)
        {
            if (models.For(reference.SyntaxTree) is not { } model) continue;
            foreach (var invocation in reference.GetSyntax().DescendantNodes().OfType<InvocationExpressionSyntax>())
                if (Symbols.InvokedName(invocation) is "View" or "PartialView" && Symbols.Invoked(invocation, model)?.ContainingType is { } owner &&
                    Symbols.Is(owner, "Microsoft.AspNetCore.Mvc.Controller"))
                    return true;
        }
        return false;
    }

    /// <summary>Indata: parametern med [FromBody] (eller en egen [ModelBinder]), annars första parametern av en typ i solutionen som kan vara kroppen (se <see cref="ParameterSources"/>).</summary>
    private static DataShape? Input(IMethodSymbol method, ServiceResolver services)
    {
        var body = method.Parameters.FirstOrDefault(p => Symbols.HasAttribute(p, "FromBody", "ModelBinder"));
        return DataShapes.Of(body?.Type) ??
               method.Parameters.Where(p => ParameterSources.CanBeBody(p, services)).Select(p => DataShapes.Of(p.Type)).FirstOrDefault(s => s is not null);
    }

    /// <summary>Utdata: returtypen (<c>ActionResult&lt;T&gt;</c>), <c>[ProducesResponseType(typeof(T), ...)]</c> eller värdet i <c>Ok(x)</c>.</summary>
    internal static DataShape? Output(IMethodSymbol method, ModelCache models)
    {
        if (DataShapes.Of(method.ReturnType) is { } returned) return returned;
        foreach (var attribute in method.GetAttributes().Where(a => a.AttributeClass?.Name is "ProducesResponseTypeAttribute" or "ProducesAttribute"))
            if (attribute.ConstructorArguments.FirstOrDefault(a => a.Value is ITypeSymbol).Value is ITypeSymbol t && DataShapes.Of(t) is { } produced)
                return produced;
        foreach (var reference in method.DeclaringSyntaxReferences)
        {
            var syntax = reference.GetSyntax();
            if (models.For(syntax.SyntaxTree) is not { } model) continue;
            foreach (var invocation in syntax.DescendantNodes().OfType<InvocationExpressionSyntax>())
                if (Symbols.InvokedName(invocation) is { } name && ResultMethods.Contains(name) && invocation.ArgumentList.Arguments.LastOrDefault() is { } last &&
                    DataShapes.Of(model.GetTypeInfo(last.Expression).Type) is { } value)
                    return value;
        }
        return null;
    }
}
