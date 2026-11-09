using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SystemMap;

/// <summary>
/// Något i koden där ett objekt skapades: ett anrop (<c>client.CreateSender("order-lagd")</c>), en konstruktion
/// (<c>new SqlConnection(...)</c>) eller en konfigurationsnyckel (<c>config["Betaltjanst:BaseUrl"]</c>).
/// </summary>
/// <param name="Kind">"call", "new" eller "config".</param>
/// <param name="Name">Metodens eller typens namn; för "config" nyckeln.</param>
/// <param name="Text">Första strängkonstanten bland argumenten, om någon.</param>
/// <param name="Type">Typen anropet gjordes på, eller typen som skapades.</param>
internal sealed record OriginFact(string Kind, string Name, string? Text, ITypeSymbol? Type);

/// <summary>
/// Följer ett uttryck bakåt till där värdet skapades: initierare och tilldelningar för lokala variabler, fält och egenskaper.
/// Används för att namnge integrationer, t.ex. kön i <c>_sender.SendMessageAsync(...)</c> när <c>_sender = client.CreateSender("order-lagd")</c>.
/// </summary>
internal sealed class Origins(Func<SyntaxTree, SemanticModel?> models)
{
    private const int MaxDepth = 3;

    /// <summary>Konfigurationssektionen per options-typ (<c>AddOptions&lt;T&gt;().BindConfiguration("X")</c> ger X). Fylls i av <see cref="ServiceResolver"/>.</summary>
    public Dictionary<string, string> OptionSections { get; } = new(StringComparer.Ordinal);

    /// <summary>Startvärdet (strängkonstanten) för en nyckel som <c>Partner:BaseUrl</c>, när egenskapen har ett.</summary>
    public Dictionary<string, string> Defaults { get; } = new(StringComparer.Ordinal);

    public IReadOnlyList<OriginFact> Of(ExpressionSyntax? expression, SemanticModel model)
    {
        var facts = new List<OriginFact>();
        if (expression is not null) Visit(expression, model, facts, new HashSet<ISymbol>(SymbolEqualityComparer.Default), 0);
        return facts;
    }

    /// <summary>
    /// Konfigurationsnycklarna i en nod. En lokal variabel i noden följs till vad den sattes till i samma metod (initierare,
    /// tilldelning eller mönstervariabel), så <c>var url = config["X:Url"]</c> och senare <c>new Uri(url)</c> ger X:Url.
    /// </summary>
    public IReadOnlyList<string> ConfigKeysIn(SyntaxNode node, SemanticModel model, Bindings? bindings = null)
    {
        var facts = new List<OriginFact>();
        CollectKeys(node, model, facts, new HashSet<ISymbol>(SymbolEqualityComparer.Default), 0, bindings ?? Bindings.None);
        return facts.Select(f => f.Name).ToList();
    }

    private void CollectKeys(SyntaxNode node, SemanticModel model, List<OriginFact> facts, HashSet<ISymbol> seen, int depth, Bindings bindings)
    {
        foreach (var n in node.DescendantNodesAndSelf())
        {
            AddConfig(n, model, facts);
            if (n is not IdentifierNameSyntax id || depth >= MaxDepth) continue;
            var symbol = model.GetSymbolInfo(id).Symbol;
            if (symbol is ILocalSymbol local && seen.Add(local))
                foreach (var (assigned, m) in Assigned(local, id))
                    CollectKeys(assigned, m, facts, seen, depth + 1, bindings);
            else if (bindings.Of(symbol) is { } bound && seen.Add(symbol!))
                CollectKeys(bound.Expression, bound.Model, facts, seen, depth + 1, bound.Outer);
        }
    }

