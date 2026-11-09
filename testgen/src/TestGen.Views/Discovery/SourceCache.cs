using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TestGen.Views.Discovery.Razor;

namespace TestGen.Views.Discovery;

/// <summary>En C#-fil: texten och dess syntaxträd (Roslyn, ingen kompilering).</summary>
internal sealed record CSharpFile(string Text, SyntaxTree Tree);

/// <summary>
/// Det som lästs ur filerna under en körning, så att varje fil läses och tolkas högst en gång: filträdet (en enda vandring, grupperad
/// per ändelse), C#-filerna som text och syntaxträd, och Razor-filerna som <see cref="RazorSource"/>. Delas av alla upptäckare i
/// samma <see cref="DiscoveryContext"/>. En fil som inte går att läsa ger en varning, en gång.
/// </summary>
internal sealed class SourceCache
{
    private static readonly ConditionalWeakTable<DiscoveryContext, SourceCache> Instances = new();

    private readonly DiscoveryContext _context;
    private readonly Dictionary<string, CSharpFile?> _csharp = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RazorSource?> _razor = new(StringComparer.Ordinal);
    private Dictionary<string, List<string>>? _byExtension;

    private SourceCache(DiscoveryContext context) => _context = context;

    public static SourceCache For(DiscoveryContext context) => Instances.GetValue(context, c => new SourceCache(c));

    /// <summary>Filerna med någon av ändelserna (t.ex. <c>.cs</c>), i sorterad ordning. Trädet vandras bara första gången.</summary>
    public IReadOnlyList<string> Files(string[] extensions)
    {
        var tree = _byExtension ??= Walk();
        if (extensions.Length == 1) return tree.GetValueOrDefault(extensions[0]) ?? [];
        return extensions.Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(e => tree.GetValueOrDefault(e) ?? [])
            .Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>C#-filen, eller null (och en varning) om den inte går att läsa.</summary>
    public CSharpFile? CSharp(string path)
    {
        if (_csharp.TryGetValue(path, out var cached)) return cached;
        var text = ReadText(path);
        return _csharp[path] = text is null ? null : new CSharpFile(text, CSharpSyntaxTree.ParseText(text));
    }

    /// <summary>.razor- eller .cshtml-filen, eller null (och en varning) om den inte går att läsa.</summary>
    public RazorSource? Razor(string path)
    {
        if (_razor.TryGetValue(path, out var cached)) return cached;
        var text = ReadText(path);
        return _razor[path] = text is null ? null : RazorSource.Parse(path, text);
    }

    private Dictionary<string, List<string>> Walk()
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var directory in _context.Directories)
            foreach (var file in SourceFiles.Enumerate(directory))
            {
                if (!seen.Add(file)) continue;
                var extension = Path.GetExtension(file);
                if (!result.TryGetValue(extension, out var list)) result[extension] = list = [];
                list.Add(file);
            }
        foreach (var list in result.Values) list.Sort(StringComparer.Ordinal);
        return result;
    }

    private string? ReadText(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _context.Warnings.Add($"Kunde inte läsa {_context.Relative(path)}: {e.Message}");
            return null;
        }
    }
}
