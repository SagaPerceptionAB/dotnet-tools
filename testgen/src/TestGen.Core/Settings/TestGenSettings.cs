using TestGen.Core.Run;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace TestGen.Core.Settings;

/// <summary>Nycklarna per projekt i en miljö: adress, inloggning och tid. Hemligheter måste vara <c>${env:NAMN}</c>.</summary>
public sealed class EndpointSettings
{
    public string? Url { get; set; }
    public string? Token { get; set; }
    public Dictionary<string, string>? Headers { get; set; }
    public int? Timeout { get; set; }
}

/// <summary>En miljö i testgen.yaml. <c>apis</c> och <c>apps</c> har projektnamn som nycklar.</summary>
public sealed class EnvironmentSettings
{
    public bool ReadOnly { get; set; }
    public Dictionary<string, EndpointSettings?>? Apis { get; set; }
    public Dictionary<string, EndpointSettings?>? Apps { get; set; }
}

/// <summary>Testanvändarens claims (<c>auth.claims</c>): claim-typ till värde. Det som inte anges får fiktiva standardvärden.</summary>
public sealed class AuthSettings
{
    public Dictionary<string, string>? Claims { get; set; }
}

/// <summary>Inloggningssteget (<c>login</c>): operationen som ger en token, värdena den får och var token finns i svaret.</summary>
public sealed class LoginSettings
{
    public string? Operation { get; set; }
    public Dictionary<string, object>? Body { get; set; }
    public string? Token { get; set; }
}

/// <summary>Inställningar för ett API (nyckeln är projektnamnet).</summary>
public sealed class ApiSettings
{
    public string? Spec { get; set; }
    public string? Excel { get; set; }
    public string? Environment { get; set; }
    public List<string>? SkipOperations { get; set; }
    public Dictionary<string, Dictionary<string, object>>? Overrides { get; set; }
    public Dictionary<string, Dictionary<string, string>>? Bindings { get; set; }
    public Dictionary<string, string>? Settings { get; set; }
    public AuthSettings? Auth { get; set; }
    public LoginSettings? Login { get; set; }
}

/// <summary>Inställningar för en webbapp (nyckeln är projektnamnet).</summary>
public sealed class AppSettings
{
    public string? Environment { get; set; }
    public Dictionary<string, string>? Settings { get; set; }
    public AuthSettings? Auth { get; set; }
}

/// <summary>
/// Konfigurationsvärden för appen när den körs i testprocessen, med <c>${env:NAMN}</c> ersatt. <see cref="Secrets"/> är de värden som kom
/// från miljövariabler; de maskas i all utdata.
/// </summary>
public sealed record ProcessSettings(IReadOnlyDictionary<string, string> Values, IReadOnlyList<string> Secrets)
{
    public static ProcessSettings None { get; } = new(new Dictionary<string, string>(), []);
}

/// <summary>Inställningar för UI-testerna: valfria kontroller (<c>accessibility</c>, <c>screenshots</c>) och Playwright-spår.</summary>
public sealed class UiSettings
{
    public List<string>? Checks { get; set; }
    public bool Trace { get; set; }
}

internal sealed class SettingsFile
{
    public UiSettings? Ui { get; set; }
    public int? Seed { get; set; }
    public string? Locale { get; set; }
    public Dictionary<string, ApiSettings?>? Apis { get; set; }
    public Dictionary<string, AppSettings?>? Apps { get; set; }
    public Dictionary<string, string>? Hosts { get; set; }
    public Dictionary<string, EnvironmentSettings?>? Environments { get; set; }
}

/// <summary>
/// Innehållet i testgen.yaml, det enda som skrivs för hand. Filen behövs inte lokalt. Samma klass används av
/// <c>testgen run</c> och av testerna: <c>TestGenSettings.Load(katalog).Environment()</c> väljer miljön ur
/// <c>TESTGEN_ENV</c> (annars <c>local</c>) och ger adress, headers, token, tidsgräns och skrivskydd per projekt.
/// </summary>
public sealed class TestGenSettings
{
    public const string FileName = "testgen.yaml";
    public const string EnvironmentVariable = "TESTGEN_ENV";
    public const string LocalEnvironment = "local";

    private readonly SettingsFile _file;

    private TestGenSettings(SettingsFile file, string? path)
    {
        _file = file;
        Path = path is null ? null : System.IO.Path.GetFullPath(path);
        BaseDirectory = Path is null ? Directory.GetCurrentDirectory() : System.IO.Path.GetDirectoryName(Path)!;
        Hosts = HostPolicy.From(file.Hosts);
    }

    /// <summary>Fullständig sökväg till filen, eller null när ingen fil används.</summary>
    public string? Path { get; }

    /// <summary>Relativa sökvägar i filen utgår härifrån.</summary>
    public string BaseDirectory { get; }

    public int Seed => _file.Seed ?? new GeneratorOptions().Seed;
    public string Locale => _file.Locale ?? new GeneratorOptions().Locale;

    /// <summary>Värdpolicy för anrop som appen under test gör till andra system. Standard är att blockera.</summary>
    public HostPolicy Hosts { get; }

