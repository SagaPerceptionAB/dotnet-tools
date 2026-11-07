using System.Text.RegularExpressions;
using TestGen.Scenarios;

namespace TestGen.Core;

public sealed record Dependencies(SortedDictionary<string, string> Bindings, List<string> Setup);

/// <summary>
/// Hittar beroenden: fältet {x}Id och {id} efter /xs pekar på POST /xs. Värden blir "${createX.id}"
/// och setup listas i körordning. Bindings i testgen.yaml har företräde.
/// </summary>
public sealed class DependencyResolver
{
    private static readonly Regex IdField = new(@"^(.+)Id$", RegexOptions.Compiled);

    private readonly Dictionary<string, OperationInfo> _ops;
    private readonly TestGenConfig _config;
    private readonly Dictionary<string, Dependencies> _cache = new();

    public DependencyResolver(IEnumerable<OperationInfo> ops, TestGenConfig config)
    {
        _ops = ops.ToDictionary(o => o.OperationId);
        _config = config;
    }

    public Dependencies Resolve(string operationId) => Resolve(operationId, new HashSet<string>());

    private Dependencies Resolve(string operationId, HashSet<string> stack)
    {
        if (_cache.TryGetValue(operationId, out var cached)) return cached;
        var bindings = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var setup = new List<string>();
        if (!_ops.TryGetValue(operationId, out var op) || !stack.Add(operationId))
            return new Dependencies(bindings, setup);

        foreach (var (field, expr) in Infer(op)) bindings[field] = expr;
        if (_config.Bindings.TryGetValue(operationId, out var configured))
            foreach (var (field, expr) in configured) bindings[field] = expr;

        foreach (var dep in bindings.Values.SelectMany(ResponseStore.ReferencedAliases))
        {
            if (!_ops.ContainsKey(dep) || dep == operationId) continue;
            foreach (var s in Resolve(dep, stack).Setup) if (!setup.Contains(s)) setup.Add(s);
            if (!setup.Contains(dep)) setup.Add(dep);
        }
        stack.Remove(operationId);
        var result = new Dependencies(bindings, setup);
        _cache[operationId] = result;
        return result;
    }

    private IEnumerable<KeyValuePair<string, string>> Infer(OperationInfo op)
    {
        var fields = new SortedSet<string>(StringComparer.Ordinal);
        if (op.BodySchema is not null) foreach (var k in SchemaUtil.Flatten(op.BodySchema).Props.Keys) fields.Add(k);
        foreach (var p in op.Parameters.Where(p => p.In is "path" or "query")) fields.Add(p.Name);

        foreach (var field in fields)
        {
            OperationInfo? creator = null;
            var m = IdField.Match(field);
            if (m.Success) creator = FindCreator(m.Groups[1].Value, op);
            else if (field == "id") creator = CreatorForPathId(op);
            if (creator is not null) yield return KeyValuePair.Create(field, $"${{{creator.OperationId}.id}}");
        }
    }

    private OperationInfo? CreatorForPathId(OperationInfo op)
    {
        var idx = op.Path.IndexOf("{id}", StringComparison.Ordinal);
        if (idx <= 0) return null;
        var prefix = op.Path[..idx].TrimEnd('/');
        return _ops.Values.Where(o => o.Method == "POST" && o.Path == prefix && o.OperationId != op.OperationId)
            .OrderBy(o => o.OperationId, StringComparer.Ordinal).FirstOrDefault();
    }

    private OperationInfo? FindCreator(string resource, OperationInfo self)
    {
        var r = resource.ToLowerInvariant();
        var names = new HashSet<string> { r, r + "s", r + "es" };
        if (r.EndsWith('y')) names.Add(r[..^1] + "ies");
        return _ops.Values
            .Where(o => o.Method == "POST" && o.OperationId != self.OperationId && !o.Path.Contains('{'))
            .Where(o => names.Contains(o.Path.TrimEnd('/').Split('/')[^1].ToLowerInvariant()))
            .OrderBy(o => o.Path.Length).ThenBy(o => o.OperationId, StringComparer.Ordinal)
            .FirstOrDefault();
    }
}
