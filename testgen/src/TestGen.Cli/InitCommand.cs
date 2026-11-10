using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TestGen.Core.Projects;
using TestGen.Core.Run;
using TestGen.Core.Views;

namespace TestGen.Cli;

/// <param name="Restore">Kör <c>dotnet restore</c> när vyerna läses.</param>
/// <param name="InstallChromium">Installerar Chromium om en webbapp finns och den saknas.</param>
/// <param name="ChromiumInstaller">Själva installationen, utbytbar i tester (så att ett misslyckande går att prova utan nätverk). Standard: Playwrights installation.</param>
/// <param name="LibraryProject">
/// Intern möjlighet (miljövariabeln <c>TESTGEN_LIBRARY_PROJECT</c>): sökväg till SagaPerception.TestGen.NUnit.csproj. Testprojektet får då en
/// ProjectReference dit i stället för paketet. Används av repots egna tester: ett paket från en lokal feed hamnar kvar i NuGet-cachen
/// och skulle ge gammal kod nästa gång.
/// </param>
internal sealed record InitOptions(bool Restore = true, bool InstallChromium = true, string? LibraryProject = null, Func<ChromiumInstallResult>? ChromiumInstaller = null)
{
    public const string LibraryProjectVariable = "TESTGEN_LIBRARY_PROJECT";
}

/// <summary>Resultatet av en Chromium-installation: lyckades den, och allt Playwright skrev (stdout, stderr och undantag).</summary>
internal sealed record ChromiumInstallResult(bool Succeeded, string Output);

/// <summary>
/// testgen init: skapar testprojektet (en rad per API och webbapp), lägger det i solutionen, skriver views.json och installerar Chromium.
/// Det som redan finns lämnas orört. Köras igen ändrar kommandot inget.
/// </summary>
internal static partial class InitCommand
{
    private const string PackageId = "SagaPerception.TestGen.NUnit";

    /// <summary>Paketen testprojektet behöver förutom testgen, med versionerna verktyget är byggt med.</summary>
    private static readonly string[] TestPackages = ["Microsoft.NET.Test.Sdk", "NUnit", "NUnit3TestAdapter"];

    public static async Task<int> RunAsync(string[] args, Terminal terminal, CancellationToken ct = default)
    {
        var a = CommandArgs.Parse("init", args, ["solution"], "no-restore");
        var solution = a.Get("solution") ?? SolutionFinder.Find(Directory.GetCurrentDirectory(), "testgen init");
        var options = new InitOptions(!a.Flag("no-restore"), true, Environment.GetEnvironmentVariable(InitOptions.LibraryProjectVariable) is { Length: > 0 } p ? p : null);
        return await RunAsync(solution, options, terminal, ct);
    }

