using TestGen.Core.Views;

namespace TestGen.Views.Discovery;

/// <summary>Översätter DataAnnotations och typer till regler, etiketter och fälttyper i vyerna.</summary>
internal static class ModelAttributes
{
    /// <summary>Valideringsreglerna för attributen. <c>Message</c> hämtas från <c>ErrorMessage</c>.</summary>
    public static List<Rule> Rules(IEnumerable<ModelAttribute> attributes)
    {
        var rules = new List<Rule>();
        foreach (var a in attributes)
        {
            var message = a.Named.GetValueOrDefault("ErrorMessage");
            // Range(typeof(decimal), "1", "10") har typen först.
            var args = a.Args.Where(x => !x.StartsWith("typeof(", StringComparison.Ordinal)).ToList();
            switch (a.Name)
            {
                case "Required":
                    rules.Add(new Rule(RuleKind.Required, null, message));
                    break;
                case "StringLength":
                    if (args.Count > 0) rules.Add(new Rule(RuleKind.MaxLength, args[0], message));
                    if (a.Named.GetValueOrDefault("MinimumLength") is { } min && min != "0") rules.Add(new Rule(RuleKind.MinLength, min, message));
                    break;
                case "MinLength" when args.Count > 0:
                    rules.Add(new Rule(RuleKind.MinLength, args[0], message));
                    break;
                case "MaxLength" when args.Count > 0:
                    rules.Add(new Rule(RuleKind.MaxLength, args[0], message));
                    break;
                case "Range" when args.Count >= 2:
                    rules.Add(new Rule(RuleKind.Min, args[0], message));
                    rules.Add(new Rule(RuleKind.Max, args[1], message));
                    break;
                case "RegularExpression" when args.Count > 0:
                    rules.Add(new Rule(RuleKind.Pattern, args[0], message));
                    break;
                case "EmailAddress":
                    rules.Add(new Rule(RuleKind.Email, null, message));
                    break;
                case "Url":
                    rules.Add(new Rule(RuleKind.Url, null, message));
                    break;
                case "Compare" when args.Count > 0:
                    rules.Add(new Rule(RuleKind.Compare, args[0], message));
                    break;
            }
        }
        return rules;
    }

    /// <summary>Etiketten från <c>[Display(Name = "...")]</c> eller <c>[DisplayName("...")]</c>.</summary>
    public static string? DisplayName(IEnumerable<ModelAttribute> attributes)
    {
        foreach (var a in attributes)
        {
            if (a.Name == "Display" && a.Named.GetValueOrDefault("Name") is { Length: > 0 } name) return name;
            if (a.Name == "DisplayName" && a.Args.Count > 0 && a.Args[0].Length > 0) return a.Args[0];
        }
        return null;
    }

    /// <summary>Fälttypen som attributen ger: <c>[EmailAddress]</c> är Email, <c>[Phone]</c> är Tel, <c>[Url]</c> är Url och <c>[DataType(DataType.Password)]</c> är Password.</summary>
    public static FieldType? TypeFromAttributes(IEnumerable<ModelAttribute> attributes)
    {
        foreach (var a in attributes)
        {
            var type = a.Name switch
            {
                "EmailAddress" => FieldType.Email,
                "Phone" => FieldType.Tel,
                "Url" => FieldType.Url,
                "DataType" when a.Args.Count > 0 => a.Args[0] switch
                {
                    "Password" => FieldType.Password,
                    "EmailAddress" => FieldType.Email,
                    "PhoneNumber" => FieldType.Tel,
                    "Url" => FieldType.Url,
                    "MultilineText" => FieldType.TextArea,
                    "Date" => FieldType.Date,
                    "DateTime" => FieldType.DateTime,
                    _ => (FieldType?)null,
                },
                _ => (FieldType?)null,
            };
            if (type is not null) return type;
        }
        return null;
    }

    /// <summary>Attributen ändrar bara en vanlig textruta; en uttrycklig typ (t.ex. <c>type="number"</c>) går före.</summary>
    public static FieldType Refine(FieldType type, IEnumerable<ModelAttribute> attributes) =>
        type == FieldType.Text ? TypeFromAttributes(attributes) ?? type : type;

    /// <summary>Fälttypen som en egenskaps C#-typ ger.</summary>
    public static FieldType FromClrType(string type) => type switch
    {
        "int" or "long" or "short" or "byte" or "sbyte" or "uint" or "ulong" or "ushort" or "decimal" or "double" or "float"
            or "Int32" or "Int64" or "Int16" or "Decimal" or "Double" or "Single" => FieldType.Number,
        "bool" or "Boolean" => FieldType.Checkbox,
        "DateTime" or "DateTimeOffset" => FieldType.DateTime,
        "DateOnly" => FieldType.Date,
        _ => FieldType.Text,
    };

    /// <summary>Fälttypen som HTML-attributet <c>type</c> ger, eller null om det saknas eller inte säger något.</summary>
    public static FieldType? FromHtmlType(string? type) => type?.Trim().ToLowerInvariant() switch
    {
        "text" => FieldType.Text,
        "email" => FieldType.Email,
        "password" => FieldType.Password,
        "number" or "range" => FieldType.Number,
        "date" => FieldType.Date,
        "datetime-local" or "datetime" => FieldType.DateTime,
        "tel" => FieldType.Tel,
        "url" => FieldType.Url,
        "hidden" => FieldType.Hidden,
        "checkbox" => FieldType.Checkbox,
        "radio" => FieldType.Radio,
        _ => null,
    };

    /// <summary><c>[Authorize]</c> finns och <c>[AllowAnonymous]</c> saknas.</summary>
    public static bool RequiresAuth(IEnumerable<ModelAttribute> attributes)
    {
        var list = attributes.ToList();
        return list.Any(a => a.Name == "Authorize") && !list.Any(a => a.Name == "AllowAnonymous");
    }

    /// <summary>Formulärets id: modellens typnamn utan <c>Input</c> eller <c>Model</c> på slutet, annars <c>Form1</c>, <c>Form2</c>. Unikt inom vyn.</summary>
    public static List<string> FormIds(IReadOnlyList<string?> modelTypes)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new List<string>();
        for (var i = 0; i < modelTypes.Count; i++)
        {
            var id = Names.Pascal(StripSuffix(modelTypes[i] ?? ""));
            if (id.Length == 0) id = $"Form{i + 1}";
            var unique = id;
            for (var n = 2; !used.Add(unique); n++) unique = id + n;
            ids.Add(unique);
        }
        return ids;
    }

    private static string StripSuffix(string type)
    {
        foreach (var suffix in new[] { "Input", "Model" })
            if (type.Length > suffix.Length && type.EndsWith(suffix, StringComparison.Ordinal)) return type[..^suffix.Length];
        return type;
    }

    /// <summary>CSS-väljare för ett id: <c>#id</c>, eller <c>[id="..."]</c> om id:t har tecken som kräver skydd.</summary>
    public static string CssId(string id) =>
        id.Length > 0 && (char.IsLetter(id[0]) || id[0] == '_') && id.All(c => char.IsLetterOrDigit(c) || c is '_' or '-')
            ? "#" + id
            : $"[id=\"{id}\"]";
}
