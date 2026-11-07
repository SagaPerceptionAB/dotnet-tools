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

    public IReadOnlyList<OriginFact> Of(ExpressionSyntax? expression, SemanticModel model)
    {
        var facts = new List<OriginFact>();
        if (expression is not null) Visit(expression, model, facts, new HashSet<ISymbol>(SymbolEqualityComparer.Default), 0);
        return facts;
    }

    /// <summary>Konfigurationsnycklar i en nod, utan att följa variabler (t.ex. i en registrering av en HTTP-klient).</summary>
    public static IReadOnlyList<string> ConfigKeysIn(SyntaxNode node, SemanticModel model)
    {
        var facts = new List<OriginFact>();
        foreach (var n in node.DescendantNodesAndSelf()) AddConfig(n, model, facts);
        return facts.Select(f => f.Name).ToList();
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

    private static void AddConfig(SyntaxNode node, SemanticModel model, List<OriginFact> facts)
    {
        switch (node)
        {
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
