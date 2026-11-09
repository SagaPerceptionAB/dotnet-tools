using System.Text.RegularExpressions;
using TestGen.Core.Views;
using TestGen.Views.Discovery.Razor;

namespace TestGen.Views.Discovery.Spa;

/// <summary>Vad som lästes ur en komponent: titel, rubrik, länkar, formulär och API-anrop.</summary>
internal sealed record Analysis(string? Title, string? Heading, IReadOnlyList<Link> Links, IReadOnlyList<Form> Forms, IReadOnlyList<ApiCall> ApiCalls);

/// <summary>
/// Läser en komponents markup (JSX, Angular-mall eller Vue-template) och kod: rubrik, titel, länkar, formulär med regler och API-anrop.
/// Resultatet cachas per fil, så flera rutter till samma komponent läser den en gång.
/// </summary>
internal sealed partial class ComponentAnalyzer(SpaFiles files, DiscoveryContext context)
{
    private static readonly string[] Verbs = ["get", "post", "put", "patch", "delete"];

    private static readonly HashSet<string> Receivers = new(StringComparer.Ordinal)
        { "axios", "http", "httpClient", "api", "client", "request", "instance", "$http", "$axios" };

    private readonly Dictionary<string, Analysis> _cache = new(StringComparer.Ordinal);

    public Analysis Analyze(SpaSource component)
    {
        if (_cache.TryGetValue(component.Path, out var cached)) return cached;
        var result = Read(component);
        _cache[component.Path] = result;
        return result;
    }

    private Analysis Read(SpaSource component)
    {
        var markupText = MarkupText(component);
        var markup = markupText is null ? null : new MarkupDoc(markupText, SpaMarkupRules.Instance);
        var title = TitleFromCode(component.Tokens);
        string? heading = null;
        var links = new List<Link>();
        var forms = new List<Form>();
        if (markup is not null)
        {
            title ??= markup.Tags.FirstOrDefault(t => t.Is("title")) is { } tt ? markup.FixedText(tt) : null;
            heading = markup.Tags.FirstOrDefault(t => t.Is("h1")) is { } h1 ? markup.FixedText(h1) : null;
            links.AddRange(Links(markup));
            forms.AddRange(Forms(markup, ValidatorRules(component.Tokens)));
        }
        var apiCalls = ApiCalls(component);
        // Ett enda skrivande anrop i en vy med ett enda formulär är med stor sannolikhet formulärets.
        var writes = apiCalls.Where(c => c.Method != "GET").ToList();
        if (forms.Count == 1 && writes.Count == 1) forms[0] = forms[0] with { SubmitsTo = writes[0] };
        return new Analysis(title, heading, links, forms, apiCalls);
    }

    // ---------- Markup ur komponenten ----------

