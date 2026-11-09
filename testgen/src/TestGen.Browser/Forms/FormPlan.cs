using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.OpenApi;
using TestGen.Core;
using TestGen.Core.Views;

namespace TestGen.Browser.Forms;

/// <summary>Ett värde att fylla i: text, eller för en kryssruta om den ska vara ikryssad.</summary>
public sealed record FieldValue(string Text, bool Checked = false)
{
    public static FieldValue Of(string text) => new(text);
    public static FieldValue Box(bool isChecked) => new("", isChecked);
}

/// <summary>Fältet och ett giltigt värde för det. <see cref="Valid"/> är null om fältet inte fylls i eller om inget giltigt värde hittades.</summary>
public sealed record FieldPlan(Field Field, FieldValue? Valid);

/// <summary>Vem som upptäcker att en regel bryts: appens egen kontroll (regeln har ett felmeddelande) eller webbläsaren (HTML-attributen).</summary>
public enum RuleChecker { App, Browser }

/// <summary>
/// Ett test som skickar formuläret med ett ogiltigt värde i ett fält, så att just <see cref="Rule"/> bryts.
/// <see cref="Checker"/> säger vem som ska upptäcka det, och testet följer det.
/// </summary>
/// <param name="Index">Fältets plats i formuläret.</param>
public sealed record InvalidCase(int Index, Field Field, Rule Rule, FieldValue Value, RuleChecker Checker)
{
    /// <summary>
    /// Webbläsarens maxlength hindrar ett för långt värde från att skrivas in, så attributet tas bort innan testet fyller i fältet.
    /// Bara när appen kontrollerar regeln: webbläsaren markerar aldrig ett för långt värde som ogiltigt, och med attributet borta kan den inte det alls.
    /// </summary>
    public bool NeedsLongInput => Checker == RuleChecker.App && Rule.Kind == RuleKind.MaxLength;
}

/// <summary>
/// Värdena för ett formulärs tester, uträknade när koden genereras: ett giltigt värde per fält och ett ogiltigt värde per regel.
/// Samma katalog och samma seed ger samma värden, och ett fälts värde beror inte på de andra fälten (utom Compare).
/// </summary>
public sealed class FormPlan
{
    /// <summary>Hur många gånger ett giltigt värde genereras om (med nästa seed) innan fältet ges upp.</summary>
    private const int Attempts = 4;

    private static readonly Regex EmailPattern = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly string[] DateFormats = ["yyyy-MM-dd"];

    public required Form Form { get; init; }
    public required IReadOnlyList<FieldPlan> Fields { get; init; }
    public required IReadOnlyList<InvalidCase> Cases { get; init; }

    /// <summary>Ett fält som inte gick att ge ett giltigt värde. Då hoppas formulärets alla tester över.</summary>
    public string? Warning { get; init; }

    /// <summary>Fylls fältet i av formulärtesterna (och finns som parameter i Page Objectens metod)?</summary>
    public static bool IsFillable(Field f) => f.Type switch
    {
        FieldType.Hidden => false,
        FieldType.Select or FieldType.Radio => FirstOption(f) is not null,
        _ => true,
    };

    private static string? FirstOption(Field f) => f.Options.FirstOrDefault(o => !string.IsNullOrWhiteSpace(o));

    /// <param name="bodySchema">Request-schemat för formulärets operation. Med det genereras värdet ur specens fält (<c>specField</c>) när det går, så att det klarar både formuläret och API:et.</param>
    public static FormPlan Build(View view, Form form, int seed, IOpenApiSchema? bodySchema = null)
    {
        var specProps = bodySchema is null ? null : SchemaUtil.Flatten(bodySchema).Props;
        var key = $"{view.Id}/{form.Id}";
        var fields = form.Fields;
        var valid = new FieldValue?[fields.Count];
        string? warning = null;

        var compareTarget = new int[fields.Count];
        for (var i = 0; i < fields.Count; i++) compareTarget[i] = CompareTarget(form, fields[i]);

        // Först fälten som är fria, sedan de som ska vara lika med ett annat fält.
        for (var pass = 0; pass < 2; pass++)
            for (var i = 0; i < fields.Count; i++)
            {
                var f = fields[i];
                if ((compareTarget[i] >= 0) != (pass == 1) || !IsFillable(f)) continue;
                var other = compareTarget[i] >= 0 ? valid[compareTarget[i]]?.Text : null;
                valid[i] = compareTarget[i] >= 0 && other is null ? null : ValidValue(f, $"{key}/{f.Name}", seed, other, SpecSchema(specProps, f));
                if (valid[i] is null && warning is null) warning = $"Hittade inget giltigt värde för fältet {f.Name} ({RuleSummary(f)}).";
            }

        var plans = fields.Select((f, i) => new FieldPlan(f, valid[i])).ToList();
        var cases = new List<InvalidCase>();
        if (warning is null)
            for (var i = 0; i < fields.Count; i++)
                if (IsFillable(fields[i])) cases.AddRange(InvalidCases(i, fields[i], valid[i]!, compareTarget[i] >= 0 ? valid[compareTarget[i]]?.Text : null));
        return new FormPlan { Form = form, Fields = plans, Cases = cases, Warning = warning };
    }

