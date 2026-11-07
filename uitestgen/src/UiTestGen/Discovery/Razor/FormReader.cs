using UiTestGen.Model;

namespace UiTestGen.Discovery.Razor;

/// <summary>
/// Läser formulären (<c>&lt;form method="post"&gt;</c>) i Razor-markup: fält, etiketter och regler ur modellens attribut via <c>asp-for</c>.
/// Delas av Razor Pages och MVC.
/// </summary>
internal static class FormReader
{
    private static readonly HashSet<string> NotFields = new(StringComparer.OrdinalIgnoreCase) { "submit", "button", "reset", "image" };

    /// <param name="modelType">Typen som <c>asp-for</c> utgår från: sidmodellen (Razor Pages) eller <c>@model</c> (MVC).</param>
    public static List<Form> Read(DiscoveryContext context, TypeIndex index, MarkupDoc doc, string? modelType, string source)
    {
        var formTags = doc.Tags.Where(t => t.Is("form") && string.Equals(t["method"], "post", StringComparison.OrdinalIgnoreCase)).ToList();
        var models = new List<string?>();
        var parts = new List<(List<Tag> Inside, List<Field> Fields)>();
        var warnedModel = false;
        foreach (var form in formTags)
        {
            var inside = doc.Inside(form).ToList();
            var controls = inside.Where(t => (t.Is("input") && !NotFields.Contains(t["type"] ?? "")) || t.Is("select") || t.Is("textarea")).ToList();
            var bound = controls.Select(c => ViewParts.Path(c["asp-for"])).Where(p => p is not null).Select(p => p!).ToList();

            if (bound.Count > 0 && !warnedModel && (modelType is null || !index.Exists(modelType)))
            {
                warnedModel = true;
                context.Warnings.Add(modelType is null
                    ? $"Sidan {source} saknar @model. Formulären får inga regler."
                    : $"Modellen {modelType} i {source} hittades inte. Formulären får inga regler.");
            }

            // Formulärets modell: ägaren till det första asp-for-fältet, eller sidmodellen själv för fält direkt på den.
            string? model = null;
            if (bound.Count > 0 && modelType is not null)
                model = bound[0].Length == 1 ? modelType : index.Resolve(modelType, bound[0][..^1])?.Type;
            models.Add(model);

            var labelsFor = inside.Where(t => t.Is("label") && t.Has("asp-for")).GroupBy(t => t["asp-for"]!.Trim()).ToDictionary(g => g.Key, g => g.First());
            var labelsById = ViewParts.LabelsByFor(doc, inside);
            var fields = new List<Field>();
            foreach (var control in controls)
            {
                var field = control.Has("asp-for")
                    ? ReadBoundField(doc, index, modelType, control, controls, labelsFor, labelsById)
                    : ReadPlainField(doc, control, labelsById);
                if (field is not null && !fields.Any(f => f.Name == field.Name && f.Locator == field.Locator)) fields.Add(field);
            }
            parts.Add((inside, fields));
        }
        var ids = ModelAttributes.FormIds(models);
        return formTags.Select((_, i) => new Form(ids[i], models[i], ViewParts.Submit(doc, parts[i].Inside), parts[i].Fields, null)).ToList();
    }

    private static Field? ReadBoundField(MarkupDoc doc, TypeIndex index, string? modelType, Tag control, List<Tag> controls,
        Dictionary<string, Tag> labelsFor, Dictionary<string, string> labelsById)
    {
        var aspFor = control["asp-for"]!.Trim();
        if (ViewParts.Path(aspFor) is not { } path) return null;
        var name = path[^1];
        var id = doc.Fixed(control, "id") ?? string.Join('_', path);
        var htmlName = doc.Fixed(control, "name") ?? string.Join('.', path);

        var member = modelType is null ? null : index.Resolve(modelType, path);
        var attributes = member?.Attributes ?? [];
        var display = ModelAttributes.DisplayName(attributes);

        // <label asp-for> ger Display(Name), annars egenskapens namn, om inte etiketten har egen text.
        string? label = null;
        if (labelsFor.TryGetValue(aspFor, out var labelTag)) label = doc.FixedText(labelTag) ?? display ?? name;
        else if (labelsById.TryGetValue(id, out var byId)) label = byId;

        var explicitType = control.Is("input") ? ModelAttributes.FromHtmlType(control["type"]) : null;
        FieldType type;
        if (control.Is("textarea")) type = FieldType.TextArea;
        else if (control.Is("select")) type = FieldType.Select;
        else if (explicitType is { } t) type = t;
        else type = ModelAttributes.Refine(member is null ? FieldType.Text : ModelAttributes.FromClrType(member.Type), attributes);

        var options = type switch
        {
            FieldType.Select => ViewParts.Options(doc, control),
            FieldType.Radio => controls.Where(c => c.Is("input") && c["asp-for"]?.Trim() == aspFor).Select(c => doc.Fixed(c, "value")).OfType<string>().ToList(),
            _ => [],
        };
        var locator = type == FieldType.Radio ? new Locator(LocatorKind.Css, $"[name=\"{htmlName}\"]") : ViewParts.ForField(label, id, htmlName);
        return new Field(name, label, type, locator, ModelAttributes.Rules(attributes), options);
    }

    // Fält utan asp-for: namnet och reglerna kommer från HTML-attributen (required, maxlength och så vidare).
    private static Field? ReadPlainField(MarkupDoc doc, Tag control, Dictionary<string, string> labelsById)
    {
        var id = doc.Fixed(control, "id");
        var name = doc.Fixed(control, "name") ?? id;
        if (name is null) return null;
        var label = id is not null && labelsById.TryGetValue(id, out var text) ? text : null;
        var type = control.Is("textarea") ? FieldType.TextArea
            : control.Is("select") ? FieldType.Select
            : ModelAttributes.FromHtmlType(control["type"]) ?? FieldType.Text;

        var options = type == FieldType.Select ? ViewParts.Options(doc, control) : [];
        return new Field(name, label, type, ViewParts.ForField(label, id, name), HtmlRules(doc, control), options);
    }

    /// <summary>Reglerna som HTML-attributen ger på en kontroll: <c>required</c>, <c>minlength</c>, <c>maxlength</c>, <c>min</c>, <c>max</c> och <c>pattern</c>. Delas av Razor och SPA.</summary>
    public static List<Rule> HtmlRules(MarkupDoc doc, Tag control)
    {
        var rules = new List<Rule>();
        if (doc.Rules.IsRequired(control)) rules.Add(new Rule(RuleKind.Required));
        AddRule(doc, rules, RuleKind.MinLength, control, "minlength");
        AddRule(doc, rules, RuleKind.MaxLength, control, "maxlength");
        AddRule(doc, rules, RuleKind.Min, control, "min");
        AddRule(doc, rules, RuleKind.Max, control, "max");
        AddRule(doc, rules, RuleKind.Pattern, control, "pattern");
        return rules;
    }

    private static void AddRule(MarkupDoc doc, List<Rule> rules, RuleKind kind, Tag control, string attribute)
    {
        if (doc.Fixed(control, attribute) is { Length: > 0 } value) rules.Add(new Rule(kind, value));
    }
}
