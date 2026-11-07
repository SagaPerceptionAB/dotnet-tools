using System.Text.RegularExpressions;

namespace UiTestGen.Discovery.Spa;

/// <summary>Vilken komponent en rutt visar: ett namn (som importeras i filen) eller en importväg (lazy).</summary>
internal sealed record ComponentRef(string? Name, string? Spec);

/// <summary>En rutt som den står i routerkonfigurationen, före hopslagning med förälderns sökväg.</summary>
internal sealed class RouteDecl(SpaSource source)
{
    public SpaSource Source { get; } = source;

    /// <summary>Sökvägen som den står. Tom för index-rutter och rutter utan path. Null om den inte är en fast text.</summary>
    public string? Path { get; set; } = "";

    /// <summary>Uttrycket när <see cref="Path"/> inte gick att läsa, för varningen.</summary>
    public string? PathText { get; set; }

    public ComponentRef? Component { get; set; }

    public bool Redirect { get; set; }

    public bool Auth { get; set; }

    public string? LoadChildren { get; set; }

    public List<RouteDecl> Children { get; } = [];
}

/// <summary>Läser rutterna ur en fil: objektform (<c>{ path, component, children }</c>) och JSX (<c>&lt;Route&gt;</c>).</summary>
internal static partial class SpaRoutes
{
    private static readonly string[] ComponentKeys = ["component", "Component", "element", "lazy", "loadComponent"];
    private static readonly string[] AuthGuardKeys = ["canActivate", "canActivateChild", "canMatch", "canLoad"];
    private static readonly string[] AuthMetaKeys = ["requiresAuth", "requireAuth", "requiresLogin", "authRequired", "auth"];

    /// <summary>Rutterna på översta nivån i filen. Tomt om filen inte har några.</summary>
    public static List<RouteDecl> Read(SpaSource file)
    {
        var roots = new List<RouteDecl>();
        if (!file.Text.Contains("path", StringComparison.Ordinal) && !file.Text.Contains("<Route", StringComparison.Ordinal)) return roots;
        var tokens = file.Tokens;
        var parser = new JsParser(tokens);
        var i = 0;
        while (i < tokens.Count)
        {
            if (tokens[i].IsPunct('['))
            {
                var array = parser.ParseArray(i, tokens.Count);
                if (LooksLikeRoutes(array))
                {
                    roots.AddRange(array.Items.OfType<JsObject>().Select(o => FromObject(o, file, parser)).OfType<RouteDecl>());
                    i = Math.Max(array.End, i + 1);
                    continue;
                }
            }
            i++;
        }
        if (file.Text.Contains("<Route", StringComparison.Ordinal))
            roots.AddRange(ReadJsx(tokens, 0, nested: false, file, out _));
        return roots;
    }

    // En array av objekt där minst ett har path eller index och minst ett har något som en rutt har.
    private static bool LooksLikeRoutes(JsArray array) =>
        array.Items.Count > 0 && array.Items.All(i => i is JsObject)
        && array.Items.Cast<JsObject>().Any(o => o.Has("path") || o.Has("index"))
        && array.Items.Cast<JsObject>().Any(o => ComponentKeys.Any(o.Has) || o.Has("children") || o.Has("loadChildren") || o.Has("redirect") || o.Has("redirectTo"));

    private static RouteDecl? FromObject(JsObject o, SpaSource file, JsParser parser)
    {
        if (!o.Has("path") && !o.Has("index") && !o.Has("children") && !ComponentKeys.Any(o.Has) && !o.Has("loadChildren")) return null;
        var decl = new RouteDecl(file);
        switch (o.Get("path"))
        {
            case JsStr s: decl.Path = s.Value; break;
            case JsNode n: decl.Path = null; decl.PathText = Describe(parser.Tokens, n); break;
        }
        foreach (var key in ComponentKeys)
            if (o.Get(key) is { } c && ComponentFrom(parser.Tokens, c.Start, c.End, decl)) break;
        if (o.Has("redirect") || o.Has("redirectTo")) decl.Redirect = decl.Component is null;
        if (AuthGuardKeys.Any(o.Has)) decl.Auth = true;
        if (o.Get("meta") is JsObject meta && AuthMetaKeys.Any(k => meta.Get(k) is { } v && IsTrue(parser.Tokens, v))) decl.Auth = true;
        if (o.Get("loadChildren") is { } lc) decl.LoadChildren = ImportSpec(parser.Tokens, lc.Start, lc.End);
        if (o.Get("children") is JsArray kids)
            decl.Children.AddRange(kids.Items.OfType<JsObject>().Select(k => FromObject(k, file, parser)).OfType<RouteDecl>());
        return decl;
    }

    // ---------- JSX ----------

