using System.Text.RegularExpressions;

namespace TestGen.Views.Discovery.Spa;

/// <summary>En källfil: texten, JS/TS-tokens (för .vue bara script-delen) och vad den importerar.</summary>
internal sealed partial class SpaSource
{
    private List<Tok>? _tokens;
    private Dictionary<string, string>? _imports;
    private List<string>? _specs;

    public SpaSource(string path, string text)
    {
        Path = path;
        Text = text;
    }

    public string Path { get; }

    public string Text { get; }

    public bool IsVue => Path.EndsWith(".vue", StringComparison.OrdinalIgnoreCase);

    /// <summary>Koden: hela filen, eller för en .vue-fil innehållet i <c>&lt;script&gt;</c>.</summary>
    public string Code => IsVue ? string.Concat(ScriptBlock().Matches(Text).Select(m => m.Groups[1].Value + "\n")) : Text;

    public IReadOnlyList<Tok> Tokens => _tokens ??= JsTokenizer.Tokenize(Code);

    /// <summary>Lokalt namn till importväg: <c>import Products from './pages/Products'</c> ger <c>Products</c> till <c>./pages/Products</c>. Även <c>const X = lazy(() =&gt; import('./X'))</c>.</summary>
    public IReadOnlyDictionary<string, string> Imports
    {
        get
        {
            if (_imports is null) Load();
            return _imports!;
        }
    }

    /// <summary>Alla importvägar i filen, statiska och dynamiska, i den ordning de står.</summary>
    public IReadOnlyList<string> ImportSpecs
    {
        get
        {
            if (_specs is null) Load();
            return _specs!;
        }
    }

    private void Load()
    {
        var imports = new Dictionary<string, string>(StringComparer.Ordinal);
        var specs = new List<string>();
        var t = Tokens;
        for (var k = 0; k < t.Count; k++)
        {
            if (t[k].IsIdent("import") && k + 1 < t.Count)
            {
                if (t[k + 1].IsPunct('('))
                {
                    if (k + 2 < t.Count && t[k + 2].IsText) specs.Add(t[k + 2].Text);
                    continue;
                }
                if (t[k + 1].IsText) { specs.Add(t[k + 1].Text); continue; }
                var from = -1;
                for (var j = k + 1; j < t.Count && j < k + 200 && !t[j].IsPunct(';'); j++)
                    if (t[j].IsIdent("from") && j + 1 < t.Count && t[j + 1].IsText) { from = j; break; }
                if (from < 0) continue;
                var spec = t[from + 1].Text;
                specs.Add(spec);
                for (var j = k + 1; j < from; j++)
                {
                    if (t[j].Kind != TokKind.Ident || t[j].Text == "as") continue;
                    if (j + 1 < from && t[j + 1].IsIdent("as")) continue;
                    if (t[j].Text == "type" && j + 1 < from && !t[j + 1].IsPunct(',')) continue;
                    imports[t[j].Text] = spec;
                }
                k = from + 1;
            }
            else if (t[k].IsIdent("lazy"))
            {
                // const Products = lazy(() => import('./Products')) eller React.lazy(...)
                var start = k > 1 && t[k - 1].IsPunct('.') ? k - 2 : k;
                if (start < 2 || !t[start - 1].IsPunct('=') || t[start - 2].Kind != TokKind.Ident) continue;
                var open = k + 1;
                while (open < t.Count && !t[open].IsPunct('(') && open < k + 20) open++;
                if (open >= t.Count || !t[open].IsPunct('(')) continue;
                var close = JsTokenizer.Matching(t, open);
                for (var j = open; close > 0 && j + 2 < close; j++)
                    if (t[j].IsIdent("import") && t[j + 1].IsPunct('(') && t[j + 2].IsText)
                    {
                        imports[t[start - 2].Text] = t[j + 2].Text;
                        break;
                    }
            }
        }
        _imports = imports;
        _specs = specs;
    }

    [GeneratedRegex(@"<script\b[^>]*>(.*?)</script>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptBlock();
}

/// <summary>Läser och cachar källfiler och löser importvägar till filer.</summary>
internal sealed class SpaFiles
{
    private static readonly string[] Extensions = [".tsx", ".jsx", ".ts", ".js", ".vue"];
    private readonly Dictionary<string, SpaSource?> _cache = new(StringComparer.Ordinal);

    public SpaSource? Get(string path)
    {
        if (_cache.TryGetValue(path, out var cached)) return cached;
        SpaSource? source = null;
        try { source = new SpaSource(path, File.ReadAllText(path)); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        _cache[path] = source;
        return source;
    }

    /// <summary>
    /// Filen en importväg pekar på, eller null. Bara relativa vägar och <c>@/</c> (mappen src). Provar vägen som den är, med
    /// ändelserna .tsx, .jsx, .ts, .js, .vue och som mapp med index.
    /// </summary>
    public string? Resolve(string spec, string fromFile)
    {
        string basePath;
        if (spec.StartsWith("./", StringComparison.Ordinal) || spec.StartsWith("../", StringComparison.Ordinal) || spec is "." or "..")
            basePath = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(fromFile)!, spec));
        else if (spec.StartsWith("@/", StringComparison.Ordinal) || spec.StartsWith("~/", StringComparison.Ordinal))
        {
            var src = SrcDirectory(fromFile);
            if (src is null) return null;
            basePath = System.IO.Path.GetFullPath(System.IO.Path.Combine(src, spec[2..]));
        }
        else return null;

        if (File.Exists(basePath)) return basePath;
        foreach (var ext in Extensions)
            if (File.Exists(basePath + ext)) return basePath + ext;
        // ESM-importer i TypeScript skrivs ofta med .js trots att filen är .ts.
        if (basePath.EndsWith(".js", StringComparison.Ordinal) || basePath.EndsWith(".jsx", StringComparison.Ordinal))
        {
            var stem = basePath[..basePath.LastIndexOf('.')];
            foreach (var ext in Extensions)
                if (File.Exists(stem + ext)) return stem + ext;
        }
        foreach (var ext in Extensions)
        {
            var index = System.IO.Path.Combine(basePath, "index" + ext);
            if (File.Exists(index)) return index;
        }
        return null;
    }

    private static string? SrcDirectory(string file)
    {
        var dir = System.IO.Path.GetDirectoryName(file);
        while (dir is not null)
        {
            if (System.IO.Path.GetFileName(dir) == "src") return dir;
            dir = System.IO.Path.GetDirectoryName(dir);
        }
        return null;
    }
}
