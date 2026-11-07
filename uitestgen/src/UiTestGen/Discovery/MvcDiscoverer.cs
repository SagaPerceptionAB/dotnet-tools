using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text.RegularExpressions;
using UiTestGen.Discovery.Mvc;
using UiTestGen.Discovery.Razor;
using UiTestGen.Model;

namespace UiTestGen.Discovery;

/// <summary>
/// Hittar MVC-vyer: GET-actions i controllers som returnerar <c>View(...)</c>. Rutten kommer från attributen <c>[Route]</c>/<c>[HttpGet]</c>,
/// annars från konventionen <c>{controller=Home}/{action=Index}/{id?}</c>. Vyfilen (<c>Views/Controller/Action.cshtml</c>) ger titel, rubrik,
/// länkar och formulär, och länkarna i layouten (<c>_Layout.cshtml</c>) läggs till på vyn. API-controllers (<c>[ApiController]</c>, bara <c>ControllerBase</c>) och POST-actions hoppas över.
/// </summary>
internal sealed partial class MvcDiscoverer : IViewDiscoverer
{
    public IEnumerable<View> Discover(DiscoveryContext context)
    {
        var controllers = ReadControllers(context);
        if (controllers.Count == 0) return [];
        WarnAboutCustomConventionalRoute(context);

        var files = new ViewFileIndex(context);
        var layouts = CshtmlLayouts.For(context);
        var index = TypeIndex.For(context);

        // Första ruttens adress per (controller, action), så att länkar via tag helpers hamnar på rätt adress.
        var routes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var planned = new List<(ControllerInfo Controller, ActionInfo Action, string Route)>();
        foreach (var controller in controllers)
            foreach (var action in controller.Actions)
                foreach (var route in Routes(context, controller, action))
                {
                    planned.Add((controller, action, route));
                    routes.TryAdd(Key(controller.Name, action.Name), route);
                }

        var views = new List<View>();
        foreach (var (controller, action, route) in planned)
            views.Add(Build(context, index, files, layouts, routes, controller, action, route));
        return views;
    }

    private static string Key(string controller, string action) => controller + "/" + action;

    private static List<ControllerInfo> ReadControllers(DiscoveryContext context)
    {
        var cache = SourceCache.For(context);
        var result = new List<ControllerInfo>();
        foreach (var file in context.Files(".cs"))
        {
            if (cache.CSharp(file) is not { } source || !source.Text.Contains("Controller", StringComparison.Ordinal)) continue;
            result.AddRange(ControllerReader.Read(source.Tree, context.Relative(file)));
        }
        return result;
    }

    // Attributrutter om klassen eller actionen har dem, annars konventionsrutten.
    private static IEnumerable<string> Routes(DiscoveryContext context, ControllerInfo controller, ActionInfo action)
    {
        if (controller.RouteUnreadable || action.RouteUnreadable)
        {
            context.Warnings.Add($"Rutten för {controller.Name}.{action.Name} i {controller.File} går inte att läsa (inte en fast sträng). Vyn hoppas över.");
            return [];
        }
        var prefixes = controller.Templates.Count == 0 ? [null] : controller.Templates.Cast<string?>().ToList();
        var bare = action.Templates.Count == 0 || action.Templates.All(t => t is null);
        if (bare && controller.Templates.Count == 0)
            return [MvcRoutes.Conventional(controller.Area, controller.Name, action.Name, action.HasId ? "{id?}" : null)];

        var templates = bare ? [null] : action.Templates.Where(t => t is not null).Distinct().ToList();
        return prefixes.SelectMany(p => templates.Select(t => MvcRoutes.Combine(p, t, controller.Name, action.Name, controller.Area))).Distinct(StringComparer.Ordinal).ToList();
    }

    private View Build(DiscoveryContext context, TypeIndex index, ViewFileIndex files, CshtmlLayouts layouts, Dictionary<string, string> routes,
        ControllerInfo controller, ActionInfo action, string route)
    {
        var requiresAuth = action.Auth ?? controller.RequiresAuth;
        var found = files.Find(context, controller, action);
        if (found is null || SourceCache.For(context).Razor(found) is not { } source)
        {
            if (found is null)
                context.Warnings.Add($"Hittade ingen vyfil för {controller.Name}.{action.Name} i {controller.File}. Vyn får inga formulär.");
            return new View(Names.IdFromRoute(route), route, ViewKind.Mvc, controller.File, null, null, Names.Parameters(route), [], [], [], requiresAuth);
        }

        var relative = context.Relative(found);
        var doc = source.Doc;
        var forms = FormReader.Read(context, index, doc, source.ModelType(), relative);
        // Vyns egna länkar och partials den använder först, därefter länkarna i layouten. Dubbletter tas bort av ViewDiscovery.
        var links = layouts.Links.Read(found, source, tag => AspLink(tag, routes, controller, action));
        var title = source.ViewDataTitle() ?? ViewBagTitle(source.Text) ?? ViewParts.FirstFixedText(doc, "title");
        return new View(Names.IdFromRoute(route), route, ViewKind.Mvc, relative, title, ViewParts.FirstFixedText(doc, "h1"),
            Names.Parameters(route), forms, links, [], requiresAuth);
    }

