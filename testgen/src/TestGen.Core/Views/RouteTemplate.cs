using System.Text;
using System.Text.RegularExpressions;

namespace TestGen.Core.Views;

/// <summary>Vilken sorts värde en ruttparameter tar, ur villkoret (<c>int</c>, <c>guid</c> ...). Allt annat är text.</summary>
internal enum ParameterKind { String, Int, Long, Guid, Bool }

/// <summary>En del av ett ruttsegment: fast text, eller en parameter.</summary>
/// <param name="Text">Den fasta texten. Null för en parameter.</param>
/// <param name="Parameter">Parametern. Null för fast text.</param>
/// <param name="CatchAll">Parametern fångar resten av sökvägen (<c>{*rest}</c>).</param>
internal sealed record RoutePart(string? Text, RouteParameter? Parameter, bool CatchAll = false);

/// <summary>Hur en sökväg passade en ruttmall: antal fasta segment och antal parametersegment.</summary>
internal readonly record struct RouteMatch(int Literals, int Parameters);

/// <summary>
/// Ett segment i en ruttmall (mellan två <c>/</c>): bara fast text, en hel parameter (<c>{id:int}</c>, <c>:id</c>) eller en blandning
/// (<c>{id}.json</c>, <c>v{major}-{minor}</c>).
/// </summary>
internal sealed class RouteSegment
{
    private readonly Lazy<Regex> _shape;

    public RouteSegment(IReadOnlyList<RoutePart> parts)
    {
        Parts = parts;
        _shape = new Lazy<Regex>(() => new Regex(Pattern(captures: true), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)));
    }

    public IReadOnlyList<RoutePart> Parts { get; }

    public bool IsLiteral => Parts.All(p => p.Parameter is null);

    /// <summary>Hela segmentet är en enda parameter, annars null.</summary>
    public RoutePart? Whole => Parts is [{ Parameter: not null } only] ? only : null;

    public bool IsCatchAll => Whole is { CatchAll: true };

    /// <summary>Segmentet får saknas i sökvägen: en valfri parameter eller en som fångar resten.</summary>
    public bool CanBeMissing => Whole is { } w && (w.CatchAll || w.Parameter!.Optional);

    /// <summary>
    /// Passar värdet segmentet? Fast text jämförs utan hänsyn till versaler. Ett värde med <c>{}</c> (dynamiskt: en länk som
    /// <c>/products/{}</c>) passar bara en parameter. Annars måste värdet uppfylla parameterns villkor: <c>create</c> är inget <c>int</c>.
    /// </summary>
    public bool Accepts(string value)
    {
        if (IsLiteral) return string.Equals(string.Concat(Parts.Select(p => p.Text)), value, StringComparison.OrdinalIgnoreCase);
        var dynamic = value.Contains("{}", StringComparison.Ordinal);
        if (Whole is { } whole) return dynamic || RouteTemplate.Satisfies(value, whole.Parameter!);
        if (_shape.Value.Match(value) is not { Success: true } m) return false;
        return dynamic || Parts.Select((p, i) => (p, i)).Where(x => x.p.Parameter is not null)
            .All(x => RouteTemplate.Satisfies(m.Groups[$"p{x.i}"].Value, x.p.Parameter!));
    }

    /// <summary>Segmentet i en normaliserad adress: parametrar blir <c>{}</c>, fast text lämnas som den är.</summary>
    public string Normalize(string value)
    {
        if (Whole is not null) return "{}";
        if (IsLiteral || _shape.Value.Match(value) is not { Success: true }) return value;
        return string.Concat(Parts.Select(p => p.Parameter is null ? p.Text : "{}"));
    }

    /// <summary>Segmentet som regex utan inledande <c>/</c>. Parametrar blir <c>[^/]+</c>, valfria <c>[^/]*</c>.</summary>
    public string ToRegex() => Pattern(captures: false);

    /// <summary>Segmentet med exempelvärden i stället för parametrarna.</summary>
    public string Example() => string.Concat(Parts.Select(p => p.Parameter is null ? p.Text : RouteExamples.Value(p.Parameter)));

    private string Pattern(bool captures)
    {
        var sb = new StringBuilder();
        if (captures) sb.Append('^');
        for (var i = 0; i < Parts.Count; i++)
        {
            var part = Parts[i];
            if (part.Parameter is null) { sb.Append(Regex.Escape(part.Text!)); continue; }
            var count = part.Parameter.Optional && Parts.Count > 1 ? '*' : '+';
            sb.Append(captures ? $"(?<p{i}>.{count})" : $"[^/]{count}");
        }
        if (captures) sb.Append('$');
        return sb.ToString();
    }
}

