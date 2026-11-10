namespace SystemMap;

public enum OverviewKind { Entry, Project, Class, Method, Integration, Operation }

/// <summary>
/// En del av systemet i översikten. Delarna ligger i varandra: projekt → klass → metod, och integration → operation
/// (t.ex. "läsa Customers" i databasen eller "skicka OrderPlaced" i en kö).
/// </summary>
/// <param name="Parent">Delen den ligger i, eller null för anrop, projekt och integrationer.</param>
/// <param name="Ref">För en integration dess id; för ett anrop dess sort (<c>controller</c>, <c>razor-page</c> ...). Namnet på ett anrop är dess nyckel, <c>POST /orders</c>.</param>
/// <param name="File">För en metod filen där den finns, relativt solutionens katalog.</param>
/// <param name="Entries">Anropen (nycklar som <c>POST /orders</c>, <c>Bakgrund: OrderCleanupWorker</c>) vars väg går genom delen.</param>
/// <param name="Reached">Falskt för ett projekt, en klass eller en metod som ingen ingång når. Sant för allt annat.</param>
public sealed record OverviewNode(
    string Id, OverviewKind Kind, string Name, string? Parent, string? Ref, string? File, int? Line, DataShape? In, DataShape? Out,
    IReadOnlyList<string> Entries, bool Reached = true);

/// <summary>Ett anrop från en del till en annan.</summary>
/// <param name="File">För ett anrop till en integration: platsen i koden där anropet görs.</param>
public sealed record OverviewLink(string From, string To, string? File, int? Line, IReadOnlyList<string> Entries);

/// <summary>Hela systemet där varje del finns en gång, och anropen mellan delarna.</summary>
public sealed record Overview(IReadOnlyList<OverviewNode> Nodes, IReadOnlyList<OverviewLink> Links);

/// <summary>
/// Räknar fram översikten ur ingångarnas anropsträd: varje klass, metod och integration en gång, med kanter mellan dem.
/// En databas blir en del med en rad per läsning och skrivning; en kö en del med en rad per meddelande.
/// </summary>
public static partial class CodeMapOverview
{
    /// <summary>Nyckeln för ett anrop, t.ex. <c>POST /orders</c>, eller för en ingång som inte är HTTP dess namn, t.ex. <c>Bakgrund: OrderCleanupWorker</c>.</summary>
    public static string EntryKey(EntryPoint entry) => entry.Key;

    /// <summary>Id:t för ett projekt i översikten.</summary>
    public static string ProjectId(string project) => "proj:" + project;
    /// <summary>Id:t för en klass i ett projekt.</summary>
    public static string ClassId(string project, string type) => $"class:{project}/{type}";
    /// <summary>Id:t för en integration i översikten (<paramref name="integration"/> är integrationens eget id, t.ex. <c>db:ShopDatabase</c>).</summary>
    public static string IntegrationNodeId(string integration) => "int:" + integration;

    /// <summary><c>OrderService.PlaceAsync</c> delas vid sista punkten: klassen och metoden.</summary>
    private static (string Type, string Method) SplitMember(string member)
    {
        var dot = member.LastIndexOf('.');
        return dot < 0 ? (member, member) : (member[..dot], member[(dot + 1)..]);
    }

    /// <summary>Delarna som ingen ingång når: projekt, klasser och metoder. De har inga pilar.</summary>
    public static IReadOnlyList<OverviewNode> Unreached(Overview overview) => overview.Nodes.Where(n => !n.Reached).ToList();

    /// <summary>Kartan utan det som ingen ingång når.</summary>
    public static Overview WithoutUnreached(Overview overview) =>
        overview.Nodes.All(n => n.Reached) ? overview : new Overview(overview.Nodes.Where(n => n.Reached).ToList(), overview.Links);

    public static Overview Build(CodeMapModel map)
    {
        var builder = new Builder(map);
        foreach (var entry in map.EntryPoints) builder.Add(entry);
        builder.AddCode(map.Code);
        return builder.Result();
    }

