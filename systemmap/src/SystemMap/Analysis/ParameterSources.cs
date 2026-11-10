using Microsoft.CodeAnalysis;

namespace SystemMap;

/// <summary>
/// Vad ASP.NET skulle binda en parameter från. Bara en parameter som kan vara request-kroppen visas som indata: en tjänst
/// (registrerad i DI), en DbContext, en typ som binder sig själv
/// (statisk <c>BindAsync</c> eller <c>TryParse</c>) och en parameter med ett annat attribut än [FromBody] är det inte. Ramverkets särskilda
/// typer (<c>HttpContext</c>, <c>CancellationToken</c> ...) behöver inget undantag: bara typer i solutionen blir indata (se <see cref="DataShapes"/>).
/// </summary>
internal static class ParameterSources
{
    private static readonly string[] OtherSources =
        ["FromServices", "FromKeyedServices", "FromQuery", "FromRoute", "FromHeader", "FromForm", "AsParameters"];

    public static bool CanBeBody(IParameterSymbol parameter, ServiceResolver services)
    {
        if (Symbols.HasAttribute(parameter, OtherSources)) return false;
        var type = Symbols.Unwrap(parameter.Type) ?? parameter.Type;
        return !Symbols.Is(type, "Microsoft.EntityFrameworkCore.DbContext") && !BindsItself(type) && !services.IsService(type);
    }

    /// <summary>Typen har en statisk <c>BindAsync</c> eller <c>TryParse</c>: ASP.NET binder den från sökväg, fråga eller huvuden, inte från kroppen.</summary>
    private static bool BindsItself(ITypeSymbol type) =>
        type.GetMembers().OfType<IMethodSymbol>().Any(m => m.IsStatic && (m.Name == "BindAsync" || (m.Name == "TryParse" && m.Parameters.Length >= 2)));
}
