using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace SystemMap;

/// <summary>
/// Hittar minimal API:er: <c>MapGet</c>, <c>MapPost</c>, <c>MapPut</c>, <c>MapDelete</c>, <c>MapPatch</c> och <c>MapMethods</c> med en
/// lambda eller en metod som hanterare. Prefixet kommer från <c>MapGroup</c> (se <see cref="CallSites.Prefixes"/>).
/// </summary>
internal static class MinimalApiFinder
{
    private static readonly Dictionary<string, string> Methods = new(StringComparer.Ordinal)
    {
        ["MapGet"] = "GET",
        ["MapPost"] = "POST",
        ["MapPut"] = "PUT",
        ["MapDelete"] = "DELETE",
        ["MapPatch"] = "PATCH",
        ["MapMethods"] = "",
    };

    public static IEnumerable<EntryCandidate> Find(ModelCache models, CallSites sites, ServiceResolver services, string root, List<string> warnings)
    {
        foreach (var name in Methods.Keys)
            foreach (var (call, model) in sites.Named(name))
            {
                var method = Symbols.Invoked(call, model);
                if (method is not null && method.ContainingType?.Name != "EndpointRouteBuilderExtensions") continue;
                if (models.ProjectOf(call.SyntaxTree) is not { } project) continue;

                var arguments = call.ArgumentList.Arguments;
                var pattern = arguments.FirstOrDefault() is { } first ? Symbols.ConstantString(first.Expression, model) : null;
                if (pattern is null || arguments.Count < 2)
                {
                    warnings.Add($"{name} i {Where(call, root)} har ingen sökväg som går att läsa. Anropet visas inte på kartan.");
                    continue;
                }
                var verbs = Methods[name] is { Length: > 0 } verb ? [verb] : VerbsOf(arguments[1].Expression, model);
                if (verbs.Count == 0)
                {
                    warnings.Add($"{name} i {Where(call, root)} har inga metoder som går att läsa. Anropet visas inte på kartan.");
                    continue;
                }
                var handler = Handler(arguments[^1].Expression, model);
                var (file, line) = handler.Symbol is { } m && m.MethodKind == MethodKind.Ordinary
                    ? Symbols.Where(m.Locations.First(l => l.IsInSource), root)
                    : Symbols.Where(call.GetLocation(), root);
                var input = handler.Symbol is { } h ? Body(h, services) : null;
                var output = handler.Symbol is { } o ? EntryPointFinder.Output(o, models) : null;
                IReadOnlyList<IMethodSymbol> roots = handler.Symbol is { } r && Symbols.InSource(r) ? [r] : [];

                foreach (var prefix in sites.Prefixes(Symbols.Receiver(call), model))
                {
                    var path = RouteTemplate.Combine(prefix, pattern.TrimStart('/', '~'), "", "");
                    foreach (var v in verbs)
                        yield return new EntryCandidate(EntryKind.MinimalApi, roots, v, path, project, handler.Name ?? Enclosing(call, model), file, line,
                            v is "GET" or "DELETE" or "HEAD" ? null : input, output);
                }
            }
    }

    /// <summary>Hanteraren: en lambda (namnet är metoden där den står), eller en metod eller lokal funktion.</summary>
    private static (IMethodSymbol? Symbol, string? Name) Handler(ExpressionSyntax expression, SemanticModel model)
    {
        while (expression is ParenthesizedExpressionSyntax or CastExpressionSyntax)
            expression = expression is ParenthesizedExpressionSyntax p ? p.Expression : ((CastExpressionSyntax)expression).Expression;
        if (expression is AnonymousFunctionExpressionSyntax)
            return (model.GetSymbolInfo(expression).Symbol as IMethodSymbol ?? (model.GetOperation(expression) as IAnonymousFunctionOperation)?.Symbol, null);
        var info = model.GetSymbolInfo(expression);
        var symbol = info.Symbol as IMethodSymbol ?? info.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
        return (symbol, symbol is null ? null : $"{symbol.ContainingType.Name}.{symbol.Name}");
    }

    /// <summary>Metoden där anropet står, t.ex. <c>HelloEndpoints.MapHello</c>. Satser på översta nivån hör till <c>Program</c>.</summary>
    private static string Enclosing(InvocationExpressionSyntax call, SemanticModel model)
    {
        var member = call.Ancestors().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault();
        return member is not null && model.GetDeclaredSymbol(member) is { } symbol ? $"{symbol.ContainingType.Name}.{symbol.Name}" : "Program.Main";
    }

    /// <summary>Indata: parametern med [FromBody], annars (för POST, PUT och PATCH) en klass i solutionen som ASP.NET skulle binda från kroppen (se <see cref="ParameterSources"/>).</summary>
    private static DataShape? Body(IMethodSymbol handler, ServiceResolver services)
    {
        if (handler.Parameters.FirstOrDefault(p => Symbols.HasAttribute(p, "FromBody")) is { } body) return DataShapes.Of(body.Type);
        return handler.Parameters
            .Where(p => p.Type is { TypeKind: TypeKind.Class or TypeKind.Struct, IsAbstract: false } && ParameterSources.CanBeBody(p, services))
            .Select(p => DataShapes.Of(p.Type)).FirstOrDefault(s => s is not null);
    }

    /// <summary>
    /// Metoderna i <c>MapMethods("/x", ["GET", "POST"], ...)</c>: strängarna i listan, i versaler, och fälten i <c>HttpMethods</c>
    /// (<c>HttpMethods.Head</c> ger HEAD). Fälten är inte konstanter, så de kännes igen på symbolen.
    /// </summary>
    private static List<string> VerbsOf(ExpressionSyntax expression, SemanticModel model) =>
        expression.DescendantNodesAndSelf().OfType<ExpressionSyntax>()
            .Select(e => e switch
            {
                MemberAccessExpressionSyntax or IdentifierNameSyntax when model.GetSymbolInfo(e).Symbol is IFieldSymbol { IsStatic: true } field &&
                                                                          Symbols.FullName(field.ContainingType) == "Microsoft.AspNetCore.Http.HttpMethods" => field.Name,
                LiteralExpressionSyntax or MemberAccessExpressionSyntax or IdentifierNameSyntax => Symbols.ConstantString(e, model),
                _ => null,
            })
            .OfType<string>().Select(s => s.ToUpperInvariant()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    private static string Where(SyntaxNode node, string root)
    {
        var (file, line) = Symbols.Where(node.GetLocation(), root);
        return $"{file}:{line}";
    }
}