    /// <summary>Valfria kontroller i UI-testerna (<c>ui.checks</c>): <c>accessibility</c> och <c>screenshots</c>.</summary>
    public IReadOnlyList<string> UiChecks => _file.Ui?.Checks ?? [];

    /// <summary>Spela in Playwright-spår (<c>ui.trace</c>). Avstängt som standard, eftersom spåret kan innehålla känsliga uppgifter.</summary>
    public bool UiTrace => _file.Ui?.Trace ?? false;

    /// <summary>Miljöernas namn: <c>local</c> först, sedan de i filen i filens ordning.</summary>
    public IReadOnlyList<string> EnvironmentNames =>
        (_file.Environments?.Keys.Where(n => !n.Equals(LocalEnvironment, StringComparison.OrdinalIgnoreCase)) ?? []).Prepend(LocalEnvironment).ToList();

    /// <summary>Miljöer som är skrivskyddade, för visning.</summary>
    public bool IsReadOnly(string environment) =>
        _file.Environments.Named(environment)?.ReadOnly ?? false;

    /// <summary>Utan fil: standardvärden och bara miljön <c>local</c>.</summary>
    public static TestGenSettings Defaults { get; } = new(new SettingsFile(), null);

    /// <summary>Hittar testgen.yaml i katalogen eller närmaste katalog ovanför, till och med repots rot (där .git ligger). Null om den saknas.</summary>
    public static string? Find(string startDirectory)
    {
        for (var dir = new DirectoryInfo(System.IO.Path.GetFullPath(startDirectory)); dir is not null; dir = dir.Parent)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, FileName);
            if (File.Exists(candidate)) return candidate;
            if (System.IO.Path.Exists(System.IO.Path.Combine(dir.FullName, ".git"))) break;
        }
        return null;
    }

    /// <summary>
    /// Hittar testgen.yaml: i testgens testprojekt (<paramref name="testProjectDirectories"/>) eller i solutionens katalog och uppåt (se <see cref="Find"/>).
    /// Finns filen på flera ställen är det ett användningsfel som säger vilka. Null om den saknas.
    /// </summary>
    public static string? Locate(string solutionDirectory, IEnumerable<string> testProjectDirectories)
    {
        var found = testProjectDirectories.Select(d => System.IO.Path.Combine(System.IO.Path.GetFullPath(d), FileName)).Where(File.Exists)
            .Append(Find(solutionDirectory)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (found.Count > 1)
            throw new UsageException($"{FileName} finns på flera ställen: {string.Join(", ", found.Select(f => Terminal.Relative(f)))}.",
                "Behåll en av dem och ta bort de andra. Den hör helst hemma i testprojektet.");
        return found.FirstOrDefault();
    }

    /// <summary>Läser testgen.yaml som hittas från katalogen (se <see cref="Find"/>); finns ingen fil används standardvärdena.</summary>
    public static TestGenSettings Load(string solutionDirectory, IEnumerable<string>? testProjectDirectories = null) =>
        Locate(solutionDirectory, testProjectDirectories ?? []) is { } path ? LoadFile(path) : Defaults;

    /// <summary>Läser en utpekad fil. Okända nycklar, fel typer och hemligheter i klartext ger ett fel med radnummer.</summary>
    public static TestGenSettings LoadFile(string path)
    {
        if (!File.Exists(path)) throw new MissingSourceException(SourceKind.Config, System.IO.Path.GetFullPath(path));
        var text = FriendlyErrors.Guard(SourceKind.Config, path, () => File.ReadAllText(path));
        var shown = Terminal.Relative(path);
        List<SettingsProblem> problems;
        try { problems = SettingsValidator.Validate(text); }
        catch (YamlException e)
        {
            throw new UsageException($"{shown} rad {e.Start.Line}: filen är inte giltig YAML.",
                "Rätta raden och kör igen. Kontrollera indragningen (mellanslag, inte tabbar) och kolon efter nycklarna.", e.Message);
        }
        if (problems.Count > 0)
            throw new UsageException($"{shown} har {(problems.Count == 1 ? "ett fel" : problems.Count + " fel")}:\n" +
                string.Join("\n", problems.Select(p => $"  rad {p.Line}: {p.Message}")),
                problems.Select(p => p.Tip).FirstOrDefault(t => t is not null) ?? "Rätta raderna ovan och kör igen.");
        var file = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).IgnoreUnmatchedProperties().Build()
            .Deserialize<SettingsFile?>(text) ?? new SettingsFile();
        return new TestGenSettings(file, path);
    }

    /// <summary>Miljönamnet: <paramref name="name"/>, annars <c>TESTGEN_ENV</c>, annars <c>local</c>.</summary>
    public static string EnvironmentName(string? name, Func<string, string?>? getEnv = null)
    {
        getEnv ??= System.Environment.GetEnvironmentVariable;
        if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
        var fromEnv = getEnv(EnvironmentVariable);
        return string.IsNullOrWhiteSpace(fromEnv) ? LocalEnvironment : fromEnv.Trim();
    }

    /// <summary>
    /// Väljer miljö (se <see cref="EnvironmentName"/>). Okänd miljö ger ett fel som listar miljöerna. Miljön <c>local</c> finns alltid.
    /// <paramref name="getEnv"/> läser miljövariabler (för <c>${env:NAMN}</c> och <c>TESTGEN_ENV</c>); standard är processens.
    /// </summary>
    public TestEnvironment Environment(string? name = null, Func<string, string?>? getEnv = null)
    {
        getEnv ??= System.Environment.GetEnvironmentVariable;
        var chosen = EnvironmentName(name, getEnv);
        var known = _file.Environments?.Keys.FirstOrDefault(k => k.Equals(chosen, StringComparison.OrdinalIgnoreCase));
        if (known is null && !chosen.Equals(LocalEnvironment, StringComparison.OrdinalIgnoreCase))
            throw new UsageException($"Okänd miljö '{chosen}'." + Suggest.DidYouMean(chosen, EnvironmentNames) + $" Miljöer: {string.Join(", ", EnvironmentNames)}.",
                $"Välj en av dem med --env eller {EnvironmentVariable}, eller lägg till miljön under environments i {FileName}.");
        return new TestEnvironment(known ?? LocalEnvironment, (known is null ? null : _file.Environments![known]) ?? new EnvironmentSettings(), getEnv);
    }

    /// <summary>
    /// Inställningar för testdatan till API:et med projektnamnet <paramref name="api"/> (skiftlägesokänsligt). Utan namn och med bara ett API
    /// i filen används det.
    /// </summary>
    public GeneratorOptions GeneratorFor(string? api = null)
    {
        var apis = _file.Apis ?? [];
        var settings = api is not null ? apis.Named(api)
            : apis.Count == 1 ? apis.Values.First() : null;
        return new GeneratorOptions
        {
            Seed = Seed,
            Locale = Locale,
            Bindings = settings?.Bindings ?? new(),
            Overrides = settings?.Overrides ?? new(),
            SkipOperations = settings?.SkipOperations ?? new(),
        };
    }

    /// <summary>Konfigurationen för API-projektet i testprocessen (<c>apis.NAMN.settings</c>). Saknade miljövariabler ger ett fel.</summary>
    public ProcessSettings ApiProcessSettings(string api, Func<string, string?>? getEnv = null) =>
        Expand("apis", api, _file.Apis.Named(api)?.Settings, getEnv);

    /// <summary>Konfigurationen för webbappen i testprocessen (<c>apps.NAMN.settings</c>).</summary>
    public ProcessSettings AppProcessSettings(string app, Func<string, string?>? getEnv = null) =>
        Expand("apps", app, _file.Apps.Named(app)?.Settings, getEnv);

    private static ProcessSettings Expand(string section, string project, Dictionary<string, string>? settings, Func<string, string?>? getEnv)
    {
        if (settings is not { Count: > 0 }) return ProcessSettings.None;
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var secrets = new List<string>();
        foreach (var (key, value) in settings)
        {
            var text = value ?? "";
            var expanded = EnvVars.Expand(text, $"{section}.{project}.settings, {key}", getEnv);
            values[key] = expanded;
            if (EnvVars.HasPlaceholder(text)) secrets.Add(expanded);
        }
        return new ProcessSettings(values, secrets);
    }

    /// <summary>Miljön (<c>environment</c>) appen körs i i testprocessen enligt filen, eller null.</summary>
    public string? ApiEnvironment(string api) => _file.Apis.Named(api)?.Environment;

    /// <summary>Miljön webbappen körs i i testprocessen enligt filen, eller null.</summary>
    public string? AppEnvironment(string app) => _file.Apps.Named(app)?.Environment;
    /// <summary>Claims för testanvändaren i API:et (<c>apis.NAMN.auth.claims</c>); tom när inga anges.</summary>
    public IReadOnlyDictionary<string, string> ApiClaims(string api) => _file.Apis.Named(api)?.Auth?.Claims ?? new Dictionary<string, string>();

    /// <summary>Claims för testanvändaren i webbappen (<c>apps.NAMN.auth.claims</c>); tom när inga anges.</summary>
    public IReadOnlyDictionary<string, string> AppClaims(string app) => _file.Apps.Named(app)?.Auth?.Claims ?? new Dictionary<string, string>();

    /// <summary>Inloggningssteget för API:et (<c>apis.NAMN.login</c>), eller null.</summary>
    public LoginSettings? LoginOf(string api) => _file.Apis.Named(api)?.Login;

    /// <summary>Excel-filen med egna tester som anges för API:et i filen (fullständig sökväg), eller null.</summary>
    public string? ExcelOf(string api)
    {
        var excel = _file.Apis.Named(api)?.Excel;
        return excel is null ? null : System.IO.Path.GetFullPath(excel, BaseDirectory);
    }

    /// <summary>Specen som anges för API:et i filen (fullständig sökväg eller webbadress), eller null.</summary>
    public string? SpecOf(string api)
    {
        var spec = _file.Apis.Named(api)?.Spec;
        return spec is null ? null : Sources.IsUrl(spec) || System.IO.Path.IsPathRooted(spec) ? spec : System.IO.Path.GetFullPath(spec, BaseDirectory);
    }
}
