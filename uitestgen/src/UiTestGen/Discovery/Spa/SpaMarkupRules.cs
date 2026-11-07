namespace UiTestGen.Discovery.Spa;

/// <summary>
/// Markup i JSX, Angular-mallar och Vue-templates: attribut med <c>{...}</c> (JSX), bundna attribut (<c>:name</c>, <c>[name]</c>,
/// <c>v-bind:name</c>), och <c>{}</c>, <c>{{ }}</c> eller <c>$</c> i texten betyder något dynamiskt.
/// </summary>
internal sealed class SpaMarkupRules : MarkupRules
{
    public static readonly SpaMarkupRules Instance = new();

    private SpaMarkupRules() { }

    public override (string Value, int Next) ReadValue(string text, int k)
    {
        var c = text[k];
        if (c is '"' or '\'')
        {
            var close = text.IndexOf(c, k + 1);
            return close < 0 ? ("", -1) : (text[(k + 1)..close], close + 1);
        }
        if (c == '{')
        {
            var close = JsTokenizer.SkipBraces(text, k);
            return close < 0 ? ("", -1) : (text[k..(close + 1)], close + 1);
        }
        var m = k;
        while (m < text.Length && !char.IsWhiteSpace(text[m]) && text[m] != '>') m++;
        return (text[k..m], m);
    }

    // JSX: {...props}
    public override int? SkipExpression(string text, int index)
    {
        if (text[index] != '{') return null;
        var close = JsTokenizer.SkipBraces(text, index);
        return close < 0 ? -1 : close + 1;
    }

    public override bool IsDynamic(ReadOnlySpan<char> text) => text.IndexOfAny("{}$") >= 0;

    /// <summary>Attributet som fast text: <c>name="x"</c>, <c>name={'x'}</c>, <c>name={3}</c> eller <c>:name="'x'"</c>. Null om det är ett uttryck.</summary>
    public override AttrValue ValueOf(Tag tag, string name)
    {
        if (tag[name] is { } v) return new AttrValue(true, Literal(v));
        foreach (var bound in new[] { ":" + name, "[" + name + "]", "v-bind:" + name })
            if (tag[bound] is { } expr) return new AttrValue(true, LiteralExpression(expr));
        // Angular: [ngValue] är ett värde som inte är fast text.
        return name == "value" && tag.Has("[ngValue]") ? new AttrValue(true, null) : default;
    }

    public override bool IsRequired(Tag tag)
    {
        if (tag.Has("required")) return !(tag["required"] is { } v && Literal(v) == "false");
        foreach (var bound in new[] { "[required]", ":required", "v-bind:required" })
            if (tag[bound] is { } expr && LiteralExpression(expr) == "true") return true;
        return false;
    }

    /// <summary>Valen i en select är utan tomma värden och dubbletter.</summary>
    public override bool KeepOption(IReadOnlyList<string> chosen, string value) => value.Length > 0 && !chosen.Contains(value);

    /// <summary>Värdet om det är fast text; <c>{'x'}</c> ger <c>x</c>, och allt med <c>{{ }}</c> eller annat uttryck ger null.</summary>
    public static string? Literal(string? v)
    {
        if (v is null) return null;
        v = v.Trim();
        if (v.StartsWith('{') && v.EndsWith('}') && !v.StartsWith("{{", StringComparison.Ordinal)) return LiteralExpression(v[1..^1]);
        return v.Contains("{{", StringComparison.Ordinal) ? null : v;
    }

    /// <summary>Uttrycket om det är en sträng, ett tal, true, false eller ett reguljärt uttryck på egen hand.</summary>
    public static string? LiteralExpression(string expression)
    {
        var tokens = JsTokenizer.Tokenize(expression.Trim());
        if (tokens.Count == 1 && tokens[0].Kind is TokKind.String or TokKind.Number && !tokens[0].Holes) return tokens[0].Text;
        if (tokens.Count == 1 && tokens[0].Kind == TokKind.Ident && tokens[0].Text is "true" or "false") return tokens[0].Text;
        if (tokens.Count == 1 && tokens[0].Kind == TokKind.Regex) return tokens[0].Text;
        return null;
    }
}
