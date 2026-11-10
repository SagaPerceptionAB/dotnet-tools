using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SystemMap;

/// <summary>
/// Hittar gRPC-tjänster och SignalR-hubbar. En gRPC-tjänst är en klass som ärver den genererade <c>Namn.NamnBase</c> (som har
/// <c>[BindServiceMethod]</c>) eller som mappas med <c>MapGrpcService&lt;T&gt;</c>; varje metod som skriver över ett anrop i basen är en ingång.
/// En SignalR-hubb är en klass som ärver <c>Hub</c> eller <c>Hub&lt;T&gt;</c>; varje publik metod är en ingång.
/// </summary>
internal static class RpcFinder
{
    public static IEnumerable<EntryCandidate> Find(IEnumerable<(INamedTypeSymbol Type, string Project)> types, CallSites sites, string root)
    {
        var mapped = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (call, model) in sites.Named("MapGrpcService"))
            if (call.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax { TypeArgumentList.Arguments: [var argument] } } &&
                model.GetTypeInfo(argument).Type is { } service)
                mapped.Add(Symbols.TypeKey(service));

        foreach (var (type, project) in types)
        {
            if (type is not { TypeKind: TypeKind.Class, IsAbstract: false }) continue;
            if (Symbols.Is(type, "Microsoft.AspNetCore.SignalR.Hub", "Microsoft.AspNetCore.SignalR.Hub`1"))
                foreach (var entry in Hub(type, project, root)) yield return entry;
            else if (Service(type, mapped.Contains(Symbols.TypeKey(type))) is { } baseType)
                foreach (var entry in Grpc(type, baseType, project, root)) yield return entry;
        }
    }

    private static IEnumerable<EntryCandidate> Hub(INamedTypeSymbol type, string project, string root)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var t = type; t is not null && Symbols.InSource(t); t = t.BaseType)
            foreach (var method in t.GetMembers().OfType<IMethodSymbol>())
            {
                if (method is not { MethodKind: MethodKind.Ordinary, DeclaredAccessibility: Accessibility.Public, IsStatic: false } || !Symbols.InSource(method) || !seen.Add(method.Name)) continue;
                var (file, line) = Symbols.Where(method.Locations.First(l => l.IsInSource), root);
                yield return new EntryCandidate(EntryKind.SignalR, [method], "", "", project, $"{type.Name}.{method.Name}", file, line,
                    DataShapes.FromParameters(method), DataShapes.Of(method.ReturnType), $"SignalR: {type.Name}.{method.Name}");
            }
    }

    /// <summary>Den genererade basklassen (<c>Greeter.GreeterBase</c>): den med [BindServiceMethod], eller för en klass som mappas en bas som heter …Base i en annan klass.</summary>
    private static INamedTypeSymbol? Service(INamedTypeSymbol type, bool mapped)
    {
        for (var b = type.BaseType; b is not null && b.SpecialType != SpecialType.System_Object; b = b.BaseType)
            if (Symbols.HasAttribute(b, "BindServiceMethod") || mapped && b.Name.EndsWith("Base", StringComparison.Ordinal) && b.ContainingType is not null) return b;
        return null;
    }

    private static IEnumerable<EntryCandidate> Grpc(INamedTypeSymbol type, INamedTypeSymbol baseType, string project, string root)
    {
        var service = baseType.ContainingType?.Name ?? (baseType.Name.EndsWith("Base", StringComparison.Ordinal) ? baseType.Name[..^4] : baseType.Name);
        foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
        {
            if (method is not { MethodKind: MethodKind.Ordinary, IsOverride: true, DeclaredAccessibility: Accessibility.Public } || !Symbols.InSource(method) ||
                !Overrides(method, baseType)) continue;
            var (file, line) = Symbols.Where(method.Locations.First(l => l.IsInSource), root);
            yield return new EntryCandidate(EntryKind.Grpc, [method], "", "", project, $"{type.Name}.{method.Name}", file, line,
                DataShapes.FromParameters(method), DataShapes.Of(method.ReturnType), $"gRPC: {service}/{method.Name}");
        }
    }

    private static bool Overrides(IMethodSymbol method, INamedTypeSymbol baseType)
    {
        for (var m = method.OverriddenMethod; m is not null; m = m.OverriddenMethod)
            if (SymbolEqualityComparer.Default.Equals(m.ContainingType.OriginalDefinition, baseType.OriginalDefinition)) return true;
        return false;
    }
}
