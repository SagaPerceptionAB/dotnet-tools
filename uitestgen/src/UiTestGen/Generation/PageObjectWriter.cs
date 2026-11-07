using System.Text;
using UiTestGen.Model;

namespace UiTestGen.Generation;

/// <summary>Skriver Page Object för en vy: rutten, <c>Path(...)</c> och locators för rubrik, fält och knappar.</summary>
internal static class PageObjectWriter
{
    /// <summary>Rollerna som finns i Playwrights <c>AriaRole</c>. Andra roller blir en CSS-väljare.</summary>
    private static readonly HashSet<string> AriaRoles = new(StringComparer.Ordinal)
    {
        "alert", "alertdialog", "application", "article", "banner", "blockquote", "button", "caption", "cell", "checkbox", "code",
        "columnheader", "combobox", "complementary", "contentinfo", "definition", "deletion", "dialog", "directory", "document",
        "emphasis", "feed", "figure", "form", "generic", "grid", "gridcell", "group", "heading", "img", "insertion", "link", "list",
        "listbox", "listitem", "log", "main", "marquee", "math", "meter", "menu", "menubar", "menuitem", "menuitemcheckbox",
        "menuitemradio", "navigation", "none", "note", "option", "paragraph", "presentation", "progressbar", "radio", "radiogroup",
        "region", "row", "rowgroup", "rowheader", "scrollbar", "search", "searchbox", "separator", "slider", "spinbutton", "status",
        "strong", "subscript", "superscript", "switch", "tab", "table", "tablist", "tabpanel", "term", "textbox", "time", "timer",
        "toolbar", "tooltip", "tree", "treegrid", "treeitem",
    };

    public static string ClassName(View view) => CSharp.Identifier(view.Id, "View") + "Page";

    public static string Write(View view, string ns)
    {
        var cls = ClassName(view);
        var w = new CodeWriter();
        w.Line(CSharp.Header);
        w.Line();
        w.Line("using System.Globalization;");
        w.Line("using Microsoft.Playwright;");
        w.Line();
        w.Line($"namespace {ns}.Pages;");
        w.Line();
        w.Line($"// Sidan {CSharp.Comment(view.Route)} (från {CSharp.Comment(view.Source)}).");
        using (w.Block($"public partial class {cls}"))
        {
            w.Line($"public const string Route = {CSharp.Literal(view.Route)};");
            w.Line();
            w.Line("public IPage Page { get; }");
            w.Line();
            w.Line($"public {cls}(IPage page) => Page = page;");
            WritePath(w, view);
            WriteLocators(w, view);
        }
        return w.ToString();
    }

    // --- Path ---

    /// <summary>Parameternamn i C#: camelCase ur ruttens namn, unika och utan nyckelord.</summary>
    public static IReadOnlyList<string> ParameterNames(IReadOnlyList<RouteParameter> parameters)
    {
        var used = new HashSet<string>(StringComparer.Ordinal) { "built" };
        var names = new List<string>();
        for (var i = 0; i < parameters.Count; i++)
        {
            var name = Names.Camel(parameters[i].Name);
            if (name.Length == 0) name = "p" + (i + 1);
            if (char.IsDigit(name[0])) name = "p" + name;
            names.Add(Names.Unique(used, name));
        }
        return names.Select(CSharp.Safe).ToList();
    }

    private static void WritePath(CodeWriter w, View view)
    {
        var ps = view.Parameters;
        var names = ParameterNames(ps);
        var lastRequired = -1;
        for (var i = 0; i < ps.Count; i++) if (!ps[i].Optional) lastRequired = i;
        // Valfria parametrar får bara ett standardvärde om ingen obligatorisk kommer efter dem.
        var signature = string.Join(", ", ps.Select((p, i) =>
            $"{RouteTemplate.ClrType(p)}{(p.Optional ? "?" : "")} {names[i]}{(p.Optional && i > lastRequired ? " = null" : "")}"));
        w.Line();
        using (w.Block($"public static string Path({signature})"))
        {
            w.Line($"var built = {PathExpression(view, names)};");
            w.Line("return built.Length == 0 ? \"/\" : built;");
        }
    }

