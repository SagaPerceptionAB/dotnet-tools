using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using UiTestGen.Discovery.Razor;

namespace UiTestGen.Discovery;

/// <summary>Ett attribut på en klass eller egenskap, utan namnrymd och utan suffixet <c>Attribute</c>. Argumenten är lästa som text.</summary>
internal sealed record ModelAttribute(string Name, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string> Named);

/// <summary>En egenskap eller ett fält. <paramref name="Type"/> är typens enkla namn utan <c>?</c> och namnrymd, t.ex. <c>ProductInput</c> eller <c>string</c>.</summary>
internal sealed record ModelMember(string Name, string Type, IReadOnlyList<ModelAttribute> Attributes);

internal sealed record ModelClass(string Name, IReadOnlyList<string> Bases, IReadOnlyList<ModelAttribute> Attributes, IReadOnlyList<ModelMember> Members);

/// <summary>
/// Klasserna i källkoden (från .cs-filer och <c>@code</c>-block), uppslagna på enkelt namn. Läser syntax med Roslyn, ingen kompilering.
/// Ett index kan ha ett överordnat index: egna klasser går före de överordnade, så att t.ex. <c>IndexModel</c> bredvid en sida väljs före en annan sidas.
/// </summary>
internal sealed class TypeIndex(TypeIndex? parent = null)
{
    /// <summary>Klassen som innehåller en komponents <c>@code</c>-block.</summary>
    public const string ComponentWrapper = "__Component";

    private static readonly ConditionalWeakTable<DiscoveryContext, TypeIndex> Cache = new();
    private readonly Dictionary<string, List<ModelClass>> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<ModelClass>> _byFile = new(StringComparer.Ordinal);

    /// <summary>Alla klasser i katalogerna. Delas av upptäckarna i samma körning.</summary>
    public static TypeIndex For(DiscoveryContext context) => Cache.GetValue(context, Build);

    public void Add(IEnumerable<ModelClass> classes)
    {
        foreach (var c in classes)
        {
            if (!_byName.TryGetValue(c.Name, out var list)) _byName[c.Name] = list = [];
            list.Add(c);
        }
    }

    public IReadOnlyList<ModelClass> Classes(string name) =>
        _byName.TryGetValue(name, out var own) ? own : parent?.Classes(name) ?? [];

    public bool Exists(string name) => Classes(name).Count > 0;

    /// <summary>
    /// Klasserna som lästes ur filen när indexet byggdes: hela klasser för en .cs-fil, och för en .razor-fil klasserna i <c>@code</c>-blocken
    /// (medlemmar utan klass ligger i <see cref="ComponentWrapper"/>). Tom om filen inte är med i indexet.
    /// </summary>
    public IReadOnlyList<ModelClass> FileClasses(string file) =>
        _byFile.TryGetValue(file, out var classes) ? classes : parent?.FileClasses(file) ?? [];

    /// <summary>Lägger till klasserna i filen bredvid <paramref name="file"/> (<c>X.razor.cs</c> eller <c>X.cshtml.cs</c>), om den finns i det överordnade indexet.</summary>
    public void AddCodeBehind(string file) => Add(FileClasses(file + ".cs"));

    /// <summary>Medlemmen <paramref name="member"/> i typen (eller dess basklasser).</summary>
    public ModelMember? Member(string type, string member, int depth = 0)
    {
        if (depth > 8) return null;
        var classes = Classes(type);
        foreach (var c in classes)
            if (c.Members.FirstOrDefault(m => m.Name == member) is { } found) return found;
        foreach (var c in classes)
            foreach (var b in c.Bases)
                if (Member(b, member, depth + 1) is { } inherited) return inherited;
        return null;
    }

    /// <summary>Följer en väg som <c>Input.Address.Street</c> från en rottyp och ger den sista medlemmen.</summary>
    public ModelMember? Resolve(string rootType, IReadOnlyList<string> path)
    {
        ModelMember? member = null;
        var type = rootType;
        foreach (var segment in path)
        {
            member = Member(type, segment);
            if (member is null) return null;
            type = member.Type;
        }
        return member;
    }

    /// <summary>Klasserna i C#-kod (hela klasser, eller medlemmar som ska ligga i klassen <see cref="ComponentWrapper"/>).</summary>
    public static IReadOnlyList<ModelClass> Parse(string code) => Parse(CSharpSyntaxTree.ParseText(code));

