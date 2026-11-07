using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SystemMap;

/// <summary>
/// Vilken data som går in och ut: bara typer som finns i solutionen (t.ex. <c>CreateOrderRequest</c>), inte <c>string</c>,
/// <c>CancellationToken</c> eller ramverkstyper. Listor visas som <c>Typ[]</c>.
/// </summary>
internal static class DataShapes
{
    public static DataShape? Of(ITypeSymbol? type)
    {
        type = Symbols.Unwrap(type);
        var (element, many) = Element(type);
        if (element is not INamedTypeSymbol named || !Interesting(named)) return null;
        var fields = named.GetMembers().OfType<IPropertySymbol>()
            .Where(p => p is { DeclaredAccessibility: Accessibility.Public, IsStatic: false, IsIndexer: false, IsImplicitlyDeclared: false })
            .Select(p => p.Name)
            .ToList();
        return new DataShape(named.Name + (many ? "[]" : ""), fields);
    }

    /// <summary>Första intressanta typen bland parametrarna.</summary>
    public static DataShape? FromParameters(IMethodSymbol method) =>
        method.Parameters.Select(p => Of(p.Type)).FirstOrDefault(s => s is not null);

    /// <summary>
    /// Första intressanta typen bland argumenten, även inne i uttryck: <c>new ServiceBusMessage(BinaryData.FromObjectAsJson(order))</c>
    /// ger typen för <c>order</c>.
    /// </summary>
    public static DataShape? FromArguments(BaseArgumentListSyntax? arguments, SemanticModel model)
    {
        if (arguments is null) return null;
        foreach (var argument in arguments.Arguments)
            foreach (var e in argument.Expression.DescendantNodesAndSelf().OfType<ExpressionSyntax>())
            {
                if (e is LambdaExpressionSyntax) break;   // c => c.Id == id säger inget om vad som skickas
                if (Of(model.GetTypeInfo(e).Type) is { } shape) return shape;
            }
        return null;
    }

    private static (ITypeSymbol? Element, bool Many) Element(ITypeSymbol? type)
    {
        if (type is IArrayTypeSymbol array) return (array.ElementType, true);
        if (type is INamedTypeSymbol { IsGenericType: true } named && type.SpecialType != SpecialType.System_String &&
            Symbols.Find(named, "System.Collections.Generic.IEnumerable`1") is { TypeArguments.Length: 1 } enumerable)
            return (enumerable.TypeArguments[0], true);
        return (type, false);
    }

    private static bool Interesting(INamedTypeSymbol type) =>
        !type.IsAnonymousType && type.TypeKind is TypeKind.Class or TypeKind.Struct or TypeKind.Interface && Symbols.InSource(type);
}
