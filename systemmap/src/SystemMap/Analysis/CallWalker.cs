using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SystemMap;

/// <summary>
/// Följer anropen från en metod genom solutionen tills de lämnar systemet. Anrop till ramverket och paket utan
/// integration hoppas över. Ett steg visas om det går via ett gränssnitt (en "port"), leder till en integration eller
/// är en tjänst i ett annat projekt; andra hjälpmetoder (t.ex. <c>Pricing.For</c>) visas inte, men deras anrop följs ändå.
/// </summary>
internal sealed class CallWalker(ServiceResolver resolver, ModelCache models, Origins origins, string root, List<string> warnings)
{
    public const int MaxDepth = 8;

    private readonly Dictionary<string, Step?> _memo = new(StringComparer.Ordinal);

    /// <summary>Integrationerna som hittats hittills, per id.</summary>
    public Dictionary<string, Integration> Integrations { get; } = new(StringComparer.Ordinal);

    public async Task<IReadOnlyList<Step>> StepsFromAsync(IMethodSymbol method, CancellationToken ct) =>
        await ChildrenAsync(method, [Symbols.Key(method)], 1, ct);

    private async Task<List<Step>> ChildrenAsync(IMethodSymbol method, ImmutableHashSet<string> path, int depth, CancellationToken ct)
    {
        var steps = new List<Step>();
        foreach (var body in Bodies(method))
        {
            if (models.For(body.SyntaxTree) is not { } model) continue;
            var project = models.ProjectOf(body.SyntaxTree) ?? "";
            var context = new SinkContext(method, body, model, resolver, origins);
            foreach (var node in body.DescendantNodes())
            {
                ct.ThrowIfCancellationRequested();
                if (node is BaseObjectCreationExpressionSyntax creation)
                {
                    if (SinkCatalog.MatchCreation(creation, context) is { } created) steps.Add(Leave(created, project, creation));
                    continue;
                }
                if (node is not InvocationExpressionSyntax invocation) continue;
                if (SinkCatalog.Match(invocation, context) is { } hit)
                {
                    steps.Add(Leave(hit, project, invocation));
                    continue;
                }
                if (Symbols.Invoked(invocation, model) is not { } target) continue;

                foreach (var (implementation, dispatch) in resolver.Resolve(target))
                {
                    var key = Symbols.Key(implementation);
                    if (path.Contains(key)) continue;   // rekursion
                    if (depth >= MaxDepth)
                    {
                        warnings.Add($"Kartan går högst {MaxDepth} anrop djupt; anropen från {method.ContainingType.Name}.{method.Name} till {implementation.ContainingType.Name}.{implementation.Name} visas inte.");
                        continue;
                    }
                    if (await StepAsync(implementation, path.Add(key), depth + 1, ct) is not { } child) continue;
                    // Ett anrop till en tjänst i ett annat projekt visas också, men inte statiska hjälpare (t.ex. fabriker för undantag).
                    if (dispatch || child.ReachesIntegration || (child.Project != project && !implementation.IsStatic)) steps.Add(child);
                    else steps.AddRange(child.Steps);
                }
            }
        }
        return Tidy(steps);
    }

    private async Task<Step?> StepAsync(IMethodSymbol method, ImmutableHashSet<string> path, int depth, CancellationToken ct)
    {
        var key = Symbols.Key(method);
        if (_memo.TryGetValue(key, out var known)) return known;
        var declaration = method.OriginalDefinition.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(ct);
        if (declaration is null) return _memo[key] = null;
        var children = await ChildrenAsync(method, path, depth, ct);
        var (file, line) = Where(declaration switch
        {
            MethodDeclarationSyntax m => m.Identifier.GetLocation(),
            LocalFunctionStatementSyntax f => f.Identifier.GetLocation(),
            _ => declaration.GetLocation(),
        });
        return _memo[key] = new Step(models.ProjectOf(declaration.SyntaxTree) ?? "", $"{method.ContainingType.Name}.{method.Name}", file, line,
            DataShapes.FromParameters(method), DataShapes.Of(method.ReturnType), null, children);
    }

    private Step Leave(SinkHit hit, string project, SyntaxNode node)
    {
        Integrations[hit.Integration.Id] = Integrations.TryGetValue(hit.Integration.Id, out var known) ? known.Merge(hit.Integration) : hit.Integration;
        var (file, line) = Where(node.GetLocation());
        return new Step(project, hit.Member, file, line, hit.Sent, hit.Received, new IntegrationUse(hit.Integration.Id, hit.Operation), []);
    }

    /// <summary>Tar bort dubbletter, och "spara ändringar" när samma metod redan visar vilken tabell som skrivs.</summary>
    private static List<Step> Tidy(List<Step> steps)
    {
        var tabled = steps.Where(s => s.Integration is { } i && !SinkCatalog.IsSaveChanges(i.Operation)).Select(s => s.Integration!.Id).ToHashSet();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return steps
            .Where(s => s.Integration is not { } i || !SinkCatalog.IsSaveChanges(i.Operation) || !tabled.Contains(i.Id))
            .Where(s => seen.Add(s.Integration is { } i ? $"{s.Project}|{i.Id}|{i.Operation}" : $"{s.Project}|{s.Member}"))
            .ToList();
    }

    private static IEnumerable<SyntaxNode> Bodies(IMethodSymbol method)
    {
        foreach (var reference in method.OriginalDefinition.DeclaringSyntaxReferences)
        {
            SyntaxNode? body = reference.GetSyntax() switch
            {
                BaseMethodDeclarationSyntax m => (SyntaxNode?)m.Body ?? m.ExpressionBody,
                LocalFunctionStatementSyntax f => (SyntaxNode?)f.Body ?? f.ExpressionBody,
                AccessorDeclarationSyntax a => (SyntaxNode?)a.Body ?? a.ExpressionBody,
                _ => null,
            };
            if (body is not null) yield return body;
        }
    }

    private (string File, int Line) Where(Location location) => Symbols.Where(location, root);
}