    private string? MarkupText(SpaSource c)
    {
        if (c.IsVue)
        {
            var start = VueTemplateStart().Match(c.Text);
            if (!start.Success) return null;
            var end = c.Text.LastIndexOf("</template>", StringComparison.Ordinal);
            return end > start.Index + start.Length ? c.Text[(start.Index + start.Length)..end] : null;
        }
        if (c.Path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
        {
            // Angular: templateUrl eller template i dekoratorn.
            var t = c.Tokens;
            for (var i = 0; i + 2 < t.Count; i++)
            {
                if (t[i].IsIdent("templateUrl") && t[i + 1].IsPunct(':') && t[i + 2].IsText)
                {
                    var html = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(c.Path)!, t[i + 2].Text));
                    return File.Exists(html) ? File.ReadAllText(html) : null;
                }
                if (t[i].IsIdent("template") && t[i + 1].IsPunct(':') && t[i + 2].IsText) return t[i + 2].Text;
            }
            return null;
        }
        return c.Text;
    }

    private static string? TitleFromCode(IReadOnlyList<Tok> t)
    {
        for (var i = 0; i + 4 < t.Count; i++)
        {
            if (t[i].IsIdent("document") && t[i + 1].IsPunct('.') && t[i + 2].IsIdent("title") && t[i + 3].IsPunct('=') && t[i + 4].IsText && !t[i + 4].Holes)
                return t[i + 4].Text;
            if (t[i].IsIdent("setTitle") && t[i + 1].IsPunct('(') && t[i + 2].IsText && !t[i + 2].Holes) return t[i + 2].Text;
        }
        return null;
    }

    // ---------- Länkar ----------

    private static IEnumerable<Link> Links(MarkupDoc markup)
    {
        foreach (var tag in markup.Tags.Where(t => !t.Closing))
        {
            string? href = null;
            var isAnchor = tag.Name.Equals("a", StringComparison.OrdinalIgnoreCase);
            if (tag.Name is "Link" or "NavLink" or "RouterLink" or "router-link")
                href = PathAttribute(tag, "to") ?? PathAttribute(tag, "routerLink");
            else if (tag.Has("routerLink") || tag.Has("[routerLink]"))
                href = PathAttribute(tag, "routerLink");
            if (href is null && isAnchor)
            {
                href = PathAttribute(tag, "href");
                if (href is not null && !href.StartsWith('/')) href = null;
            }
            if (!Hrefs.IsInternal(href)) continue;
            yield return new Link(href!.Trim(), markup.FixedText(tag));
        }
    }

    // Värdet av attributet name, i vanlig form (to="/x"), JSX-form (to={`/x/${id}`}) eller bunden form ([routerLink]="['/x', id]", :to="'/x'").
    private static string? PathAttribute(Tag tag, string name)
    {
        foreach (var bound in new[] { "[" + name + "]", ":" + name, "v-bind:" + name })
            if (tag.Has(bound) && tag[bound] is { } expr) return ExpressionPath(expr);
        if (tag[name] is not { } v) return null;
        v = v.Trim();
        if (v.StartsWith('{') && v.EndsWith('}')) return ExpressionPath(v[1..^1]);
        return MustacheHole().Replace(v, "{}");
    }

    // Ett uttryck som är en sträng, en template literal eller en array av sådana ger en sökväg; allt annat null.
    private static string? ExpressionPath(string expression)
    {
        var tokens = JsTokenizer.Tokenize(expression.Trim());
        if (tokens.Count == 0) return null;
        if (tokens[0].IsPunct('['))
        {
            var items = new JsParser(tokens).ParseArray(0, tokens.Count).Items;
            if (items.Count == 0) return null;
            var sb = new System.Text.StringBuilder();
            foreach (var item in items)
            {
                var part = item is JsStr s ? s.Value : "{}";
                if (sb.Length == 0) sb.Append(part);
                else sb.Append('/').Append(part.Trim('/'));
            }
            return sb.ToString();
        }
        if (!tokens[0].IsText) return null;
        return tokens.Count > 1 && tokens[1].IsPunct('+') ? tokens[0].Text + "{}" : tokens[0].Text;
    }

    // ---------- Formulär ----------

    private static IEnumerable<Form> Forms(MarkupDoc markup, Dictionary<string, List<Rule>> validators)
    {
        var n = 0;
        foreach (var form in markup.Tags.Where(t => t.Is("form")))
        {
            var inside = markup.Inside(form).Where(t => !t.Closing).ToList();
            var fields = Fields(markup, inside, validators);
            if (fields.Count == 0) continue;
            yield return new Form($"Form{++n}", null, Submit(markup, inside), fields, null);
        }
    }

    private static List<Field> Fields(MarkupDoc markup, List<Tag> inside, Dictionary<string, List<Rule>> validators)
    {
        var labels = markup.Tags.Where(t => t.Is("label")).ToList();
        var forLabels = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var l in labels)
            if ((l["for"] ?? l["htmlFor"]) is { } target && SpaMarkupRules.Literal(target) is { } id) forLabels.TryAdd(id, markup.FixedText(l));

        var fields = new List<Field>();
        var radios = new Dictionary<string, (int Index, List<string> Options)>(StringComparer.Ordinal);
        foreach (var tag in inside.Where(t => t.Name.Equals("input", StringComparison.OrdinalIgnoreCase)
                     || t.Name.Equals("select", StringComparison.OrdinalIgnoreCase) || t.Name.Equals("textarea", StringComparison.OrdinalIgnoreCase)))
        {
            var kind = tag.Name.ToLowerInvariant();
            var typeAttr = (markup.Fixed(tag, "type") ?? "text").ToLowerInvariant();
            if (kind == "input" && typeAttr is "submit" or "button" or "reset" or "image" or "file") continue;

            var id = markup.Fixed(tag, "id");
            var name = markup.Fixed(tag, "name");
            var control = markup.Fixed(tag, "formControlName");
            var model = tag.Attrs.FirstOrDefault(a => a.Name == "v-model" || a.Name.StartsWith("v-model.", StringComparison.Ordinal))?.Value;
            model = model is null ? null : model.Split('.').Last().Trim();
            var fieldName = name ?? id ?? control ?? (string.IsNullOrEmpty(model) ? null : model);
            if (fieldName is null) continue;

            var type = kind switch
            {
                "select" => FieldType.Select,
                "textarea" => FieldType.TextArea,
                _ => ModelAttributes.FromHtmlType(typeAttr) ?? FieldType.Text,
            };

            if (type == FieldType.Radio)
            {
                var value = markup.Fixed(tag, "value");
                if (radios.TryGetValue(fieldName, out var group)) { if (value is not null) group.Options.Add(value); continue; }
                var options = new List<string>();
                if (value is not null) options.Add(value);
                radios[fieldName] = (fields.Count, options);
                fields.Add(new Field(fieldName, null, type, new Locator(LocatorKind.Css, $"input[name=\"{fieldName}\"]"), Rules(markup, tag, type, control, validators), options));
                continue;
            }

            var label = (id is not null && forLabels.TryGetValue(id, out var byFor) ? byFor : null)
                ?? labels.Where(l => !l.SelfClosing && tag.Start >= l.End && tag.Start < markup.CloseStart(l)).Select(markup.FixedText).FirstOrDefault(x => x is not null);
            Locator? locator = label is not null ? new Locator(LocatorKind.Label, label)
                : id is not null ? new Locator(LocatorKind.Css, ModelAttributes.CssId(id))
                : name is not null ? new Locator(LocatorKind.Css, $"[name=\"{name}\"]")
                : control is not null ? new Locator(LocatorKind.Css, $"[formcontrolname=\"{control}\"]")
                : null;
            if (locator is null) continue;
            var selectOptions = kind == "select" ? ViewParts.Options(markup, tag) : [];
            fields.Add(new Field(fieldName, label, type, locator, Rules(markup, tag, type, control, validators), selectOptions));
        }
        return fields;
    }

    private static List<Rule> Rules(MarkupDoc markup, Tag tag, FieldType type, string? control, Dictionary<string, List<Rule>> validators)
    {
        var rules = FormReader.HtmlRules(markup, tag);
        if (type == FieldType.Email) rules.Add(new Rule(RuleKind.Email));
        if (type == FieldType.Url) rules.Add(new Rule(RuleKind.Url));
        if (control is not null && validators.TryGetValue(control, out var fromCode))
            foreach (var r in fromCode)
                if (rules.All(x => x.Kind != r.Kind)) rules.Add(r);
        return rules.OrderBy(r => r.Kind).ToList();
    }

    // Knappen som skickar formuläret. Saknas en submit-knapp tas första knappen av vilken typ som helst, annars en knapp utan text.
    private static Locator Submit(MarkupDoc markup, List<Tag> inside)
    {
        if (ViewParts.SubmitButton(markup, inside) is { } submit) return new Locator(LocatorKind.Role, "button", submit.Text);
        var any = inside.FirstOrDefault(t => t.Is("button"));
        return new Locator(LocatorKind.Role, "button", any is null ? null : markup.FixedText(any));
    }

    // ---------- Angular: Validators i komponentens kod ----------

    /// <summary>Reglerna per fältnamn ur <c>name: ['', [Validators.required, Validators.minLength(3)]]</c> och <c>name: new FormControl('', Validators.required)</c>.</summary>
    private static Dictionary<string, List<Rule>> ValidatorRules(IReadOnlyList<Tok> t)
    {
        var result = new Dictionary<string, List<Rule>>(StringComparer.Ordinal);
        for (var i = 0; i + 2 < t.Count; i++)
        {
            if (t[i].Kind is not (TokKind.Ident or TokKind.String) || !t[i + 1].IsPunct(':')) continue;
            var v = i + 2;
            var end = -1;
            if (t[v].IsPunct('[')) end = JsTokenizer.Matching(t, v);
            else
            {
                // new FormControl(...), this.fb.control(...)
                var j = v;
                while (j < t.Count && j < v + 8 && (t[j].Kind == TokKind.Ident || t[j].IsPunct('.'))) j++;
                if (j < t.Count && t[j].IsPunct('(')) end = JsTokenizer.Matching(t, j);
            }
            if (end < 0) continue;
            var rules = new List<Rule>();
            for (var k = v; k + 2 < end; k++)
            {
                if (!t[k].IsIdent("Validators") || !t[k + 1].IsPunct('.') || t[k + 2].Kind != TokKind.Ident) continue;
                var arg = k + 4 < end && t[k + 3].IsPunct('(') ? t[k + 4] : (Tok?)null;
                switch (t[k + 2].Text)
                {
                    case "required" or "requiredTrue": rules.Add(new Rule(RuleKind.Required)); break;
                    case "email": rules.Add(new Rule(RuleKind.Email)); break;
                    case "minLength" when arg is { Kind: TokKind.Number } a: rules.Add(new Rule(RuleKind.MinLength, a.Text)); break;
                    case "maxLength" when arg is { Kind: TokKind.Number } a: rules.Add(new Rule(RuleKind.MaxLength, a.Text)); break;
                    case "min" when arg is { Kind: TokKind.Number } a: rules.Add(new Rule(RuleKind.Min, a.Text)); break;
                    case "max" when arg is { Kind: TokKind.Number } a: rules.Add(new Rule(RuleKind.Max, a.Text)); break;
                    case "pattern" when arg is { Kind: TokKind.String or TokKind.Regex } a: rules.Add(new Rule(RuleKind.Pattern, a.Text)); break;
                }
            }
            if (rules.Count > 0) result.TryAdd(t[i].Text, rules);
        }
        return result;
    }

    // ---------- API-anrop ----------

    private List<ApiCall> ApiCalls(SpaSource component)
    {
        var calls = new List<ApiCall>();
        ScanCalls(component, calls);
        var seen = new HashSet<string>(StringComparer.Ordinal) { component.Path };
        foreach (var spec in component.ImportSpecs)
        {
            if (files.Resolve(spec, component.Path) is not { } path || !seen.Add(path) || files.Get(path) is not { } imported) continue;
            ScanCalls(imported, calls);
        }
        return calls;
    }

    private void ScanCalls(SpaSource source, List<ApiCall> calls)
    {
        var t = source.Tokens;
        for (var i = 0; i < t.Count; i++)
        {
            string callee;
            string method;
            int open;
            if (t[i].IsIdent("fetch") && (i == 0 || !t[i - 1].IsPunct('.') || i > 1 && t[i - 2].IsIdent("window")) && i + 1 < t.Count && t[i + 1].IsPunct('('))
            {
                callee = "fetch";
                open = i + 1;
                method = FetchMethod(t, open);
            }
            else if (t[i].Kind == TokKind.Ident && Receivers.Contains(t[i].Text) && i + 3 < t.Count && t[i + 1].IsPunct('.')
                     && t[i + 2].Kind == TokKind.Ident && Verbs.Contains(t[i + 2].Text))
            {
                callee = $"{t[i].Text}.{t[i + 2].Text}";
                method = t[i + 2].Text.ToUpperInvariant();
                open = i + 3;
                if (t[open].IsPunct('<'))
                {
                    // http.get<Product[]>(...)
                    var depth = 0;
                    while (open < t.Count && open < i + 40)
                    {
                        if (t[open].IsPunct('<')) depth++;
                        else if (t[open].IsPunct('>') && --depth == 0) break;
                        open++;
                    }
                    open++;
                }
                if (open >= t.Count || !t[open].IsPunct('(')) continue;
            }
            else continue;

            if (open + 1 >= t.Count) continue;
            var arg = t[open + 1];
            if (arg.IsText)
            {
                var path = arg.Text;
                if (open + 2 < t.Count && t[open + 2].IsPunct('+')) path += "{}";
                var q = path.IndexOf('?');
                if (q >= 0) path = path[..q];
                if (path.StartsWith('/') && !path.StartsWith("//", StringComparison.Ordinal)) calls.Add(new ApiCall(method, path));
                else if (path.StartsWith("{}", StringComparison.Ordinal)) Unreadable(callee, source);
                // Andra adresser (http://..., relativa) är inte backend i appen och hoppas över utan varning.
            }
            else if (arg.Kind == TokKind.Ident || arg.IsPunct('{')) Unreadable(callee, source);
        }
    }

    private void Unreadable(string callee, SpaSource source) =>
        context.Warnings.Add($"API-anropet {callee}(...) i {context.Relative(source.Path)} går inte att läsa: sökvägen är inte en fast text. Anropet hoppas över.");

    private static string FetchMethod(IReadOnlyList<Tok> t, int open)
    {
        var close = JsTokenizer.Matching(t, open);
        for (var k = open; close > 0 && k + 2 < close; k++)
            if (t[k].IsIdent("method") && t[k + 1].IsPunct(':') && t[k + 2].IsText) return t[k + 2].Text.ToUpperInvariant();
        return "GET";
    }

    [GeneratedRegex(@"\{\{[^}]*\}\}")]
    private static partial Regex MustacheHole();

    [GeneratedRegex(@"^[ \t]*<template\b[^>]*>", RegexOptions.Multiline)]
    private static partial Regex VueTemplateStart();
}
