using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SystemMap;

/// <summary>
/// Hittar sidor: Razor Pages (<c>.cshtml</c> med <c>@page</c>) och Blazor-sidor (<c>.razor</c> med <c>@page</c>). Razors
/// källgenerator ger klasser med route-attribut som läses som kod: Blazor-sidans <c>[Route]</c> och Razor Page-klassens
/// <c>RazorCompiledItemMetadata</c> ("RouteTemplate" och "Identifier"). Filen och raden är <c>@page</c>-raden.
/// </summary>
internal static class PageFinder
{
    private static readonly string[] Verbs = ["Get", "Post", "Put", "Delete", "Patch", "Head", "Options"];
    private static readonly string[] Lifecycle = ["OnInitialized", "OnInitializedAsync", "OnParametersSet", "OnParametersSetAsync"];
    private static readonly string[] SubmitEvents = ["OnValidSubmit", "OnSubmit", "OnInvalidSubmit"];

    public static IEnumerable<EntryCandidate> Find(IEnumerable<Project> projects, ModelCache models, string root)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in projects)
            foreach (var tree in models.Trees(project))
            {
                if (models.For(tree) is not { } model) continue;
                foreach (var declaration in tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
                {
                    if (model.GetDeclaredSymbol(declaration) is not INamedTypeSymbol type || !seen.Add(Symbols.TypeKey(type))) continue;
                    var entries = BlazorPage(type, project.Name, models, root) ?? RazorPage(type, project, root);
                    if (entries is not null)
                        foreach (var entry in entries) yield return entry;
                }
            }
    }

    // --- Blazor ----------------------------------------------------------------------------

    private static List<EntryCandidate>? BlazorPage(INamedTypeSymbol type, string project, ModelCache models, string root)
    {
        if (!Symbols.Is(type, "Microsoft.AspNetCore.Components.IComponent")) return null;
        var routes = type.GetAttributes().Where(a => a.AttributeClass is { } c && Symbols.FullName(c) == "Microsoft.AspNetCore.Components.RouteAttribute" &&
                                                     a.ConstructorArguments is [{ Value: string }]).ToList();
        if (routes.Count == 0) return null;

        var lifecycle = type.GetMembers().OfType<IMethodSymbol>().Where(m => m.IsOverride && Lifecycle.Contains(m.Name) && Symbols.InSource(m)).ToList();
        var form = type.GetMembers().FirstOrDefault(m => Symbols.HasAttribute(m, "SupplyParameterFromForm")) as IPropertySymbol;
        var hasForm = form is not null || HasEditForm(type);
        var submit = hasForm ? SubmitHandler(type, models) : null;
        var result = new List<EntryCandidate>();
        foreach (var route in routes)
        {
            var path = RouteTemplate.Combine(null, (string)route.ConstructorArguments[0].Value!, "", "");
            var (file, line) = Locate(route, root);
            var get = lifecycle.FirstOrDefault(m => !m.Name.EndsWith("Async", StringComparison.Ordinal)) ?? lifecycle.FirstOrDefault();
            result.Add(new EntryCandidate(EntryKind.BlazorPage, lifecycle, "GET", path, project, $"{type.Name}.{get?.Name ?? "OnInitialized"}", file, line, null, null));
            if (!hasForm) continue;
            var (postFile, postLine) = submit is { MethodKind: MethodKind.Ordinary } m ? Symbols.Where(m.Locations.First(l => l.IsInSource), root) : (file, line);
            result.Add(new EntryCandidate(EntryKind.BlazorPage, submit is null ? [] : [submit], "POST", path, project,
                $"{type.Name}.{(submit is { MethodKind: MethodKind.Ordinary } ? submit.Name : "OnValidSubmit")}", postFile, postLine,
                form is null ? null : DataShapes.Of(form.Type), null));
        }
        return result;
    }

    private static bool HasEditForm(INamedTypeSymbol type) =>
        type.DeclaringSyntaxReferences.Any(r => r.GetSyntax().DescendantNodes().OfType<IdentifierNameSyntax>().Any(i => i.Identifier.ValueText == "EditForm"));

    /// <summary>
    /// Metoden som tar emot formuläret: i den genererade koden är det <c>AddComponentParameter(..., nameof(EditForm.OnValidSubmit), ...)</c>
    /// med metoden (eller en lambda) i tredje argumentet.
    /// </summary>
    private static IMethodSymbol? SubmitHandler(INamedTypeSymbol type, ModelCache models)
    {
        foreach (var reference in type.DeclaringSyntaxReferences)
        {
            if (models.For(reference.SyntaxTree) is not { } model) continue;
            foreach (var call in reference.GetSyntax().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                // nameof(EditForm.OnValidSubmit): namnet är medlemmen efter punkten.
                if (Symbols.InvokedName(call) != "AddComponentParameter" || call.ArgumentList.Arguments is not [_, var name, var value] ||
                    name.Expression.DescendantNodes().OfType<MemberAccessExpressionSyntax>().FirstOrDefault()?.Name.Identifier.ValueText is not { } member ||
                    !SubmitEvents.Contains(member)) continue;
                foreach (var node in value.Expression.DescendantNodesAndSelf())
                {
                    if (node is AnonymousFunctionExpressionSyntax lambda && model.GetSymbolInfo(lambda).Symbol is IMethodSymbol anonymous) return anonymous;
                    if (node is IdentifierNameSyntax id && model.GetSymbolInfo(id).Symbol is IMethodSymbol { MethodKind: MethodKind.Ordinary } method &&
                        SymbolEqualityComparer.Default.Equals(method.ContainingType, type) && Symbols.InSource(method))
                        return method;
                }
            }
        }
        return null;
    }

    // --- Razor Pages -----------------------------------------------------------------------

    private static List<EntryCandidate>? RazorPage(INamedTypeSymbol page, Project project, string root)
    {
        var metadata = new Dictionary<string, AttributeData>(StringComparer.Ordinal);
        foreach (var a in page.GetAttributes())
            if (a.AttributeClass?.Name == "RazorCompiledItemMetadataAttribute" && a.ConstructorArguments is [{ Value: string key }, _])
                metadata[key] = a;
        if (!metadata.TryGetValue("Identifier", out var identifier) || !Symbols.Is(page, "Microsoft.AspNetCore.Mvc.RazorPages.Page")) return null;
        // @page utan mall ger inget RouteTemplate.
        var template = metadata.TryGetValue("RouteTemplate", out var routeAttribute) ? routeAttribute.ConstructorArguments[1].Value as string ?? "" : "";
        var file = identifier.ConstructorArguments[1].Value as string ?? "";
        var route = PageRoute(file, template);
        var (pageFile, pageLine) = routeAttribute is not null
            ? Locate(routeAttribute, root)
            : (Path.GetRelativePath(root, Path.Combine(Path.GetDirectoryName(project.FilePath ?? "") ?? "", file.TrimStart('/'))).Replace(Path.DirectorySeparatorChar, '/'), 1);

        // Hanterarna står i sidans modell (@model), annars i sidan själv (@functions).
        // Utan @model är sidan själv modellen.
        var model = page.GetMembers("Model").OfType<IPropertySymbol>().FirstOrDefault()?.Type as INamedTypeSymbol;
        if (SymbolEqualityComparer.Default.Equals(model, page)) model = null;
        var owner = model ?? page;
        var ownerName = model?.Name ?? Path.GetFileNameWithoutExtension(file) + "Model";
        var handlers = new List<(string Verb, string Name, IMethodSymbol Method)>();
        for (var t = owner; t is not null && Symbols.InSource(t); t = t.BaseType)
            foreach (var method in t.GetMembers().OfType<IMethodSymbol>())
                if (method is { MethodKind: MethodKind.Ordinary, DeclaredAccessibility: Accessibility.Public, IsStatic: false } && Handler(method.Name) is { } parsed)
                    handlers.Add((parsed.Verb, parsed.Name, method));

        var bind = owner.GetMembers().OfType<IPropertySymbol>().FirstOrDefault(p => Symbols.HasAttribute(p, "BindProperty"));
        var result = new List<EntryCandidate>();
        foreach (var (verb, name, method) in handlers.OrderBy(h => h.Verb, StringComparer.Ordinal).ThenBy(h => h.Name, StringComparer.Ordinal))
        {
            var (f, l) = Symbols.Where(method.Locations.First(x => x.IsInSource), root);
            var input = verb is "GET" or "DELETE" or "HEAD" ? null : DataShapes.Of(bind?.Type) ?? DataShapes.FromParameters(method);
            result.Add(new EntryCandidate(EntryKind.RazorPage, [method], verb, name.Length == 0 ? route : $"{route}?handler={name}", project.Name,
                $"{(SymbolEqualityComparer.Default.Equals(method.ContainingType, page) ? ownerName : method.ContainingType.Name)}.{method.Name}", f, l, input, null));
        }
        // Sidan visas med GET också när den inte har någon OnGet.
        if (!handlers.Any(h => h.Verb == "GET" && h.Name.Length == 0))
            result.Add(new EntryCandidate(EntryKind.RazorPage, [], "GET", route, project.Name, $"{ownerName}.OnGet", pageFile, pageLine, null, null));
        return result;
    }

    /// <summary>Hanterare som <c>OnGet</c>, <c>OnPostAsync</c> och <c>OnPostDelete</c>: HTTP-metoden och namnet efter den (tomt för standardhanteraren).</summary>
    private static (string Verb, string Name)? Handler(string method)
    {
        if (!method.StartsWith("On", StringComparison.Ordinal)) return null;
        var rest = method[2..];
        if (rest.EndsWith("Async", StringComparison.Ordinal)) rest = rest[..^5];
        var verb = Verbs.FirstOrDefault(v => rest.StartsWith(v, StringComparison.Ordinal));
        return verb is null ? null : (verb.ToUpperInvariant(), rest[verb.Length..]);
    }

    /// <summary>
    /// Sidans route: <c>@page "/x"</c> är hela sökvägen; annars gäller filens plats under <c>/Pages</c> (<c>/Pages/Orders/Index.cshtml</c> ger
    /// <c>/Orders</c>), följd av mallen om det finns en (<c>@page "{id}"</c>).
    /// </summary>
    internal static string PageRoute(string identifier, string? template)
    {
        if (template is not null && template.StartsWith('/')) return RouteTemplate.Combine(null, template, "", "");
        var path = identifier.Replace('\\', '/');
        if (path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase)) path = path[..^".cshtml".Length];
        if (path.StartsWith("/Pages/", StringComparison.OrdinalIgnoreCase)) path = path["/Pages".Length..];
        if (path.Equals("/Index", StringComparison.OrdinalIgnoreCase)) path = "";
        else if (path.EndsWith("/Index", StringComparison.OrdinalIgnoreCase)) path = path[..^"/Index".Length];
        return RouteTemplate.Combine(path, template, "", "");
    }

    /// <summary>Platsen för attributets värde: i den genererade koden pekar <c>#line</c> på raden med <c>@page</c>.</summary>
    private static (string File, int Line) Locate(AttributeData attribute, string root) =>
        attribute.ApplicationSyntaxReference?.GetSyntax() is AttributeSyntax { ArgumentList.Arguments: [.., var last] }
            ? Symbols.Where(last.GetLocation(), root)
            : ("", 0);
}
