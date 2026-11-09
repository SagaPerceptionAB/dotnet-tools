using TestGen.Core.Views;
using TestGen.Views.Discovery.Spa;

namespace TestGen.Views.Discovery;

/// <summary>
/// Hittar vyerna i en SPA genom att läsa routerkonfigurationen ur källfilerna: React Router (JSX och objektform), Angular
/// (<c>Routes</c> med <c>loadChildren</c>) och Vue Router. Läser filer med en liten tokeniserare, kör aldrig koden och kräver inget Node.
/// </summary>
internal sealed class SpaDiscoverer : IViewDiscoverer
{
    private const int MaxDepth = 12;

    public IEnumerable<View> Discover(DiscoveryContext context)
    {
        var files = new SpaFiles();
        var analyzer = new ComponentAnalyzer(files, context);
        var routeFiles = new Dictionary<string, List<RouteDecl>>(StringComparer.Ordinal);

        List<RouteDecl> RoutesIn(string path)
        {
            if (routeFiles.TryGetValue(path, out var cached)) return cached;
            var source = files.Get(path);
            return routeFiles[path] = source is null ? [] : SpaRoutes.Read(source);
        }

        // Filerna som har rutter, och de som en annan fil laddar med loadChildren (de är inte egna rötter).
        var candidates = context.Files(".ts", ".tsx", ".js", ".jsx", ".vue").Where(f => RoutesIn(f).Count > 0).ToList();
        var loaded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in candidates)
            foreach (var decl in Flatten(RoutesIn(file)))
                if (decl.LoadChildren is { } spec)
                    foreach (var target in RouteFilesFor(spec, file, files, RoutesIn))
                        loaded.Add(target);

        var views = new List<View>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in candidates.Where(f => !loaded.Contains(f)))
            foreach (var decl in RoutesIn(file))
                Emit(decl, "", false, 0, new Emitter(context, files, analyzer, RoutesIn, views, seen));
        return views;
    }

    private sealed record Emitter(
        DiscoveryContext Context, SpaFiles Files, ComponentAnalyzer Analyzer,
        Func<string, List<RouteDecl>> RoutesIn, List<View> Views, HashSet<string> Seen);

    private static void Emit(RouteDecl decl, string prefix, bool auth, int depth, Emitter e)
    {
        if (depth > MaxDepth) return;
        var source = e.Context.Relative(decl.Source.Path);
        if (decl.Path is null)
        {
            e.Context.Warnings.Add($"Rutten i {source} går inte att läsa (path: {decl.PathText}). Rutten och dess barn hoppas över.");
            return;
        }
        if (IsCatchAll(decl.Path)) return;

        var full = Join(prefix, decl.Path);
        auth |= decl.Auth;

        if (decl.LoadChildren is { } spec)
            foreach (var target in RouteFilesFor(spec, decl.Source.Path, e.Files, e.RoutesIn))
                foreach (var child in e.RoutesIn(target))
                    Emit(child, full, auth, depth + 1, e);
        foreach (var child in decl.Children)
            Emit(child, full, auth, depth + 1, e);

        // En rutt med barn är en layout; vyerna är barnen.
        if (decl.Redirect || decl.Component is null || decl.Children.Count > 0 || decl.LoadChildren is not null) return;
        var route = Normalize(full);
        if (!e.Seen.Add(route)) return;

        var componentPath = ComponentFile(decl, e.Files);
        var component = componentPath is null ? null : e.Files.Get(componentPath);
        if (component is null && decl.Component is { Name: { } name })
            e.Context.Warnings.Add($"Komponenten {name} för rutten {route} i {source} hittades inte via någon import. Vyn får ingen markup.");
        var analysis = component is null ? null : e.Analyzer.Analyze(component);
        e.Views.Add(new View(
            ViewId.Of(route), route, ViewKind.Spa,
            component is null ? source : e.Context.Relative(component.Path),
            analysis?.Title, analysis?.Heading, Names.Parameters(route),
            analysis?.Forms ?? [], analysis?.Links ?? [], analysis?.ApiCalls ?? [], auth));
    }

    // Komponentens fil via importen av namnet eller via import('./x') i själva rutten.
    private static string? ComponentFile(RouteDecl decl, SpaFiles files)
    {
        var c = decl.Component!;
        var spec = c.Spec ?? (c.Name is not null && decl.Source.Imports.TryGetValue(c.Name, out var s) ? s : null);
        return spec is null ? null : files.Resolve(spec, decl.Source.Path);
    }

    // Filen en loadChildren-import pekar på. Saknar den rutter (en Angular-modul) letar vi i dess importer (routing-modulen).
    private static IEnumerable<string> RouteFilesFor(string spec, string fromFile, SpaFiles files, Func<string, List<RouteDecl>> routesIn)
    {
        if (files.Resolve(spec, fromFile) is not { } target) yield break;
        if (routesIn(target).Count > 0) { yield return target; yield break; }
        var source = files.Get(target);
        if (source is null) yield break;
        foreach (var import in source.ImportSpecs)
            if (files.Resolve(import, target) is { } inner && inner != target && routesIn(inner).Count > 0) yield return inner;
    }

    private static IEnumerable<RouteDecl> Flatten(IEnumerable<RouteDecl> decls)
    {
        foreach (var d in decls)
        {
            yield return d;
            foreach (var c in Flatten(d.Children)) yield return c;
        }
    }

    // *, **, /:pathMatch(.*)*, catchAll: sidor för "hittades inte" och inga egna vyer.
    private static bool IsCatchAll(string path) =>
        path.Trim('/') is "*" or "**" || path.Contains("(.*)", StringComparison.Ordinal) || path.Contains("catchAll", StringComparison.OrdinalIgnoreCase);

    private static string Join(string prefix, string path)
    {
        if (path.StartsWith('/')) return path;
        if (path.Length == 0) return prefix;
        return prefix.TrimEnd('/') + "/" + path;
    }

    private static string Normalize(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return "/" + string.Join('/', parts);
    }
}