    /// <summary>Specens schema för fältet (<c>specField</c>), om det finns.</summary>
    internal static IOpenApiSchema? SpecSchema(IDictionary<string, IOpenApiSchema>? props, Field f)
    {
        if (props is null || f.SpecField is not { } name) return null;
        if (props.TryGetValue(name, out var exact)) return exact;
        return props.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
    }

    private static string RuleSummary(Field f) =>
        f.Rules.Count == 0 ? "inga regler" : string.Join(", ", f.Rules.Select(r => r.Value is null ? r.Kind.ToString() : $"{r.Kind} {r.Value}"));

    /// <summary>Index för fältet som <paramref name="f"/> ska vara lika med (Compare), eller -1.</summary>
    private static int CompareTarget(Form form, Field f)
    {
        var rule = f.Rules.FirstOrDefault(r => r.Kind == RuleKind.Compare);
        if (rule?.Value is not { } name) return -1;
        name = name[(name.LastIndexOf('.') + 1)..];
        for (var i = 0; i < form.Fields.Count; i++)
            if (!ReferenceEquals(form.Fields[i], f) && string.Equals(form.Fields[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    // --- Giltiga värden ---

    private static FieldValue? ValidValue(Field f, string key, int seed, string? compareWith, IOpenApiSchema? spec)
    {
        switch (f.Type)
        {
            case FieldType.Select or FieldType.Radio:
                return FieldValue.Of(FirstOption(f)!);
            case FieldType.Checkbox:
                return FieldValue.Box(f.Rules.Any(r => r.Kind == RuleKind.Required));
        }
        if (compareWith is not null)
        {
            var same = FieldValue.Of(compareWith);
            return Violations(f, same, compareWith).Count == 0 ? same : null;
        }
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var salt = Fnv.OperationSeed(key, seed, attempt);
            // Först värdet ur specen (klarar då API:et), sedan det som formulärets regler ger.
            var texts = new[] { FixedDate(f), spec is null ? null : GenerateText(f, spec, salt), GenerateText(f, Schema(f), salt) };
            foreach (var text in texts.OfType<string>())
            {
                var value = FieldValue.Of(text);
                if (Typeable(f, text) && Violations(f, value, null).Count == 0) return value;
            }
        }
        return null;
    }

    /// <summary>Ett datumfält med en gräns får gränsen som värde: den är fast och ligger inom intervallet.</summary>
    private static string? FixedDate(Field f)
    {
        if (f.Type != FieldType.Date) return null;
        foreach (var kind in new[] { RuleKind.Min, RuleKind.Max })
            if (f.Rules.FirstOrDefault(r => r.Kind == kind)?.Value is { } v && TryDate(v, out var d)) return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return null;
    }

    /// <summary>Fältets typ och regler som ett OpenAPI-schema, som testgens datagenerator förstår.</summary>
    public static IOpenApiSchema Schema(Field f)
    {
        var schema = new OpenApiSchema { Type = JsonSchemaType.String };
        decimal? min = null, max = null;
        foreach (var rule in f.Rules)
            switch (rule.Kind)
            {
                case RuleKind.MinLength when TryInt(rule.Value, out var n): schema.MinLength = n; break;
                case RuleKind.MaxLength when TryInt(rule.Value, out var n): schema.MaxLength = n; break;
                case RuleKind.Pattern when !string.IsNullOrEmpty(rule.Value): schema.Pattern = rule.Value; break;
                case RuleKind.Email: schema.Format = "email"; break;
                case RuleKind.Url: schema.Format = "uri"; break;
                case RuleKind.Min when TryDecimal(rule.Value, out var d): min = d; break;
                case RuleKind.Max when TryDecimal(rule.Value, out var d): max = d; break;
            }
        switch (f.Type)
        {
            case FieldType.Number:
                // Heltal om gränserna är heltal: ett <input type=number> utan step godtar bara det.
                var whole = (min ?? 0) % 1 == 0 && (max ?? 0) % 1 == 0;
                schema.Type = whole ? JsonSchemaType.Integer : JsonSchemaType.Number;
                schema.MinLength = null;
                schema.MaxLength = null;
                schema.Pattern = null;
                break;
            case FieldType.Email: schema.Format ??= "email"; break;
            case FieldType.Url: schema.Format ??= "uri"; break;
            case FieldType.Date: schema.Format ??= "date"; break;
            case FieldType.DateTime: schema.Format ??= "date-time"; break;
            case FieldType.Password: schema.Format ??= "password"; break;
        }
        schema.Minimum = min?.ToString(CultureInfo.InvariantCulture);
        schema.Maximum = max?.ToString(CultureInfo.InvariantCulture);
        return schema;
    }

    private static string? GenerateText(Field f, IOpenApiSchema schema, int seed)
    {
        try
        {
            var generator = new SchemaDataGenerator(seed, "sv");
            var node = generator.GenerateField(f.Name, schema);
            var text = node switch
            {
                null => null,
                JsonValue v when v.TryGetValue<string>(out var s) => s,
                _ => node.ToJsonString(),
            };
            if (text is not null && f.Type == FieldType.DateTime && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when))
                text = when.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);
            return text;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or FormatException or IndexOutOfRangeException or RegexMatchTimeoutException)
        {
            return null; // t.ex. ett mönster som generatorn inte klarar
        }
    }

    // --- Kontroll av värden ---

    /// <summary>
    /// Vilka av fältets regler värdet bryter. En tom text bryter bara Required: övriga regler gäller inte tomma värden
    /// (så som DataAnnotations behandlar dem när fältet inte är obligatoriskt).
    /// </summary>
    public static IReadOnlyList<RuleKind> Violations(Field f, FieldValue value, string? compareWith)
    {
        var broken = new List<RuleKind>();
        var text = value.Text;
        foreach (var rule in f.Rules)
        {
            var violated = rule.Kind switch
            {
                RuleKind.Required => f.Type == FieldType.Checkbox ? !value.Checked : text.Trim().Length == 0,
                _ when f.Type == FieldType.Checkbox || text.Length == 0 => false,
                RuleKind.MinLength => TryInt(rule.Value, out var n) && text.Length < n,
                RuleKind.MaxLength => TryInt(rule.Value, out var n) && text.Length > n,
                RuleKind.Min => Outside(text, rule.Value, below: true),
                RuleKind.Max => Outside(text, rule.Value, below: false),
                RuleKind.Pattern => !string.IsNullOrEmpty(rule.Value) && !FullMatch(text, rule.Value),
                RuleKind.Email => !EmailPattern.IsMatch(text),
                RuleKind.Url => !(Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "ftp"),
                RuleKind.Compare => compareWith is not null && text != compareWith,
                _ => false,
            };
            if (violated && !broken.Contains(rule.Kind)) broken.Add(rule.Kind);
        }
        return broken;
    }

    /// <summary>Som RegularExpression i DataAnnotations: mönstret ska täcka hela värdet. Ett ogiltigt mönster räknas som uppfyllt.</summary>
    private static bool FullMatch(string text, string pattern)
    {
        try
        {
            var m = Regex.Match(text, pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
            return m.Success && m.Index == 0 && m.Length == text.Length;
        }
        catch (ArgumentException) { return true; }
        catch (RegexMatchTimeoutException) { return true; }
    }

    /// <summary>Ligger värdet utanför gränsen (under den, eller över den)? Ett värde som inte går att jämföra räknas som utanför.</summary>
    private static bool Outside(string text, string? bound, bool below)
    {
        if (bound is null || !(TryDecimal(bound, out _) || TryDate(bound, out _))) return false; // en gräns som inte är läsbar gäller inte
        return Compare(text, bound) is not { } c || (below ? c < 0 : c > 0);
    }

    /// <summary>Jämför ett värde med en gräns, som tal eller som datum. Null om de inte går att jämföra.</summary>
    private static int? Compare(string text, string? bound)
    {
        if (bound is null) return null;
        if (TryDecimal(text, out var a) && TryDecimal(bound, out var b)) return a.CompareTo(b);
        if (TryDate(text, out var d) && TryDate(bound, out var e)) return d.CompareTo(e);
        return null;
    }

    /// <summary>Går värdet att skriva i fältet? Playwright vägrar text som inte är ett tal i ett talfält, eller ett datum i ett datumfält.</summary>
    public static bool Typeable(Field f, string text) => text.Length == 0 || f.Type switch
    {
        FieldType.Number => TryDecimal(text, out _),
        FieldType.Date => TryDate(text, out _),
        FieldType.DateTime => DateTime.TryParseExact(text, "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
        _ => true,
    };

    private static bool TryInt(string? text, out int n) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out n);

    private static bool TryDecimal(string? text, out decimal d) =>
        decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out d);

    private static bool TryDate(string? text, out DateOnly d) => DateOnly.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out d);