/// <summary>
/// En tolkad ruttmall. Förstår både ASP.NET-formen (<c>{id:int}</c>, <c>{slug?}</c>, <c>{*rest}</c>) och SPA-formen (<c>:id</c>, <c>:id?</c>),
/// och en parameter kan dela segment med fast text (<c>{id}.json</c>). Den enda platsen som vet hur en rutt är uppbyggd:
/// parametrar, matchning, regex och exempeladresser bygger alla på den.
/// </summary>
internal sealed partial class RouteTemplate
{
    private RouteTemplate(string route, IReadOnlyList<RouteSegment> segments)
    {
        Route = route;
        Segments = segments;
        Parameters = segments.SelectMany(s => s.Parts).Where(p => p.Parameter is not null).Select(p => p.Parameter!).ToList();
    }

    public string Route { get; }
    public IReadOnlyList<RouteSegment> Segments { get; }

    /// <summary>Parametrarna i den ordning de står i mallen.</summary>
    public IReadOnlyList<RouteParameter> Parameters { get; }

    public static RouteTemplate Parse(string route) => new(route, SplitSegments(route).Select(ParseSegment).ToList());

    /// <summary>Sökvägens segment: delat på <c>/</c>, utan tomma.</summary>
    public static string[] Split(string path) => path.Split('/', StringSplitOptions.RemoveEmptyEntries);

    public static ParameterKind KindOf(RouteParameter p) => p.Constraint?.ToLowerInvariant() switch
    {
        "int" => ParameterKind.Int,
        "long" => ParameterKind.Long,
        "guid" => ParameterKind.Guid,
        "bool" => ParameterKind.Bool,
        _ => ParameterKind.String,
    };

    /// <summary>C#-typen för en ruttparameter ur villkoret.</summary>
    public static string ClrType(RouteParameter p) => KindOf(p) switch
    {
        ParameterKind.Int => "int",
        ParameterKind.Long => "long",
        ParameterKind.Guid => "Guid",
        ParameterKind.Bool => "bool",
        _ => "string",
    };

    /// <summary>Uppfyller värdet parameterns villkor? Andra villkor än <c>int</c>, <c>long</c>, <c>guid</c> och <c>bool</c> prövas inte.</summary>
    public static bool Satisfies(string value, RouteParameter p) => KindOf(p) switch
    {
        ParameterKind.Int => int.TryParse(value, out _),
        ParameterKind.Long => long.TryParse(value, out _),
        ParameterKind.Guid => Guid.TryParse(value, out _),
        ParameterKind.Bool => bool.TryParse(value, out _),
        _ => true,
    };

    /// <summary>Hör sökvägen till rutten? Fast text måste stämma och villkoren uppfyllas. Null om den inte passar.</summary>
    public RouteMatch? Match(string path) => Match(Split(path));

    /// <summary>Som <see cref="Match(string)"/>, för en redan delad sökväg. Ett segment <c>{}</c> är ett dynamiskt värde som bara passar en parameter.</summary>
    public RouteMatch? Match(IReadOnlyList<string> path)
    {
        int literals = 0, parameters = 0;
        for (var i = 0; i < Segments.Count; i++)
        {
            var segment = Segments[i];
            if (segment.IsCatchAll) return new RouteMatch(literals, parameters + 1);
            if (i >= path.Count)
            {
                for (var j = i; j < Segments.Count; j++)
                    if (!Segments[j].CanBeMissing) return null;
                return new RouteMatch(literals, parameters + Segments.Count - i);
            }
            if (!segment.Accepts(path[i])) return null;
            if (segment.IsLiteral) literals++;
            else parameters++;
        }
        return path.Count == Segments.Count ? new RouteMatch(literals, parameters) : null;
    }

