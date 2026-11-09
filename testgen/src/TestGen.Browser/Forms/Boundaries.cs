using System.Globalization;
using Microsoft.OpenApi;
using TestGen.Core;
using TestGen.Core.Views;

namespace TestGen.Browser.Forms;

/// <summary>Vilken gräns i specen formuläret inte håller.</summary>
public enum BoundaryKind { MaxLength, MinLength, Maximum, Minimum, Pattern, Enum, Required }

/// <summary>
/// Ett fall där formuläret släpper igenom något som API:et avvisar: <see cref="Value"/> ligger precis utanför specens gräns
/// men godtas av formulärets egna regler. <see cref="Explanation"/> är meningen som visas om formuläret skickar det.
/// </summary>
public sealed record BoundaryCase(Field Field, BoundaryKind Kind, FieldValue Value, string Explanation)
{
    /// <summary>Gemener med bindestreck, för testnycklar.</summary>
    public string Id => Kind switch
    {
        BoundaryKind.MaxLength => "max-length",
        BoundaryKind.MinLength => "min-length",
        BoundaryKind.Maximum => "maximum",
        BoundaryKind.Minimum => "minimum",
        BoundaryKind.Pattern => "pattern",
        BoundaryKind.Enum => "enum",
        _ => "required",
    };
}

/// <summary>Jämför formulärets regler med specens schema för fälten och listar där formuläret tillåter mer än API:et.</summary>
public static class Boundaries
{
    /// <summary>Längre än så här skrivs inget värde (en gräns som är orimligt stor ska inte ge ett enormt värde).</summary>
    private const int MaxGeneratedLength = 100_000;

    /// <summary>Fallen för formuläret i <paramref name="plan"/>. Där formuläret är striktare än specen blir det inget fall.</summary>
    /// <param name="bodySchema">Request-schemat för formulärets operation.</param>
    public static IReadOnlyList<BoundaryCase> Find(FormPlan plan, IOpenApiSchema? bodySchema)
    {
        var form = plan.Form;
        if (bodySchema is null || form.OperationId is not { } operation) return [];
        var (props, required) = SchemaUtil.Flatten(bodySchema);
        var cases = new List<BoundaryCase>();
        foreach (var fieldPlan in plan.Fields)
        {
            var f = fieldPlan.Field;
            if (f.Type is FieldType.Hidden or FieldType.Checkbox || FormPlan.SpecSchema(props, f) is not { } schema) continue;
            var name = f.SpecField!;
            var valid = fieldPlan.Valid?.Text ?? "";
            cases.AddRange(FieldCases(f, name, operation, schema, valid, required.Contains(name)));
        }
        return cases;
    }

    private static IEnumerable<BoundaryCase> FieldCases(Field f, string name, string op, IOpenApiSchema schema, string valid, bool requiredBySpec)
    {
        var type = SchemaUtil.TypeOf(schema);
        var isNumber = type is "integer" or "number";

        if (requiredBySpec && First(f, [""]) is { } empty)
            yield return new(f, BoundaryKind.Required, empty, $"Formuläret tillåter tomt {name}, men API:et kräver det ({op})");

        if (schema.EnumValues().Count > 0)
        {
            var allowed = schema.EnumValues().Select(SchemaUtil.ToNode).Where(n => n is not null).Select(n => n!.ToString()).ToList();
            var candidates = f.Type is FieldType.Select or FieldType.Radio
                ? f.Options.Where(o => o.Length > 0 && !allowed.Contains(o))
                : ["invalid-value", "-1"];
            if (First(f, candidates.Where(c => !allowed.Contains(c))) is { } outside)
                yield return new(f, BoundaryKind.Enum, outside, $"Formuläret tillåter {name} = '{outside.Text}', men API:et accepterar bara {string.Join(", ", allowed)} ({op})");
        }

        if (type == "string")
        {
            if (schema.MaxLength is { } max && max < MaxGeneratedLength
                && First(f, FormPlan.LongerThan(f, valid, max)) is { } tooLong)
                yield return new(f, BoundaryKind.MaxLength, tooLong, $"Formuläret tillåter {name} med {tooLong.Text.Length} tecken, men API:et tar högst {max} ({op})");

            if (schema.MinLength is { } min and >= 2
                && First(f, [new string('x', min - 1), valid.Length >= min ? valid[..(min - 1)] : ""]) is { } tooShort)
                yield return new(f, BoundaryKind.MinLength, tooShort, $"Formuläret tillåter {name} med {tooShort.Text.Length} tecken, men API:et kräver minst {min} ({op})");

            if (!string.IsNullOrEmpty(schema.Pattern) && MismatchCandidates(schema, valid) is { } mismatch
                && First(f, [mismatch]) is { } wrong)
                yield return new(f, BoundaryKind.Pattern, wrong, $"Formuläret tillåter {name} = '{wrong.Text}', men API:et kräver mönstret {schema.Pattern} ({op})");
        }

        if (isNumber)
        {
            var step = type == "integer" ? new[] { 1m } : new[] { 0.01m, 1m };
            if (schema.Upper() is var (hi, highExclusive))
            {
                var exclusive = highExclusive;
                var candidates = exclusive ? [hi] : step.Select(s => hi + s);
                if (First(f, candidates.Select(Format)) is { } above)
                    yield return new(f, BoundaryKind.Maximum, above,
                        $"Formuläret tillåter {name} = {above.Text}, men API:et kräver {(exclusive ? "lägre än" : "högst")} {Format(hi)} ({op})");
            }
            if (schema.Lower() is var (lo, lowExclusive))
            {
                var exclusive = lowExclusive;
                var candidates = exclusive ? [lo] : step.Select(s => lo - s);
                if (First(f, candidates.Select(Format)) is { } below)
                    yield return new(f, BoundaryKind.Minimum, below,
                        $"Formuläret tillåter {name} = {below.Text}, men API:et kräver {(exclusive ? "högre än" : "minst")} {Format(lo)} ({op})");
            }
        }
    }

    private static string? MismatchCandidates(IOpenApiSchema schema, string valid)
    {
        try { return PatternMismatch.Find(schema.Pattern!, valid, schema.MinLength, schema.MaxLength); }
        catch (Exception e) when (e is ArgumentException or System.Text.RegularExpressions.RegexMatchTimeoutException) { return null; }
    }

    /// <summary>Första värdet som går att skriva i fältet och som formulärets egna regler godtar.</summary>
    private static FieldValue? First(Field f, IEnumerable<string> candidates)
    {
        foreach (var text in candidates)
        {
            if (!FormPlan.Typeable(f, text)) continue;
            if (f.Type is FieldType.Select or FieldType.Radio && !f.Options.Contains(text)) continue;
            var value = FieldValue.Of(text);
            if (FormPlan.Violations(f, value, null).Count == 0) return value;
        }
        return null;
    }

    private static string Format(decimal value) => value.ToString(CultureInfo.InvariantCulture);
}
