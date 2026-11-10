using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SystemMap;

/// <summary>
/// Följer anropen från en metod genom solutionen tills de lämnar systemet. Anrop till ramverket och paket utan
/// integration hoppas över. Ett steg visas om det går via ett gränssnitt (en "port"), leder till en integration eller
/// är en tjänst i ett annat projekt; andra hjälpmetoder (t.ex. <c>Pricing.For</c>) visas inte, men deras anrop följs ändå.
/// </summary>
/// <param name="mainWalker">Satt för en <see cref="ForReachability"/>-vandrare: den följer bara vad som nås och bygger inga steg.</param>
internal sealed class CallWalker(ServiceResolver resolver, ModelCache models, Origins origins, string root, List<string> warnings, CallWalker? mainWalker = null)
{
    public const int MaxDepth = 8;

    private readonly Dictionary<string, Step?> _memo = new(StringComparer.Ordinal);

    /// <summary>Integrationsanropen under varje steg (se <see cref="Uses"/>), räknade en gång per steg eftersom stegen delas via minnet.</summary>
    private readonly Dictionary<Step, string[]> _uses = new(ReferenceEqualityComparer.Instance);

    /// <summary>Integrationerna som hittats hittills, per id.</summary>
    public Dictionary<string, Integration> Integrations { get; } = new(StringComparer.Ordinal);

    /// <summary>Nycklarna (<see cref="Symbols.Key"/>) för metoderna som anropen har gått genom, även hjälpmetoder som kartan inte ritar.</summary>
    public HashSet<string> Visited { get; } = new(StringComparer.Ordinal);

