using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SystemMap;

/// <summary>Argumentet en parameter fick vid anropet: uttrycket, dess modell, anroparens bindningar och typen som anropade.</summary>
internal sealed record Binding(ExpressionSyntax Expression, SemanticModel Model, Bindings Outer, ITypeSymbol? Owner);

/// <summary>Parametrarna i en anropad metod bundna till argumenten hos anroparen, så att en parameter kan följas till sitt värde.</summary>
internal sealed class Bindings
{
    public static readonly Bindings None = new([]);

    private readonly Dictionary<IParameterSymbol, Binding> _map;

    private Bindings(Dictionary<IParameterSymbol, Binding> map) => _map = map;

    public bool IsEmpty => _map.Count == 0;

    public IEnumerable<(IParameterSymbol Parameter, Binding Binding)> All => _map.Select(p => (p.Key, p.Value));

    public Binding? Of(ISymbol? symbol) => symbol is IParameterSymbol p && _map.TryGetValue(p, out var binding) ? binding : null;

    /// <summary>Bindningarna för anropet av <paramref name="callee"/>: argument efter position eller namn, och mottagaren för en extension-metod i kort form.</summary>
    public static Bindings For(IMethodSymbol callee, InvocationExpressionSyntax invocation, IMethodSymbol? invoked, SemanticModel model, Bindings outer, ITypeSymbol? owner)
    {
        var parameters = callee.OriginalDefinition.Parameters;
        var map = new Dictionary<IParameterSymbol, Binding>(SymbolEqualityComparer.Default);
        var offset = 0;
        if (invoked is { IsExtensionMethod: true, ReducedFrom: not null } && Symbols.Receiver(invocation) is { } receiver && parameters.Length > 0)
        {
            map[parameters[0]] = new Binding(receiver, model, outer, OwnerOf(receiver, model, outer, owner));
            offset = 1;
        }
        var position = 0;
        foreach (var argument in invocation.ArgumentList.Arguments)
        {
            var index = argument.NameColon is { } name
                ? parameters.ToList().FindIndex(p => p.Name == name.Name.Identifier.ValueText)
                : position + offset;
            position++;
            if (index >= 0 && index < parameters.Length) map[parameters[index]] = new Binding(argument.Expression, model, outer, OwnerOf(argument.Expression, model, outer, owner));
        }
        return map.Count == 0 ? None : new Bindings(map);
    }

    /// <summary>Klassen som skickar värdet: anroparens klass, eller den ursprungliga när värdet själv är en parameter som skickats vidare.</summary>
    private static ITypeSymbol? OwnerOf(ExpressionSyntax expression, SemanticModel model, Bindings outer, ITypeSymbol? caller) =>
        expression is IdentifierNameSyntax && outer.Of(model.GetSymbolInfo(expression).Symbol)?.Owner is { } passedOn ? passedOn : caller;
}
