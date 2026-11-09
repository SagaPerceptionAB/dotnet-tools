using System.Text.Json;

namespace TestGen.Core.Projects;

/// <summary>Vad ett projekt är. Ett testprojekt är aldrig ett API eller en webbapp.</summary>
public enum ProjectKind
{
    Library,

    /// <summary>Webbprojekt med controllers, ett OpenAPI-paket eller en incheckad spec.</summary>
    Api,

    /// <summary>Övriga webbprojekt (Blazor, Razor Pages).</summary>
    Web,
    Test,
}

/// <summary>Ett API eller en app som en testklass pekar ut med projektnamn. <paramref name="Layer"/> är <c>api</c> eller <c>ui</c>.</summary>
public sealed record TestTarget(string Layer, string Project) : IComparable<TestTarget>
{
    public int CompareTo(TestTarget? other) =>
        other is null ? 1 : (string.CompareOrdinal(Layer, other.Layer) is var l and not 0 ? l : string.CompareOrdinal(Project, other.Project));
}

/// <summary>Fakta om ett projekt, utläst ur <c>.csproj</c>, <c>launchSettings.json</c> och <c>appsettings*.json</c> utan MSBuild.</summary>
public sealed record ProjectFacts(
    string Name,
    string Path,
    ProjectKind Kind,
    string? TestFramework,
    IReadOnlyList<string> ProjectReferences,
    IReadOnlyList<string> Packages,
    string? LocalUrl,
    string? LaunchEnvironment,
    IReadOnlyDictionary<string, string> Settings,
    IReadOnlyList<string> Specs,
    string? GeneratedSpec = null)
{
    public static readonly string[] SpecPatterns = ["*.openapi.yaml", "*.openapi.yml", "*.openapi.json"];

    private static readonly string[] OpenApiPackages = ["Microsoft.AspNetCore.OpenApi", "Swashbuckle.AspNetCore", "NSwag.AspNetCore"];

    /// <summary>Specen är den som bygget skriver (<c>GeneratedSpec</c>): den jämförs inte med appens spec, och finns först efter bygget.</summary>
    public bool IsGenerated(string specPath) =>
        GeneratedSpec is not null && System.IO.Path.GetFullPath(specPath).Equals(System.IO.Path.GetFullPath(GeneratedSpec), StringComparison.OrdinalIgnoreCase);

    public string Directory => System.IO.Path.GetDirectoryName(Path)!;

    public bool IsApp => Kind is ProjectKind.Api or ProjectKind.Web;
    /// <summary>Projektet har ett OpenAPI-paket och serverar därför sin spec själv (code-first).</summary>
    public bool ServesSpec => Packages.Any(p => OpenApiPackages.Any(o => p.StartsWith(o, StringComparison.OrdinalIgnoreCase)));
    public bool UsesPlaywright => Packages.Any(p => p.StartsWith("Microsoft.Playwright", StringComparison.OrdinalIgnoreCase));
    /// <summary>Projektet använder testgens NUnit-bibliotek (som paket, eller som projekt i samma repo).</summary>
    public bool UsesTestGen => Packages.Concat(ProjectReferences).Any(p => p.StartsWith("SagaPerception.Testing", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Vad testklasserna pekar ut: <c>ApiTests("ShopApi")</c> ger ("api", "ShopApi") och <c>ViewTests("ShopWeb")</c> ger ("ui", "ShopWeb").
    /// Läses som text ur .cs-filerna i projektet (inte bin och obj), sorterat och utan dubbletter.
    /// </summary>
    public IReadOnlyList<TestTarget> TestTargets()
    {
        var found = new SortedSet<TestTarget>();
        foreach (var file in SourceFiles(Directory))
        {
            string text;
            try
            {
                if (new FileInfo(file).Length > JsonFiles.MaxBytes) continue;
                text = File.ReadAllText(file);
            }
            catch (IOException) { continue; }
            foreach (System.Text.RegularExpressions.Match m in TestClass.Matches(text))
                found.Add(new TestTarget(m.Groups[1].Value == "ApiTests" ? "api" : "ui", m.Groups[2].Value));
        }
        return found.ToList();
    }

    private static readonly string[] SkippedDirectories = ["bin", "obj", "node_modules"];

    /// <summary>Alla <c>*.cs</c> under katalogen. Katalogerna bin, obj, node_modules och de som börjar med punkt går inte igenom alls.</summary>
    private static IEnumerable<string> SourceFiles(string root)
    {
        var pending = new Stack<string>([root]);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            IEnumerable<string> files, subdirectories;
            try
            {
                files = System.IO.Directory.EnumerateFiles(dir, "*.cs");
                subdirectories = System.IO.Directory.EnumerateDirectories(dir)
                    .Where(d => System.IO.Path.GetFileName(d) is var n && !n.StartsWith('.') && !SkippedDirectories.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var file in files) yield return file;
            foreach (var sub in subdirectories) pending.Push(sub);
        }
    }

    private static readonly System.Text.RegularExpressions.Regex TestClass = new(@"\b(ApiTests|ViewTests)\(\s*""([^""]+)""\s*\)", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));

    /// <summary>Läser fakta om projektet. Delar som saknas eller inte går att tolka blir tomma, inga fel.</summary>
    public static ProjectFacts Read(string projectPath)
    {
        var full = System.IO.Path.GetFullPath(projectPath);
        var dir = System.IO.Path.GetDirectoryName(full)!;
        var root = XmlFiles.Load(full)?.Root;

        var sdk = (string?)root?.Attribute("Sdk") ?? "";
        var packages = root?.Descendants("PackageReference").Select(p => (string?)p.Attribute("Include") ?? (string?)p.Attribute("Update")).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
        var references = root?.Descendants("ProjectReference").Select(p => (string?)p.Attribute("Include")).OfType<string>()
            .Select(p => System.IO.Path.GetFileNameWithoutExtension(p.Replace('\\', '/')))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
        var testFramework = TestFrameworkOf(packages);
        var isTest = testFramework is not null
            || string.Equals(root?.Descendants("IsTestProject").FirstOrDefault()?.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        var specs = SpecsIn(dir);
        var profile = LaunchProfileOf(dir);
        var isWeb = sdk.Equals("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase);
        var isApi = isWeb && (System.IO.Directory.Exists(System.IO.Path.Combine(dir, "Controllers")) || specs.Count > 0
            || packages.Any(p => OpenApiPackages.Any(o => p.StartsWith(o, StringComparison.OrdinalIgnoreCase))));
        var kind = isTest ? ProjectKind.Test : isApi ? ProjectKind.Api : isWeb ? ProjectKind.Web : ProjectKind.Library;

        return new ProjectFacts(System.IO.Path.GetFileNameWithoutExtension(full), full, kind, testFramework, references, packages,
            profile.Url, profile.Environment, AppSettings.Read(dir), specs, BuildSpec.PathOf(root, full, packages));
    }

    /// <summary>Incheckade specar direkt i katalogen (inte i bin eller obj), som fulla sökvägar i bokstavsordning.</summary>
    public static IReadOnlyList<string> SpecsIn(string dir) =>
        SpecPatterns.SelectMany(pattern => System.IO.Directory.EnumerateFiles(dir, pattern))
            .Order(StringComparer.OrdinalIgnoreCase).ToList();

    private static string? TestFrameworkOf(IReadOnlyList<string> packages)
    {
        if (packages.Any(p => p.Equals("NUnit", StringComparison.OrdinalIgnoreCase))) return "nunit";
        if (packages.Any(p => p.StartsWith("xunit", StringComparison.OrdinalIgnoreCase))) return "xunit";
        if (packages.Any(p => p.StartsWith("MSTest", StringComparison.OrdinalIgnoreCase))) return "mstest";
        return null;
    }

    /// <summary>
    /// Adressen och miljön ur första profilen i <c>Properties/launchSettings.json</c> som har en http-adress. Har ingen profil det används
    /// första profilen som anger en miljö. Delar som saknas blir null.
    /// </summary>
    public static LaunchProfile LaunchProfileOf(string projectDirectory)
    {
        using var document = JsonFiles.Load(System.IO.Path.Combine(projectDirectory, "Properties", "launchSettings.json"));
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("profiles", out var profiles) || profiles.ValueKind != JsonValueKind.Object) return new LaunchProfile(null, null);
        LaunchProfile? onlyEnvironment = null;
        foreach (var profile in profiles.EnumerateObject())
        {
            if (profile.Value.ValueKind != JsonValueKind.Object) continue;
            var environment = profile.Value.TryGetProperty("environmentVariables", out var variables) && variables.ValueKind == JsonValueKind.Object
                && variables.TryGetProperty("ASPNETCORE_ENVIRONMENT", out var name) && name.ValueKind == JsonValueKind.String && name.GetString() is { Length: > 0 } text ? text : null;
            var http = profile.Value.TryGetProperty("applicationUrl", out var urls) && urls.ValueKind == JsonValueKind.String
                ? urls.GetString()!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .FirstOrDefault(u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                : null;
            if (http is not null) return new LaunchProfile(http, environment);
            if (environment is not null) onlyEnvironment ??= new LaunchProfile(null, environment);
        }
        return onlyEnvironment ?? new LaunchProfile(null, null);
    }

    /// <summary>
    /// Miljön appen körs i i testprocessen: <paramref name="configured"/> (<c>environment</c> i testgen.yaml), annars <c>Testing</c> om
    /// <c>appsettings.Testing.json</c> finns, annars launch-profilens miljö, annars <c>Development</c>.
    /// </summary>
    public string EnvironmentFor(string? configured) =>
        configured is { Length: > 0 } ? configured
        : File.Exists(System.IO.Path.Combine(Directory, "appsettings.Testing.json")) ? "Testing"
        : RunEnvironment;

    /// <summary>Miljön när utvecklaren kör appen lokalt: launch-profilens, annars Development.</summary>
    public string RunEnvironment => LaunchEnvironment ?? "Development";
}

/// <summary>Adressen och miljön i en launch-profil.</summary>
public sealed record LaunchProfile(string? Url, string? Environment);

/// <summary>Läser JSON ur repot som opålitlig indata: storleksgräns, kommentarer och släpande kommatecken tillåts.</summary>
internal static class JsonFiles
{
    public const long MaxBytes = 2 * 1024 * 1024;

    /// <summary>Dokumentet, eller null om filen saknas, är för stor eller inte är JSON. Anroparen kasserar det.</summary>
    public static JsonDocument? Load(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxBytes) return null;
            return JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary><c>appsettings.json</c> och <c>appsettings.Development.json</c> utplattade till nycklar som <c>ShopApi:BaseUrl</c>.</summary>
public static class AppSettings
{
    /// <summary>Nyckel till värde, sorterat på nyckel. Development vinner över grundfilen. Listor får index (<c>Urls:0</c>).</summary>
    public static IReadOnlyDictionary<string, string> Read(string projectDirectory)
    {
        var settings = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in new[] { "appsettings.json", "appsettings.Development.json" })
        {
            using var document = JsonFiles.Load(System.IO.Path.Combine(projectDirectory, file));
            if (document is not null) Flatten(document.RootElement, "", settings);
        }
        return settings;
    }

    private static void Flatten(JsonElement element, string prefix, SortedDictionary<string, string> into)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                    Flatten(property.Value, prefix.Length == 0 ? property.Name : prefix + ":" + property.Name, into);
                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray()) Flatten(item, prefix + ":" + index++, into);
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                break;
            default:
                if (prefix.Length > 0) into[prefix] = element.ToString();
                break;
        }
    }
}

/// <summary><c>global.json</c> utläst som opålitlig indata.</summary>
public static class GlobalJson
{
    /// <summary>Värdet av <c>test.runner</c>, eller null om filen eller nyckeln saknas.</summary>
    public static string? TestRunner(string path)
    {
        using var document = JsonFiles.Load(path);
        return document is not null && document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("test", out var test) && test.ValueKind == JsonValueKind.Object
            && test.TryGetProperty("runner", out var runner) && runner.ValueKind == JsonValueKind.String ? runner.GetString() : null;
    }
}