    public static async Task<int> RunAsync(string solution, InitOptions options, Terminal terminal, CancellationToken ct = default)
    {
        var facts = RepositoryFacts.Read(solution);
        if (!facts.Apis.Any() && !facts.WebApps.Any())
            throw new UsageException($"Hittade varken API eller webbapp i {Path.GetFileName(facts.SolutionPath)}.",
                "testgen letar efter webbprojekt (Microsoft.NET.Sdk.Web) i solutionen. Peka på rätt solution med --solution.");

        var changes = new List<string>();
        var existing = facts.TestGenProjects.FirstOrDefault();
        var testDirectory = existing?.Directory ?? Path.Combine(facts.SolutionDirectory, "tests", Path.GetFileNameWithoutExtension(facts.SolutionPath) + ".Tests");
        var testProject = existing?.Path ?? Path.Combine(testDirectory, Path.GetFileName(testDirectory) + ".csproj");
        if (existing is null && Directory.Exists(testDirectory) && Directory.EnumerateFiles(testDirectory, "*.csproj").Any())
            throw new UsageException($"{Terminal.Relative(testDirectory)} finns redan men använder inte testgen.",
                $"Lägg till paketet {PackageId} och en rad per API och app (public sealed class MinaTester() : ApiTests(\"MittApi\");) i det projektet, eller välj en annan solution.");

        var done = existing is null ? [] : facts.TestTargetsOf(existing);
        var apis = new List<ProjectFacts>();
        var specFailures = 0;
        foreach (var api in facts.Apis.Where(x => !done.Contains(new TestTarget("api", x.Name))))
        {
            switch (await EnsureSpecAsync(facts, api, testDirectory, terminal, ct))
            {
                case true: apis.Add(api); break;
                case false: specFailures++; break;
            }
        }
        var apps = facts.WebApps.Where(x => !done.Contains(new TestTarget("ui", x.Name))).ToList();
        var references = facts.Apis.Concat(facts.WebApps).Where(x => !(existing?.ProjectReferences.Contains(x.Name, StringComparer.OrdinalIgnoreCase) ?? false)).ToList();

        var platform = UsesTestingPlatform(facts.SolutionDirectory);
        var centrally = CentralPackages(testDirectory);
        var version = ToolVersion();
        if (existing is null)
        {
            var framework = FrameworkOf(facts);
            Directory.CreateDirectory(testDirectory);
            await File.WriteAllTextAsync(testProject, Csproj(testDirectory, framework, references, centrally is not null, version, options.LibraryProject, facts.WebApps.Any(), platform), ct);
            terminal.Confirm(testProject, "testprojekt");
            changes.Add(testProject);
            if (centrally is not null && AddPackageVersions(centrally, version, options.LibraryProject is null) is { Count: > 0 } added)
            {
                terminal.Confirm(centrally, $"paketversioner: {string.Join(", ", added)}");
                changes.Add(centrally);
            }
            await AddToSolutionAsync(facts.SolutionPath, testProject, terminal, ct);
        }
        else if (references.Count > 0)
        {
            await Dotnet.RunAsync(["add", testProject, "reference", .. references.Select(r => r.Path)], ct);
            terminal.Confirm(testProject, $"referens till {string.Join(", ", references.Select(r => r.Name))}");
            changes.Add(testProject);
        }

        var ns = Namespace(Path.GetFileNameWithoutExtension(testProject));
        foreach (var (project, template) in apis.Select(x => (x, "ApiTests")).Concat(apps.Select(x => (x, "ViewTests"))))
        {
            var className = TestClassName(project.Name, template);
            var file = Path.Combine(testDirectory, className + ".cs");
            if (File.Exists(file))
            {
                terminal.Warning($"{Terminal.Relative(file)} finns redan och lämnades orörd. Den behöver {template}(\"{project.Name}\") för att {project.Name} ska testas.");
                continue;
            }
            await File.WriteAllTextAsync(file, TestClass(ns, className, template, project.Name), ct);
            terminal.Confirm(file, $"{(template == "ApiTests" ? "API" : "UI")}-tester för {project.Name}");
            changes.Add(file);
        }

        if (facts.WebApps.Any())
        {
            var views = await ViewsCommand.WriteAsync(facts, Path.Combine(testDirectory, ViewsJson.FileName), options.Restore, null, terminal, ct);
            if (views.Changed) changes.Add(Path.Combine(testDirectory, ViewsJson.FileName));
            if (existing is not null && EnsureViewsCopied(testProject)) { terminal.Confirm(testProject, "views.json kopieras till utdata"); changes.Add(testProject); }
            if (options.InstallChromium && !ChromiumLocator.IsInstalled())
            {
                terminal.Err.WriteLine("Installerar Chromium ...");
                if (InstallChromium(terminal, options.ChromiumInstaller ?? RunPlaywrightInstall)) changes.Add("chromium");
            }
        }

        var relativeTests = Terminal.Relative(testDirectory, facts.SolutionDirectory);
        if (changes.Count == 0) terminal.Err.WriteLine($"Allt finns redan i {Terminal.Relative(testDirectory)}. Inget ändrades.");
        terminal.Out.Write(CiStep(relativeTests, FrameworkOf(facts) ?? "net10.0", facts.WebApps.Any(), platform));
        terminal.Out.WriteLine();
        terminal.Out.WriteLine("Nästa steg: " + (platform ? TestCommand(relativeTests, true) : "dotnet test"));
        return specFailures > 0 ? ExitCodes.Usage : ExitCodes.Ok;
    }

