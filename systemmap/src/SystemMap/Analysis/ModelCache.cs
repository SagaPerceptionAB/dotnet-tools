using Microsoft.CodeAnalysis;

namespace SystemMap;

/// <summary>Kompileringarna för solutionens projekt, så att semantiska modeller kan hämtas direkt för varje fil.</summary>
internal sealed class ModelCache
{
    private readonly Dictionary<SyntaxTree, (Compilation Compilation, Project Project)> _trees = new();
    private readonly Dictionary<SyntaxTree, SemanticModel> _models = new();

    public static async Task<ModelCache> CreateAsync(IEnumerable<Project> projects, CancellationToken ct)
    {
        var cache = new ModelCache();
        var list = projects.ToList();
        // Projekten kompileras samtidigt; filerna läggs ändå till i solutionens ordning, så resultatet är detsamma varje gång.
        var compilations = await Task.WhenAll(list.Select(p => p.GetCompilationAsync(ct)));
        for (var i = 0; i < list.Count; i++)
            if (compilations[i] is { } compilation)
                foreach (var tree in compilation.SyntaxTrees) cache._trees.TryAdd(tree, (compilation, list[i]));
        return cache;
    }

    public IEnumerable<Compilation> Compilations => _trees.Values.Select(v => v.Compilation).Distinct();

    public Compilation? CompilationOf(Project project) =>
        _trees.Values.Where(v => v.Project.Id == project.Id).Select(v => v.Compilation).FirstOrDefault();

    public IEnumerable<SyntaxTree> Trees(Project project) => _trees.Where(t => t.Value.Project.Id == project.Id).Select(t => t.Key);

    public SemanticModel? For(SyntaxTree tree)
    {
        if (_models.TryGetValue(tree, out var model)) return model;
        if (!_trees.TryGetValue(tree, out var entry)) return null;
        return _models[tree] = entry.Compilation.GetSemanticModel(tree);
    }

    public string? ProjectOf(SyntaxTree tree) => _trees.TryGetValue(tree, out var entry) ? entry.Project.Name : null;
}