    /// <summary>Klasserna i ett redan tolkat syntaxträd.</summary>
    public static IReadOnlyList<ModelClass> Parse(SyntaxTree tree)
    {
        var root = tree.GetRoot();
        var result = new List<ModelClass>();
        foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            var members = new List<ModelMember>();
            foreach (var member in type.Members)
            {
                switch (member)
                {
                    case PropertyDeclarationSyntax p:
                        members.Add(new ModelMember(p.Identifier.Text, TypeName(p.Type), Attributes(p.AttributeLists)));
                        break;
                    case FieldDeclarationSyntax f:
                        members.AddRange(f.Declaration.Variables.Select(v => new ModelMember(v.Identifier.Text, TypeName(f.Declaration.Type), Attributes(f.AttributeLists))));
                        break;
                }
            }
            if (type is RecordDeclarationSyntax { ParameterList: { } parameters })
                members.AddRange(parameters.Parameters.Select(p => new ModelMember(p.Identifier.Text, TypeName(p.Type), Attributes(p.AttributeLists))));
            var bases = type.BaseList?.Types.Select(t => TypeName(t.Type)).ToList() ?? [];
            result.Add(new ModelClass(type.Identifier.Text, bases, Attributes(type.AttributeLists), members));
        }
        return result;
    }

    /// <summary>Koden i ett <c>@code</c>-block som klass, för att Roslyn ska kunna läsa den.</summary>
    public static IReadOnlyList<ModelClass> ParseComponentCode(string code) =>
        Parse($"partial class {ComponentWrapper}\n{{\n{code}\n}}");

    private static TypeIndex Build(DiscoveryContext context)
    {
        var cache = SourceCache.For(context);
        var index = new TypeIndex();
        foreach (var file in context.Files(".cs"))
        {
            if (cache.CSharp(file) is not { } source) continue;
            var classes = Parse(source.Tree);
            index.Add(classes);
            index._byFile[file] = classes;
        }
        foreach (var file in context.Files(".razor"))
        {
            if (cache.Razor(file) is not { } source) continue;
            var classes = source.CodeBlocks.SelectMany(ParseComponentCode).ToList();
            index.Add(classes.Where(c => c.Name != ComponentWrapper));
            index._byFile[file] = classes;
        }
        return index;
    }

    private static string TypeName(TypeSyntax? type) => type switch
    {
        null => "",
        NullableTypeSyntax n => TypeName(n.ElementType),
        QualifiedNameSyntax q => TypeName(q.Right),
        AliasQualifiedNameSyntax a => TypeName(a.Name),
        GenericNameSyntax g => g.Identifier.Text,
        IdentifierNameSyntax i => i.Identifier.Text,
        PredefinedTypeSyntax p => p.Keyword.Text,
        ArrayTypeSyntax a => TypeName(a.ElementType) + "[]",
        _ => type.ToString(),
    };

    private static List<ModelAttribute> Attributes(SyntaxList<AttributeListSyntax> lists)
    {
        var result = new List<ModelAttribute>();
        foreach (var attribute in lists.SelectMany(l => l.Attributes))
        {
            var name = TypeName(attribute.Name);
            if (name.EndsWith("Attribute", StringComparison.Ordinal) && name.Length > "Attribute".Length) name = name[..^"Attribute".Length];
            var args = new List<string>();
            var named = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var arg in attribute.ArgumentList?.Arguments ?? [])
            {
                var key = arg.NameEquals?.Name.Identifier.Text ?? arg.NameColon?.Name.Identifier.Text;
                if (key is null) args.Add(Value(arg.Expression));
                else named[key] = Value(arg.Expression);
            }
            result.Add(new ModelAttribute(name, args, named));
        }
        return result;
    }

    // Argumentets värde som text: strängar utan citattecken, tal som de står, nameof(X) och Enum.Värde som X respektive Värde.
    private static string Value(ExpressionSyntax expression) => expression switch
    {
        LiteralExpressionSyntax { Token.Value: string s } => s,
        LiteralExpressionSyntax { Token.Value: { } v } => Convert.ToString(v, CultureInfo.InvariantCulture) ?? expression.ToString(),
        PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.UnaryMinusExpression } u => "-" + Value(u.Operand),
        InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: "nameof" }, ArgumentList.Arguments: [{ } first] }
            => Last(first.Expression.ToString()),
        MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
        ParenthesizedExpressionSyntax p => Value(p.Expression),
        BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AddExpression } b => Value(b.Left) + Value(b.Right),
        _ => expression.ToString(),
    };

    private static string Last(string dotted) => dotted[(dotted.LastIndexOf('.') + 1)..];
}