    // Läser <Route>-element från läget i. Med nested läser den till slutaggen </Route> och returnerar läget efter den.
    private static List<RouteDecl> ReadJsx(IReadOnlyList<Tok> t, int i, bool nested, SpaSource file, out int next)
    {
        var result = new List<RouteDecl>();
        while (i < t.Count)
        {
            if (t[i].IsPunct('<') && i + 2 < t.Count && t[i + 1].IsPunct('/') && t[i + 2].IsIdent("Route"))
            {
                if (!nested) { i++; continue; }
                i += 3;
                while (i < t.Count && !t[i].IsPunct('>')) i++;
                next = i + 1;
                return result;
            }
            if (t[i].IsPunct('<') && i + 1 < t.Count && t[i + 1].IsIdent("Route"))
            {
                var decl = ReadRouteTag(t, i + 2, file, out var j, out var selfClosing);
                if (!selfClosing) decl.Children.AddRange(ReadJsx(t, j, nested: true, file, out j));
                result.Add(decl);
                i = j;
                continue;
            }
            i++;
        }
        next = i;
        return result;
    }

    private static RouteDecl ReadRouteTag(IReadOnlyList<Tok> t, int j, SpaSource file, out int next, out bool selfClosing)
    {
        var decl = new RouteDecl(file) { Path = "" };
        selfClosing = false;
        while (j < t.Count)
        {
            var tok = t[j];
            if (tok.IsPunct('/') && j + 1 < t.Count && t[j + 1].IsPunct('>')) { selfClosing = true; j += 2; break; }
            if (tok.IsPunct('>')) { j++; break; }
            if (tok.IsPunct('{')) { var m = JsTokenizer.Matching(t, j); j = m < 0 ? j + 1 : m + 1; continue; }
            if (tok.Kind != TokKind.Ident) { j++; continue; }
            var name = tok.Text;
            if (j + 2 >= t.Count || !t[j + 1].IsPunct('=')) { j++; continue; }
            // Värdet: "text" eller {uttryck}
            var v = t[j + 2];
            int valueStart, valueEnd;
            if (v.IsText) { valueStart = j + 2; valueEnd = j + 3; j += 3; }
            else if (v.IsPunct('{'))
            {
                var m = JsTokenizer.Matching(t, j + 2);
                if (m < 0) { j++; continue; }
                valueStart = j + 3; valueEnd = m; j = m + 1;
            }
            else { j += 2; continue; }
            switch (name)
            {
                case "path":
                    if (valueEnd - valueStart == 1 && t[valueStart].IsText && !t[valueStart].Holes) decl.Path = t[valueStart].Text;
                    else { decl.Path = null; decl.PathText = Describe(t, valueStart, valueEnd); }
                    break;
                case "element" or "Component" or "component":
                    ComponentFrom(t, valueStart, valueEnd, decl);
                    break;
            }
        }
        // index (utan värde) syns bara som attribut; en index-rutt har tom sökväg, vilket är standard här.
        next = j;
        return decl;
    }

    // ---------- Gemensamt ----------

    private static readonly Regex AuthWrapper = AuthWrapperRegex();

    // Hittar komponenten i ett tokenintervall: import('./x'), <Komponent/> (utan inloggningsomslag) eller ett namn.
    private static bool ComponentFrom(IReadOnlyList<Tok> t, int start, int end, RouteDecl decl)
    {
        end = Math.Min(end, t.Count);
        if (ImportSpec(t, start, end) is { } spec) { decl.Component = new ComponentRef(null, spec); return true; }
        for (var k = start; k + 1 < end; k++)
        {
            if (!t[k].IsPunct('<') || t[k + 1].Kind != TokKind.Ident) continue;
            var name = t[k + 1].Text;
            if (name == "Navigate") { decl.Redirect = true; return true; }
            if (!char.IsUpper(name[0])) continue;
            if (AuthWrapper.IsMatch(name)) { decl.Auth = true; continue; }
            decl.Component = new ComponentRef(name, null);
            return true;
        }
        if (end - start == 1 && t[start].Kind == TokKind.Ident && t[start].Text is not ("null" or "undefined"))
        {
            decl.Component = new ComponentRef(t[start].Text, null);
            return true;
        }
        return false;
    }

    /// <summary>Vägen i <c>import('./x')</c> inom intervallet, eller null.</summary>
    public static string? ImportSpec(IReadOnlyList<Tok> t, int start, int end)
    {
        end = Math.Min(end, t.Count);
        for (var k = start; k + 2 < end; k++)
            if (t[k].IsIdent("import") && t[k + 1].IsPunct('(') && t[k + 2].IsText) return t[k + 2].Text;
        return null;
    }

    private static bool IsTrue(IReadOnlyList<Tok> t, JsNode n) => n.End - n.Start == 1 && t[n.Start].IsIdent("true");

    private static string Describe(IReadOnlyList<Tok> t, JsNode n) => Describe(t, n.Start, n.End);

    private static string Describe(IReadOnlyList<Tok> t, int start, int end)
    {
        var text = string.Concat(t.Skip(start).Take(Math.Max(0, Math.Min(end, t.Count) - start)).Take(8).Select(x => x.Kind == TokKind.String ? $"'{x.Text}'" : x.Text));
        return text.Length == 0 ? "(tomt)" : text;
    }

    [GeneratedRegex("Require|Protect|Private|Auth|Guard")]
    private static partial Regex AuthWrapperRegex();
}