    /// <summary>Sökvägen med de dynamiska segmenten som <c>{}</c>: <c>/products/42</c> blir <c>/products/{}</c>. Förutsätter att sökvägen passar rutten.</summary>
    public string Normalize(IReadOnlyList<string> path)
    {
        var result = new List<string>();
        for (var i = 0; i < path.Count && i < Segments.Count; i++)
        {
            if (Segments[i].IsCatchAll) { result.Add("{}"); break; }
            result.Add(Segments[i].Normalize(path[i]));
        }
        return "/" + string.Join('/', result);
    }

    /// <summary>
    /// Regexen för en sökväg som hör till rutten, mellan <paramref name="prefix"/> och <paramref name="suffix"/> (regexkod som skrivs som den är).
    /// Fasta segment skrivs ut, parametrar blir <c>[^/]+</c>, och valfria parametrar och de som fångar resten blir valfria, t.ex. <c>(?:/[^/]+)?</c>.
    /// Rotrutten ger bara prefix och suffix.
    /// </summary>
    public string ToRegex(string prefix, string suffix)
    {
        var sb = new StringBuilder(prefix);
        foreach (var segment in Segments)
        {
            if (segment.IsCatchAll) sb.Append("(?:/.*)?");
            else if (segment.CanBeMissing) sb.Append("(?:/").Append(segment.ToRegex()).Append(")?");
            else sb.Append('/').Append(segment.ToRegex());
        }
        return sb.Append(suffix).ToString();
    }

    /// <summary>
    /// Adressen med exempelvärden för alla parametrar, de valfria med. Börjar alltid med <c>/</c>.
    /// </summary>
    public string ExamplePath() => "/" + string.Join('/', Segments.Select(s => s.Example()));

    // --- Tolkning ---

    /// <summary>Delar mallen på <c>/</c> utanför klammrar, så att ett villkor som <c>{x:regex(a/b)}</c> hålls ihop.</summary>
    private static IEnumerable<string> SplitSegments(string route)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i <= route.Length; i++)
        {
            if (i < route.Length)
            {
                if (route[i] == '{') depth++;
                else if (route[i] == '}' && depth > 0) depth--;
                if (route[i] != '/' || depth > 0) continue;
            }
            if (i > start) yield return route[start..i];
            start = i + 1;
        }
    }

    private static RouteSegment ParseSegment(string text)
    {
        var parts = new List<RoutePart>();
        var literal = new StringBuilder();

        void Flush()
        {
            if (literal.Length > 0) parts.Add(new RoutePart(literal.ToString(), null));
            literal.Clear();
        }

        var i = 0;
        if (SpaParameter().Match(text) is { Success: true } spa)
        {
            parts.Add(new RoutePart(null, new RouteParameter(spa.Groups["name"].Value, null, spa.Groups["opt"].Success)));
            i = spa.Length;
        }
        while (i < text.Length)
        {
            var close = text[i] == '{' ? MatchingBrace(text, i) : -1;
            if (close > i + 1)
            {
                Flush();
                parts.Add(ParseBraces(text[(i + 1)..close]));
                i = close + 1;
            }
            else literal.Append(text[i++]);
        }
        Flush();
        return new RouteSegment(parts);
    }

    private static int MatchingBrace(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}' && --depth == 0) return i;
        }
        return -1;
    }

    /// <summary>Innehållet i klammrarna: <c>id:int</c>, <c>slug?</c>, <c>*rest</c>, <c>id=5</c>.</summary>
    private static RoutePart ParseBraces(string body)
    {
        var catchAll = body.StartsWith('*');
        body = body.TrimStart('*');
        var optional = body.EndsWith('?');
        body = body.TrimEnd('?');
        var colon = body.IndexOf(':');
        var name = colon < 0 ? body : body[..colon];
        var constraint = colon < 0 ? null : body[(colon + 1)..].Split(':')[0].Split('(')[0];
        var eq = name.IndexOf('=');
        if (eq >= 0) name = name[..eq];
        return new RoutePart(null, new RouteParameter(name, string.IsNullOrEmpty(constraint) ? null : constraint, optional), catchAll);
    }

    [GeneratedRegex(@"^:(?<name>[A-Za-z_][A-Za-z0-9_]*)(?<opt>\?)?")]
    private static partial Regex SpaParameter();
}
