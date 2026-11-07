using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SystemMap;

/// <summary>En HTTP-klient registrerad med AddHttpClient eller AddRefitClient: integrationens namn och nyckeln för adressen.</summary>
internal sealed record HttpClientInfo(string Name, string? ConfigKey);

/// <summary>
/// Översätter ett anrop på ett gränssnitt till klassen som gör jobbet. Först gäller DI-registreringarna i solutionen
/// (<c>AddScoped&lt;IOrderService, OrderService&gt;()</c>, <c>AddHttpClient&lt;I, C&gt;</c>, formen med <c>typeof</c>); finns ingen
/// registrering används den enda klassen i solutionen som implementerar gränssnittet.
/// </summary>
internal sealed class ServiceResolver
{
    private static readonly HashSet<string> RegistrationMethods =
    [
        "AddScoped", "AddTransient", "AddSingleton", "TryAddScoped", "TryAddTransient", "TryAddSingleton",
        "AddKeyedScoped", "AddKeyedTransient", "AddKeyedSingleton", "TryAddKeyedScoped", "TryAddKeyedTransient", "TryAddKeyedSingleton",
        "AddHttpClient", "AddRefitClient",
    ];

    private static readonly HashSet<string> DbContextRegistrations =
        ["AddDbContext", "AddDbContextPool", "AddDbContextFactory", "AddPooledDbContextFactory"];

    private const string DbContext = "Microsoft.EntityFrameworkCore.DbContext";

    private readonly Dictionary<string, List<INamedTypeSymbol>> _registered = new(StringComparer.Ordinal);
    // Klasserna i solutionen per gränssnitt de implementerar, och överskuggningarna per virtuell metod. Byggs en gång,
    // vilket går mycket fortare än SymbolFinder (som indexerar alla paket första gången).
    private readonly Dictionary<string, List<INamedTypeSymbol>> _implementations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<IMethodSymbol>> _overrides = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HttpClientInfo> _typedClients = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HttpClientInfo> _namedClients = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _databases = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

    public List<string> Warnings { get; } = new();

    public static ServiceResolver Build(IEnumerable<Project> projects, ModelCache models)
    {
        var resolver = new ServiceResolver();
        foreach (var compilation in models.Compilations)
            foreach (var type in SourceTypes(compilation.Assembly.GlobalNamespace))
                resolver.Index(type);
        foreach (var project in projects)
            foreach (var tree in models.Trees(project))
            {
                if (models.For(tree) is not { } model) continue;
                foreach (var invocation in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (Symbols.InvokedName(invocation) is not { } name) continue;
                    if (RegistrationMethods.Contains(name)) resolver.Register(invocation, name, model);
                    else if (DbContextRegistrations.Contains(name)) resolver.RegisterDbContext(invocation, model);
                }
                foreach (var method in tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>())
                    if (method.Identifier.ValueText == "OnConfiguring" && model.GetDeclaredSymbol(method)?.ContainingType is { } context && Symbols.Is(context, DbContext))
                        resolver.AddDatabase(context, method, model);
            }
        return resolver;
    }

    /// <summary>
    /// Databasen som en DbContext går mot, enligt var anslutningen kommer ifrån: en konfigurationsnyckel
    /// (<c>GetConnectionString("Shop")</c> ger "Shop") eller en klass i solutionen (<c>GetRequiredService&lt;ShopDatabase&gt;()</c>).
    /// Dapper och ADO.NET namnger sina anslutningar på samma sätt, så samma databas blir en integration.
    /// </summary>
    public string? DatabaseOf(ITypeSymbol context) =>
        Symbols.SelfAndBases(context).Select(t => _databases.GetValueOrDefault(Symbols.TypeKey(t))).FirstOrDefault(n => n is not null);

    /// <summary>HTTP-klienten för en typad klient (klassen som får en HttpClient) eller ett Refit-gränssnitt.</summary>
    public HttpClientInfo? HttpClientFor(ITypeSymbol type) =>
        Symbols.SelfAndBases(type).Select(t => _typedClients.GetValueOrDefault(Symbols.TypeKey(t))).FirstOrDefault(i => i is not null);

    /// <summary>HTTP-klienten för <c>IHttpClientFactory.CreateClient("namn")</c>.</summary>
    public HttpClientInfo? NamedClient(string name) => _namedClients.GetValueOrDefault(name);

