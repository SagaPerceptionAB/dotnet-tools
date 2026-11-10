using Microsoft.CodeAnalysis;

namespace SystemMap;

/// <summary>Kompileringarna för solutionens projekt, så att semantiska modeller kan hämtas direkt för varje fil.</summary>
internal sealed class ModelCache
{
    private readonly Dictionary<SyntaxTree, (Compilation Compilation, Project Project)> _trees = new();
    private readonly Dictionary<ProjectId, (Compilation Compilation, List<SyntaxTree> Trees)> _projects = new();
    private readonly Dictionary<SyntaxTree, SemanticModel> _models = new();
    private readonly Dictionary<(ProjectId, string), object?> _facts = new();

    public static async Task<ModelCache> CreateAsync(IEnumerable<Project> projects, CancellationToken ct)
    {
        var cache = new ModelCache();
        var list = projects.ToList();
        // Projekten kompileras samtidigt; filerna läggs ändå till i solutionens ordning, så resultatet är detsamma varje gång.
        var compilations = await Task.WhenAll(list.Select(p => p.GetCompilationAsync(ct)));
        for (var i = 0; i < list.Count; i++)
            if (compilations[i] is { } compilation)
                foreach (var tree in compilation.SyntaxTrees)
                    if (cache._trees.TryAdd(tree, (compilation, list[i])))
                    {
                        if (!cache._projects.TryGetValue(list[i].Id, out var owned)) cache._projects[list[i].Id] = owned = (compilation, []);
                        owned.Trees.Add(tree);
                    }
        return cache;
    }

    public IEnumerable<Compilation> Compilations => _trees.Values.Select(v => v.Compilation).Distinct();

    public Compilation? CompilationOf(Project project) => _projects.TryGetValue(project.Id, out var owned) ? owned.Compilation : null;

    public IReadOnlyList<SyntaxTree> Trees(Project project) => _projects.TryGetValue(project.Id, out var owned) ? owned.Trees : [];

    /// <summary>Ett program som körs (exe: konsol eller Windows) har en kompilering med startpunkt. Ett bibliotek har ingen.</summary>
    public bool IsProgram(Project project) => ProgramCompilation(project) is not null;

    /// <summary><c>Main</c> (eller satserna på översta nivån) i ett program som körs, annars null. Hämtas en gång per projekt.</summary>
    public IMethodSymbol? EntryPointOf(Project project, CancellationToken ct) =>
        Fact(project, "entry point", () => ProgramCompilation(project)?.GetEntryPoint(ct));

    private Compilation? ProgramCompilation(Project project) =>
        CompilationOf(project) is { Options.OutputKind: OutputKind.ConsoleApplication or OutputKind.WindowsApplication } compilation ? compilation : null;

    /// <summary>Det som reglerna frågar efter om ett projekt flera gånger (är det en app, en värd ...), räknat en gång.</summary>
    public T Fact<T>(Project project, string name, Func<T> compute)
    {
        if (_facts.TryGetValue((project.Id, name), out var known)) return (T)known!;
        var value = compute();
        _facts[(project.Id, name)] = value;
        return value;
    }

    public SemanticModel? For(SyntaxTree tree)
    {
        if (_models.TryGetValue(tree, out var model)) return model;
        if (!_trees.TryGetValue(tree, out var entry)) return null;
        return _models[tree] = entry.Compilation.GetSemanticModel(tree);
    }

    public string? ProjectOf(SyntaxTree tree) => _trees.TryGetValue(tree, out var entry) ? entry.Project.Name : null;
}
