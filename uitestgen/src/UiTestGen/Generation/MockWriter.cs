using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using TestGen.Core;
using UiTestGen.Model;

namespace UiTestGen.Generation;

/// <summary>
/// Skriver de mockade svaren ur OpenAPI-specen: tabellen <c>ApiMocks</c> (en rad per operation) och stub-servern <c>ApiStub</c>.
/// Tabellen räknas ut när koden genereras: samma spec och samma frö ger exakt samma svar. Den genererade koden beror inte på testgen.
/// </summary>
internal static class MockWriter
{
    private static readonly FieldRules Rules = new(new Dictionary<string, string>());

    private static readonly JsonSerializerOptions BodyOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Raden för en operation: metod, sökvägsmall, regex, status och JSON-kropp.</summary>
    internal sealed record Row(string Method, string Template, string Pattern, int Status, string Body);

    /// <summary>
    /// Raderna, i en fast ordning: mallar utan parametrar först (så att <c>/orders/latest</c> går före <c>/orders/{id}</c>),
    /// sedan sökväg och metod.
    /// </summary>
    internal static IReadOnlyList<Row> Rows(ApiSpec spec, int seed) => spec.Operations
        .Select(op => RowFor(op, seed))
        .OrderBy(r => r.Template.Count(c => c == '{'))
        .ThenBy(r => r.Template, StringComparer.Ordinal)
        .ThenBy(r => r.Method, StringComparer.Ordinal)
        .ToList();

    private static Row RowFor(OperationInfo op, int seed)
    {
        var status = op.SuccessStatuses.Count > 0 ? op.SuccessStatuses[0] : 200;
        var body = op.ResponseSchemas is { } schemas && schemas.TryGetValue(status, out var schema) ? Generate(op, schema, seed) : "";
        return new Row(op.Method, op.Path, PathPattern(op.Path), status, body);
    }

    /// <summary><c>/orders/{id}</c> blir <c>^/orders/[^/]+$</c>, <c>/orders/{id?}</c> blir <c>^/orders(?:/[^/]+)?$</c> och <c>/files/{*path}</c> blir <c>^/files(?:/.*)?$</c>.</summary>
    internal static string PathPattern(string template)
    {
        var route = RouteTemplate.Parse(template);
        return route.Segments.Count == 0 ? "^/$" : route.ToRegex("^", "$");
    }

    /// <summary>
    /// Regexen för hela adresser som webbläsaren ska låta <c>ConfigureMocksAsync</c> se: sökvägen får ha ett prefix (som
    /// <c>TryMatch</c> tillåter) och följas av fråga eller fragment. Mallen <c>/</c> gäller bara roten. Grövre än
    /// <c>TryMatch</c>, som avgör svaret; den här håller bara sidor, skript och bilder borta från routningen.
    /// </summary>
    internal static string UrlPattern(IEnumerable<Row> rows)
    {
        var routes = rows.Select(r => RouteTemplate.Parse(r.Template)).ToList();
        var parts = new List<string>();
        var paths = routes.Where(r => r.Segments.Count > 0).Select(r => r.ToRegex("", "")).Distinct().ToList();
        if (paths.Count > 0) parts.Add($"[^?#]*(?:{string.Join('|', paths)})/?");
        if (routes.Any(r => r.Segments.Count == 0)) parts.Add("[a-z][a-z0-9+.-]*://[^/?#]*/?");
        return parts.Count == 0 ? "(?!)" : $"^(?:{string.Join('|', parts)})(?:[?#].*)?$";
    }

    private static string Generate(OperationInfo op, Microsoft.OpenApi.Models.OpenApiSchema schema, int seed)
    {
        try
        {
            // Frö per operation, så att en rad inte ändras när en annan operation läggs till eller tas bort.
            var node = new SchemaDataGenerator(Fnv.OperationSeed(op.OperationId, seed), "sv", Rules).GenerateField("", schema);
            return node?.ToJsonString(BodyOptions) ?? "";
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or FormatException or IndexOutOfRangeException or RegexMatchTimeoutException)
        {
            return ""; // t.ex. ett mönster som generatorn inte klarar: svaret blir tomt
        }
    }

