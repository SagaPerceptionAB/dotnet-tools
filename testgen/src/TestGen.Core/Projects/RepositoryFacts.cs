using TestGen.Core.Settings;
using TestGen.Core.Views;

namespace TestGen.Core.Projects;

/// <summary>Det som finns i repot: projekten i solutionen och hur API:erna, apparna och specarna hänger ihop. Läses utan MSBuild.</summary>
public sealed class RepositoryFacts
{
    private RepositoryFacts(string solutionPath, IReadOnlyList<ProjectFacts> projects)
    {
        SolutionPath = solutionPath;
        Projects = projects;
    }

    /// <summary>Fullständig sökväg till solutionen (eller projektet).</summary>
    public string SolutionPath { get; }

    public string SolutionDirectory => Path.GetDirectoryName(SolutionPath)!;

    /// <summary>Alla projekt i solutionen, sorterade på namn.</summary>
    public IReadOnlyList<ProjectFacts> Projects { get; }

    public IEnumerable<ProjectFacts> Apis => Projects.Where(p => p.Kind == ProjectKind.Api);
    public IEnumerable<ProjectFacts> WebApps => Projects.Where(p => p.Kind == ProjectKind.Web);
    public IEnumerable<ProjectFacts> TestProjects => Projects.Where(p => p.Kind == ProjectKind.Test);

    /// <summary>Testprojekt som använder testgen (<c>SagaPerception.TestGen.NUnit</c>).</summary>
    public IEnumerable<ProjectFacts> TestGenProjects => TestProjects.Where(p => p.UsesTestGen);

    /// <summary>Något projekt behöver en webbläsare: det använder Playwright eller har UI-tester (<c>ViewTests</c>) från testgen.</summary>
    public bool NeedsChromium => Projects.Any(p => p.UsesPlaywright) || TestGenProjects.Any(p => TestTargetsOf(p).Any(t => t.Layer == "ui"));

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<TestTarget>> _targets = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Vad testklasserna i projektet pekar ut. Källfilerna läses en gång per <see cref="RepositoryFacts"/>.</summary>
    public IReadOnlyList<TestTarget> TestTargetsOf(ProjectFacts project) => _targets.GetOrAdd(project.Path, _ => project.TestTargets());

    public static RepositoryFacts Read(string solutionPath)
    {
        var full = Path.GetFullPath(solutionPath);
        if (!File.Exists(full)) throw UsageException.MissingFile("solutionen", full);
        var projects = SolutionFile.ProjectPaths(full).Select(ProjectFacts.Read)
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return new RepositoryFacts(full, projects);
    }

    public ProjectFacts? Project(string name) => Projects.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Specen som hör till API-projektet (fullständig sökväg), eller null: först <c>&lt;projekt&gt;.openapi.*</c> i testgens testprojekt,
    /// sedan det <see cref="SpecLocator"/> hittar vid projektet, sist den som bygget skriver.
    /// </summary>
    public string? SpecOf(ProjectFacts api) => SpecInTestProject(api) ?? SpecLocator.Find(api, SolutionDirectory, Apis.Count());

    private string? SpecInTestProject(ProjectFacts api) =>
        TestGenProjects.SelectMany(p => ProjectFacts.SpecsIn(p.Directory))
            .FirstOrDefault(spec => Path.GetFileName(spec).StartsWith(api.Name + ".openapi.", StringComparison.OrdinalIgnoreCase));

    /// <summary>Läser testgen.yaml: i <paramref name="testProject"/> (eller i testgens testprojekt) eller vid solutionen och uppåt. Se <see cref="TestGenSettings.Locate"/>.</summary>
    public TestGenSettings LoadSettings(string? testProject = null) =>
        TestGenSettings.Load(SolutionDirectory,
            (testProject is null ? TestGenProjects : TestProjects.Where(p => p.Name.Equals(testProject, StringComparison.OrdinalIgnoreCase))).Select(p => p.Directory));