    /// <summary>
    /// Metoderna i solutionen som ett anrop kan hamna i. <c>Dispatch</c> är sant när anropet går via ett gränssnitt
    /// eller en virtuell metod (en "port" mellan delar av systemet).
    /// </summary>
    public IReadOnlyList<(IMethodSymbol Target, bool Dispatch)> Resolve(IMethodSymbol method)
    {
        method = method.ReducedFrom ?? method;
        if (method.ContainingType is not { } type) return [];

        if (type.TypeKind == TypeKind.Interface)
        {
            var targets = new List<(IMethodSymbol, bool)>();
            foreach (var implementation in Implementations(type))
                if (Implementation(implementation, type, method) is { } member && Symbols.InSource(member))
                    targets.Add((member, true));
            if (targets.Count > 1 && _reported.Add("många:" + Symbols.Key(method)))
                Warnings.Add($"Flera klasser implementerar {type.Name}.{method.Name} ({string.Join(", ", targets.Select(t => t.Item1.ContainingType.Name).Order(StringComparer.Ordinal))}). Alla visas på kartan.");
            if (targets.Count == 0 && Symbols.InSource(type) && _reported.Add("ingen:" + Symbols.TypeKey(type)))
                Warnings.Add($"Hittar ingen klass i solutionen som implementerar {type.Name}. Vägen slutar där.");
            return targets;
        }

        if ((method.IsAbstract || method.IsVirtual || method.IsOverride) && !method.IsSealed && Symbols.InSource(type))
        {
            var overrides = _overrides.GetValueOrDefault(Symbols.Key(method), []).Select(m => (m, true)).ToList();
            if (overrides.Count > 0) return method.IsAbstract ? overrides : [(method, false), .. overrides];
        }

        return Symbols.InSource(method) ? [(method, false)] : [];
    }

    private IReadOnlyList<INamedTypeSymbol> Implementations(INamedTypeSymbol iface)
    {
        var key = Symbols.TypeKey(iface);
        if (_registered.TryGetValue(key, out var registered)) return registered;
        // Bara gränssnitt i solutionen: ramverkets (IDisposable m.fl.) har för många implementationer för att säga något.
        return Symbols.InSource(iface) ? [.. _implementations.GetValueOrDefault(key, []).OrderBy(t => t.ToDisplayString(), StringComparer.Ordinal)] : [];
    }

    private void Index(INamedTypeSymbol type)
    {
        if (type is { TypeKind: TypeKind.Class, IsAbstract: false })
            foreach (var iface in type.AllInterfaces)
            {
                var key = Symbols.TypeKey(iface);
                if (!_implementations.TryGetValue(key, out var list)) _implementations[key] = list = [];
                list.Add(type);
            }
        foreach (var method in type.GetMembers().OfType<IMethodSymbol>().Where(m => m.IsOverride))
            for (var overridden = method.OverriddenMethod; overridden is not null; overridden = overridden.OverriddenMethod)
            {
                var key = Symbols.Key(overridden);
                if (!_overrides.TryGetValue(key, out var list)) _overrides[key] = list = [];
                list.Add(method);
            }
    }

    private static IEnumerable<INamedTypeSymbol> SourceTypes(INamespaceSymbol ns)
    {
        foreach (var member in ns.GetMembers())
        {
            if (member is INamespaceSymbol child)
                foreach (var t in SourceTypes(child)) yield return t;
            else if (member is INamedTypeSymbol type && Symbols.InSource(type))
                foreach (var t in Nested(type)) yield return t;
        }
    }

    private static IEnumerable<INamedTypeSymbol> Nested(INamedTypeSymbol type)
    {
        yield return type;
        foreach (var inner in type.GetTypeMembers().SelectMany(Nested)) yield return inner;
    }

    private static IMethodSymbol? Implementation(INamedTypeSymbol implementation, INamedTypeSymbol iface, IMethodSymbol method)
    {
        // Öppen generisk registrering: typeof(Repository<>) för typeof(IRepository<>).
        if (implementation.IsGenericType && implementation.IsDefinition && iface.TypeArguments.Length == implementation.TypeParameters.Length && !iface.IsDefinition)
            implementation = implementation.Construct([.. iface.TypeArguments]);
        if (implementation.FindImplementationForInterfaceMember(method) is IMethodSymbol direct) return direct;
        // Symbolen kan komma från en annan kompilering: jämför namn och antal parametrar.
        for (var t = implementation; t is not null; t = t.BaseType)
            if (t.GetMembers(method.Name).OfType<IMethodSymbol>().FirstOrDefault(m => m.Parameters.Length == method.Parameters.Length) is { } byName)
                return byName;
        return null;
    }