    public static string ApiMocks(string ns, ApiSpec spec, int seed)
    {
        var w = new CodeWriter();
        w.Line(CSharp.Header);
        w.Line();
        w.Line("using System.Diagnostics.CodeAnalysis;");
        w.Line("using System.Text.RegularExpressions;");
        w.Line();
        w.Line($"namespace {ns}.Support;");
        w.Line();
        w.Line("/// <summary>");
        w.Line("/// Mockade svar ur OpenAPI-specen: en rad per operation. Används både av <c>ConfigureMocksAsync</c> (anrop som webbläsaren gör)");
        w.Line("/// och av stub-servern <c>ApiStub</c> (anrop som en serverrenderad app gör).");
        w.Line("/// Formen är ett kontrakt: <c>ApiMocks.All</c> med raderna <c>new(metod, mall, mönster, status, kropp)</c> och <c>TryMatch</c> får inte ändras, andra delar bygger på dem.");
        w.Line("/// </summary>");
        using (w.Block("public static class ApiMocks"))
        {
            w.Line("/// <summary>Ett mockat svar: statusen och JSON-kroppen (tom om specen inte beskriver något svarsschema).</summary>");
            using (w.Block("public sealed class Mock(string method, string template, string pathPattern, int status, string body)"))
            {
                w.Line("private readonly Regex _path = new(pathPattern, RegexOptions.CultureInvariant);");
                w.Line();
                w.Line("public string Method { get; } = method;");
                w.Line("/// <summary>Sökvägen som den står i specen, t.ex. <c>/orders/{id}</c>.</summary>");
                w.Line("public string Template { get; } = template;");
                w.Line("public int Status { get; } = status;");
                w.Line("public string Body { get; } = body;");
                w.Line();
                w.Line("public bool IsMatch(string path) => _path.IsMatch(path);");
            }
            w.Line();
            w.Line("/// <summary>Raderna. Ordningen är fast: mallar utan parametrar först.</summary>");
            w.Line("public static IReadOnlyList<Mock> All { get; } =");
            w.Line("[");
            foreach (var r in Rows(spec, seed))
                w.Line($"    new({CSharp.Literal(r.Method)}, {CSharp.Literal(r.Template)}, {CSharp.Literal(r.Pattern)}, {r.Status}, {CSharp.Literal(r.Body)}),");
            w.Line("];");
            w.Line();
            w.Line("/// <summary>Fullständiga adresser som kan vara ett av anropen ovan (för <c>page.RouteAsync</c>): valfritt prefix och valfri fråga.</summary>");
            w.Line($"public const string UrlPattern = {CSharp.Literal(UrlPattern(Rows(spec, seed)))};");
            w.Line();
            w.Line("/// <summary>");
            w.Line("/// Hittar svaret för anropet. Sökvägen jämförs på slutet: <c>/api/orders/5</c> träffar också <c>/orders/{id}</c>, eftersom appar");
            w.Line("/// ofta lägger ett prefix framför API:et. Fråga och fragment ignoreras, liksom ett avslutande snedstreck.");
            w.Line("/// </summary>");
            using (w.Block("public static bool TryMatch(string method, string path, [NotNullWhen(true)] out Mock? mock)"))
            {
                w.Line("var end = path.IndexOfAny(['?', '#']);");
                w.Line("if (end >= 0) path = path[..end];");
                w.Line("if (path.Length > 1) path = path.TrimEnd('/');");
                using (w.Block("for (var i = 0; i < path.Length; i++)"))
                {
                    w.Line("if (path[i] != '/') continue;");
                    w.Line("var suffix = path[i..];");
                    using (w.Block("foreach (var candidate in All)"))
                    {
                        using (w.Block("if (string.Equals(candidate.Method, method, StringComparison.OrdinalIgnoreCase) && candidate.IsMatch(suffix))"))
                        {
                            w.Line("mock = candidate;");
                            w.Line("return true;");
                        }
                    }
                }
                w.Line("mock = null;");
                w.Line("return false;");
            }
        }
        return w.ToString();
    }

