namespace TestGen.Views.Discovery.Spa;

/// <summary>Ett värde i en objektlitteral. <see cref="Start"/> och <see cref="End"/> är tokenindex (End är exklusivt).</summary>
internal abstract class JsNode(int start, int end)
{
    public int Start { get; } = start;
    public int End { get; } = end;
}

/// <summary>En sträng eller template literal som är hela värdet. <c>${}</c> är redan <c>{}</c> i <see cref="Value"/>.</summary>
internal sealed class JsStr(string value, bool holes, int start, int end) : JsNode(start, end)
{
    public string Value { get; } = value;
    public bool Holes { get; } = holes;
}

/// <summary>Allt annat (identifierare, funktioner, JSX, tal): läses via tokenintervallet.</summary>
internal sealed class JsRaw(int start, int end) : JsNode(start, end);

internal sealed class JsObject(List<(string Key, JsNode Value)> props, int start, int end) : JsNode(start, end)
{
    public IReadOnlyList<(string Key, JsNode Value)> Props { get; } = props;

    public JsNode? Get(string key) => Props.FirstOrDefault(p => p.Key == key).Value;

    public bool Has(string key) => Props.Any(p => p.Key == key);
}

internal sealed class JsArray(List<JsNode> items, int start, int end) : JsNode(start, end)
{
    public IReadOnlyList<JsNode> Items { get; } = items;
}

/// <summary>
/// Läser objektlitteraler och arrayer ur en tokenlista. Förstår inte uttryck: värden som inte är strängar, objekt eller arrayer
/// blir <see cref="JsRaw"/> med sitt tokenintervall. Kraschar aldrig på trasig kod.
/// Varje array tolkas högst en gång: resultatet minns vilket tokenindex <c>[</c> stod på, så att den som söker arrayer token för token
/// (<see cref="SpaRoutes"/>) inte tolkar om de nästlade arrayerna från varje <c>[</c>.
/// </summary>
internal sealed class JsParser(IReadOnlyList<Tok> tokens)
{
    private readonly Dictionary<int, JsArray> _arrays = [];

    public IReadOnlyList<Tok> Tokens { get; } = tokens;

    public JsNode ParseValue(int i, int limit)
    {
        if (i >= limit) return new JsRaw(i, i);
        var t = Tokens[i];
        if (t.IsPunct('{')) return ParseObject(i, limit);
        if (t.IsPunct('[')) return ParseArray(i, limit);
        if (t.IsText)
        {
            var end = i + 1;
            if (end >= limit || Tokens[end].Kind == TokKind.Punct && Tokens[end].Text is "," or "}" or "]" or ")")
                return new JsStr(t.Text, t.Holes, i, end);
        }
        return new JsRaw(i, Math.Max(SkipValue(i, limit), i + 1));
    }

    public JsObject ParseObject(int open, int limit)
    {
        var close = CloseOf(open, limit);
        var props = new List<(string, JsNode)>();
        var j = open + 1;
        while (j < close)
        {
            var t = Tokens[j];
            if (t.IsPunct(',')) { j++; continue; }
            if (t.Kind is TokKind.Ident or TokKind.String or TokKind.Number && j + 1 <= close)
            {
                var next = j + 1;
                if (next < close && Tokens[next].IsPunct(':'))
                {
                    var value = ParseValue(next + 1, close);
                    props.Add((t.Text, value));
                    j = Math.Max(value.End, next + 1);
                    continue;
                }
                if (next >= close || Tokens[next].IsPunct(','))
                {
                    props.Add((t.Text, new JsRaw(j, j + 1)));
                    j = next;
                    continue;
                }
            }
            var skipped = SkipValue(j, close);
            j = Math.Max(skipped, j + 1);
        }
        return new JsObject(props, open, close + 1);
    }

    public JsArray ParseArray(int open, int limit)
    {
        if (_arrays.TryGetValue(open, out var known)) return known;
        var close = CloseOf(open, limit);
        var items = new List<JsNode>();
        var j = open + 1;
        while (j < close)
        {
            if (Tokens[j].IsPunct(',')) { j++; continue; }
            var value = ParseValue(j, close);
            items.Add(value);
            j = Math.Max(value.End, j + 1);
        }
        return _arrays[open] = new JsArray(items, open, close + 1);
    }

    // Slutet på ett rått värde: nästa komma eller avslutande parentes på samma nivå.
    private int SkipValue(int i, int limit)
    {
        var j = i;
        while (j < limit)
        {
            var t = Tokens[j];
            if (t.Kind == TokKind.Punct)
            {
                if (t.Text is "(" or "[" or "{")
                {
                    var m = JsTokenizer.Matching(Tokens, j);
                    if (m < 0 || m >= limit) return limit;
                    j = m + 1;
                    continue;
                }
                if (t.Text is "," or ")" or "]" or "}") return j;
            }
            j++;
        }
        return limit;
    }

    private int CloseOf(int open, int limit)
    {
        var m = JsTokenizer.Matching(Tokens, open);
        return m < 0 || m >= limit ? Math.Max(limit - 1, open) : m;
    }
}
