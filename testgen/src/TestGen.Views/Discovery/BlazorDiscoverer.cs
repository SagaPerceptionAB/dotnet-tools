using TestGen.Core.Views;
using TestGen.Views.Discovery.Razor;

namespace TestGen.Views.Discovery;

/// <summary>
/// Hittar Blazor-sidor: varje <c>@page "..."</c> i en .razor-fil är en vy. Läser markup och <c>@code</c> som text och
/// modellklasser med Roslyn-syntaxträd. Länkarna i sidans layout (och komponenterna den använder) läggs till på sidan.
/// Kör aldrig koden och startar inte MSBuild.
/// </summary>
internal sealed class BlazorDiscoverer : IViewDiscoverer
{
    private static readonly HashSet<string> InputComponents = new(StringComparer.OrdinalIgnoreCase)
    {
        "InputText", "InputNumber", "InputDate", "InputTextArea", "InputSelect", "InputCheckbox",
    };

    public IEnumerable<View> Discover(DiscoveryContext context)
    {
        var cache = SourceCache.For(context);
        var index = TypeIndex.For(context);
        var layouts = new BlazorLayouts(context);
        var views = new List<View>();
        foreach (var file in context.Files(".razor"))
        {
            if (cache.Razor(file) is not { } source) continue;
            var pages = source.Pages();
            if (pages.Count == 0) continue;

            var relative = context.Relative(file);
            var routes = new List<string>();
            foreach (var page in pages)
            {
                if (!page.Readable || string.IsNullOrWhiteSpace(page.Template))
                {
                    var shown = page.Raw.Length == 0 ? "saknar rutt" : page.Raw;
                    context.Warnings.Add($"Rutten i {relative} går inte att läsa (@page {shown}). Vyn hoppas över.");
                    continue;
                }
                var route = page.Template.Trim();
                routes.Add(route.StartsWith('/') ? route : "/" + route);
            }
            if (routes.Count == 0) continue;

            var doc = source.Doc;
            var local = LocalIndex(index, file);
            var forms = ReadForms(context, local, doc, relative);
            var title = ViewParts.FirstFixedText(doc, "PageTitle");
            var heading = ViewParts.FirstFixedText(doc, "h1");
            // Sidans egna länkar och komponenterna den använder först, därefter länkarna i layouten (menyn). Dubbletter tas bort av ViewDiscovery.
            var links = layouts.Links.Read(file, source, null);
            var auth = source.HasAuthorize();

            foreach (var route in routes.Distinct(StringComparer.Ordinal))
                views.Add(new View(ViewId.Of(route), route, ViewKind.Blazor, relative, title, heading,
                    Names.Parameters(route), forms, links, [], auth));
        }
        return views;
    }

    // Komponentens egna klasser (@code och eventuell .razor.cs bredvid) går före övriga klasser med samma namn.
    private static TypeIndex LocalIndex(TypeIndex global, string file)
    {
        var local = new TypeIndex(global);
        var own = global.FileClasses(file);
        local.Add(own.Where(c => c.Name != TypeIndex.ComponentWrapper));
        local.AddCodeBehind(file);
        var wrapper = own.Where(c => c.Name == TypeIndex.ComponentWrapper).SelectMany(c => c.Members).ToList();
        local.Add([new ModelClass(TypeIndex.ComponentWrapper, [Path.GetFileNameWithoutExtension(file)], [], wrapper)]);
        return local;
    }

    private static List<Form> ReadForms(DiscoveryContext context, TypeIndex index, MarkupDoc doc, string source)
    {
        var labels = ViewParts.LabelsByFor(doc, doc.Tags);
        var formTags = doc.Tags.Where(t => t.Is("EditForm")).ToList();
        var models = new List<string?>();
        var parts = new List<(List<Tag> Inside, List<Field> Fields)>();
        foreach (var form in formTags)
        {
            var inside = doc.Inside(form).ToList();
            var inputs = inside.Where(t => !t.Closing && InputComponents.Contains(t.Name)).ToList();
            var model = ModelType(context, index, form, inputs, source);
            models.Add(model);
            var fields = new List<Field>();
            foreach (var input in inputs)
                if (ReadField(doc, index, input, labels) is { } field && !fields.Any(f => f.Name == field.Name && f.Locator == field.Locator))
                    fields.Add(field);
            parts.Add((inside, fields));
        }
        var ids = ModelAttributes.FormIds(models);
        return formTags.Select((_, i) => new Form(ids[i], models[i], ViewParts.Submit(doc, parts[i].Inside), parts[i].Fields, null)).ToList();
    }

    // Modellens typ: först EditForm Model="X" som fält eller egenskap i @code, annars ägaren till det första fältets @bind-Value.
    private static string? ModelType(DiscoveryContext context, TypeIndex index, Tag form, List<Tag> inputs, string source)
    {
        var modelPath = ViewParts.Path(form["Model"]);
        string? type = null;
        if (modelPath is not null) type = index.Resolve(TypeIndex.ComponentWrapper, modelPath)?.Type;
        if (type is null)
        {
            var owner = inputs.Select(i => ViewParts.Path(i["@bind-Value"])).FirstOrDefault(p => p is { Length: > 1 });
            if (owner is not null) type = index.Resolve(TypeIndex.ComponentWrapper, owner[..^1])?.Type;
        }
        if (type is null)
        {
            if (form.Has("Model") || inputs.Count > 0)
                context.Warnings.Add($"Modellen för formuläret i {source} gick inte att avgöra. Formuläret får inga regler.");
            return null;
        }
        if (!index.Exists(type))
            context.Warnings.Add($"Modellen {type} i {source} hittades inte. Formuläret får inga regler.");
        return type;
    }

    private static Field? ReadField(MarkupDoc doc, TypeIndex index, Tag input, Dictionary<string, string> labels)
    {
        var path = ViewParts.Path(input["@bind-Value"]);
        var id = doc.Fixed(input, "id");
        var nameAttribute = doc.Fixed(input, "name");
        var name = path?[^1] ?? id ?? nameAttribute;
        if (name is null) return null;

        var member = path is null ? null : index.Resolve(TypeIndex.ComponentWrapper, path);
        var attributes = member?.Attributes ?? [];

        var type = ComponentType(input);
        type = ModelAttributes.Refine(type, attributes);

        var label = id is not null && labels.TryGetValue(id, out var text) ? text : ModelAttributes.DisplayName(attributes);
        var nameSelector = path is null ? name : string.Join('.', path);
        var options = type == FieldType.Select ? ViewParts.Options(doc, input) : [];
        return new Field(name, label, type, ViewParts.ForField(label, id, nameSelector), ModelAttributes.Rules(attributes), options);
    }

    private static FieldType ComponentType(Tag input)
    {
        var fromType = ModelAttributes.FromHtmlType(input["type"]);
        return input.Name.ToLowerInvariant() switch
        {
            "inputtextarea" => FieldType.TextArea,
            "inputselect" => FieldType.Select,
            "inputcheckbox" => FieldType.Checkbox,
            "inputnumber" => fromType ?? FieldType.Number,
            "inputdate" => fromType ?? FieldType.Date,
            _ => fromType ?? FieldType.Text,
        };
    }
}