    /// <summary>Klasserna (<see cref="Symbols.TypeKey"/>) som någon instans skapas av i koden som följts.</summary>
    public HashSet<string> Created { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Metoder i solutionen som anropen når men som inte följs här: de som koden skickar vidare som värde (<c>list.Select(Load)</c>,
    /// <c>handler = OnDone</c>), som anropas av någon annan än den som skickar dem, och de som ligger bortom <see cref="MaxDepth"/>.
    /// <see cref="CodeInventoryFinder"/> följer dem själv och räknar dem som nådda.
    /// </summary>
    public Dictionary<string, IMethodSymbol> Referenced { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// En vandrare som bara håller reda på vad anropen når (<see cref="Visited"/>, <see cref="Created"/>, <see cref="Referenced"/>), för koden som
    /// ramverket eller uppstarten anropar: den bygger inga steg och letar inga integrationer, och hoppar över det den här vandraren redan följt.
    /// </summary>
    public CallWalker ForReachability() => new(resolver, models, origins, root, [], this);

    /// <summary>Har anropen gått genom metoden (nyckel enligt <see cref="Symbols.Key"/>), här eller i vandraren som den här bygger på?</summary>
    public bool HasVisited(string key) => Visited.Contains(key) || mainWalker?.HasVisited(key) == true;

    /// <summary>Skapas en instans av klassen (nyckel enligt <see cref="Symbols.TypeKey"/>) av koden som följts, här eller i vandraren som den här bygger på?</summary>
    public bool HasCreated(string typeKey) => Created.Contains(typeKey) || mainWalker?.HasCreated(typeKey) == true;

    /// <summary>Följer anropen från metoderna utan att bygga steg (bara för en <see cref="ForReachability"/>-vandrare).</summary>
    public async Task ReachFromAsync(IEnumerable<IMethodSymbol> methods, CancellationToken ct)
    {
        foreach (var method in methods)
            if (!HasVisited(Symbols.Key(method))) await ChildrenAsync(method, [Symbols.Key(method)], 1, Bindings.None, ct);
    }

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
        Visited.Add(Symbols.Key(method));
        foreach (var body in Bodies(method))
        {
            if (models.For(body.SyntaxTree) is not { } model) continue;
            var project = models.ProjectOf(body.SyntaxTree) ?? "";
            var context = mainWalker is null ? new SinkContext(method, body, model, resolver, origins, project, bindings) : null;
            foreach (var node in body.DescendantNodesAndSelf())   // en lambda med uttryck som kropp är själva anropet
            {
                ct.ThrowIfCancellationRequested();
                if (node is BaseObjectCreationExpressionSyntax creation)
                {
                    if (model.GetTypeInfo(creation).Type is { } created && Symbols.InSource(created)) Created.Add(Symbols.TypeKey(created));
                    if (context is not null && SinkCatalog.MatchCreation(creation, context) is { } sink) steps.Add(Leave(sink, project, creation));
                    continue;
                }
                if (node is IdentifierNameSyntax or MemberAccessExpressionSyntax && IsValueContext(node) &&
                    model.GetSymbolInfo(node).Symbol is IMethodSymbol { MethodKind: MethodKind.Ordinary or MethodKind.LocalFunction } group && Symbols.InSource(group))
                    Referenced.TryAdd(Symbols.Key(group), group);
                if (node is not InvocationExpressionSyntax invocation) continue;
                if (context is not null && SinkCatalog.Match(invocation, context) is { } hit)
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
                        Referenced.TryAdd(key, implementation);   // följs inte här, men räknas som nådd (se Referenced)
                        warnings.Add($"Kartan går högst {MaxDepth} anrop djupt; anropen från {method.ContainingType.Name}.{method.Name} till {implementation.ContainingType.Name}.{implementation.Name} visas inte.");
                        continue;
                    }
                    if (mainWalker is not null)
                    {
                        if (!HasVisited(key) && implementation.OriginalDefinition.DeclaringSyntaxReferences.Length > 0)
                            await ChildrenAsync(implementation, path.Add(key), depth + 1, Bindings.None, ct);
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

    /// <summary>Ett namn som står där ett värde förväntas (argument, tilldelning, return ...) och inte är anropet självt: kan vara en metod som skickas vidare.</summary>
    private static bool IsValueContext(SyntaxNode node) => node.Parent switch
    {
        ArgumentSyntax or EqualsValueClauseSyntax or ReturnStatementSyntax or ArrowExpressionClauseSyntax or CastExpressionSyntax => true,
        AssignmentExpressionSyntax assignment => assignment.Right == node,
        _ => false,
    };

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
    /// en konstant, en konfigurationsnyckel eller en typad klient, och OIDC-hanterare med sin adress). Andra metoder minns som metoden själv.
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
            else if (Symbols.Is(parameter.Type, DiscoveryAddress.ManagerType, DiscoveryAddress.ManagerInterface))
            {
                // Två OIDC-hanterare (Apple och Google) som skickas till samma metod hämtar från olika adresser.
                var address = resolver.Discovery.OfManager(binding.Expression, binding.Model, binding.Outer, models.ProjectOf(binding.Expression.SyntaxTree) ?? "");
                if (address.IsKnown) parts.Add($"{parameter.Ordinal}m:{address.Text}|{address.ConfigKey}");
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
        return new Step(project, hit.Member, file, line, hit.Sent, hit.Received, new IntegrationUse(hit.Integration.Id, hit.Operation, hit.AddressAtRuntime), []);
    }

    /// <summary>Tar bort dubbletter, och "spara ändringar" när samma metod redan visar vilken tabell som skrivs.</summary>
    private List<Step> Tidy(List<Step> steps)
    {
        var tabled = steps.Where(s => s.Integration is { } i && !SinkCatalog.IsSaveChanges(i.Operation)).Select(s => s.Integration!.Id).ToHashSet();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return steps
            .Where(s => s.Integration is not { } i || !SinkCatalog.IsSaveChanges(i.Operation) || !tabled.Contains(i.Id))
            .Where(s => seen.Add(s.Integration is { } i ? $"{s.Project}|{i.Key}" : $"{s.Project}|{s.Member}|{string.Join(';', Uses(s))}"))
            .ToList();
    }

    /// <summary>Integrationsanropen under ett steg, så att samma metod anropad med olika sökvägar inte slås ihop.</summary>
    private string[] Uses(Step step)
    {
        if (!_uses.TryGetValue(step, out var uses))
            _uses[step] = uses = (step.Integration is { } i ? [i.Key] : step.Steps.SelectMany(s => Uses(s)))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return uses;
    }

    private static IEnumerable<SyntaxNode> Bodies(IMethodSymbol method)
    {
        foreach (var reference in method.OriginalDefinition.DeclaringSyntaxReferences)
        {
            var syntax = reference.GetSyntax();
            SyntaxNode? body = syntax switch
            {
                BaseMethodDeclarationSyntax m => (SyntaxNode?)m.Body ?? m.ExpressionBody,
                LocalFunctionStatementSyntax f => (SyntaxNode?)f.Body ?? f.ExpressionBody,
                AnonymousFunctionExpressionSyntax l => l.Body,
                AccessorDeclarationSyntax a => (SyntaxNode?)a.Body ?? a.ExpressionBody,
                PropertyDeclarationSyntax p => p.ExpressionBody,   // egenskap med => som get
                IndexerDeclarationSyntax i => i.ExpressionBody,
                ArrowExpressionClauseSyntax arrow => arrow,
                _ => null,
            };
            if (body is not null) yield return body;
            // Main utan Program-klass (satser på översta nivån): satserna i filen, men inte klasserna som står bredvid dem.
            if (syntax is CompilationUnitSyntax unit)
                foreach (var statement in unit.Members.OfType<GlobalStatementSyntax>()) yield return statement;
            // En konstruktor kör också basanropet och fält- och egenskapsvärdena (också när den är primär eller inte skriven alls).
            if (method.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor)
            {
                if (syntax is ConstructorDeclarationSyntax { Initializer: { } initializer }) yield return initializer;
                var owner = syntax as TypeDeclarationSyntax ?? syntax.Parent as TypeDeclarationSyntax;
                if (owner is null) continue;
                if (owner.BaseList is { } list && !method.IsStatic) yield return list;
                foreach (var member in owner.Members)
                {
                    var modifiers = member switch { FieldDeclarationSyntax f => f.Modifiers, PropertyDeclarationSyntax p => p.Modifiers, _ => default };
                    if (modifiers.Any(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.StaticKeyword)) != method.IsStatic) continue;
                    if (member is FieldDeclarationSyntax field)
                        foreach (var variable in field.Declaration.Variables)
                            if (variable.Initializer is { } value) yield return value;
                    if (member is PropertyDeclarationSyntax { Initializer: { } property }) yield return property;
                }
            }
        }
    }

    private (string File, int Line) Where(Location location) => Symbols.Where(location, root);
}