    // --- Ogiltiga värden ---

    private static IEnumerable<InvalidCase> InvalidCases(int index, Field f, FieldValue valid, string? compareWith)
    {
        foreach (var rule in f.Rules)
            if (CheckedBy(rule) is { } checker && InvalidValue(f, rule, valid, compareWith) is { } value)
                yield return new InvalidCase(index, f, rule, value, checker);
    }

    /// <summary>
    /// Vem som kontrollerar regeln, eller null om ett brott inte går att framkalla och inget test ska skrivas. Har regeln ett felmeddelande
    /// kontrollerar appen den. Annars är det webbläsaren, men bara för regler som finns som HTML-attribut: webbläsaren markerar aldrig ett
    /// för långt värde som ogiltigt (MaxLength) och känner inte till Compare.
    /// </summary>
    public static RuleChecker? CheckedBy(Rule rule)
    {
        if (!string.IsNullOrEmpty(rule.Message)) return RuleChecker.App;
        return rule.Kind is RuleKind.MaxLength or RuleKind.Compare ? null : RuleChecker.Browser;
    }

    private static FieldValue? InvalidValue(Field f, Rule rule, FieldValue valid, string? compareWith)
    {
        var text = valid.Text;
        if (f.Type is FieldType.Checkbox or FieldType.Select or FieldType.Radio)
        {
            if (rule.Kind != RuleKind.Required) return null;
            return f.Type switch
            {
                FieldType.Checkbox => FieldValue.Box(false),
                FieldType.Select when f.Options.Contains("") => FieldValue.Of(""),
                _ => null, // en radioknapp går inte att avmarkera
            };
        }

        IEnumerable<string> candidates = rule.Kind switch
        {
            RuleKind.Required => [""],
            RuleKind.MinLength when TryInt(rule.Value, out var n) && n > 1 => [text.Length >= n ? text[..(n - 1)] : "", new string('x', n - 1)],
            RuleKind.MaxLength when TryInt(rule.Value, out var n) => LongerThan(f, text, n),
            RuleKind.Min when OneStep(rule.Value, -1) is { } below => [below],
            RuleKind.Max when OneStep(rule.Value, 1) is { } above => [above],
            RuleKind.Pattern when !string.IsNullOrEmpty(rule.Value) => PatternCandidates(f, rule.Value, text),
            RuleKind.Email => ["inte-en-epost"],
            RuleKind.Url => ["inte en adress"],
            RuleKind.Compare when compareWith is not null => [compareWith + "x", "x" + compareWith, compareWith.Length > 1 ? compareWith[..^1] : "y"],
            _ => [],
        };
        string? fallback = null;
        foreach (var c in candidates)
        {
            if (c.Length == 0 && rule.Kind != RuleKind.Required) continue;
            if (!Typeable(f, c)) continue;
            var broken = Violations(f, FieldValue.Of(c), compareWith);
            if (!broken.Contains(rule.Kind)) continue;
            if (broken.Count == 1) return FieldValue.Of(c);
            fallback ??= c;
        }
        return fallback is null ? null : FieldValue.Of(fallback);
    }

