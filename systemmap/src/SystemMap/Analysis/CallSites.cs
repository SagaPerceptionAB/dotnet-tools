using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SystemMap;

/// <summary>
/// Alla anrop i solutionen per metodnamn (utan att tolka dem först), och vilket prefix en <c>MapGroup</c>-kedja ger ett
/// anrop. Används för minimal API:er och hälsovägar, där prefixet kan komma från den som anropar en extension-metod.
/// </summary>
internal sealed class CallSites
{
    private const int MaxDepth = 8;
    private readonly Dictionary<string, List<(InvocationExpressionSyntax Invocation, SemanticModel Model)>> _byName = new(StringComparer.Ordinal);

    public CallSites(IEnumerable<Project> projects, ModelCache models)
    {
        foreach (var project in projects)
            foreach (var tree in models.Trees(project))
            {
                if (models.For(tree) is not { } model) continue;
                foreach (var invocation in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (Symbols.InvokedName(invocation) is not { } name) continue;
                    if (!_byName.TryGetValue(name, out var list)) _byName[name] = list = [];
                    list.Add((invocation, model));
                }
            }
    }

    public IReadOnlyList<(InvocationExpressionSyntax Invocation, SemanticModel Model)> Named(string name) =>
        _byName.TryGetValue(name, out var list) ? list : [];

    /// <summary>
    /// Prefixen som gäller för det man anropar <c>MapGet</c> på: <c>app.MapGroup("/api")</c> ger "/api", en lokal variabel följs till
    /// sitt värde och en parameter till argumenten hos den som anropar metoden. Flera anropare med olika prefix ger flera prefix.
    /// </summary>
    public IReadOnlyList<string> Prefixes(ExpressionSyntax? receiver, SemanticModel model) =>
        [.. Prefixes(receiver, model, 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    private IEnumerable<string> Prefixes(ExpressionSyntax? expression, SemanticModel model, int depth)
    {
        if (expression is null || depth > MaxDepth) return [""];
        switch (expression)
        {
            case ParenthesizedExpressionSyntax p:
                return Prefixes(p.Expression, model, depth + 1);
            case InvocationExpressionSyntax { } invocation when Symbols.InvokedName(invocation) == "MapGroup":
                {
                    var group = invocation.ArgumentList.Arguments.FirstOrDefault() is { } first ? Symbols.ConstantString(first.Expression, model) : null;
                    return Prefixes(Symbols.Receiver(invocation), model, depth + 1).Select(outer => Join(outer, group)).ToList();
                }
            // Fluenta anrop som WithTags(...) och RequireAuthorization() ändrar inte prefixet.
            case InvocationExpressionSyntax invocation:
                return Prefixes(Symbols.Receiver(invocation), model, depth + 1);
            case IdentifierNameSyntax id:
                switch (model.GetSymbolInfo(id).Symbol)
                {
                    case ILocalSymbol local:
                        {
                            var values = local.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).OfType<VariableDeclaratorSyntax>()
                                .Select(v => v.Initializer?.Value).OfType<ExpressionSyntax>().ToList();
                            return values.Count == 0 ? [""] : values.SelectMany(v => Prefixes(v, model, depth + 1)).ToList();
                        }
                    case IParameterSymbol { ContainingSymbol: IMethodSymbol method } parameter:
                        return FromCallers(method, parameter, depth);
                }
                return [""];
            default:
                return [""];
        }
    }

    private IEnumerable<string> FromCallers(IMethodSymbol method, IParameterSymbol parameter, int depth)
    {
        var found = new List<string>();
        foreach (var (invocation, model) in Named(method.Name))
        {
            if (Symbols.Invoked(invocation, model) is not { } called || Symbols.Key(called.ReducedFrom ?? called) != Symbols.Key(method)) continue;
            // Extension-metoden anropad som app.MapHello(): mottagaren är första parametern.
            var reduced = called.ReducedFrom is not null;
            ExpressionSyntax? argument = reduced && parameter.Ordinal == 0
                ? Symbols.Receiver(invocation)
                : invocation.ArgumentList.Arguments.ElementAtOrDefault(parameter.Ordinal - (reduced ? 1 : 0))?.Expression;
            found.AddRange(Prefixes(argument, model, depth + 1));
        }
        return found.Count == 0 ? [""] : found;
    }

    /// <summary>"/api" och "v1" ger "/api/v1"; tomma delar hoppas över.</summary>
    public static string Join(string? prefix, string? next)
    {
        var parts = new[] { prefix, next }.Where(s => !string.IsNullOrEmpty(s)).Select(s => s!.Trim('/', '~')).Where(s => s.Length > 0).ToList();
        return parts.Count == 0 ? "" : "/" + string.Join('/', parts);
    }
}
