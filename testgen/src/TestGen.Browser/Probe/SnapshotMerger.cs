using System.Globalization;
using System.Text.RegularExpressions;
using TestGen.Core.Views;

namespace TestGen.Browser.Probe;

/// <summary>
/// Slår ihop det statiska med det som lästes ur DOM:en. Regler med felmeddelande från koden vinner alltid. Sonderingen
/// fyller i det som saknas: titel, rubrik, etiketter, HTML-regler, länkar och formulär.
/// </summary>
internal static partial class SnapshotMerger
{
    public const string ProbedSource = "(probe)";

    private static readonly HashSet<string> HiddenInputTypes = new(StringComparer.Ordinal) { "hidden", "submit", "button", "image", "reset" };

    /// <summary>En länk inom appen, redan rensad: sökväg utan query och fragment.</summary>
    internal sealed record LocalLink(string Path, string? Text);

    /// <summary>
    /// Länkarna som pekar inom samma origin. Utelämnar länkar med query, utloggning och filer, eftersom de varken ska följas eller
    /// stå i katalogen. Sorterade och utan dubbletter.
    /// </summary>
    public static IReadOnlyList<LocalLink> LocalLinks(PageSnapshot snapshot, string origin, string currentPath)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var link in snapshot.Links)
        {
            if (!Uri.TryCreate(link.Href, UriKind.Absolute, out var uri)) continue;
            if (!string.Equals(uri.GetLeftPart(UriPartial.Authority), origin, StringComparison.OrdinalIgnoreCase)) continue;
            if (uri.Query.Length > 1) continue;
            var path = TrimSlash(uri.AbsolutePath);
            if (uri.Fragment.Length > 1 && path == TrimSlash(currentPath)) continue;
            if (Excluded().IsMatch(path) || FileExtension().IsMatch(path)) continue;
            var text = Clean(link.Text);
            if (!result.TryGetValue(path, out var existing) || existing is null) result[path] = text ?? existing;
        }
        return result.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new LocalLink(p.Key, p.Value)).ToList();
    }

    public static string TrimSlash(string path) => path.Length > 1 ? path.TrimEnd('/') is { Length: > 0 } t ? t : "/" : "/";

    /// <summary>Vyn för en sida som bara sonderingen hittade.</summary>
    public static View NewView(string path, PageSnapshot snapshot, RouteMatcher matcher, string origin) =>
        Merge(new View(ViewId.Of(path), path, ViewKind.Probed, ProbedSource, null, null, [], [], [], [], false),
            snapshot, matcher, origin, path);

    public static View Merge(View view, PageSnapshot snapshot, RouteMatcher matcher, string origin, string currentPath)
    {
        var links = view.Links.ToList();
        foreach (var l in LocalLinks(snapshot, origin, currentPath))
        {
            var href = matcher.Normalize(l.Path);
            var i = links.FindIndex(x => x.Href == href);
            if (i < 0) links.Add(new Link(href, l.Text));
            else if (links[i].Text is null && l.Text is not null) links[i] = links[i] with { Text = l.Text };
        }
        return view with
        {
            Title = view.Title ?? Clean(snapshot.Title),
            Heading = view.Heading ?? Clean(snapshot.Heading),
            Links = links,
            Forms = MergeForms(view.Forms, snapshot),
        };
    }

    // --- formulär ---

    private sealed record Probed(DomField Field, string? Name, FieldType Type, string? Label, bool UniqueLabel, IReadOnlyList<string> Options);

    private static IReadOnlyList<Form> MergeForms(IReadOnlyList<Form> statics, PageSnapshot snapshot)
    {
        var allFields = snapshot.Forms.Select(f => Collapse(f.Fields)).ToList();
        var labelCounts = allFields.SelectMany(f => f).Select(p => p.Label).Where(l => l is not null)
            .GroupBy(l => l!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var forms = snapshot.Forms.Select((f, i) => (Dom: f, Fields: allFields[i]
            .Select(p => p with { UniqueLabel = p.Label is not null && labelCounts[p.Label] == 1 }).ToList()))
            .Where(f => f.Fields.Count > 0).ToList();

        var used = new HashSet<int>();
        var result = new List<Form>();
        foreach (var form in statics)
        {
            var best = -1;
            var bestOverlap = 0;
            for (var i = 0; i < forms.Count; i++)
            {
                if (used.Contains(i)) continue;
                var overlap = form.Fields.Count(f => FindProbed(f, forms[i].Fields, []) is not null);
                if (overlap > bestOverlap) { best = i; bestOverlap = overlap; }
            }
            if (best < 0) { result.Add(form); continue; }
            used.Add(best);
            result.Add(MergeForm(form, forms[best].Dom, forms[best].Fields));
        }

        var taken = new HashSet<string>(result.Select(f => f.Id), StringComparer.OrdinalIgnoreCase);
        var n = 1;
        for (var i = 0; i < forms.Count; i++)
        {
            if (used.Contains(i)) continue;
            while (!taken.Add("Form" + n)) n++;
            var fields = forms[i].Fields.Select(NewField).ToList();
            result.Add(new Form("Form" + n, null, SubmitLocator(forms[i].Dom, null), fields, null));
        }
        return result;
    }

    private static Form MergeForm(Form form, DomForm dom, List<Probed> probed)
    {
        var claimed = new HashSet<Probed>();
        var fields = new List<Field>();
        foreach (var f in form.Fields)
        {
            if (FindProbed(f, probed, claimed) is not { } p) { fields.Add(f); continue; }
            claimed.Add(p);
            fields.Add(MergeField(f, p));
        }
        fields.AddRange(probed.Where(p => !claimed.Contains(p)).Select(NewField));
        return form with { Fields = fields, Submit = SubmitLocator(dom, form.Submit) };
    }

    private static Field MergeField(Field field, Probed p)
    {
        var rules = field.Rules.ToList();
        foreach (var rule in HtmlRules(p.Field))
            if (rules.All(r => r.Kind != rule.Kind)) rules.Add(rule);
        var label = field.Label ?? p.Label;
        var locator = field.Locator;
        if (locator.Kind == LocatorKind.Css)
        {
            if (p.Field.TestId is { Length: > 0 } testId) locator = new Locator(LocatorKind.TestId, testId);
            else if (p.UniqueLabel && p.Label is not null) locator = new Locator(LocatorKind.Label, p.Label);
        }
        return field with
        {
            Label = label,
            Locator = locator,
            Rules = rules.OrderBy(r => r.Kind).ToList(),
            Options = field.Options.Count > 0 ? field.Options : p.Options,
        };
    }

    private static Field NewField(Probed p, int index)
    {
        var f = p.Field;
        var name = p.Name ?? p.Label ?? $"Field{index + 1}";
        Locator locator;
        if (f.TestId is { Length: > 0 } testId) locator = new Locator(LocatorKind.TestId, testId);
        else if (p.UniqueLabel && p.Label is not null) locator = new Locator(LocatorKind.Label, p.Label);
        else if (f.Id is { Length: > 0 } id && !id.Contains(':')) locator = new Locator(LocatorKind.Css, $"[id=\"{id}\"]");
        else if (f.Name is { Length: > 0 } n) locator = new Locator(LocatorKind.Css, $"[name=\"{n}\"]");
        else locator = new Locator(LocatorKind.Css, f.Tag);
        return new Field(name, p.Label, p.Type, locator, HtmlRules(f).OrderBy(r => r.Kind).ToList(), p.Options);
    }

    private static Locator SubmitLocator(DomForm dom, Locator? existing)
    {
        if (existing is not null && existing.Kind != LocatorKind.Css) return existing;
        if (Clean(dom.SubmitTestId) is { } testId) return new Locator(LocatorKind.TestId, testId);
        if (Clean(dom.SubmitText) is { } text) return new Locator(LocatorKind.Role, "button", text);
        return existing ?? new Locator(LocatorKind.Css, "[type=submit]");
    }

    /// <summary>Matchar ett statiskt fält mot ett läst fält på namn (utan prefix som <c>Input.</c>), annars på id.</summary>
    private static Probed? FindProbed(Field field, IEnumerable<Probed> probed, ICollection<Probed> claimed)
    {
        var key = Key(field.Name);
        if (key.Length == 0) return null;
        var candidates = probed.Where(p => !claimed.Contains(p)).ToList();
        return candidates.FirstOrDefault(p => Key(p.Name) == key) ?? candidates.FirstOrDefault(p => Key(p.Field.Id) == key);
    }

    private static string Key(string? name)
    {
        if (name is null) return "";
        name = BracketIndex().Replace(name, "");
        var dot = name.LastIndexOf('.');
        return (dot >= 0 ? name[(dot + 1)..] : name).ToLowerInvariant();
    }

    /// <summary>Läser fälten i ordning. Radioknappar med samma namn blir ett fält med valen som alternativ.</summary>
    private static List<Probed> Collapse(IReadOnlyList<DomField> fields)
    {
        var result = new List<Probed>();
        foreach (var f in fields.Where(f => f.Type is null || !HiddenInputTypes.Contains(f.Type)))
        {
            var name = Clean(f.Name) ?? Clean(f.Id) ?? Clean(f.TestId);
            if (f.Type == "radio" && name is not null && result.FindIndex(p => p.Field.Type == "radio" && p.Name == name) is var i and >= 0)
            {
                var group = result[i];
                result[i] = group with { Options = group.Options.Append(f.Value ?? "on").ToList() };
                continue;
            }
            var options = f.Type == "radio" ? [f.Value ?? "on"] : f.Options.Where(o => o.Length > 0).ToList();
            var label = f.Type == "radio" ? null : Clean(f.Label);
            result.Add(new Probed(f, name, FieldTypeOf(f), label, false, options));
        }
        return result;
    }

    private static FieldType FieldTypeOf(DomField f) => f.Tag switch
    {
        "select" => FieldType.Select,
        "textarea" => FieldType.TextArea,
        _ => f.Type switch
        {
            "email" => FieldType.Email,
            "password" => FieldType.Password,
            "number" or "range" => FieldType.Number,
            "date" => FieldType.Date,
            "datetime-local" => FieldType.DateTime,
            "tel" => FieldType.Tel,
            "url" => FieldType.Url,
            "checkbox" => FieldType.Checkbox,
            "radio" => FieldType.Radio,
            _ => FieldType.Text,
        },
    };

    /// <summary>Reglerna som HTML-attributen ger. Utan felmeddelande, eftersom webbläsarens egna texter inte är appens.</summary>
    private static IEnumerable<Rule> HtmlRules(DomField f)
    {
        if (f.Required) yield return new Rule(RuleKind.Required);
        if (f.MinLength is { } minLength and > 0) yield return new Rule(RuleKind.MinLength, minLength.ToString(CultureInfo.InvariantCulture));
        if (f.MaxLength is { } maxLength and >= 0) yield return new Rule(RuleKind.MaxLength, maxLength.ToString(CultureInfo.InvariantCulture));
        if (Clean(f.Min) is { } min) yield return new Rule(RuleKind.Min, min);
        if (Clean(f.Max) is { } max) yield return new Rule(RuleKind.Max, max);
        if (Clean(f.Pattern) is { } pattern) yield return new Rule(RuleKind.Pattern, pattern);
    }

    private static string? Clean(string? text)
    {
        if (text is null) return null;
        var cleaned = Whitespace().Replace(text, " ").Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    [GeneratedRegex(@"log-?out|logga-?ut|sign-?out", RegexOptions.IgnoreCase)]
    private static partial Regex Excluded();

    [GeneratedRegex(@"\.[A-Za-z0-9]{1,5}$")]
    private static partial Regex FileExtension();

    [GeneratedRegex(@"\[[^\]]*\]")]
    private static partial Regex BracketIndex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