    public static string ApiStub(string ns)
    {
        var w = new CodeWriter();
        w.Line(CSharp.Header);
        w.Line();
        w.Line("using Microsoft.AspNetCore.Builder;");
        w.Line("using Microsoft.AspNetCore.Hosting;");
        w.Line("using Microsoft.AspNetCore.Http;");
        w.Line("using Microsoft.Extensions.Hosting;");
        w.Line("using Microsoft.Extensions.Logging;");
        w.Line("using NUnit.Framework;");
        w.Line($"using {ns}.Support;");
        w.Line();
        // Ligger i rotnamnrymden: en SetUpFixture gäller bara tester i sin egen namnrymd och under den.
        w.Line($"namespace {ns};");
        w.Line();
        w.Line("/// <summary>");
        w.Line("/// Stub-server för serverrenderade appar (Blazor SSR, Razor Pages, MVC). Där är det servern som anropar API:et, så webbläsaren ser");
        w.Line("/// aldrig anropet och <c>RouteAsync</c> hjälper inte. Stubben svarar ur <see cref=\"ApiMocks\"/> och startas en gång för alla tester.");
        w.Line($"/// Den startar bara när backend mockas ({GenerateOptions.BackendVariable}=mock) och antingen {GenerateOptions.StubPortVariable} är satt (den porten)");
        w.Line($"/// eller {GenerateOptions.StubVariable}=on (port {GenerateOptions.DefaultStubPort}). {GenerateOptions.StubVariable}=off stänger av den. Starta sedan appen med stubbens adress som API-adress.");
        w.Line("/// </summary>");
        w.Line("[SetUpFixture]");
        using (w.Block("public sealed class ApiStub"))
        {
            w.Line("private WebApplication? _app;");
            w.Line();
            w.Line("/// <summary>Porten stubben ska lyssna på, eller null om den inte ska startas.</summary>");
            using (w.Block("public static int? ConfiguredPort()"))
            {
                w.Line("if (!UiTestBase.IsMockBackend) return null;");
                w.Line($"var mode = Environment.GetEnvironmentVariable({CSharp.Literal(GenerateOptions.StubVariable)});");
                w.Line("if (string.Equals(mode, \"off\", StringComparison.OrdinalIgnoreCase)) return null;");
                w.Line($"var port = Environment.GetEnvironmentVariable({CSharp.Literal(GenerateOptions.StubPortVariable)});");
                using (w.Block("if (!string.IsNullOrWhiteSpace(port))"))
                {
                    w.Line("if (int.TryParse(port, out var n) && n is > 0 and < 65536) return n;");
                    w.Line($"throw new InvalidOperationException($\"{GenerateOptions.StubPortVariable} ska vara ett portnummer (1-65535), inte '{{port}}'.\");");
                }
                w.Line($"return string.Equals(mode, \"on\", StringComparison.OrdinalIgnoreCase) ? {GenerateOptions.DefaultStubPort} : null;");
            }
            w.Line();
            w.Line("[OneTimeSetUp]");
            using (w.Block("public async Task StartAsync()"))
            {
                w.Line("if (ConfiguredPort() is not { } port) return;");
                w.Line("var builder = WebApplication.CreateSlimBuilder();");
                w.Line("builder.Logging.ClearProviders();");
                w.Line("builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(port));");
                w.Line("var app = builder.Build();");
                w.Line("app.Run(HandleAsync);");
                using (w.Block("try"))
                {
                    w.Line("await app.StartAsync();");
                }
                using (w.Block("catch (IOException e)"))
                {
                    w.Line("throw new InvalidOperationException($\"Stub-servern kunde inte starta på port {port} (är den upptagen?). Välj en annan med " + GenerateOptions.StubPortVariable + ".\", e);");
                }
                w.Line("_app = app;");
            }
            w.Line();
            w.Line("[OneTimeTearDown]");
            using (w.Block("public async Task StopAsync()"))
            {
                using (w.Block("if (_app is not null)"))
                {
                    w.Line("await _app.StopAsync();");
                    w.Line("await _app.DisposeAsync();");
                    w.Line("_app = null;");
                }
            }
            w.Line();
            using (w.Block("private static async Task HandleAsync(HttpContext context)"))
            {
                w.Line("var request = context.Request;");
                w.Line("var response = context.Response;");
                w.Line("response.Headers.AccessControlAllowOrigin = \"*\";");
                using (w.Block("if (HttpMethods.IsOptions(request.Method))"))
                {
                    w.Line("response.Headers.AccessControlAllowMethods = \"*\";");
                    w.Line("response.Headers.AccessControlAllowHeaders = \"*\";");
                    w.Line("response.StatusCode = StatusCodes.Status204NoContent;");
                    w.Line("return;");
                }
                using (w.Block("if (ApiMocks.TryMatch(request.Method, request.Path.Value ?? \"/\", out var mock))"))
                {
                    w.Line("response.StatusCode = mock.Status;");
                    using (w.Block("if (mock.Body.Length > 0)"))
                    {
                        w.Line("response.ContentType = \"application/json; charset=utf-8\";");
                        w.Line("await response.WriteAsync(mock.Body);");
                    }
                    w.Line("return;");
                }
                w.Line("response.StatusCode = StatusCodes.Status404NotFound;");
                w.Line("response.ContentType = \"text/plain; charset=utf-8\";");
                w.Line("await response.WriteAsync($\"uitestgen-stub: ingen mock för {request.Method} {request.Path}. Sökvägen finns inte i OpenAPI-specen.\");");
            }
        }
        return w.ToString();
    }
}
