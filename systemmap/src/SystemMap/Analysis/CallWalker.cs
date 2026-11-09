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

    /// <summary>Integrationsanropen under varje steg (se <see cref="Uses"/>), räknade en gång per steg eftersom stegen delas via minnet.</summary>
    private readonly Dictionary<Step, string[]> _uses = new(ReferenceEqualityComparer.Instance);

    /// <summary>Integrationerna som hittats hittills, per id.</summary>
    public Dictionary<string, Integration> Integrations { get; } = new(StringComparer.Ordinal);

    /// <summary>Anropen från metoderna (t.ex. en sidas hanterare och dess livscykelmetoder), utan dubbletter.</summary>
    public async Task<IReadOnlyList<Step>> StepsFromAsync(IReadOnlyList<IMethodSymbol> methods, CancellationToken ct)
    {
        var steps = new List<Step>();
        foreach (var method in methods) steps.AddRange(await ChildrenAsync(method, [Symbols.Key(method)], 1, Bindings.None, ct));
        return Tidy(steps);
    }

    private async Task<List<Step>> ChildrenAsync(IMethodSymbol method, ImmutableHashSet<string> path, int depth, Bindings bindings, CancellationToken ct)
    {
        var steps = new List<Step>();
        foreach (var body in Bodies(method))
        {
            if (models.For(body.SyntaxTree) is not { } model) continue;
            var project = models.ProjectOf(body.SyntaxTree) ?? "";
            var context = new SinkContext(method, body, model, resolver, origins, project, bindings);
            foreach (var node in body.DescendantNodesAndSelf())   // en lambda med uttryck som kropp är själva anropet
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
                    var inner = Bindings.For(implementation, invocation, target, model, bindings, method.ContainingType);
                    if (await StepAsync(implementation, path.Add(key), depth + 1, inner, ct) is not { } child) continue;
                    // Ett anrop till en tjänst i ett annat projekt visas också, men inte statiska hjälpare (t.ex. fabriker för undantag).
                    if (dispatch || child.ReachesIntegration || (child.Project != project && !implementation.IsStatic)) steps.Add(child);
                    else steps.AddRange(child.Steps);
                }
            }
        }
        return Tidy(steps);
    }

    private async Task<Step?> StepAsync(IMethodSymbol method, ImmutableHashSet<string> path, int depth, Bindings bindings, CancellationToken ct)
    {
        var key = MemoKey(method, bindings);
        if (_memo.TryGetValue(key, out var known)) return known;
        var declaration = method.OriginalDefinition.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(ct);
        if (declaration is null) return _memo[key] = null;
        var children = await ChildrenAsync(method, path, depth, bindings, ct);
        var (file, line) = Where(declaration switch
        {
            MethodDeclarationSyntax m => m.Identifier.GetLocation(),
            LocalFunctionStatementSyntax f => f.Identifier.GetLocation(),
            _ => declaration.GetLocation(),
        });
        return _memo[key] = new Step(models.ProjectOf(declaration.SyntaxTree) ?? "", $"{method.ContainingType.Name}.{method.Name}", file, line,
            DataShapes.FromParameters(method), DataShapes.Of(method.ReturnType), null, children);
    }

    /// <summary>
    /// Nyckeln för minnet: metoden, och det som parametrarna är bundna till när det påverkar resultatet (HttpClient, strängar och Uri som löser till
    /// en konstant, en konfigurationsnyckel eller en typad klient). Andra metoder minns som metoden själv.
    /// </summary>
    private string MemoKey(IMethodSymbol method, Bindings bindings)
    {
        var key = Symbols.Key(method);
        if (bindings.IsEmpty) return key;
        var parts = new List<string>();
        foreach (var (parameter, binding) in bindings.All.OrderBy(b => b.Parameter.Ordinal))
        {
            if (Symbols.Is(parameter.Type, "System.Net.Http.HttpClient"))
            {
                if (binding.Owner is { } owner) parts.Add($"{parameter.Ordinal}c:{Symbols.TypeKey(owner)}");
            }
            else if (parameter.Type.SpecialType == SpecialType.System_String || Symbols.Is(parameter.Type, "System.Uri"))
            {
                var text = SinkCatalog.Text(binding.Expression, binding.Model, binding.Outer);
                var config = origins.ConfigKeysIn(binding.Expression, binding.Model, binding.Outer).FirstOrDefault();
                if (text is not null || config is not null) parts.Add($"{parameter.Ordinal}t:{text}|{config}");
            }
        }
        return parts.Count == 0 ? key : key + "#" + string.Join(";", parts);
    }

    private Step Leave(SinkHit hit, string project, SyntaxNode node)
    {
        Integrations[hit.Integration.Id] = Integrations.TryGetValue(hit.Integration.Id, out var known) ? known.Merge(hit.Integration) : hit.Integration;
        var (file, line) = Where(node.GetLocation());
        return new Step(project, hit.Member, file, line, hit.Sent, hit.Received, new IntegrationUse(hit.Integration.Id, hit.Operation), []);
    }

    /// <summary>Tar bort dubbletter, och "spara ändringar" när samma metod redan visar vilken tabell som skrivs.</summary>
    private List<Step> Tidy(List<Step> steps)
    {
        var tabled = steps.Where(s => s.Integration is { } i && !SinkCatalog.IsSaveChanges(i.Operation)).Select(s => s.Integration!.Id).ToHashSet();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return steps
            .Where(s => s.Integration is not { } i || !SinkCatalog.IsSaveChanges(i.Operation) || !tabled.Contains(i.Id))
            .Where(s => seen.Add(s.Integration is { } i ? $"{s.Project}|{i.Id}|{i.Operation}" : $"{s.Project}|{s.Member}|{string.Join(';', Uses(s))}"))
            .ToList();
    }

    /// <summary>Integrationsanropen under ett steg, så att samma metod anropad med olika sökvägar inte slås ihop.</summary>
    private string[] Uses(Step step)
    {
        if (!_uses.TryGetValue(step, out var uses))
            _uses[step] = uses = (step.Integration is { } i ? [$"{i.Id}|{i.Operation}"] : step.Steps.SelectMany(s => Uses(s)))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return uses;
    }

    private static IEnumerable<SyntaxNode> Bodies(IMethodSymbol method)
    {
        foreach (var reference in method.OriginalDefinition.DeclaringSyntaxReferences)
        {
            SyntaxNode? body = reference.GetSyntax() switch
            {
                BaseMethodDeclarationSyntax m => (SyntaxNode?)m.Body ?? m.ExpressionBody,
                LocalFunctionStatementSyntax f => (SyntaxNode?)f.Body ?? f.ExpressionBody,
                AnonymousFunctionExpressionSyntax l => l.Body,
                AccessorDeclarationSyntax a => (SyntaxNode?)a.Body ?? a.ExpressionBody,
                _ => null,
            };
            if (body is not null) yield return body;
        }
    }

    private (string File, int Line) Where(Location location) => Symbols.Where(location, root);
}
