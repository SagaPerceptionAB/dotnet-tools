using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace UiTestGen.Discovery.Mvc;

/// <summary>En vy-action: en GET-metod i en MVC-controller som returnerar <c>View(...)</c>.</summary>
/// <param name="Name">Metodens namn, som det står i koden.</param>
/// <param name="Templates">Mallarna från <c>[HttpGet("...")]</c> och <c>[Route("...")]</c> på metoden. Null betyder ett attribut utan mall.</param>
/// <param name="RouteUnreadable">Någon mall är inte en fast sträng.</param>
/// <param name="ViewName">Namnet i <c>View("Namn")</c>, eller null för <c>View()</c>.</param>
/// <param name="HasId">Metoden har en parameter som heter <c>id</c>.</param>
/// <param name="Auth">Actionens eget besked: true för <c>[Authorize]</c>, false för <c>[AllowAnonymous]</c>, null om inget står.</param>
internal sealed record ActionInfo(string Name, IReadOnlyList<string?> Templates, bool RouteUnreadable, string? ViewName, bool HasId, bool? Auth);

/// <param name="Name">Klassnamnet utan <c>Controller</c>, som det står i koden.</param>
/// <param name="Templates">Mallarna från klassens <c>[Route("...")]</c>.</param>
internal sealed record ControllerInfo(string Name, string? Area, string File, IReadOnlyList<string> Templates, bool RouteUnreadable, bool RequiresAuth, IReadOnlyList<ActionInfo> Actions);

/// <summary>Läser controllers ur C#-källa med Roslyns syntaxträd. Ingen kompilering.</summary>
internal static class ControllerReader
{
    private const string Suffix = "Controller";

    public static List<ControllerInfo> Read(SyntaxTree tree, string file)
    {
        var result = new List<ControllerInfo>();
        foreach (var type in tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            if (type.Modifiers.Any(m => m.IsKind(SyntaxKind.AbstractKeyword)) || !IsMvcController(type)) continue;
            var attributes = Attributes(type.AttributeLists);
            var routes = attributes.Where(a => a.Name == "Route").ToList();
            var auth = attributes.Any(a => a.Name == "Authorize") && !attributes.Any(a => a.Name == "AllowAnonymous");
            var area = attributes.FirstOrDefault(a => a.Name == "Area")?.Template;
            var name = type.Identifier.Text;
            if (name.Length > Suffix.Length && name.EndsWith(Suffix, StringComparison.Ordinal)) name = name[..^Suffix.Length];
            var actions = type.Members.OfType<MethodDeclarationSyntax>().Select(Action).OfType<ActionInfo>().ToList();
            result.Add(new ControllerInfo(name, area, file, routes.Select(r => r.Template).OfType<string>().ToList(), routes.Any(r => r.Unreadable), auth, actions));
        }
        return result;
    }

    // Ärver Controller (inte bara ControllerBase), eller heter *Controller och ärver något *Controller. [ApiController] är API, inte MVC.
    private static bool IsMvcController(ClassDeclarationSyntax type)
    {
        var attributes = Attributes(type.AttributeLists);
        if (attributes.Any(a => a.Name is "ApiController" or "NonController")) return false;
        var bases = type.BaseList?.Types.Select(t => SimpleName(t.Type)).ToList() ?? [];
        if (bases.Contains(Suffix)) return true;
        return type.Identifier.Text.EndsWith(Suffix, StringComparison.Ordinal) && bases.Any(b => b.EndsWith(Suffix, StringComparison.Ordinal));
    }

    private static ActionInfo? Action(MethodDeclarationSyntax method)
    {
        if (!method.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)) || method.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword))) return null;
        var returnType = method.ReturnType.ToString();
        if (!returnType.Contains("ActionResult", StringComparison.Ordinal) && !returnType.Contains("ViewResult", StringComparison.Ordinal)) return null;

        var attributes = Attributes(method.AttributeLists);
        if (attributes.Any(a => a.Name == "NonAction")) return null;
        // Bara GET: varje annan Http-metod, och AcceptVerbs utan GET, hoppas över.
        if (attributes.Any(a => a.Name.StartsWith("Http", StringComparison.Ordinal) && a.Name != "HttpGet")) return null;
        if (attributes.Any(a => a.Name == "AcceptVerbs" && !a.Args.Any(v => v.Contains("get", StringComparison.OrdinalIgnoreCase)))) return null;

        var calls = ViewCalls(method);
        if (calls.Count == 0) return null;
        // View() och View(model) går före View("Namn"): det senare är ofta ett undantagsfall som View("NotFound").
        var viewName = calls.Any(c => c is null) ? null : calls[0];

        var templated = attributes.Where(a => a.Name is "HttpGet" or "Route").ToList();
        bool? auth = attributes.Any(a => a.Name == "AllowAnonymous") ? false : attributes.Any(a => a.Name == "Authorize") ? true : null;
        var hasId = method.ParameterList.Parameters.Any(p => p.Identifier.Text.Equals("id", StringComparison.OrdinalIgnoreCase));
        return new ActionInfo(method.Identifier.Text, templated.Select(a => a.Template).ToList(), templated.Any(a => a.Unreadable), viewName, hasId, auth);
    }

    // Namnen i anropen av View(...), i den ordning de står. Null betyder standardvyn (View() eller View(model)).
    private static List<string?> ViewCalls(MethodDeclarationSyntax method)
    {
        var calls = new List<string?>();
        foreach (var call in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var name = call.Expression switch
            {
                IdentifierNameSyntax i => i.Identifier.Text,
                MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } m => m.Name.Identifier.Text,
                _ => null,
            };
            if (name != "View") continue;
            var first = call.ArgumentList.Arguments.FirstOrDefault(a => a.NameColon is null || a.NameColon.Name.Identifier.Text == "viewName");
            calls.Add(first?.Expression switch
            {
                LiteralExpressionSyntax { Token.Value: string s } => s,
                InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: "nameof" }, ArgumentList.Arguments: [{ } n] } => SimpleName(n.Expression),
                _ => null,
            });
        }
        return calls;
    }

    /// <param name="Template">Första argumentet om det är en fast sträng.</param>
    /// <param name="Unreadable">Det finns ett mallargument men det är inte en fast sträng.</param>
    private sealed record Attr(string Name, string? Template, bool Unreadable, IReadOnlyList<string> Args);

    private static List<Attr> Attributes(SyntaxList<AttributeListSyntax> lists)
    {
        var result = new List<Attr>();
        foreach (var attribute in lists.SelectMany(l => l.Attributes))
        {
            var name = SimpleName(attribute.Name);
            if (name.EndsWith("Attribute", StringComparison.Ordinal) && name.Length > "Attribute".Length) name = name[..^"Attribute".Length];
            var args = attribute.ArgumentList?.Arguments ?? [];
            var template = args.FirstOrDefault(a => a.NameEquals is null && (a.NameColon is null || a.NameColon.Name.Identifier.Text == "template"));
            var text = template?.Expression is LiteralExpressionSyntax { Token.Value: string s } ? s : null;
            result.Add(new Attr(name, text, template is not null && text is null, args.Select(a => a.Expression.ToString()).ToList()));
        }
        return result;
    }

    private static string SimpleName(SyntaxNode node) => node switch
    {
        QualifiedNameSyntax q => SimpleName(q.Right),
        AliasQualifiedNameSyntax a => SimpleName(a.Name),
        GenericNameSyntax g => g.Identifier.Text,
        IdentifierNameSyntax i => i.Identifier.Text,
        MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
        _ => node.ToString(),
    };
}
