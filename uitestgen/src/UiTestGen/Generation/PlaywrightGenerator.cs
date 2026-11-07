using UiTestGen.Model;

namespace UiTestGen.Generation;

/// <summary>
/// Skriver ett fristående NUnit-testprojekt med Playwright: en Page Object och en testklass per vy.
/// Deterministiskt: ingen tid, inga GUID:ar, <c>\n</c> som radslut och sorterad ordning.
/// </summary>
public sealed class PlaywrightGenerator
{
    /// <summary>Filerna att skriva, sorterade på <see cref="GeneratedFile.Path"/>. Rent: samma katalog och samma val ger exakt samma filer.</summary>
    public IReadOnlyList<GeneratedFile> Generate(ViewCatalog catalog, GenerateOptions options)
    {
        var name = options.ProjectName;
        var ns = CSharp.Namespace(name);
        var mocks = options.Spec is not null;
        var files = new List<GeneratedFile>
        {
            new($"{name}.csproj", SupportWriter.Csproj(ns, mocks), true),
            new("Directory.Build.props", SupportWriter.DirectoryBuildProps, true),
            new("Directory.Build.targets", SupportWriter.DirectoryBuildTargets, true),
            new("Directory.Packages.props", SupportWriter.DirectoryPackagesProps, true),
            new("Support/UiTestBase.g.cs", SupportWriter.UiTestBase(ns, options.Backend, mocks), true),
            new("Support/Screenshots.g.cs", ScreenshotsWriter.Screenshots(ns, name), true),
            new("Support/ImageComparer.g.cs", ScreenshotsWriter.ImageComparer(ns), true),
            new("Tests/CatalogReport.g.cs", CatalogInsights.CatalogReport(catalog, ns), true),
            new("README.md", SupportWriter.Readme(name, mocks), false),
        };
        if (options.Spec is { } spec)
        {
            files.Add(new("Support/ApiMocks.g.cs", MockWriter.ApiMocks(ns, spec, options.Seed), true));
            files.Add(new("Support/ApiStub.g.cs", MockWriter.ApiStub(ns), true));
        }
        foreach (var view in catalog.Views.OrderBy(v => v.Id, StringComparer.Ordinal))
        {
            var id = CSharp.Identifier(view.Id, "View");
            files.Add(new($"Pages/{id}Page.g.cs", PageObjectWriter.Write(view, ns), true));
            files.Add(new($"Pages/{id}Page.cs",
                CSharp.HandwrittenPartial($"{ns}.Pages", PageObjectWriter.ClassName(view), "Egna tillägg till sidan (fler locators, hjälpmetoder) skrivs här."), false));
            files.Add(new($"Tests/{id}Tests.g.cs", TestsWriter.Write(view, ns, options, catalog), true));
            files.Add(new($"Tests/{id}Tests.cs",
                CSharp.HandwrittenPartial($"{ns}.Tests", TestsWriter.ClassName(view), "Egna tester för vyn skrivs här."), false));
        }
        return files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
    }
}
