using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SystemMap;

/// <summary>Adressen till ett metadatadokument (OIDC-discovery): texten, om den går att läsa, och nyckeln i konfigurationen, om den kommer därifrån.</summary>
internal sealed record MetadataAddress(string? Text, string? ConfigKey)
{
    public bool IsKnown => Text is not null || ConfigKey is not null;
}

/// <summary>
/// Följer en <c>ConfigurationManager&lt;OpenIdConnectConfiguration&gt;</c> (eller ett uttryck som är en adress) till där den skapades och läser adressen
/// i <c>new ConfigurationManager&lt;T&gt;(adress, ...)</c>. Följer lokala variabler, fält och egenskaper, parametrar (till argumenten hos anroparen),
/// konstruktorparametrar (till <c>new</c> av klassen), och metoder i solutionen som returnerar hanteraren (<c>Discover("https://...")</c>).
/// Det är ingen allmän dataflödesanalys: går värdet inte att följa med dessa steg är adressen okänd. Ger två vägar olika adresser är den också okänd.
/// </summary>
internal sealed class DiscoveryAddress(ServiceResolver resolver, Origins origins)
{
    private const int MaxDepth = 8;
    internal const string ManagerType = "Microsoft.IdentityModel.Protocols.ConfigurationManager`1";
    internal const string ManagerInterface = "Microsoft.IdentityModel.Protocols.IConfigurationManager`1";

    private sealed record Source(ExpressionSyntax Address, SemanticModel Model, Bindings Bindings);

    /// <summary>Adressen för hanteraren som <paramref name="manager"/> pekar ut. Okänd (<see cref="MetadataAddress.IsKnown"/> är falskt) om den inte går att läsa.</summary>
    public MetadataAddress OfManager(ExpressionSyntax? manager, SemanticModel model, Bindings bindings, string project)
    {
        var sources = new List<Source>();
        if (manager is not null) Collect(manager, model, bindings, sources, new HashSet<ISymbol>(SymbolEqualityComparer.Default), 0);
        return Distinct(sources.Select(s => OfAddress(s.Address, s.Model, s.Bindings, project)));
    }

    /// <summary>Adressen i ett uttryck som är själva adressen (första argumentet till <c>OpenIdConnectConfigurationRetriever.GetAsync</c>).</summary>
    public MetadataAddress OfAddress(ExpressionSyntax address, SemanticModel model, Bindings bindings, string project)
    {
        var key = origins.ConfigKeysIn(address, model, bindings).FirstOrDefault();
        return new MetadataAddress(Fold(address, model, bindings, project, 0), key);
    }

    /// <summary>En adress om alla vägar ger samma, annars okänd: ett fel namn är värre än ett som sägs bestämmas vid körning.</summary>
    private static MetadataAddress Distinct(IEnumerable<MetadataAddress> found)
    {
        var known = found.Where(a => a.IsKnown).Distinct().ToList();
        return known.Count == 1 ? known[0] : new MetadataAddress(null, null);
    }

    private void Collect(ExpressionSyntax expression, SemanticModel model, Bindings bindings, List<Source> found, HashSet<ISymbol> seen, int depth)
    {
        if (depth > MaxDepth) return;
        switch (expression)
        {
            case ParenthesizedExpressionSyntax p:
                Collect(p.Expression, model, bindings, found, seen, depth);
                return;
            case AwaitExpressionSyntax a:
                Collect(a.Expression, model, bindings, found, seen, depth);
                return;
            case CastExpressionSyntax c:
                Collect(c.Expression, model, bindings, found, seen, depth);
                return;
            case PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression } bang:
                Collect(bang.Operand, model, bindings, found, seen, depth);
                return;
            case ConditionalExpressionSyntax conditional:
                Collect(conditional.WhenTrue, model, bindings, found, seen, depth);
                Collect(conditional.WhenFalse, model, bindings, found, seen, depth);
                return;
            case BinaryExpressionSyntax { RawKind: (int)SyntaxKind.CoalesceExpression } coalesce:
                Collect(coalesce.Left, model, bindings, found, seen, depth);
                Collect(coalesce.Right, model, bindings, found, seen, depth);
                return;
            case BaseObjectCreationExpressionSyntax creation:
                if (Symbols.Is(model.GetTypeInfo(creation).Type, ManagerType) && creation.ArgumentList?.Arguments.FirstOrDefault() is { } address)
                    found.Add(new Source(address.Expression, model, bindings));
                return;
            case InvocationExpressionSyntax invocation:
                // En metod i solutionen som returnerar hanteraren: dess return-uttryck, med argumenten bundna till parametrarna.
                if (Symbols.Invoked(invocation, model) is { } method && Symbols.InSource(method))
                {
                    var inner = Bindings.For(method, invocation, Symbols.Invoked(invocation, model), model, bindings, null);
                    foreach (var (value, m) in Returned(method))
                        Collect(value, m, inner, found, seen, depth + 1);
                }
                return;
            case IdentifierNameSyntax or MemberAccessExpressionSyntax:
                break;
            default:
                return;
        }