    /// <summary>n+1 tecken. 'x' om fältet tål det, annars det giltiga värdet förlängt framtill (en e-postadress måste fortfarande se ut som en).</summary>
    internal static IEnumerable<string> LongerThan(Field f, string valid, int n)
    {
        var padded = valid.Length is > 0 && valid.Length <= n ? new string(valid[0], n + 1 - valid.Length) + valid : "";
        var plain = new string('x', n + 1);
        var structured = f.Type is FieldType.Email or FieldType.Url || f.Rules.Any(r => r.Kind is RuleKind.Email or RuleKind.Url);
        return structured ? [padded, plain] : [plain, padded];
    }

    /// <summary>Gränsen ett steg bort: ett tal ±1, eller ett datum ±1 dag.</summary>
    private static string? OneStep(string? bound, int step)
    {
        if (TryDecimal(bound, out var d)) return (d + step).ToString(CultureInfo.InvariantCulture);
        if (TryDate(bound, out var date)) return date.AddDays(step).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return null;
    }

    private static IEnumerable<string> PatternCandidates(Field f, string pattern, string valid)
    {
        int? min = f.Rules.FirstOrDefault(r => r.Kind == RuleKind.MinLength) is { } a && TryInt(a.Value, out var mn) ? mn : null;
        int? max = f.Rules.FirstOrDefault(r => r.Kind == RuleKind.MaxLength) is { } b && TryInt(b.Value, out var mx) ? mx : null;
        string? found = null;
        try { found = PatternMismatch.Find(pattern, valid, min, max); }
        catch (Exception e) when (e is ArgumentException or RegexMatchTimeoutException) { }
        if (found is not null) yield return found;
        if (valid.Length > 0) yield return "!" + valid[1..];
        yield return valid + "!";
        yield return "!" + valid;
    }
}
