using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SystemMap;

/// <summary>
/// Vilka bakgrundstjänster en app registrerar. En bakgrundstjänst körs bara av en app vars uppstart registrerar den: <c>AddHostedService&lt;T&gt;()</c>,
/// <c>AddHostedService(sp =&gt; new T(...))</c>, <c>AddSingleton&lt;IHostedService, T&gt;()</c> (och <c>AddSingleton&lt;IHostedService&gt;(sp =&gt; new T())</c>,
/// <c>typeof</c>-formen och <c>ServiceDescriptor.Singleton&lt;IHostedService, T&gt;()</c>). Registreringen kan stå i en metod som uppstarten anropar, t.ex. en
/// extension-metod <c>services.AddJobs()</c> i ett bibliotek, och i <c>ConfigureServices</c> i en <c>Startup</c> som <c>UseStartup&lt;Startup&gt;()</c> pekar ut.
/// Det räknas fram genom att följa anropen från appens <c>Main</c> (eller satserna på översta nivån), inte genom att se vilka projekt appen refererar.
/// </summary>
internal sealed class HostRegistrations(ModelCache models)
{
    private const string HostedService = "Microsoft.Extensions.Hosting.IHostedService";

    private static readonly HashSet<string> Registrations =
    [
        "AddSingleton", "AddTransient", "AddScoped", "TryAddSingleton", "TryAddTransient", "TryAddScoped", "Singleton", "Transient", "Scoped",
    ];

    private readonly Dictionary<ProjectId, Registered> _memo = new();

    /// <summary>Tjänsterna en app registrerar. <paramref name="Unknown"/> är sant när någon registrering inte går att läsa (t.ex. <c>AddHostedService&lt;T&gt;()</c> med en typparameter): då kan vilken tjänst som helst vara registrerad.</summary>
    internal sealed record Registered(HashSet<string> Types, bool Unknown)
    {
        public bool Unknown { get; private set; } = Unknown;

        public void SetUnknown() => Unknown = true;

        /// <summary>Registreras tjänsten?</summary>
        public bool Includes(INamedTypeSymbol type) => Unknown || Types.Contains(Symbols.TypeKey(type));
    }

    public Registered From(Project app)
    {
        if (_memo.TryGetValue(app.Id, out var known)) return known;
        var found = new Registered([], false);
        // Ett program börjar vid Main. Ett projekt utan startpunkt (ett bibliotek med en webbapp i) kan vi inte se början på: då räknas allt i projektet.
        var roots = models.EntryPointOf(app, CancellationToken.None) is { } main ? [main] : Methods(app).ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<IMethodSymbol>(roots);
        while (pending.Count > 0)
        {
            var method = pending.Pop();
            if (!seen.Add(Symbols.Key(method))) continue;
            foreach (var body in CallWalker.Bodies(method))
            {
                if (models.For(body.SyntaxTree) is not { } model) continue;
                foreach (var invocation in body.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
                {
                    Collect(invocation, model, found);
                    if (Symbols.Invoked(invocation, model) is not { } called) continue;
                    // UseStartup<Startup>() kör Startup.ConfigureServices.
                    if (called.Name == "UseStartup" && called.TypeArguments.FirstOrDefault() is { } startup)
                        foreach (var configure in startup.GetMembers("ConfigureServices").OfType<IMethodSymbol>().Where(m => Symbols.InSource(m))) pending.Push(configure);
                    var target = (called.ReducedFrom ?? called).OriginalDefinition;
                    if (Symbols.InSource(target)) pending.Push(target);
                }
            }
        }
        return _memo[app.Id] = found;
    }

    /// <summary>Bakgrundstjänsterna (klasserna) som ingen av apparna registrerar, i den ordning de hittades.</summary>
    public IEnumerable<INamedTypeSymbol> Unregistered(IEnumerable<Project> apps, IEnumerable<INamedTypeSymbol> hosted)
    {
        var registered = apps.Select(From).ToList();
        return hosted.Where(h => !registered.Any(r => r.Includes(h)));
    }

    private IEnumerable<IMethodSymbol> Methods(Project project)
    {
        foreach (var tree in models.Trees(project))
            if (models.For(tree) is { } model)
                foreach (var declaration in tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>())
                    if (model.GetDeclaredSymbol(declaration) is IMethodSymbol symbol) yield return symbol;
    }

    private static void Collect(InvocationExpressionSyntax invocation, SemanticModel model, Registered found)
    {
        if (Symbols.InvokedName(invocation) is not { } name || name != "AddHostedService" && !Registrations.Contains(name)) return;
        var typeArguments = TypeArguments(invocation, model);
        var arguments = invocation.ArgumentList.Arguments.Select(a => a.Expression).ToList();
        var typeOfs = arguments.OfType<TypeOfExpressionSyntax>().Select(t => model.GetTypeInfo(t.Type).Type).ToList();
        IEnumerable<ITypeSymbol?> registered;
        if (name == "AddHostedService")
            registered = typeArguments.Count > 0 ? [typeArguments[0]] : Created(invocation, model);
        else if (typeArguments.Count >= 1 && IsHostedService(typeArguments[0]))
            registered = typeArguments.Count >= 2 ? [typeArguments[1]] : Created(invocation, model);
        else if (typeOfs.Count == 2 && IsHostedService(typeOfs[0]))
            registered = [typeOfs[1]];
        else return;

        var any = false;
        foreach (var type in registered)
        {
            any = true;
            if (type is not INamedTypeSymbol { TypeKind: not TypeKind.Error } named) found.SetUnknown();
            else found.Types.Add(Symbols.TypeKey(named));
        }
        if (!any) found.SetUnknown();   // en registrering vars typ inte syns (t.ex. en fabrik som hämtar den ur tjänsterna)
    }

    /// <summary>Typerna som en fabrik i argumenten skapar (<c>sp =&gt; new Worker(...)</c>) eller hämtar (<c>sp.GetRequiredService&lt;Worker&gt;()</c>).</summary>
    private static IEnumerable<ITypeSymbol?> Created(InvocationExpressionSyntax invocation, SemanticModel model)
    {
        var nodes = invocation.ArgumentList.DescendantNodes();
        foreach (var creation in nodes.OfType<BaseObjectCreationExpressionSyntax>()) yield return model.GetTypeInfo(creation).Type;
        foreach (var call in nodes.OfType<InvocationExpressionSyntax>().Where(i => Symbols.InvokedName(i) is "GetRequiredService" or "GetService"))
            foreach (var type in TypeArguments(call, model)) yield return type;
    }

    private static List<ITypeSymbol> TypeArguments(InvocationExpressionSyntax invocation, SemanticModel model)
    {
        if (Symbols.Invoked(invocation, model) is { TypeArguments.Length: > 0 } method) return [.. method.TypeArguments];
        var generic = invocation.Expression switch
        {
            MemberAccessExpressionSyntax { Name: GenericNameSyntax g } => g,
            GenericNameSyntax g => g,
            _ => null,
        };
        return generic?.TypeArgumentList.Arguments.Select(a => model.GetTypeInfo(a).Type).OfType<ITypeSymbol>().ToList() ?? [];
    }

    private static bool IsHostedService(ITypeSymbol? type) =>
        type is not null && (type.TypeKind == TypeKind.Error ? type.Name == "IHostedService" : Symbols.FullName(type) == HostedService);
}