    private void Register(InvocationExpressionSyntax invocation, string name, SemanticModel model)
    {
        var method = Symbols.Invoked(invocation, model);
        var typeArguments = method?.TypeArguments.OfType<INamedTypeSymbol>().ToList() ?? SyntacticTypeArguments(invocation, model);

        if (name is "AddHttpClient" or "AddRefitClient")
        {
            RegisterHttpClient(invocation, typeArguments, model);
            if (typeArguments.Count == 2) Add(typeArguments[0], typeArguments[1]);
            return;
        }

        switch (typeArguments.Count)
        {
            case 2:
                Add(typeArguments[0], typeArguments[1]);
                break;
            case 1:
                // AddSingleton<IFoo>(sp => new Foo(...)) eller AddSingleton<IFoo>(new Foo()).
                var created = invocation.ArgumentList.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>()
                    .Select(c => model.GetTypeInfo(c).Type).OfType<INamedTypeSymbol>()
                    .FirstOrDefault(t => Symbols.SelfAndBases(t).Any(b => Symbols.TypeKey(b) == Symbols.TypeKey(typeArguments[0])));
                if (created is not null) Add(typeArguments[0], created);
                break;
            case 0:
                // AddScoped(typeof(IRepository<>), typeof(Repository<>))
                var types = invocation.ArgumentList.Arguments.Select(a => a.Expression).OfType<TypeOfExpressionSyntax>()
                    .Select(t => model.GetTypeInfo(t.Type).Type).OfType<INamedTypeSymbol>().ToList();
                if (types.Count == 2) Add(types[0], types[1]);
                break;
        }
    }

    private void RegisterDbContext(InvocationExpressionSyntax invocation, SemanticModel model)
    {
        var method = Symbols.Invoked(invocation, model);
        var context = (method?.TypeArguments.OfType<INamedTypeSymbol>().ToList() ?? SyntacticTypeArguments(invocation, model)).LastOrDefault();
        if (context is not null && context.TypeKind != TypeKind.Error)
            foreach (var arguments in ChainArguments(invocation)) AddDatabase(context, arguments, model);
    }

    private void AddDatabase(ITypeSymbol context, SyntaxNode node, SemanticModel model)
    {
        var key = Symbols.TypeKey(context);
        if (_databases.ContainsKey(key)) return;
        if (Origins.ConfigKeysIn(node, model).FirstOrDefault() is { } config)
        {
            _databases[key] = Origins.NameFromConfigKey(config);
            return;
        }
        foreach (var id in node.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            var symbol = model.GetSymbolInfo(id).Symbol;
            // o och sp i (sp, o) => o.UseSqlServer(...) är ramverkets byggare, inte anslutningens källa.
            if (symbol is IParameterSymbol { ContainingSymbol: IMethodSymbol { MethodKind: MethodKind.AnonymousFunction } }) continue;
            var type = symbol as INamedTypeSymbol ?? model.GetTypeInfo(id).Type as INamedTypeSymbol;
            if (type is null || type.TypeKind == TypeKind.Error || !Symbols.InSource(type) || Symbols.Is(type, DbContext)) continue;
            _databases[key] = type.Name;
            return;
        }
    }

    private void RegisterHttpClient(InvocationExpressionSyntax invocation, List<INamedTypeSymbol> typeArguments, SemanticModel model)
    {
        // Adressen sätts i registreringens argument eller i en kedjad ConfigureHttpClient(...).
        var key = ChainArguments(invocation).SelectMany(a => Origins.ConfigKeysIn(a, model)).FirstOrDefault();
        var clientName = Origins.FirstString(invocation.ArgumentList, model);
        var typeName = typeArguments.LastOrDefault()?.Name;
        var info = new HttpClientInfo(clientName ?? (key is null ? null : Origins.NameFromConfigKey(key)) ?? typeName ?? "HTTP", key);
        foreach (var t in typeArguments) _typedClients.TryAdd(Symbols.TypeKey(t), info);
        if (clientName is not null) _namedClients.TryAdd(clientName, info);
    }

    /// <summary>Argumenten till anropet och till anrop som kedjas på det: <c>AddHttpClient&lt;X&gt;().ConfigureHttpClient(c =&gt; ...)</c>.</summary>
    private static IEnumerable<SyntaxNode> ChainArguments(InvocationExpressionSyntax invocation)
    {
        yield return invocation.ArgumentList;
        SyntaxNode node = invocation;
        while (node.Parent is MemberAccessExpressionSyntax { Parent: InvocationExpressionSyntax outer } && outer.Expression == node.Parent)
        {
            yield return outer.ArgumentList;
            node = outer;
        }
    }

    private static List<INamedTypeSymbol> SyntacticTypeArguments(InvocationExpressionSyntax invocation, SemanticModel model)
    {
        var generic = invocation.Expression switch
        {
            MemberAccessExpressionSyntax { Name: GenericNameSyntax g } => g,
            GenericNameSyntax g => g,
            _ => null,
        };
        return generic?.TypeArgumentList.Arguments.Select(a => model.GetTypeInfo(a).Type).OfType<INamedTypeSymbol>().ToList() ?? [];
    }

    private void Add(INamedTypeSymbol service, INamedTypeSymbol implementation)
    {
        if (service.TypeKind == TypeKind.Error || implementation.TypeKind == TypeKind.Error) return;
        var key = Symbols.TypeKey(service);
        if (!_registered.TryGetValue(key, out var list)) _registered[key] = list = [];
        var implementationKey = Symbols.TypeKey(implementation);
        if (list.All(i => Symbols.TypeKey(i) != implementationKey)) list.Add(implementation.OriginalDefinition);
    }
}