        var symbol = model.GetSymbolInfo(expression).Symbol;
        if (symbol is null) return;
        if (symbol is IParameterSymbol parameter)
        {
            if (bindings.Of(parameter) is { } bound)
            {
                Collect(bound.Expression, bound.Model, bound.Outer, found, seen, depth + 1);
                return;
            }
            if (parameter.ContainingSymbol is IMethodSymbol { MethodKind: MethodKind.Constructor } constructor && seen.Add(parameter))
                foreach (var (creation, m) in resolver.CreationsOf(constructor.ContainingType))
                    if (ArgumentFor(creation, m, constructor, parameter) is { } argument)
                        Collect(argument, m, Bindings.None, found, seen, depth + 1);
            return;
        }
        if (symbol is ILocalSymbol or IFieldSymbol or IPropertySymbol && Symbols.InSource(symbol) && seen.Add(symbol))
            foreach (var (value, m) in origins.Assigned(symbol, expression))
                Collect(value, m, Bindings.None, found, seen, depth + 1);
    }

    /// <summary>Argumentet i <c>new T(...)</c> för konstruktorparametern, efter namn eller plats.</summary>
    private static ExpressionSyntax? ArgumentFor(BaseObjectCreationExpressionSyntax creation, SemanticModel model, IMethodSymbol constructor, IParameterSymbol parameter)
    {
        if (creation.ArgumentList is not { } list) return null;
        if (model.GetSymbolInfo(creation).Symbol is IMethodSymbol called && Symbols.Key(called) != Symbols.Key(constructor)) return null;
        var named = list.Arguments.FirstOrDefault(a => a.NameColon?.Name.Identifier.ValueText == parameter.Name);
        if (named is not null) return named.Expression;
        var positional = list.Arguments.Where(a => a.NameColon is null).ToList();
        return parameter.Ordinal < positional.Count ? positional[parameter.Ordinal].Expression : null;
    }

    /// <summary>Värdena som en metod returnerar: uttrycket efter <c>=&gt;</c>, eller varje <c>return</c> (inte i lambdor eller lokala funktioner).</summary>
    private IEnumerable<(ExpressionSyntax Value, SemanticModel Model)> Returned(IMethodSymbol method)
    {
        foreach (var reference in method.OriginalDefinition.DeclaringSyntaxReferences)
        {
            var syntax = reference.GetSyntax();
            if (resolver.ModelFor(syntax.SyntaxTree) is not { } model) continue;
            if (syntax is MethodDeclarationSyntax { ExpressionBody: { } arrow }) yield return (arrow.Expression, model);
            if (syntax is not BaseMethodDeclarationSyntax { Body: { } body }) continue;
            foreach (var statement in body.DescendantNodes(n => n is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)).OfType<ReturnStatementSyntax>())
                if (statement.Expression is { } value) yield return (value, model);
        }
    }

    /// <summary>
    /// Adressen som text: strängkonstanter, sammansättning och interpolering, där parametrar och lokala variabler ersätts med det de bundits till.
    /// Ett hål som kommer ur konfigurationen blir värdet i appsettings (när det är en adress) eller <c>{Nyckel}</c>. Null om någon del inte går att läsa.
    /// </summary>
    private string? Fold(ExpressionSyntax expression, SemanticModel model, Bindings bindings, string project, int depth)
    {
        if (depth > MaxDepth) return null;
        if (Symbols.ConstantString(expression, model) is { } constant) return constant;
        if (origins.ConfigKeysIn(expression, model, bindings).FirstOrDefault() is { } key && expression is not InterpolatedStringExpressionSyntax and not BinaryExpressionSyntax)
            return resolver.AddressOf(key, project) ?? "{" + key + "}";
        switch (expression)
        {
            case ParenthesizedExpressionSyntax p:
                return Fold(p.Expression, model, bindings, project, depth + 1);
            case PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression } bang:
                return Fold(bang.Operand, model, bindings, project, depth + 1);
            case BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AddExpression } add:
                return Fold(add.Left, model, bindings, project, depth + 1) is { } left && Fold(add.Right, model, bindings, project, depth + 1) is { } right ? left + right : null;
            case BaseObjectCreationExpressionSyntax { ArgumentList.Arguments: [var only] } creation when Symbols.Is(model.GetTypeInfo(creation).Type, "System.Uri"):
                return Fold(only.Expression, model, bindings, project, depth + 1);
            case InterpolatedStringExpressionSyntax interpolated:
                {
                    var text = new System.Text.StringBuilder();
                    foreach (var part in interpolated.Contents)
                    {
                        if (part is InterpolatedStringTextSyntax literal) text.Append(literal.TextToken.ValueText);
                        else if (part is InterpolationSyntax hole && Fold(hole.Expression, model, bindings, project, depth + 1) is { } value) text.Append(value);
                        else return null;
                    }
                    return text.ToString();
                }
            case IdentifierNameSyntax or MemberAccessExpressionSyntax:
                break;
            default:
                return null;
        }
        var symbol = model.GetSymbolInfo(expression).Symbol;
        if (symbol is IParameterSymbol && bindings.Of(symbol) is { } bound) return Fold(bound.Expression, bound.Model, bound.Outer, project, depth + 1);
        if (symbol is ILocalSymbol or IFieldSymbol or IPropertySymbol && Symbols.InSource(symbol))
        {
            var values = origins.Assigned(symbol, expression).Select(v => Fold(v.Expression, v.Model, Bindings.None, project, depth + 1)).Distinct().ToList();
            return values.Count == 1 ? values[0] : null;
        }
        return null;
    }
}
