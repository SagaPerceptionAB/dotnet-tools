using TestGen.Core.Projects;
using TestGen.Core.Settings;

namespace TestGen.Cli;

/// <summary>Statusvyn som <c>testgen</c> utan argument visar: vad som finns i repot och nästa steg. Bara snabb läsning, ingen MSBuild.</summary>
public static class StatusView
{
    private const int LabelWidth = 9;

    public static int Run(string? solutionArg, Terminal terminal)
    {
        var solution = solutionArg ?? SolutionFinder.TryFind(Directory.GetCurrentDirectory(), "testgen");
        if (solution is null)
        {
            terminal.Out.WriteLine("testgen – ingen solution hittades här.");
            terminal.Out.WriteLine(SolutionFinder.HintBelow(Directory.GetCurrentDirectory(), "testgen")
                ?? "Gå till mappen där solutionen ligger, eller se kommandona: testgen --help");
            return ExitCodes.Ok;
        }
        var facts = RepositoryFacts.Read(solution);
        var settings = TestGenSettings.Defaults;
        try { settings = facts.LoadSettings(); }
        catch (UsageException e) { terminal.Warning(e.Message); }
        Render(facts, terminal, ChromiumLocator.IsInstalled(), settings);
        return ExitCodes.Ok;
    }

    /// <summary>Skriver vyn till stdout och problemen som <c>Varning: …</c> till stderr.</summary>
    public static void Render(RepositoryFacts facts, Terminal terminal, bool chromiumInstalled, TestGenSettings? settings = null)
    {
        var o = terminal.Out;
        o.WriteLine($"testgen – {Terminal.Relative(facts.SolutionPath)}");
        o.WriteLine();

        Rows(o, "API:er", facts.Apis.Select(api =>
        {
            var spec = facts.SpecOf(api);
            var parts = new List<string> { api.Name, spec is null ? "spec: saknas" : $"spec: {Terminal.Relative(spec)}{(api.IsGenerated(spec) ? " (skrivs vid bygget)" : "")}" };
            if (api.LocalUrl is not null) parts.Add($"lokalt: {api.LocalUrl}");
            if (spec is null)
                terminal.Warning($"{api.Name} saknar spec. {SpecLocator.MissingAdvice(api)}");
            return string.Join("   ", parts);
        }));

        var viewsFile = facts.ReadViews();
        Rows(o, "Appar", facts.WebApps.Select(app =>
        {
            var views = ViewsSummary.From(viewsFile, app.Name);
            if (views is null) return app.Name;
            var shown = views.Kind is null ? $"{views.ViewCount} vyer" : $"{views.ViewCount} vyer ({views.Kind})";
            var calls = views.Apis.Count == 0 ? "" : "   anropar " + string.Join(", ", views.Apis.Select(a => a.ConfigKey is null ? a.Project : $"{a.Project} via {a.ConfigKey}"));
            return $"{app.Name}   {shown}{calls}";
        }));

        var tests = facts.TestGenProjects.Select(t => facts.TestTargetsOf(t) is { Count: > 0 } targets
            ? $"{t.Name} ({string.Join(", ", targets.Select(x => $"{(x.Layer == "api" ? "API" : "UI")}: {x.Project}"))})" : t.Name).ToList();
        o.WriteLine(Label("Tester") + (tests.Count == 0 ? "saknas" : string.Join(", ", tests)));
        o.WriteLine(Label("Miljöer") + (settings?.Path is null ? "local (ingen konfiguration behövs)"
            : string.Join(", ", settings.EnvironmentNames.Select(n => settings.IsReadOnly(n) ? n + " (skrivskyddad)" : n))));
        o.WriteLine();
        o.WriteLine("Nästa steg: testgen init");

        if (facts.NeedsChromium && !chromiumInstalled)
            terminal.Warning("Chromium saknas för UI-testerna. Kör testgen init, som installerar den.");
    }

    private static string Label(string text) => "  " + text.PadRight(LabelWidth);

    private static void Rows(TextWriter o, string label, IEnumerable<string> rows)
    {
        var first = true;
        foreach (var row in rows)
        {
            o.WriteLine((first ? Label(label) : Label("")) + row);
            first = false;
        }
        if (first) o.WriteLine(Label(label) + "saknas");
    }
}