    /// <summary>
    /// <c>views.json</c> som <c>testgen views</c> skrev: i ett testprojekt som använder testgen, annars i solutionens katalog.
    /// Null om ingen sådan fil finns.
    /// </summary>
    public string? ViewsFilePath => ViewsLocator.Existing(this);

    /// <summary>Sammanfattningen av appens vyer i <c>views.json</c>, eller null om filen eller appen saknas eller filen inte går att läsa.</summary>
    public ViewsSummary? ViewsOf(ProjectFacts app) => ViewsSummary.From(ReadViews(), app.Name);

    /// <summary>Innehållet i <c>views.json</c>, eller null om filen saknas eller inte går att läsa. Läs en gång och använd för flera appar.</summary>
    public ViewsFile? ReadViews() => ViewsFilePath is { } path ? ViewsJson.TryRead(path) : null;
}

/// <summary>Var <c>views.json</c> ligger.</summary>
public static class ViewsLocator
{
    /// <summary>Filen som finns: i det första testprojektet för testgen som har en, annars i solutionens katalog.</summary>
    public static string? Existing(RepositoryFacts facts) => Candidates(facts).FirstOrDefault(File.Exists);

    /// <summary>Där filen skrivs som standard: i testprojektet för testgen om ett sådant finns, annars i <paramref name="fallbackDirectory"/>.</summary>
    public static string Default(RepositoryFacts facts, string fallbackDirectory) =>
        Path.Combine(facts.TestGenProjects.FirstOrDefault()?.Directory ?? fallbackDirectory, ViewsJson.FileName);

    private static IEnumerable<string> Candidates(RepositoryFacts facts) =>
        facts.TestGenProjects.Select(p => p.Directory).Append(facts.SolutionDirectory).Select(d => Path.Combine(d, ViewsJson.FileName));
}

/// <summary>Kopplar en incheckad OpenAPI-spec till ett API-projekt.</summary>
public static class SpecLocator
{
    /// <summary>
    /// Specen i API-projektets egen katalog; annars en spec i en katalog ovanför, upp till solutionens katalog, vars namn matchar
    /// projektet (<c>shop.openapi.yaml</c> hör till <c>ShopApi</c>, skiftlägesokänsligt, utan <c>Api</c>-suffix). I solutionens
    /// katalog räcker det att det bara finns ett API-projekt och en spec. Fullständig sökväg, eller null.
    /// </summary>
    public static string? Find(ProjectFacts api, string solutionDirectory, int apiProjectCount)
    {
        if (api.Specs.Count > 0) return api.Specs[0];
        var solution = Path.GetFullPath(solutionDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var key = Key(api.Name);
        var dirs = new List<string>();
        for (var dir = new DirectoryInfo(api.Directory).Parent; dir is not null; dir = dir.Parent)
        {
            dirs.Add(dir.FullName);
            if (dir.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Equals(solution, StringComparison.OrdinalIgnoreCase)) break;
            if (dir.FullName == dir.Root.FullName) { dirs.Clear(); break; }
        }
        if (dirs.Count == 0) dirs.Add(solution);   // projektet ligger utanför solutionens katalog
        foreach (var dir in dirs)
        {
            if (!System.IO.Directory.Exists(dir)) continue;
            var specs = ProjectFacts.SpecsIn(dir);
            if (specs.FirstOrDefault(x => Key(SpecName(x)).Equals(key, StringComparison.OrdinalIgnoreCase)) is { } match) return match;
            if (dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Equals(solution, StringComparison.OrdinalIgnoreCase)
                && apiProjectCount == 1 && specs.Count == 1) return specs[0];
        }
        return api.GeneratedSpec is { } generated && File.Exists(generated) ? generated : null;
    }

    /// <summary>Filnamnet vi föreslår när specen saknas: <c>&lt;projektnamn&gt;.openapi.json</c>.</summary>
    public static string SuggestedName(ProjectFacts api) => api.Name + ".openapi.json";

    /// <summary>Vad man gör när specen saknas, på en rad (efter en mening som säger att den saknas).</summary>
    public static string MissingAdvice(ProjectFacts api, string? settingsKey = null)
    {
        var how = api.GeneratedSpec is { } generated
            ? $"Bygg projektet (bygget skriver specen till {System.IO.Path.GetRelativePath(api.Directory, generated).Replace(System.IO.Path.DirectorySeparatorChar, (char)47)}), "
            : $"Lägg {SuggestedName(api)} i testprojektet, ";
        return how + "ange den med --spec <fil|url>" + (settingsKey is null ? "" : $", eller sätt {settingsKey} i {Settings.TestGenSettings.FileName}")
            + (api.ServesSpec && api.GeneratedSpec is null ? ". Appen serverar sin spec själv: testgen init hämtar den och sparar den i testprojektet" : "") + ".";
    }

    /// <summary>"ShopApi" och "Shop.Api" blir "Shop", "shop.openapi.yaml" blir "shop".</summary>
    private static string Key(string name)
    {
        foreach (var suffix in new[] { ".Api", "Api" })
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return name[..^suffix.Length];
        return name;
    }

    private static string SpecName(string path)
    {
        var name = Path.GetFileName(path);
        var at = name.IndexOf(".openapi.", StringComparison.OrdinalIgnoreCase);
        return at > 0 ? name[..at] : Path.GetFileNameWithoutExtension(name);
    }
}

/// <summary>Finns Playwrights Chromium på datorn? Ser efter i standardkatalogen, inte i något projekt.</summary>
public static class ChromiumLocator
{
    public static bool IsInstalled() => IsInstalled(Environment.GetEnvironmentVariable, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetEnvironmentVariable("LOCALAPPDATA"));

