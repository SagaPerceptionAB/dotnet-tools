using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SystemMap;

/// <summary>
/// Hittar ingångar som sätts igång av ett meddelande eller en tid: meddelandehanterare (MassTransit <c>IConsumer&lt;T&gt;</c>, NServiceBus
/// <c>IHandleMessages&lt;T&gt;</c>, Azure Service Bus <c>ProcessMessageAsync += hanterare</c>) och jobb (Hangfire <c>RecurringJob.AddOrUpdate</c> och
/// <c>BackgroundJob.Enqueue</c>, Quartz <c>IJob</c>). Typerna känns igen på namnrymd och namn, så att kartan inte behöver paketen.
/// MediatR är inte med: <c>INotificationHandler</c> och <c>IRequestHandler</c> anropas inom processen och nås från den som anropar dem.
/// </summary>
internal static class MessagingFinder
{
    private static readonly (string Interface, string Method)[] Handlers =
        [("MassTransit.IConsumer`1", "Consume"), ("NServiceBus.IHandleMessages`1", "Handle")];

    private static readonly string[] ProcessorEvents = ["ProcessMessageAsync", "ProcessSessionMessageAsync"];
    private static readonly string[] Processors = ["Azure.Messaging.ServiceBus.ServiceBusProcessor", "Azure.Messaging.ServiceBus.ServiceBusSessionProcessor"];
    private static readonly string[] HangfireEntrances = ["Hangfire.RecurringJob", "Hangfire.BackgroundJob", "Hangfire.IRecurringJobManager", "Hangfire.IBackgroundJobClient",
        "Hangfire.RecurringJobManagerExtensions", "Hangfire.BackgroundJobClientExtensions"];
    private static readonly string[] HangfireMethods = ["AddOrUpdate", "Enqueue", "Schedule"];