    private sealed class Node(string id, OverviewKind kind, string name, string? parent)
    {
        public string Id { get; } = id;
        public OverviewKind Kind { get; } = kind;
        public string Name { get; } = name;
        public string? Parent { get; } = parent;
        public string? Ref { get; init; }
        public string? File { get; init; }
        public int? Line { get; init; }
        public DataShape? In { get; init; }
        public DataShape? Out { get; init; }
        public bool Reached { get; init; } = true;
        public List<string> Entries { get; } = [];
        public List<Node> Children { get; } = [];

        public void Touch(string entry)
        {
            if (!Entries.Contains(entry)) Entries.Add(entry);
        }

        public OverviewNode ToRecord() => new(Id, Kind, Name, Parent, Ref, File, Line, In, Out, Entries, Reached);
    }

    private sealed class Builder
    {
        private readonly Dictionary<string, Integration> _integrations;
        private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal);
        private readonly List<Node> _entries = [];
        private readonly List<Node> _projects = [];
        private readonly List<Node> _external = [];
        private readonly Dictionary<(string, string), List<string>> _linkEntries = [];
        private readonly List<OverviewLink> _links = [];

        public Builder(CodeMapModel map)
        {
            _integrations = map.Integrations.ToDictionary(i => i.Id, StringComparer.Ordinal);
            foreach (var project in map.Projects) Project(project);
        }

        public void Add(EntryPoint entry)
        {
            var key = EntryKey(entry);
            var node = GetOrAdd("in:" + key, () => new Node("in:" + key, OverviewKind.Entry, key, null) { Ref = CodeMapJson.KindCode(entry.Kind) }, _entries);
            node.Touch(key);
            var handler = Member(entry.Project, entry.Handler, entry.File, entry.Line, entry.Input, entry.Output, key);
            Connect(node.Id, handler.Id, null, null, key);
            Walk(entry.Steps, handler, key, new HashSet<IReadOnlyList<Step>>(ReferenceEqualityComparer.Instance));
        }

        private void Walk(IReadOnlyList<Step> steps, Node from, string key, HashSet<IReadOnlyList<Step>> walked)
        {
            // Samma lista av steg (delad via minnet) behöver bara gås igenom en gång per anrop.
            if (!walked.Add(steps)) return;
            foreach (var step in steps)
            {
                if (step.Integration is { } use)
                {
                    var operation = Operation(use, step, key);
                    Connect(from.Id, operation.Id, step.File, step.Line, key);
                    continue;
                }
                var member = Member(step.Project, step.Member, step.File, step.Line, step.In, step.Out, key);
                Connect(from.Id, member.Id, null, null, key);
                Walk(step.Steps, member, key, walked);
            }
        }

        private Node Project(string name, bool reached = true) =>
            GetOrAdd(ProjectId(name), () => new Node(ProjectId(name), OverviewKind.Project, name, null) { Reached = reached }, _projects);

        /// <summary>
        /// Det ingen ingång når, sist i sitt projekt: projekt, klasser och metoder med kod (se <see cref="CodeInventory"/>). I en klass som
        /// kartan ritar läggs bara metoderna som inte nås till. En klass som nås bara via hjälpmetoder som kartan inte ritar ritas inte.
        /// </summary>
        public void AddCode(CodeInventory? code)
        {
            if (code is null) return;
            foreach (var project in code.Projects) Project(project.Name, project.Reached);
            foreach (var c in code.Classes)
            {
                var projectNode = Project(c.Project);
                var id = ClassId(c.Project, c.Name);
                if (!_nodes.TryGetValue(id, out var type))
                {
                    if (c.Reached) continue;
                    type = GetOrAdd(id, () => new Node(id, OverviewKind.Class, c.Name, projectNode.Id) { File = c.File, Line = c.Line, Reached = false }, projectNode.Children);
                }
                foreach (var m in c.Methods.Where(m => !m.Reached))
                    GetOrAdd($"method:{c.Project}/{c.Name}.{m.Name}", () =>
                        new Node($"method:{c.Project}/{c.Name}.{m.Name}", OverviewKind.Method, m.Name, type.Id) { File = m.File, Line = m.Line, Reached = false }, type.Children);
            }
        }

