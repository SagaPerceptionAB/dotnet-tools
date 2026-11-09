using TestGen.Browser;
using TestGen.Browser.Probe;
using TestGen.Core.Projects;
using TestGen.Core.Views;
using TestGen.Mock;
using TestGen.Views.Discovery;

namespace TestGen.Cli;

/// <summary>
/// <c>testgen views --probe</c>: startar varje webbapp, med API-anropen pekade på en mock ur specen, öppnar vyerna i Chromium och
/// lägger till det som bara syns när appen körs (etiketter, HTML-regler, länkar och sidor som källkoden inte nämner).
/// </summary>
internal static class ViewProbe
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(3);

    public static async Task<ViewsFile> RunAsync(RepositoryFacts facts, ViewsFile file, ProbeOptions options, Terminal terminal, CancellationToken ct)
    {
        await using var browser = await StartBrowserAsync();
        var apps = new List<AppViews>();
        foreach (var app in file.Apps)
        {
            terminal.Err.WriteLine($"Sonderar {app.Project} ...");
            apps.Add(await ProbeAppAsync(facts, app, browser, options, terminal, ct));
        }
        return ViewsFile.Of(apps);
    }

    private static async Task<BrowserHost> StartBrowserAsync()
    {
        try { return await BrowserHost.StartAsync(); }
        catch (ChromiumMissingException e)
        {
            throw new UsageException("Chromium saknas, så vyerna kan inte sonderas.", "Kör testgen init, som installerar den. Utan --probe läser testgen views bara källkoden.", e.Message);
        }
    }

    private static async Task<AppViews> ProbeAppAsync(RepositoryFacts facts, AppViews app, BrowserHost browser, ProbeOptions options, Terminal terminal, CancellationToken ct)
    {
        var project = facts.Project(app.Project)!;
        var mocks = new List<MockServer>();
        try
        {
            var settings = new Dictionary<string, string>();
            foreach (var api in app.Apis.Where(a => a.ConfigKey is not null))
            {
                var spec = facts.Project(api.Project) is { } apiProject ? facts.SpecOf(apiProject) : null;
                if (spec is null)
                {
                    terminal.Warning($"{api.Project} saknar spec, så {app.Project} får ingen mock för {api.ConfigKey}. Sidor som anropar API:et kan visa fel.");
                    continue;
                }
                var mock = await MockServer.StartAsync(spec, new MockOptions(), ct);
                mocks.Add(mock);
                settings[api.ConfigKey!] = mock.Url;
            }

            AppProcess process;
            try { process = await AppProcess.StartAsync(project.Path, settings, project.RunEnvironment, StartTimeout, ct); }
            catch (IOException e)
            {
                terminal.Warning($"{app.Project} startade inte, så vyerna sonderades inte. {e.Message}");
                return app;
            }
            await using (process)
            {
                var result = await ViewProber.ProbeAsync(app.Views, process.Url, browser, options, ct);
                foreach (var warning in result.Warnings) terminal.Warning(warning);
                return app with { Views = ViewDiscovery.Finish(result.Views, []).Views };
            }
        }
        finally
        {
            foreach (var mock in mocks) await mock.DisposeAsync();
        }
    }
}
