using System.Xml.Linq;

namespace Repository.Tests;

/// <summary>Modulgränserna (plan, avsnitt 3): läser alla .csproj i repot och kontrollerar referenserna mot regeltabellen.</summary>
public class ArchitectureTests
{
    /// <summary>Ett projekt i repot. <see cref="Path"/> är relativt repots rot och använder snedstreck.</summary>
    private sealed record Project(string Path, string[] ProjectReferences, string[] PackageReferences)
    {
        public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
        public bool IsTest => Path.Contains("/tests/") || Path.StartsWith("tests/");
        public bool IsProduct => !IsTest;
    }

    /// <summary>En regel: <paramref name="Forbidden"/> pekar ut en otillåten referens för de projekt som <paramref name="AppliesTo"/> väljer.</summary>
    private sealed record Rule(string Why, Func<Project, bool> AppliesTo, Func<string, bool> ForbiddenProject, Func<string, bool> ForbiddenPackage);

    private static bool Never(string _) => false;
    private static bool Always(string _) => true;
    private static bool IsNUnit(string package) => package.StartsWith("NUnit", StringComparison.OrdinalIgnoreCase);
    private static bool IsTestGen(string path) => path.StartsWith("testgen/");

    private static readonly Rule[] Rules =
    [
        // Basen delas av alla verktyg. Om den refererar ett verktyg får verktygen en cirkel och går inte att bygga eller paketera var för sig.
        new("shared/CommandLine ska stå längst ner och får inte referera något annat projekt.",
            p => p.Path == "shared/CommandLine/CommandLine.csproj", Always, Never),

        new("Testerna för shared/CommandLine ska bara referera shared: de bevisar att basen står på egna ben.",
            p => p.Path.StartsWith("shared/tests/"), path => !path.StartsWith("shared/"), Never),

        // systemmap är en egen produkt (egna paket). testgen använder dess bibliotek åt ett håll, aldrig tvärtom.
        new("systemmap är fristående: det får inte referera testgen.",
            p => p.Path.StartsWith("systemmap/src/"), IsTestGen, Never),

        // Mocken körs i testprocessen (och som ett eget kommando): den får inte dra in webbläsare, Roslyn eller något annat än Core.
        new("TestGen.Mock får bara referera TestGen.Core, och inte Playwright, Roslyn eller NUnit.",
            p => p.Path == "testgen/src/TestGen.Mock/TestGen.Mock.csproj", path => !path.EndsWith("/TestGen.Core.csproj"),
            package => IsNUnit(package) || package.StartsWith("Microsoft.Playwright", StringComparison.OrdinalIgnoreCase)
                || package.StartsWith("Microsoft.CodeAnalysis", StringComparison.OrdinalIgnoreCase)),

        // Webbläsardelen körs i testprocessen tillsammans med mocken. Den får bara bero på Core och Mock, och inte dra in testramverk eller Roslyn.
        new("TestGen.Browser får bara referera TestGen.Core och TestGen.Mock, och inte NUnit eller Roslyn.",
            p => p.Path == "testgen/src/TestGen.Browser/TestGen.Browser.csproj",
            path => !path.EndsWith("/TestGen.Core.csproj") && !path.EndsWith("/TestGen.Mock.csproj"),
            package => IsNUnit(package) || package.StartsWith("Microsoft.CodeAnalysis", StringComparison.OrdinalIgnoreCase)),

        // Vyerna läses ur källkoden med Roslyn; testprocessen läser bara views.json och ska aldrig dra in Roslyn. Därför ligger
        // modellen för views.json i Core, och Views får bara bero på Core och systemmap.
        new("TestGen.Views får bara referera TestGen.Core och systemmap, och inte Playwright eller NUnit.",
            p => p.Path == "testgen/src/TestGen.Views/TestGen.Views.csproj",
            path => !path.EndsWith("/TestGen.Core.csproj") && !path.EndsWith("/SystemMap.csproj"),
            package => IsNUnit(package) || package.StartsWith("Microsoft.Playwright", StringComparison.OrdinalIgnoreCase)),

        new("TestGen.Core får inte referera ASP.NET Core, Playwright, NUnit, Roslyn eller ClosedXML: det läses in i testprocessen.",
            p => p.Path == "testgen/src/TestGen.Core/TestGen.Core.csproj", Never,
            package => IsNUnit(package)
                || package.StartsWith("Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase)
                || package.StartsWith("Microsoft.CodeAnalysis", StringComparison.OrdinalIgnoreCase)
                || package.StartsWith("Microsoft.Playwright", StringComparison.OrdinalIgnoreCase)
                || package.StartsWith("ClosedXML", StringComparison.OrdinalIgnoreCase)),

        // Excel läses in först när Excel används. Det ligger utanför Core så att ClosedXML aldrig hamnar i testprocessen.
        new("TestGen.Excel får bara referera TestGen.Core, och inte ASP.NET Core, Playwright, Roslyn eller NUnit.",
            p => p.Path == "testgen/src/TestGen.Excel/TestGen.Excel.csproj", path => !path.EndsWith("/TestGen.Core.csproj"),
            package => IsNUnit(package)
                || package.StartsWith("Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase)
                || package.StartsWith("Microsoft.CodeAnalysis", StringComparison.OrdinalIgnoreCase)
                || package.StartsWith("Microsoft.Playwright", StringComparison.OrdinalIgnoreCase)),

        // Värden för testgens tester i dotnet test: den får bo på Core, Excel, Mock och Browser, men inte på Roslyn, Cli eller Views.
        new("SagaPerception.TestGen.NUnit får bara referera TestGen.Core, Excel, Mock och Browser, och inte Roslyn, Cli eller Views.",
            p => p.Path == "testgen/src/SagaPerception.TestGen.NUnit/SagaPerception.TestGen.NUnit.csproj",
            path => !(path.EndsWith("/TestGen.Core.csproj") || path.EndsWith("/TestGen.Excel.csproj") || path.EndsWith("/TestGen.Mock.csproj")
                      || path.EndsWith("/TestGen.Browser.csproj")),
            package => package.StartsWith("Microsoft.CodeAnalysis", StringComparison.OrdinalIgnoreCase)),

        // Produktkod som drar in NUnit tvingar användarna av paketet att få testramverket i sin utdata och sina beroenden.
        new("Produktkod under src får inte referera NUnit (utom värden SagaPerception.TestGen.NUnit). NUnit hör hemma i testprojekt.",
            p => p.IsProduct && (p.Path.Contains("/src/") || p.Path.StartsWith("src/")) && p.Name != "SagaPerception.TestGen.NUnit", Never, IsNUnit),

        // Annars hamnar testkod (och dess testramverk) i det som levereras.
        new("Produktprojekt får inte referera ett testprojekt.",
            p => p.IsProduct, path => path.StartsWith("tests/") || path.Contains("/tests/"), Never),
    ];

    [Test]
    public void Projects_respect_the_module_boundaries()
    {
        var projects = LoadProjects();
        Assert.That(projects, Has.Count.GreaterThan(10), "Hittade för få projekt: letar testet på fel ställe?");

        var violations = new List<string>();
        foreach (var project in projects)
            foreach (var rule in Rules.Where(r => r.AppliesTo(project)))
            {
                violations.AddRange(project.ProjectReferences.Where(rule.ForbiddenProject).Select(reference =>
                    $"{project.Name} ({project.Path}) refererar projektet {reference}, vilket inte är tillåtet. {rule.Why}"));
                violations.AddRange(project.PackageReferences.Where(rule.ForbiddenPackage).Select(package =>
                    $"{project.Name} ({project.Path}) refererar paketet {package}, vilket inte är tillåtet. {rule.Why}"));
            }

        Assert.That(violations, Is.Empty, "Otillåtna referenser:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Test]
    public void Rules_apply_to_existing_projects()
    {
        // Skyddar mot att en omdöpt eller flyttad katalog tyst gör en regel verkningslös.
        var projects = LoadProjects();
        var unused = Rules.Where(rule => !projects.Any(rule.AppliesTo)).Select(rule => rule.Why).ToList();
        Assert.That(unused, Is.Empty, "Regler som inte gäller något projekt (stämmer sökvägarna fortfarande?):" + Environment.NewLine + string.Join(Environment.NewLine, unused));
    }

    private static List<Project> LoadProjects()
    {
        var root = FindRepositoryRoot();
        return Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'))
            .Where(path => !IsExcluded(path))
            .Order(StringComparer.Ordinal)
            .Select(path => Parse(root, path))
            .ToList();
    }

    // bin, obj och dolda kataloger (.git, .claude) innehåller inga projekt vi äger.
    private static bool IsExcluded(string path) =>
        path.Split('/').Any(part => part is "bin" or "obj" or "node_modules" || part.StartsWith('.'));

    private static Project Parse(string root, string path)
    {
        var document = XDocument.Load(Path.Combine(root, path));
        var directory = Path.GetDirectoryName(path)!;
        var projectReferences = document.Descendants("ProjectReference")
            .Select(e => (string?)e.Attribute("Include"))
            .OfType<string>()
            .Select(include => Path.GetFullPath(Path.Combine(root, directory, include.Replace('\\', '/'))))
            .Select(full => Path.GetRelativePath(root, full).Replace('\\', '/'))
            .ToArray();
        var packageReferences = document.Descendants("PackageReference")
            .Select(e => (string?)e.Attribute("Include") ?? (string?)e.Attribute("Update"))
            .OfType<string>()
            .Concat(document.Descendants("FrameworkReference").Select(e => (string?)e.Attribute("Include")).OfType<string>())   // t.ex. Microsoft.AspNetCore.App
            .ToArray();
        return new Project(path, projectReferences, packageReferences);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "SagaPerception.Tools.slnx")))
                return directory.FullName;
        throw new InvalidOperationException("Hittar inte repots rot (SagaPerception.Tools.slnx) uppåt från " + AppContext.BaseDirectory);
    }
}