    /// <summary>Uttryck som bygger adressen: fast text och parametrarna, med valfria parametrar utelämnade när de är null.</summary>
    private static string PathExpression(View view, IReadOnlyList<string> names)
    {
        var template = RouteTemplate.Parse(view.Route);
        if (template.Segments.Count == 0) return CSharp.Literal("/");
        var parts = new List<string>();
        var literal = new StringBuilder();

        void FlushLiteral()
        {
            if (literal.Length > 0) parts.Add(CSharp.Literal(literal.ToString()));
            literal.Clear();
        }

        var i = 0;
        foreach (var segment in template.Segments)
        {
            // En valfri parameter som är ett helt segment tar snedstrecket före sig med sig när den utelämnas.
            var optionalWhole = segment.Whole is { Parameter.Optional: true };
            if (!optionalWhole) literal.Append('/');
            foreach (var part in segment.Parts)
            {
                if (part.Parameter is null) { literal.Append(part.Text); continue; }
                if (i >= view.Parameters.Count) continue;
                var p = view.Parameters[i];
                var name = names[i++];
                var value = Format(p, name, part.CatchAll);
                FlushLiteral();
                if (!p.Optional) parts.Add(value);
                else parts.Add($"({name} is null ? \"\" : {(optionalWhole ? "\"/\" + " : "")}{value})");
            }
        }
        FlushLiteral();
        return parts.Count == 0 ? CSharp.Literal("/") : string.Join(" + ", parts);
    }

    private static string Format(RouteParameter p, string name, bool catchAll)
    {
        var access = p.Optional && RouteTemplate.ClrType(p) != "string" ? name + ".Value" : name;
        return RouteTemplate.ClrType(p) switch
        {
            "int" or "long" => $"{access}.ToString(CultureInfo.InvariantCulture)",
            "Guid" => $"{access}.ToString()",
            "bool" => $"{access}.ToString().ToLowerInvariant()",
            _ => catchAll ? access : $"Uri.EscapeDataString({access})",
        };
    }

    // --- Locators ---

    /// <summary>Namnen på medlemmarna i Page Object för ett formulär: en locator per fält, knappen och metoden som fyller i formuläret.</summary>
    /// <param name="Fields">Locatorns namn, ett per fält i <see cref="Form.Fields"/>.</param>
    /// <param name="Fill">Metoden som fyller i formuläret, t.ex. <c>FillProductAsync</c>.</param>
    /// <param name="Parameters">Metodens parametrar: de fält som fylls i, med parameternamn.</param>
    internal sealed record FormMembers(Form Form, IReadOnlyList<string> Fields, string Submit, string Fill, IReadOnlyList<(int Index, string Name)> Parameters);

    /// <summary>Namnen för vyns formulär. Samma namn används av Page Object och av testerna.</summary>
    internal static IReadOnlyList<FormMembers> Members(View view)
    {
        var used = new HashSet<string>(StringComparer.Ordinal) { "Route", "Path", "Page", ClassName(view) };
        if (view.Heading is not null) used.Add("Heading");
        var several = view.Forms.Count > 1;
        var members = new List<(Form Form, List<string> Fields, string Submit)>();
        foreach (var form in view.Forms)
        {
            var prefix = several ? Names.Pascal(form.Id) : "";
            var fields = new List<string>();
            var fieldNo = 0;
            foreach (var field in form.Fields)
            {
                fieldNo++;
                fields.Add(Unique(used, prefix + Names.Pascal(field.Name), prefix + "Field" + fieldNo));
            }
            members.Add((form, fields, Unique(used, prefix + "Submit", prefix + "Submit")));
        }
        // Metodnamnen sist, så att de aldrig tar ett namn från en locator.
        var result = new List<FormMembers>();
        var formNo = 0;
        foreach (var (form, fields, submit) in members)
        {
            formNo++;
            var fill = Unique(used, "Fill" + Names.Pascal(form.Id) + "Async", "Fill" + formNo + "Async");
            var parameterNames = new HashSet<string>(StringComparer.Ordinal);
            var parameters = new List<(int, string)>();
            for (var i = 0; i < form.Fields.Count; i++)
            {
                if (!FormPlan.IsFillable(form.Fields[i])) continue;
                var name = Names.Camel(form.Fields[i].Name);
                if (name.Length == 0 || char.IsDigit(name[0])) name = "field" + (i + 1);
                parameters.Add((i, CSharp.Safe(Names.Unique(parameterNames, name))));
            }
            result.Add(new FormMembers(form, fields, submit, fill, parameters));
        }
        return result;
    }

    private static void WriteLocators(CodeWriter w, View view)
    {
        if (view.Heading is { } heading)
        {
            w.Line();
            w.Line("/// <summary>Sidans första rubrik.</summary>");
            w.Line($"public ILocator Heading => Page.GetByRole(AriaRole.Heading, new() {{ Name = {CSharp.Literal(heading)}, Level = 1 }});");
        }
        var members = Members(view);
        foreach (var m in members)
        {
            for (var i = 0; i < m.Form.Fields.Count; i++)
            {
                w.Line();
                w.Line($"public ILocator {m.Fields[i]} => {LocatorExpression(m.Form.Fields[i].Locator)};");
            }
            w.Line();
            w.Line($"public ILocator {m.Submit} => {LocatorExpression(m.Form.Submit)};");
        }
        foreach (var m in members.Where(m => m.Parameters.Count > 0)) WriteFill(w, m);
        if (members.Any(m => m.Parameters.Any(p => m.Form.Fields[p.Index].Type == FieldType.Radio)))
        {
            w.Line();
            w.Line("""private static string ValueSelector(string value) => $"[value=\"{value.Replace("\"", "\\\"")}\"]";""");
        }
    }

    /// <summary>Metoden som fyller i alla fält i formuläret (inte hidden), men skickar det inte.</summary>
    private static void WriteFill(CodeWriter w, FormMembers m)
    {
        var signature = string.Join(", ", m.Parameters.Select(p => $"{(m.Form.Fields[p.Index].Type == FieldType.Checkbox ? "bool" : "string")} {p.Name}"));
        w.Line();
        w.Line($"/// <summary>Fyller i formuläret {CSharp.Comment(m.Form.Id)}, men skickar det inte.</summary>");
        using (w.Block($"public async Task {m.Fill}({signature})"))
            foreach (var (index, name) in m.Parameters)
            {
                var field = m.Form.Fields[index];
                var locator = m.Fields[index];
                switch (field.Type)
                {
                    case FieldType.Select: w.Line($"await {locator}.SelectOptionAsync({name});"); break;
                    case FieldType.Checkbox:
                        w.Line($"if ({name}) await {locator}.CheckAsync();");
                        w.Line($"else await {locator}.UncheckAsync();");
                        break;
                    case FieldType.Radio: w.Line($"await {locator}.And(Page.Locator(ValueSelector({name}))).CheckAsync();"); break;
                    default: w.Line($"await {locator}.FillAsync({name});"); break;
                }
            }
    }

    private static string Unique(HashSet<string> used, string wanted, string fallback)
    {
        var name = wanted.Length == 0 || char.IsDigit(wanted[0]) ? CSharp.Identifier("Field" + wanted, fallback) : wanted;
        return Names.Unique(used, name);
    }

    /// <summary>Modellens <see cref="Locator"/> som Playwright-kod mot <c>Page</c>.</summary>
    public static string LocatorExpression(Locator l) => l.Kind switch
    {
        LocatorKind.Label => $"Page.GetByLabel({CSharp.Literal(l.Value)}, new() {{ Exact = true }})",
        LocatorKind.Role when AriaRoles.Contains(l.Value.ToLowerInvariant()) =>
            $"Page.GetByRole(AriaRole.{char.ToUpperInvariant(l.Value[0])}{l.Value[1..].ToLowerInvariant()}" +
            (l.Name is null ? ")" : $", new() {{ Name = {CSharp.Literal(l.Name)} }})"),
        LocatorKind.Role => $"Page.Locator({CSharp.Literal($"[role={CssString(l.Value)}]")})",
        LocatorKind.TestId => $"Page.GetByTestId({CSharp.Literal(l.Value)})",
        _ => $"Page.Locator({CSharp.Literal(l.Value)})",
    };

    private static string CssString(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