    public static IEnumerable<EntryCandidate> Find(
        IEnumerable<(INamedTypeSymbol Type, string Project)> types, IReadOnlyList<Project> projects, ModelCache models, CallSites sites, ServiceResolver services, string root)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (type, project) in types)
        {
            if (type is not { TypeKind: TypeKind.Class, IsAbstract: false }) continue;
            foreach (var (interfaceName, methodName) in Handlers)
                foreach (var iface in type.AllInterfaces.Where(i => Symbols.FullName(i) == interfaceName))
                    if (Implementation(type, iface, methodName) is { } method)
                    {
                        var message = iface.TypeArguments[0];
                        var (file, line) = Symbols.Where(method.Locations.First(l => l.IsInSource), root);
                        var name = $"Meddelande: {message.Name} → {type.Name}";
                        if (names.Add(name))
                            yield return new EntryCandidate(EntryKind.Consumer, [method], "", "", project, $"{type.Name}.{method.Name}", file, line, DataShapes.Of(message), null, name);
                    }
            if (type.AllInterfaces.FirstOrDefault(i => Symbols.FullName(i) == "Quartz.IJob") is { } job && Implementation(type, job, "Execute") is { } execute)
            {
                var (file, line) = Symbols.Where(execute.Locations.First(l => l.IsInSource), root);
                var name = "Jobb: " + type.Name;
                if (names.Add(name))
                    yield return new EntryCandidate(EntryKind.Job, [execute], "", "", project, $"{type.Name}.{execute.Name}", file, line, null, null, name);
            }
        }

        foreach (var entry in ServiceBus(models, root, names)) yield return entry;
        foreach (var entry in Hangfire(sites, models, services, root, names)) yield return entry;
    }

    /// <summary>Klassens metod som implementerar gränssnittets metod, om den finns i solutionen.</summary>
    private static IMethodSymbol? Implementation(INamedTypeSymbol type, INamedTypeSymbol iface, string name) =>
        iface.GetMembers(name).OfType<IMethodSymbol>().Select(m => type.FindImplementationForInterfaceMember(m) as IMethodSymbol)
            .FirstOrDefault(m => m is not null && Symbols.InSource(m) && !m.IsAbstract);

    /// <summary><c>processor.ProcessMessageAsync += HandleAsync;</c>: hanteraren är ingången, och kön står i namnet om den går att läsa.</summary>
    private static IEnumerable<EntryCandidate> ServiceBus(ModelCache models, string root, HashSet<string> names)
    {
        foreach (var (assignment, model, project) in Assignments(models))
        {
            if (assignment.Left is not MemberAccessExpressionSyntax left || !ProcessorEvents.Contains(left.Name.Identifier.ValueText) ||
                model.GetSymbolInfo(left).Symbol is not IEventSymbol { ContainingType: { } owner } || !Processors.Contains(Symbols.FullName(owner))) continue;
            var (symbol, lambdaName) = MinimalApiFinder.Handler(assignment.Right, model);
            var handler = lambdaName ?? (symbol is null ? MinimalApiFinder.Enclosing(assignment, model) : $"{symbol.ContainingType.Name}.{symbol.Name}");
            var queue = QueueOf(left.Expression, model) ?? "Service Bus";
            var name = $"Meddelande: {queue} → {handler}";
            var (file, line) = symbol is { } m && m.MethodKind == MethodKind.Ordinary && Symbols.InSource(m)
                ? Symbols.Where(m.Locations.First(l => l.IsInSource), root)
                : Symbols.Where(assignment.GetLocation(), root);
            if (names.Add(name))
                yield return new EntryCandidate(EntryKind.Consumer, symbol is not null && Symbols.InSource(symbol) ? [symbol] : [], "", "", project, handler, file, line, null, null, name);
        }
    }

    private static IEnumerable<(AssignmentExpressionSyntax Assignment, SemanticModel Model, string Project)> Assignments(ModelCache models)
    {
        foreach (var compilation in models.Compilations)
            foreach (var tree in compilation.SyntaxTrees)
            {
                if (models.For(tree) is not { } model || models.ProjectOf(tree) is not { } project) continue;
                foreach (var assignment in tree.GetRoot().DescendantNodes().OfType<AssignmentExpressionSyntax>())
                    if (assignment.IsKind(SyntaxKind.AddAssignmentExpression)) yield return (assignment, model, project);
            }
    }

    /// <summary>Kön (eller ämnet och prenumerationen) i <c>client.CreateProcessor("kö")</c> som processorn kommer från, om den står som text.</summary>
    private static string? QueueOf(ExpressionSyntax receiver, SemanticModel model)
    {
        var creation = receiver as InvocationExpressionSyntax;
        if (creation is null && model.GetSymbolInfo(receiver).Symbol is { } symbol)
            creation = symbol.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).OfType<VariableDeclaratorSyntax>().Select(v => v.Initializer?.Value).OfType<InvocationExpressionSyntax>().FirstOrDefault();
        if (creation is null || Symbols.InvokedName(creation) is not ("CreateProcessor" or "CreateSessionProcessor")) return null;
        var parts = creation.ArgumentList.Arguments.Select(a => Symbols.ConstantString(a.Expression, model)).TakeWhile(s => s is not null).Take(2).ToList();
        return parts.Count == 0 ? null : string.Join("/", parts);
    }

    /// <summary>
    /// <c>RecurringJob.AddOrUpdate("natt", () =&gt; rapporter.Kör())</c> och <c>BackgroundJob.Enqueue(() =&gt; ...)</c>: metoden i uttrycket är ingången.
    /// Anropas den via ett gränssnitt är det klasserna som implementerar det.
    /// </summary>
    private static IEnumerable<EntryCandidate> Hangfire(CallSites sites, ModelCache models, ServiceResolver services, string root, HashSet<string> names)
    {
        foreach (var method in HangfireMethods)
            foreach (var (call, model) in sites.Named(method))
            {
                if (Symbols.Invoked(call, model) is not { } called || called.ContainingType is not { } owner || !HangfireEntrances.Contains(Symbols.FullName(owner))) continue;
                if (models.ProjectOf(call.SyntaxTree) is not { } project) continue;
                var lambda = call.ArgumentList.Arguments.Select(a => a.Expression).OfType<LambdaExpressionSyntax>().FirstOrDefault();
                if (lambda is null) continue;
                var target = lambda.Body as InvocationExpressionSyntax ?? lambda.Body.DescendantNodes().OfType<InvocationExpressionSyntax>().FirstOrDefault();
                if (target is null || Symbols.Invoked(target, model) is not { } job) continue;
                var id = call.ArgumentList.Arguments.FirstOrDefault() is { } first && first.Expression is not LambdaExpressionSyntax ? Symbols.ConstantString(first.Expression, model) : null;
                var handler = $"{job.ContainingType.Name}.{job.Name}";
                var name = "Jobb: " + (id is not null && id != handler ? $"{id} → {handler}" : handler);
                if (!names.Add(name)) continue;
                IReadOnlyList<IMethodSymbol> roots = [.. services.Resolve(job).Select(t => t.Target).DistinctBy(Symbols.Key)];
                var shown = roots.Count > 0 ? roots[0] : job;
                var (file, line) = Symbols.InSource(shown) ? Symbols.Where(shown.Locations.First(l => l.IsInSource), root) : Symbols.Where(call.GetLocation(), root);
                yield return new EntryCandidate(EntryKind.Job, roots, "", "", project, handler, file, line, null, null, name);
            }
    }
}