    /// <summary>Namnet på en integration ur en konfigurationsnyckel: <c>Betaltjanst:BaseUrl</c> ger "Betaltjanst".</summary>
    public static string NameFromConfigKey(string key)
    {
        var parts = key.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1 && parts[0].Equals("ConnectionStrings", StringComparison.OrdinalIgnoreCase)) return parts[^1];
        return parts.Length > 1 ? parts[^2] : parts.Length == 1 ? parts[0] : key;
    }

    private void Visit(ExpressionSyntax expression, SemanticModel model, List<OriginFact> facts, HashSet<ISymbol> seen, int depth)
    {
        foreach (var node in expression.DescendantNodesAndSelf())
        {
            switch (node)
            {
                case InvocationExpressionSyntax inv when Symbols.InvokedName(inv) is { } name:
                    {
                        var receiver = Symbols.Receiver(inv);
                        facts.Add(new OriginFact("call", name, FirstString(inv.ArgumentList, model), receiver is null ? null : model.GetTypeInfo(receiver).Type));
                        break;
                    }
                case BaseObjectCreationExpressionSyntax creation:
                    {
                        var type = model.GetTypeInfo(creation).Type;
                        facts.Add(new OriginFact("new", type?.Name ?? "", FirstString(creation.ArgumentList, model), type));
                        break;
                    }
                case IdentifierNameSyntax id when depth < MaxDepth &&
                                                  model.GetSymbolInfo(id).Symbol is (ILocalSymbol or IFieldSymbol or IPropertySymbol) and var symbol &&
                                                  Symbols.InSource(symbol) && seen.Add(symbol):
                    foreach (var (assigned, m) in Assigned(symbol, id))
                        Visit(assigned, m, facts, seen, depth + 1);
                    break;
            }
            AddConfig(node, model, facts);
        }
    }

    private void AddConfig(SyntaxNode node, SemanticModel model, List<OriginFact> facts)
    {
        switch (node)
        {
            // options.Value.BaseUrl: egenskapen på en options-typ som binds till en sektion ger Sektion:BaseUrl.
            case MemberAccessExpressionSyntax member when OptionsKey(member, model) is { } key &&
                                                          !(member.Parent is MemberAccessExpressionSyntax outer && outer.Expression == member && OptionsKey(outer, model) is not null):
                facts.Add(new OriginFact("config", key, key, null));
                break;
            // config["Betaltjanst:BaseUrl"], ConfigurationManager.AppSettings["Url"]
            case ElementAccessExpressionSyntax { ArgumentList.Arguments: [var arg] } access when Symbols.ConstantString(arg.Expression, model) is { } key:
                {
                    var type = model.GetTypeInfo(access.Expression).Type;
                    var isConfig = Symbols.Is(type, "Microsoft.Extensions.Configuration.IConfiguration") ||
                                   access.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "AppSettings" or "ConnectionStrings" };
                    if (isConfig) facts.Add(new OriginFact("config", key, key, null));
                    break;
                }
            // config.GetValue<string>("X:Url"), GetSection("X"), GetConnectionString("Shop")
            case InvocationExpressionSyntax inv when Symbols.InvokedName(inv) is "GetValue" or "GetSection" or "GetRequiredSection" or "GetConnectionString" &&
                                                     FirstString(inv.ArgumentList, model) is { } key:
                facts.Add(new OriginFact("config", Symbols.InvokedName(inv) == "GetConnectionString" ? "ConnectionStrings:" + key : key, key, null));
                break;
        }
    }

    /// <summary>
    /// Nyckeln för <c>x.Egenskap</c> när x är en options-typ med en känd sektion, eller <c>x.Under.Egenskap</c> när Under är en klass
    /// i solutionen (nästlad inställning). Typen räcker: <c>options.Value</c> och <c>var s = options.Value</c> har samma typ.
    /// </summary>
    private string? OptionsKey(MemberAccessExpressionSyntax member, SemanticModel model)
    {
        if (OptionSections.Count == 0) return null;
        var property = model.GetSymbolInfo(member).Symbol;
        if (property is not (IPropertySymbol or IFieldSymbol) || property.IsStatic) return null;
        var type = model.GetTypeInfo(member.Expression).Type;
        string? key = null;
        if (type is not null && Symbols.SelfAndBases(type).Select(t => OptionSections.GetValueOrDefault(Symbols.TypeKey(t))).FirstOrDefault(v => v is not null) is { } section)
            key = section + ":" + property.Name;
        else if (type is { TypeKind: TypeKind.Class } && Symbols.InSource(type) && member.Expression is MemberAccessExpressionSyntax inner && OptionsKey(inner, model) is { } parent)
            key = parent + ":" + property.Name;
        if (key is not null && !Defaults.ContainsKey(key) && DefaultOf(property) is { } value) Defaults[key] = value;
        return key;
    }

    private string? DefaultOf(ISymbol property)
    {
        foreach (var reference in property.DeclaringSyntaxReferences)
            if (reference.GetSyntax() is PropertyDeclarationSyntax { Initializer.Value: { } value } && models(value.SyntaxTree) is { } m)
                return Symbols.ConstantString(value, m);
        return null;
    }

    /// <summary>Uttrycken som tilldelas symbolen: initieraren, och tilldelningar i metoden (lokal variabel) eller i typen (fält, egenskap).</summary>
    private IEnumerable<(ExpressionSyntax Expression, SemanticModel Model)> Assigned(ISymbol symbol, SyntaxNode usage)
    {
        foreach (var reference in symbol.DeclaringSyntaxReferences)
        {
            var declaration = reference.GetSyntax();
            if (models(declaration.SyntaxTree) is not { } m) continue;
            var value = declaration switch
            {
                VariableDeclaratorSyntax v => v.Initializer?.Value,
                // if (config["X:Url"] is string url) ...: värdet är uttrycket till vänster om mönstret.
                SingleVariableDesignationSyntax d => d.Ancestors().OfType<IsPatternExpressionSyntax>().FirstOrDefault()?.Expression,
                PropertyDeclarationSyntax p => p.Initializer?.Value ?? p.ExpressionBody?.Expression,
                _ => null,
            };
            if (value is not null) yield return (value, m);
        }

        IEnumerable<SyntaxNode> scopes = symbol is ILocalSymbol
            ? usage.Ancestors().OfType<MemberDeclarationSyntax>().Take(1)
            : symbol.ContainingType?.DeclaringSyntaxReferences.Select(r => r.GetSyntax()) ?? [];
        foreach (var scope in scopes)
        {
            if (models(scope.SyntaxTree) is not { } m) continue;
            foreach (var assignment in scope.DescendantNodes().OfType<AssignmentExpressionSyntax>())
                if (SymbolEqualityComparer.Default.Equals(m.GetSymbolInfo(assignment.Left).Symbol, symbol))
                    yield return (assignment.Right, m);
        }
    }

    /// <summary>Första argumentet som är en strängkonstant.</summary>
    public static string? FirstString(BaseArgumentListSyntax? arguments, SemanticModel model) =>
        arguments?.Arguments.Select(a => Symbols.ConstantString(a.Expression, model)).FirstOrDefault(s => s is not null);
}