    /// <summary>Katalogen med webbläsare: <c>PLAYWRIGHT_BROWSERS_PATH</c>, annars standardplatsen för operativsystemet.</summary>
    public static string BrowsersDirectory(Func<string, string?> env, string home, string? localAppData)
    {
        if (env("PLAYWRIGHT_BROWSERS_PATH") is { Length: > 0 } custom && custom != "0") return custom;
        if (OperatingSystem.IsWindows()) return Path.Combine(localAppData ?? Path.Combine(home, "AppData", "Local"), "ms-playwright");
        return OperatingSystem.IsMacOS() ? Path.Combine(home, "Library", "Caches", "ms-playwright") : Path.Combine(home, ".cache", "ms-playwright");
    }

    public static bool IsInstalled(Func<string, string?> env, string home, string? localAppData)
    {
        var dir = BrowsersDirectory(env, home, localAppData);
        return Directory.Exists(dir) && Directory.EnumerateDirectories(dir)
            .Any(d => Path.GetFileName(d).StartsWith("chromium-", StringComparison.OrdinalIgnoreCase)
                   || Path.GetFileName(d).StartsWith("chromium_headless_shell-", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Det statusvyn behöver ur <c>views.json</c> (skrivs av <c>testgen views</c>): antal vyer, sort och vilka API:er appen anropar.</summary>
public sealed record ViewsSummary(int ViewCount, string? Kind, IReadOnlyList<ViewsApi> Apis)
{
    /// <summary>Sammanfattningen för appen, eller null om filen saknas, inte går att läsa eller inte har appen.</summary>
    public static ViewsSummary? Read(string? path, string app) => From(path is null ? null : ViewsJson.TryRead(path), app);

    public static ViewsSummary? From(ViewsFile? file, string app)
    {
        if (file is null) return null;
        if (file.Apps.FirstOrDefault(a => a.Project.Equals(app, StringComparison.OrdinalIgnoreCase)) is not { } found) return null;
        var kinds = found.Views.Select(v => v.Kind).Distinct().Order().Select(k => k.Name()).ToList();
        return new ViewsSummary(found.Views.Count, kinds.Count == 0 ? null : string.Join(", ", kinds), found.Apis);
    }
}