    // <a asp-controller asp-action asp-route-id> översatt till samma ruttformat som href. Null för länkar utan asp-controller och asp-action.
    private static string? AspLink(Tag tag, Dictionary<string, string> routes, ControllerInfo current, ActionInfo currentAction)
    {
        if (!tag.Has("asp-controller") && !tag.Has("asp-action")) return null;
        var controller = tag["asp-controller"] ?? current.Name;
        var action = tag["asp-action"] ?? currentAction.Name;
        if (controller.Contains('@') || action.Contains('@')) return null;
        var values = tag.Attrs.Where(a => a.Name.StartsWith("asp-route-", StringComparison.OrdinalIgnoreCase))
            .Select(a => a.Name["asp-route-".Length..].ToLowerInvariant()).ToHashSet();
        var area = tag.Has("asp-area") ? tag["asp-area"] : current.Area;
        return MvcRoutes.ForLink(routes.GetValueOrDefault(Key(controller, action)), area is { Length: > 0 } a && !a.Contains('@') ? a : null,
            controller, action, values);
    }

    private static string? ViewBagTitle(string text)
    {
        var m = ViewBagTitleSet().Match(text);
        return m.Success ? m.Groups[1].Value.Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal) : null;
    }

    // Program.cs/Startup.cs: MapControllerRoute med ett annat mönster än standardens betyder att konventionsrutterna kan vara fel.
    private static void WarnAboutCustomConventionalRoute(DiscoveryContext context)
    {
        var cache = SourceCache.For(context);
        foreach (var file in context.Files(".cs"))
        {
            if (cache.CSharp(file) is not { } source || !source.Text.Contains("MapControllerRoute", StringComparison.Ordinal)) continue;

            foreach (var call in source.Tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var name = call.Expression switch { MemberAccessExpressionSyntax m => m.Name.Identifier.Text, IdentifierNameSyntax i => i.Identifier.Text, _ => null };
                if (name != "MapControllerRoute") continue;
                var args = call.ArgumentList.Arguments;
                var pattern = args.FirstOrDefault(a => a.NameColon?.Name.Identifier.Text == "pattern")
                    ?? args.Where(a => a.NameColon is null).Skip(1).FirstOrDefault();
                var text = pattern?.Expression is LiteralExpressionSyntax { Token.Value: string s } ? s : null;
                if (text is not null && MvcRoutes.Squash(text) == MvcRoutes.Squash(MvcRoutes.DefaultPattern)) continue;
                context.Warnings.Add(text is null
                    ? $"{context.Relative(file)} mappar en konventionell rutt vars mönster inte går att läsa. Rutterna för controllers utan attributrutt antas följa {MvcRoutes.DefaultPattern}."
                    : $"{context.Relative(file)} mappar den konventionella rutten \"{text}\". Rutterna för controllers utan attributrutt antas följa {MvcRoutes.DefaultPattern} och kan därför vara fel.");
            }
        }
    }

    [GeneratedRegex(@"ViewBag\s*\.\s*Title\s*=\s*""((?:[^""\\]|\\.)*)""\s*;")]
    private static partial Regex ViewBagTitleSet();

    /// <summary>Alla .cshtml-filer, så att vyer hittas utan hänsyn till stora och små bokstäver i sökvägen.</summary>
    private sealed class ViewFileIndex
    {
        private readonly Dictionary<string, string> _byPath = new(StringComparer.OrdinalIgnoreCase);

        public ViewFileIndex(DiscoveryContext context)
        {
            foreach (var file in context.Files(".cshtml")) _byPath.TryAdd(file, file);
        }

        // Namnet som i ASP.NET: en sökväg med ~/ eller .cshtml används som den är, annars letar vi i vyernas mappar.
        public string? Find(DiscoveryContext context, ControllerInfo controller, ActionInfo action)
        {
            var project = context.ProjectOf(Path.GetFullPath(Path.Combine(context.Root, controller.File)));
            var name = action.ViewName ?? action.Name;
            IEnumerable<string> candidates;
            if (name.StartsWith("~/", StringComparison.Ordinal) || name.StartsWith('/') || name.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase))
                candidates = [name.TrimStart('~').TrimStart('/')];
            else
            {
                var areaViews = controller.Area is null ? [] : new[] { $"Areas/{controller.Area}/Views/{controller.Name}/{name}.cshtml", $"Areas/{controller.Area}/Views/Shared/{name}.cshtml" };
                candidates = [.. areaViews, $"Views/{controller.Name}/{name}.cshtml", $"Views/Shared/{name}.cshtml"];
            }
            foreach (var candidate in candidates)
            {
                var path = Path.GetFullPath(Path.Combine(project, candidate.Replace('/', Path.DirectorySeparatorChar)));
                if (_byPath.TryGetValue(path, out var actual)) return actual;
            }
            return null;
        }
    }
}
