using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SystemMap;

/// <summary>Hjälpare för Roslyn-symboler: namn, arv och uppackning av typer.</summary>
internal static class Symbols
{
    /// <summary>Filen relativt <paramref name="root"/> (med snedstreck) och raden, räknad från 1.</summary>
    public static (string File, int Line) Where(Location location, string root)
    {
        var span = location.GetMappedLineSpan();   // följer #line: kod från Razor-filer pekar på .razor- eller .cshtml-filen
        var file = string.IsNullOrEmpty(span.Path) ? "" : Path.GetRelativePath(root, span.Path).Replace('\\', '/');
        return (file, span.StartLinePosition.Line + 1);
    }

    /// <summary>Platsen där symbolen deklareras i koden: som <see cref="Where(Location, string)"/>. Symbolen måste finnas i källkoden (<see cref="InSource"/>).</summary>
    public static (string File, int Line) Where(ISymbol symbol, string root) => Where(symbol.Locations.First(l => l.IsInSource), root);

    /// <summary>Metadatanamnet med namnrymd men utan assembly, t.ex. <c>Microsoft.EntityFrameworkCore.DbSet`1</c>.</summary>
    public static string FullName(ITypeSymbol type) => FullNames.GetValue(type.OriginalDefinition, static t =>
    {
        var name = t.MetadataName;
        for (var c = t.ContainingType; c is not null; c = c.ContainingType) name = c.MetadataName + "+" + name;
        return t.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() + "." + name : name;
    });

    // Reglerna frågar efter samma typers namn för varje anrop som gås igenom. Tabellen håller inte typerna (eller
    // kompileringarna) vid liv när analysen är klar.
    private static readonly ConditionalWeakTable<ITypeSymbol, string> FullNames = new();

    /// <summary>Typen, dess bastyper och alla gränssnitt den implementerar.</summary>
    public static IEnumerable<ITypeSymbol> SelfAndBases(ITypeSymbol? type)
    {
        if (type is null) yield break;
        for (var t = type; t is not null; t = t.BaseType) yield return t;
        foreach (var i in type.AllInterfaces) yield return i;
    }

    /// <summary>
    /// Är typen (eller en bastyp eller ett gränssnitt) någon av <paramref name="fullNames"/>? En typ som inte gick att läsa
    /// (paketet saknas, t.ex. för att restore misslyckades) jämförs på det korta namnet.
    /// </summary>
    public static bool Is(ITypeSymbol? type, params string[] fullNames)
    {
        foreach (var t in SelfAndBases(type))
        {
            if (t.TypeKind == TypeKind.Error)
            {
                if (fullNames.Any(f => ShortName(f) == t.Name)) return true;
                continue;
            }
            if (fullNames.Contains(FullName(t))) return true;
        }
        return false;
    }

    /// <summary>Den första av typen och dess bastyper/gränssnitt som heter <paramref name="fullName"/>.</summary>
    public static INamedTypeSymbol? Find(ITypeSymbol? type, string fullName) =>
        SelfAndBases(type).OfType<INamedTypeSymbol>().FirstOrDefault(t => t.TypeKind == TypeKind.Error ? t.Name == ShortName(fullName) : FullName(t) == fullName);

    public static string ShortName(string fullName)
    {
        var s = fullName[(fullName.LastIndexOf('.') + 1)..];
        var tick = s.IndexOf('`');
        return tick < 0 ? s : s[..tick];
    }

    /// <summary>Sant om symbolen är deklarerad i solutionens källkod (inte i ett paket eller ramverket).</summary>
    public static bool InSource(ISymbol symbol) => symbol.Locations.Any(l => l.IsInSource);

    public static bool HasAttribute(ISymbol symbol, params string[] shortNames) =>
        symbol.GetAttributes().Any(a => a.AttributeClass is { } c && shortNames.Any(n => c.Name == n || c.Name == n + "Attribute"));

    /// <summary>Nyckel för en metod (för cykler och cachning), oberoende av vilken kompilering symbolen kommer från.</summary>
    public static string Key(IMethodSymbol m)
    {
        var original = m.OriginalDefinition;
        // Lambdor och lokala funktioner har inget dokumentations-id: platsen skiljer dem åt.
        var id = original.GetDocumentationCommentId() ??
                 (original.Locations.FirstOrDefault(l => l.IsInSource) is { SourceTree: { } tree } at ? $"{tree.FilePath}:{at.SourceSpan.Start}" : original.ToDisplayString());
        return id + "@" + m.ContainingAssembly?.Name;
    }

    public static string TypeKey(ITypeSymbol t) => FullName(t) + "@" + t.ContainingAssembly?.Name;

    /// <summary><c>Task&lt;T&gt;</c>, <c>ValueTask&lt;T&gt;</c>, <c>ActionResult&lt;T&gt;</c> och <c>Nullable&lt;T&gt;</c> blir T; Task och void blir null.</summary>
    public static ITypeSymbol? Unwrap(ITypeSymbol? type)
    {
        while (type is INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } n &&
               FullName(n) is "System.Threading.Tasks.Task`1" or "System.Threading.Tasks.ValueTask`1" or "Microsoft.AspNetCore.Mvc.ActionResult`1" or "System.Nullable`1")
            type = n.TypeArguments[0];
        if (type is null || type.SpecialType == SpecialType.System_Void) return null;
        return FullName(type) is "System.Threading.Tasks.Task" or "System.Threading.Tasks.ValueTask" ? null : type;
    }

    /// <summary>Strängkonstanten i uttrycket (literal, const-fält, nameof ...), annars null.</summary>
    public static string? ConstantString(ExpressionSyntax expression, SemanticModel model) =>
        model.GetConstantValue(expression) is { HasValue: true, Value: string s } ? s : null;

    /// <summary>Metoden som anropas, även när överlagringen inte gick att avgöra helt (första kandidaten).</summary>
    public static IMethodSymbol? Invoked(SyntaxNode invocation, SemanticModel model)
    {
        var info = model.GetSymbolInfo(invocation);
        return info.Symbol as IMethodSymbol ?? info.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
    }

    /// <summary>Uttrycket som metoden anropas på: <c>a.B()</c> ger <c>a</c>, <c>a?.B()</c> ger <c>a</c>, <c>B()</c> ger null.</summary>
    public static ExpressionSyntax? Receiver(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax ma => ma.Expression,
        MemberBindingExpressionSyntax => invocation.Ancestors().OfType<ConditionalAccessExpressionSyntax>().FirstOrDefault()?.Expression,
        _ => null,
    };

    /// <summary>Namnet på metoden i anropet, även när symbolen inte gick att läsa.</summary>
    public static string? InvokedName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax mb => mb.Name.Identifier.ValueText,
        IdentifierNameSyntax id => id.Identifier.ValueText,
        GenericNameSyntax g => g.Identifier.ValueText,
        _ => null,
    };
}