        /// <summary>Metoden, i sin klass i sitt projekt. <c>OrderService.PlaceAsync</c> delas vid sista punkten.</summary>
        private Node Member(string project, string member, string file, int line, DataShape? input, DataShape? output, string key)
        {
            var projectNode = Project(project);
            var (typeName, methodName) = SplitMember(member);
            var type = GetOrAdd(ClassId(project, typeName), () => new Node(ClassId(project, typeName), OverviewKind.Class, typeName, projectNode.Id),
                projectNode.Children);
            var method = GetOrAdd($"method:{project}/{member}", () =>
                new Node($"method:{project}/{member}", OverviewKind.Method, methodName, type.Id) { File = file, Line = line, In = input, Out = output }, type.Children);
            projectNode.Touch(key);
            type.Touch(key);
            method.Touch(key);
            return method;
        }

        /// <summary>Raden i integrationen: operationen, och för en kö även meddelandets typ ("skicka OrderPlaced").</summary>
        private Node Operation(IntegrationUse use, Step step, string key)
        {
            var integration = _integrations.GetValueOrDefault(use.Id) ?? new Integration(use.Id, IntegrationKind.Http, use.Id, []);
            var parent = GetOrAdd(IntegrationNodeId(use.Id), () => new Node(IntegrationNodeId(use.Id), OverviewKind.Integration, integration.Name, null) { Ref = use.Id }, _external);
            var label = integration.Kind == IntegrationKind.Queue && step.In is { } message ? $"{use.Operation} {message.Type}" : use.Operation;
            var operation = GetOrAdd($"op:{use.Id}|{label}", () =>
                new Node($"op:{use.Id}|{label}", OverviewKind.Operation, label, parent.Id) { In = step.In, Out = step.Out }, parent.Children);
            parent.Touch(key);
            operation.Touch(key);
            return operation;
        }

        /// <summary>Noden med id:t; finns den inte skapas den och läggs sist i <paramref name="into"/>.</summary>
        private Node GetOrAdd(string id, Func<Node> create, List<Node> into)
        {
            if (_nodes.TryGetValue(id, out var node)) return node;
            _nodes[id] = node = create();
            into.Add(node);
            return node;
        }

        private void Connect(string from, string to, string? file, int? line, string key)
        {
            if (from == to) return;
            if (!_linkEntries.TryGetValue((from, to), out var entries))
            {
                _linkEntries[(from, to)] = entries = [];
                _links.Add(new OverviewLink(from, to, file, line, entries));
            }
            if (!entries.Contains(key)) entries.Add(key);
        }

        /// <summary>
        /// Anropen, sedan projekten med sina klasser och metoder (i den ordning anropen når dem), sist integrationerna med
        /// raderna sorterade, så att "lägga till i …" och "läsa …" hamnar i var sin grupp.
        /// </summary>
        public Overview Result()
        {
            var nodes = new List<OverviewNode>();
            nodes.AddRange(_entries.Select(n => n.ToRecord()));
            foreach (var project in _projects)
            {
                nodes.Add(project.ToRecord());
                foreach (var type in project.Children)
                {
                    nodes.Add(type.ToRecord());
                    nodes.AddRange(type.Children.Select(m => m.ToRecord()));
                }
            }
            foreach (var integration in _external)
            {
                nodes.Add(integration.ToRecord());
                nodes.AddRange(integration.Children.OrderBy(o => o.Name, StringComparer.Ordinal).Select(o => o.ToRecord()));
            }
            return new Overview(nodes, _links);
        }
    }
}
