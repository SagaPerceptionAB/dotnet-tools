using System.Text;
using Bogus;

namespace TestGen.Core;

/// <summary>Genererar en sträng som matchar ett enkelt regex (klasser, kvantifierare, grupper, alternativ).</summary>
public sealed class PatternGenerator
{
    private readonly string _p;
    private int _i;
    private readonly Randomizer _r;

    private PatternGenerator(string pattern, Randomizer r) { _p = pattern; _r = r; }

    public static string Generate(string pattern, Randomizer r)
    {
        var g = new PatternGenerator(pattern.TrimStart('^').TrimEnd('$'), r);
        var sb = new StringBuilder();
        g.Alternation(sb);
        return sb.ToString();
    }

    private void Alternation(StringBuilder sb)
    {
        // Välj ett slumpmässigt alternativ på nivå 0 och hoppa sedan till slutet av hela alternationen.
        var starts = new List<int> { _i };
        var end = FindEnd(_i, starts);
        _i = starts[_r.Number(0, starts.Count - 1)];
        Sequence(sb);
        _i = end;
    }

    /// <summary>Index för avslutande ')' (eller slutet) för nivån som börjar vid <paramref name="from"/>; samlar '|'-alternativens startpositioner.</summary>
    private int FindEnd(int from, List<int>? alternatives = null)
    {
        var depth = 0;
        var j = from;
        for (; j < _p.Length; j++)
        {
            var c = _p[j];
            if (c == '\\') { j++; continue; }
            if (c == '[') { while (j < _p.Length && _p[j] != ']') { if (_p[j] == '\\') j++; j++; } continue; }
            if (c == '(') depth++;
            else if (c == ')') { if (depth == 0) break; depth--; }
            else if (c == '|' && depth == 0) alternatives?.Add(j + 1);
        }
        return j;
    }

    private void Sequence(StringBuilder sb)
    {
        while (_i < _p.Length && _p[_i] != '|' && _p[_i] != ')')
        {
            var atom = Atom();
            var (min, max) = Quantifier();
            var n = min == max ? min : _r.Number(min, max);
            for (var k = 0; k < n; k++) sb.Append(atom());
        }
    }

    private Func<string> Atom()
    {
        var c = _p[_i];
        if (c == '(')
        {
            _i++;
            if (_p.AsSpan(_i).StartsWith("?:")) _i += 2;
            var start = _i;
            var j = FindEnd(_i);
            var src = _p[start..j];
            _i = j + 1;
            return () => Generate(src, _r);
        }
        if (c == '[')
        {
            _i++;
            var neg = _p[_i] == '^';
            if (neg) _i++;
            var chars = new List<char>();
            while (_p[_i] != ']')
            {
                char lo;
                if (_p[_i] == '\\') { _i++; var e = Escape(_p[_i++]); if (e.Length > 1) { chars.AddRange(e); continue; } lo = e[0]; }
                else lo = _p[_i++];
                if (_p[_i] == '-' && _p[_i + 1] != ']')
                {
                    _i++;
                    var hi = _p[_i] == '\\' ? Escape(_p[++_i])[0] : _p[_i];
                    _i++;
                    for (var x = lo; x <= hi; x++) chars.Add(x);
                }
                else chars.Add(lo);
            }
            _i++;
            if (neg)
            {
                var excluded = chars.ToHashSet();
                chars = Enumerable.Range('a', 26).Select(x => (char)x).Where(x => !excluded.Contains(x)).ToList();
            }
            var arr = chars.ToArray();
            return () => arr[_r.Number(0, arr.Length - 1)].ToString();
        }
        if (c == '\\')
        {
            _i++;
            var set = Escape(_p[_i++]);
            return () => set[_r.Number(0, set.Length - 1)].ToString();
        }
        if (c == '.')
        {
            _i++;
            return () => ((char)_r.Number('a', 'z')).ToString();
        }
        _i++;
        var lit = c.ToString();
        return () => lit;
    }

    private static string Escape(char e) => e switch
    {
        'd' => "0123456789",
        'w' => "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_",
        's' => " ",
        'n' => "\n",
        't' => "\t",
        _ => e.ToString(),
    };

    private (int, int) Quantifier()
    {
        if (_i >= _p.Length) return (1, 1);
        switch (_p[_i])
        {
            case '?': _i++; return (0, 1);
            case '*': _i++; return (0, 5);
            case '+': _i++; return (1, 5);
            case '{':
                var end = _p.IndexOf('}', _i);
                var parts = _p[(_i + 1)..end].Split(',');
                _i = end + 1;
                var min = int.Parse(parts[0]);
                var max = parts.Length == 1 ? min : parts[1] == "" ? min + 5 : int.Parse(parts[1]);
                return (min, max);
            default: return (1, 1);
        }
    }
}