    /// <summary>
    /// Finns specen (eller bygget skriver den) är allt klart. Annars hämtas den från en app som serverar den själv och sparas i testprojektet
    /// (true), eller så sägs det (false: appen startade inte, ett fel; null: appen serverar ingen spec, en varning).
    /// </summary>
    private static async Task<bool?> EnsureSpecAsync(RepositoryFacts facts, ProjectFacts api, string testDirectory, Terminal terminal, CancellationToken ct)
    {
        if (facts.SpecOf(api) is not null || api.GeneratedSpec is not null) return true;
        if (!api.ServesSpec)
        {
            terminal.Warning($"{api.Name} saknar spec, så inga API-tester skapades för det. {SpecLocator.MissingAdvice(api)} Kör sedan testgen init igen.");
            return null;
        }
        terminal.Err.WriteLine($"Startar {api.Name} för att hämta specen ...");
        var (json, error) = await AppSpec.FetchAsync(api, ct);
        if (json is null)
        {
            terminal.Err.WriteLine($"Fel: {error}");
            terminal.Err.WriteLine($"{api.Name} fick inga API-tester. {SpecLocator.MissingAdvice(api)} Kör sedan testgen init igen.");
            return false;
        }
        Directory.CreateDirectory(testDirectory);
        var file = Path.Combine(testDirectory, SpecLocator.SuggestedName(api));
        await File.WriteAllTextAsync(file, json, ct);
        terminal.Confirm(file, $"spec för {api.Name}, hämtad från appen");
        return true;
    }

    // --- Filerna ---

    /// <summary>Target framework ur första API:ets eller appens projektfil. Null om projekten inte anger något själva (Directory.Build.props gör det).</summary>
    private static string? FrameworkOf(RepositoryFacts facts) =>
        facts.Apis.Concat(facts.WebApps).Select(p => XDocument.Load(p.Path).Descendants("TargetFramework").FirstOrDefault()?.Value.Trim()).FirstOrDefault(f => !string.IsNullOrEmpty(f));

