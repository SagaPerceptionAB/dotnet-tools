using System.Reflection;
using System.Text.Json;
using Microsoft.Playwright;

namespace TestGen.Browser;

/// <param name="Selector">CSS-väljaren som pekar ut elementet.</param>
/// <param name="Html">Elementets HTML.</param>
public sealed record AccessibilityElement(string Selector, string Html);

/// <param name="Rule">axe-regelns id, t.ex. <c>color-contrast</c>.</param>
/// <param name="Impact">minor, moderate, serious eller critical.</param>
/// <param name="Help">Regelns beskrivning (engelska, från axe).</param>
public sealed record AccessibilityViolation(string Rule, string Impact, string Help, string HelpUrl, IReadOnlyList<AccessibilityElement> Elements)
{
    public string Describe() => $"{Rule} ({Impact}): {Help}. {Elements.Count} element, t.ex. {Elements[0].Selector}. {HelpUrl}";
}

/// <summary>Tillgänglighetskontroll med axe-core (WCAG 2.1 A och AA).</summary>
public static class Accessibility
{
    /// <summary>Versionen av axe-core som är inbäddad (se THIRD-PARTY-NOTICES.md).</summary>
    public const string AxeVersion = "4.10.3";

    private static readonly Lazy<string> AxeSource = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("axe.min.js")
            ?? throw new InvalidOperationException("axe.min.js saknas som resurs.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    private const string RunScript = """
        async () => {
          const result = await axe.run(document, { runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'] } });
          return JSON.stringify(result.violations.map(v => ({
            id: v.id, impact: v.impact, help: v.help, helpUrl: v.helpUrl,
            nodes: v.nodes.map(n => ({ target: n.target.join(' '), html: n.html }))
          })));
        }
        """;

    /// <summary>Kontrollerar sidan som den ser ut nu. Tom lista om inget fel hittas; annars sorterad på regel.</summary>
    public static async Task<IReadOnlyList<AccessibilityViolation>> CheckAsync(IPage page)
    {
        await page.EvaluateAsync(AxeSource.Value);
        var json = await page.EvaluateAsync<string>(RunScript);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray()
            .Select(v => new AccessibilityViolation(
                v.GetProperty("id").GetString()!,
                v.TryGetProperty("impact", out var impact) && impact.ValueKind == JsonValueKind.String ? impact.GetString()! : "unknown",
                v.GetProperty("help").GetString()!,
                v.GetProperty("helpUrl").GetString()!,
                v.GetProperty("nodes").EnumerateArray()
                    .Select(n => new AccessibilityElement(n.GetProperty("target").GetString()!, n.GetProperty("html").GetString()!)).ToList()))
            .OrderBy(v => v.Rule, StringComparer.Ordinal)
            .ToList();
    }
}