    private static string Csproj(string directory, string? framework, IReadOnlyList<ProjectFacts> references, bool centrally, string version, string? libraryProject, bool views, bool platform)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">").AppendLine();
        if (libraryProject is not null)
            sb.AppendLine($"  <Import Project=\"{Path.Combine(Path.GetDirectoryName(Path.GetFullPath(libraryProject))!, "build", PackageId + ".props")}\" />").AppendLine();
        sb.AppendLine("  <PropertyGroup>");
        if (framework is not null) sb.AppendLine($"    <TargetFramework>{framework}</TargetFramework>");
        sb.AppendLine("    <IsTestProject>true</IsTestProject>");
        if (platform) sb.AppendLine("    <OutputType>Exe</OutputType>").AppendLine("    <EnableNUnitRunner>true</EnableNUnitRunner>");
        sb.AppendLine("  </PropertyGroup>").AppendLine();

        sb.AppendLine("  <ItemGroup>");
        foreach (var package in libraryProject is null ? TestPackages.Append(PackageId) : TestPackages)
            sb.AppendLine(centrally ? $"    <PackageReference Include=\"{package}\" />" : $"    <PackageReference Include=\"{package}\" Version=\"{PackageVersion(package, version)}\" />");
        sb.AppendLine("  </ItemGroup>").AppendLine();

        sb.AppendLine("  <ItemGroup>");
        foreach (var reference in references)
            sb.AppendLine($"    <ProjectReference Include=\"{Path.GetRelativePath(directory, reference.Path).Replace('/', '\\')}\" />");
        if (libraryProject is not null) sb.AppendLine($"    <ProjectReference Include=\"{Path.GetFullPath(libraryProject)}\" />");
        sb.AppendLine("  </ItemGroup>").AppendLine();

        if (views)
            sb.AppendLine("  <ItemGroup>").AppendLine($"    <None Include=\"{ViewsJson.FileName}\" CopyToOutputDirectory=\"PreserveNewest\" />").AppendLine("  </ItemGroup>").AppendLine();
        sb.AppendLine("</Project>");
        return sb.ToString();
    }

    private static string TestClass(string ns, string className, string template, string project) =>
        $"using SagaPerception.TestGen.NUnit;\n\nnamespace {ns};\n\npublic sealed class {className}() : {template}(\"{project}\");\n";

    /// <summary>Lägger views.json bland det som kopieras till utdata om projektfilen inte nämner den. True om filen ändrades.</summary>
    private static bool EnsureViewsCopied(string testProject)
    {
        var text = File.ReadAllText(testProject);
        if (text.Contains(ViewsJson.FileName, StringComparison.Ordinal)) return false;
        var item = $"  <ItemGroup>\n    <None Include=\"{ViewsJson.FileName}\" CopyToOutputDirectory=\"PreserveNewest\" />\n  </ItemGroup>\n\n";
        var end = text.LastIndexOf("</Project>", StringComparison.Ordinal);
        if (end < 0) return false;
        var newline = text.Contains("\r\n", StringComparison.Ordinal);
        File.WriteAllText(testProject, text.Insert(end, newline ? item.Replace("\n", "\r\n") : item));
        return true;
    }

    // Klassen får aldrig heta som basklassen (ApiTests, ViewTests): då ärver den sig själv eller skuggar basen i hela namnrymden.
    internal static string TestClassName(string projectName, string template)
    {
        var suffix = template == "ApiTests" ? "Tests" : "ViewTests";
        var name = Identifier(projectName);
        return name + suffix is "ApiTests" or "ViewTests" ? name + "Project" + suffix : name + suffix;
    }

    private static string Identifier(string name) => NotIdentifier().Replace(name, "");

    private static string Namespace(string projectName) =>
        string.Join('.', projectName.Split('.').Select(s => NotIdentifier().Replace(s, "_")).Select(s => s.Length == 0 || char.IsDigit(s[0]) ? "_" + s : s));

    [GeneratedRegex(@"[^A-Za-z0-9_]")]
    private static partial Regex NotIdentifier();

    // --- Paketversioner ---

    /// <summary>Directory.Packages.props närmast testprojektet, om den slår på central pakethantering. Annars null.</summary>
    private static string? CentralPackages(string directory)
    {
        for (var dir = new DirectoryInfo(directory); dir is not null; dir = dir.Parent)
        {
            var file = Path.Combine(dir.FullName, "Directory.Packages.props");
            if (!File.Exists(file)) continue;
            try
            {
                var central = XDocument.Load(file).Descendants("ManagePackageVersionsCentrally").FirstOrDefault()?.Value.Trim();
                return string.Equals(central, "true", StringComparison.OrdinalIgnoreCase) ? file : null;
            }
            catch (System.Xml.XmlException) { return null; }
        }
        return null;
    }

    /// <summary>Lägger de PackageVersion-rader som saknas, i bokstavsordning bland de andra. Ger paketnamnen som lades till.</summary>
    private static List<string> AddPackageVersions(string file, string version, bool withLibrary)
    {
        var lines = File.ReadAllText(file).Replace("\r\n", "\n").Split('\n').ToList();
        var newline = File.ReadAllText(file).Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var added = new List<string>();
        foreach (var package in withLibrary ? TestPackages.Append(PackageId) : TestPackages)
        {
            if (lines.Any(l => PackageVersionLine().Match(l) is { Success: true } m && m.Groups[2].Value.Equals(package, StringComparison.OrdinalIgnoreCase))) continue;
            var entries = lines.Select((l, i) => (Match: PackageVersionLine().Match(l), Index: i)).Where(x => x.Match.Success).ToList();
            var line = $"{(entries.Count > 0 ? entries[0].Match.Groups[1].Value : "    ")}<PackageVersion Include=\"{package}\" Version=\"{PackageVersion(package, version)}\" />";
            if (entries.Count > 0)
            {
                var after = entries.FirstOrDefault(x => string.Compare(x.Match.Groups[2].Value, package, StringComparison.OrdinalIgnoreCase) > 0);
                lines.Insert(after.Match is null ? entries[^1].Index + 1 : after.Index, line);
            }
            else
            {
                var close = lines.FindLastIndex(l => l.Contains("</Project>", StringComparison.Ordinal));
                if (close < 0) continue;
                lines.InsertRange(close, ["  <ItemGroup>", line, "  </ItemGroup>"]);
            }
            added.Add(package);
        }
        if (added.Count > 0) File.WriteAllText(file, string.Join(newline, lines));
        return added;
    }

    [GeneratedRegex(@"^(\s*)<PackageVersion\s+Include=""([^""]+)""")]
    private static partial Regex PackageVersionLine();

    /// <summary>Versionen verktyget är byggt med (se AssemblyMetadata i TestGen.Cli.csproj); testgens eget paket har verktygets version.</summary>
    private static string PackageVersion(string package, string toolVersion) =>
        package == PackageId ? toolVersion
        : typeof(InitCommand).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>().FirstOrDefault(m => m.Key == "PackageVersion:" + package)?.Value
          ?? throw new InvalidOperationException($"Verktyget saknar version för {package}.");

    private static string ToolVersion() =>
        (typeof(InitCommand).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "1.0.0").Split('+')[0];

    // --- Solutionen, Chromium och CI ---

    private static async Task AddToSolutionAsync(string solution, string project, Terminal terminal, CancellationToken ct)
    {
        if (solution.EndsWith(SolutionFinder.ProjectExtension, StringComparison.OrdinalIgnoreCase)) return;
        await Dotnet.RunAsync(["sln", solution, "add", "--include-references", "false", project], ct);
        terminal.Confirm(solution, $"projektet {Path.GetFileNameWithoutExtension(project)} lades till");
    }

    /// <summary>
    /// Playwrights installation, körd som dess medföljande node-drivrutin med utskrifterna (förlopp, omförsök, stackspår) uppsamlade.
    /// Playwright.Program.Main ärver i stället våra egna ström-handtag, så utskriften går inte att fånga därifrån.
    /// </summary>
    private static ChromiumInstallResult RunPlaywrightInstall()
    {
        var driver = Path.Combine(AppContext.BaseDirectory, ".playwright");
        var node = Environment.GetEnvironmentVariable("PLAYWRIGHT_NODEJS_PATH") is { Length: > 0 } custom ? custom
            : Path.Combine(driver, "node", NodePlatform(), OperatingSystem.IsWindows() ? "node.exe" : "node");
        var cli = Path.Combine(driver, "package", "cli.js");
        if (!File.Exists(node) || !File.Exists(cli)) return new ChromiumInstallResult(false, "Error: Playwrights medföljande drivrutin för den här plattformen hittades inte: " + node);
        try
        {
            var start = new ProcessStartInfo(node) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(cli);
            start.ArgumentList.Add("install");
            start.ArgumentList.Add("chromium");
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Drivrutinen startade inte.");
            var text = new StringBuilder();
            var gate = new object();
            void Collect(object _, DataReceivedEventArgs e) { if (e.Data is not null) lock (gate) text.AppendLine(Ansi().Replace(e.Data, "")); }
            process.OutputDataReceived += Collect;
            process.ErrorDataReceived += Collect;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.WaitForExit();
            lock (gate) return new ChromiumInstallResult(process.ExitCode == 0, text.ToString());
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new ChromiumInstallResult(false, "Error: " + e.Message);
        }
    }

    /// <summary>Mappen under .playwright/node för den här datorn, med Playwrights namn. Paketet kan ha flera plattformars node.</summary>
    private static string NodePlatform()
    {
        var (os, separator) = OperatingSystem.IsWindows() ? ("win32", '_') : OperatingSystem.IsMacOS() ? ("darwin", '-') : ("linux", '-');
        var architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";
        return os + separator + architecture;
    }

    [GeneratedRegex("\\u001b\\[[0-9;]*m")]
    private static partial Regex Ansi();

    /// <summary>
    /// Kör <paramref name="install"/> och ger sant om Chromium nu finns. Misslyckas den skrivs hela Playwright-utskriften till en loggfil, och
    /// terminalen får en varning med de sista raderna och vad man kan göra.
    /// </summary>
    internal static bool InstallChromium(Terminal terminal, Func<ChromiumInstallResult> install)
    {
        var result = install();
        if (result.Succeeded) return true;
        string? log = null;
        try
        {
            // Hela Playwright-utskriften, så att den går att läsa utan att fylla terminalen. Ett nytt namn varje gång och CreateNew:
            // temp-mappen kan vara delad, och en fil eller länk som någon annan lagt dit ska aldrig skrivas över.
            var path = Path.Combine(Path.GetTempPath(), $"testgen-chromium-install-{Guid.NewGuid():N}.log");
            using (var writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write))) writer.Write(result.Output);
            log = path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* loggen är en extra; varningen räcker */ }
        terminal.Warning(ChromiumFailure(result.Output, log, Path.Combine(AppContext.BaseDirectory, "playwright.ps1")));
        return false;
    }

    /// <summary>
    /// Varningen när Chromium inte gick att installera: orsaken så långt den går att läsa ur Playwrights utskrift (en gång, inte varje omförsök)
    /// och hur det rättas utan pwsh: testgen init igen, som bara installerar det som saknas.
    /// </summary>
    internal static string ChromiumFailure(string playwrightOutput, string? logPath, string playwrightScript)
    {
        var errorLine = playwrightOutput.Split('\n').Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("Error:", StringComparison.Ordinal))?["Error:".Length..].Trim();
        var host = errorLine is null ? null : DownloadHost().Match(errorLine) is { Success: true } m ? m.Groups[1].Value : null;
        var reason = errorLine switch
        {
            null => "Playwright gav ingen närmare förklaring.",
            _ when NetworkFailure().IsMatch(errorLine) =>
                $"Nedladdningen från {host ?? "Playwrights server"} misslyckades, troligen för att datorn inte når den (brandvägg eller proxy).",
            _ when DiskFailure().IsMatch(errorLine) => "Det saknas diskutrymme eller skrivrätt i Playwrights katalog för webbläsare.",
            _ => "Playwright svarade: " + (errorLine.Length > 160 ? errorLine[..160] + " ..." : errorLine),
        };
        var sb = new StringBuilder("Chromium gick inte att installera. ").Append(reason).Append(" UI-testerna blir osäkra tills det finns.");
        sb.Append(" Kör testgen init igen när det är rättat; den installerar då bara Chromium. Utan testgen kan pwsh göra det: pwsh ").Append(playwrightScript).Append(" install chromium.");
        if (logPath is not null) sb.Append(" Hela utskriften: ").Append(logPath).Append('.');
        return sb.ToString();
    }

    [GeneratedRegex(@"Download failed|ENOTFOUND|ECONNREFUSED|ECONNRESET|ETIMEDOUT|EAI_AGAIN|getaddrinfo|socket hang up|proxy|returned code (403|407|5\d\d)", RegexOptions.IgnoreCase)]
    private static partial Regex NetworkFailure();

    [GeneratedRegex(@"ENOSPC|EACCES|EPERM|EROFS|no space left|permission denied", RegexOptions.IgnoreCase)]
    private static partial Regex DiskFailure();

    [GeneratedRegex(@"https?://([^/\s'""]+)")]
    private static partial Regex DownloadHost();

    /// <summary>Ett GitHub Actions-utdrag som kör dotnet test, med Chromium cachad om det behövs.</summary>
    internal static string CiStep(string testProject, string framework, bool chromium, bool platform = false)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Lägg i CI (GitHub Actions), efter checkout och setup-dotnet:");
        sb.AppendLine();
        if (chromium)
        {
            sb.AppendLine("      # Förutsätter en Ubuntu-runner: cachesökvägen och --with-deps gäller Linux (pwsh finns där).");
            sb.AppendLine("      - uses: actions/cache@v4");
            sb.AppendLine("        with:");
            sb.AppendLine("          path: ~/.cache/ms-playwright");
            sb.AppendLine("          key: ${{ runner.os }}-chromium-${{ hashFiles('**/Directory.Packages.props', '**/*.csproj') }}");
            sb.AppendLine($"      - run: dotnet build {testProject}");
            sb.AppendLine($"      - run: pwsh {testProject}/bin/Debug/{framework}/playwright.ps1 install --with-deps chromium");
            sb.AppendLine($"      - run: {TestCommand(testProject, platform)} --no-build");
        }
        else sb.AppendLine($"      - run: {TestCommand(testProject, platform)}");
        return sb.ToString();
    }

    /// <summary>Kommandot som kör testprojektet. Med Microsoft.Testing.Platform pekas projektet ut med <c>--project</c>.</summary>
    internal static string TestCommand(string testProject, bool platform) => platform ? $"dotnet test --project {testProject}" : $"dotnet test {testProject}";

    /// <summary>
    /// <c>test.runner</c> i närmaste <c>global.json</c> (från katalogen och uppåt) är Microsoft.Testing.Platform. Då kräver <c>dotnet test</c>
    /// att alla testprojekt använder den.
    /// </summary>
    internal static bool UsesTestingPlatform(string directory)
    {
        for (var dir = new DirectoryInfo(directory); dir is not null; dir = dir.Parent)
        {
            var file = Path.Combine(dir.FullName, "global.json");
            if (!File.Exists(file)) continue;
            return GlobalJson.TestRunner(file)?.Equals("Microsoft.Testing.Platform", StringComparison.OrdinalIgnoreCase) == true;
        }
        return false;
    }

    /// <summary>Kör dotnet och ger ett fel med utdata om det misslyckas.</summary>
    private static class Dotnet
    {
        public static async Task RunAsync(string[] args, CancellationToken ct)
        {
            var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in args) info.ArgumentList.Add(arg);
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync(ct);
            var error = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0)
                throw new UsageException($"dotnet {args[0]} misslyckades.", "Åtgärda felet nedan och kör testgen init igen.", (await output + await error).Trim());
        }
    }
}
