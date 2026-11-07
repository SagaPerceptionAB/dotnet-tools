using UiTestGen.Discovery;
using UiTestGen.Model;

namespace UiTestGen.Probe;

/// <param name="MaxPages">Hur många sidor som öppnas som mest, de kända vyerna medräknade.</param>
/// <param name="MaxDepth">Hur många länkar från en känd vy som följs när nya vyer letas.</param>
/// <param name="TimeoutMs">Tidsgräns per sida i millisekunder.</param>
/// <param name="BrowserPath">Sökväg till webbläsaren. Utan den används <c>UITEST_BROWSER_PATH</c>, annars Playwrights egen.</param>
public sealed record ProbeOptions(int MaxPages = 50, int MaxDepth = 3, int TimeoutMs = 15000, string? BrowserPath = null);

/// <summary>Öppnar en adress och läser sidan. Webbläsaren i skarpt läge, en låtsasserver i tester av crawlen.</summary>
internal interface IPageSource
{
    Task<PageResult> OpenAsync(string path, CancellationToken cancellationToken);
}

/// <summary>
/// Sonderar vyerna i den körande appen: öppnar varje vy i en riktig webbläsare, läser DOM:en och slår ihop med det som källkoden gav.
/// Länkar till okända sidor inom appen följs en bit (begränsat antal sidor och djup) och blir nya vyer av sorten
/// <see cref="ViewKind.Probed"/>. Samma app ger samma katalog.
/// </summary>
public static class ViewProber
{
    /// <summary>Sonderar katalogen mot appen på <paramref name="appUrl"/> med Chromium.</summary>
    public static async Task<ViewCatalog> ProbeAsync(ViewCatalog catalog, string appUrl, ProbeOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new ProbeOptions();
        var origin = Origin(appUrl);
        await using var source = await PlaywrightPageSource.LaunchAsync(origin, options);
        return await ProbeAsync(catalog, origin, options, source, cancellationToken);
    }

    /// <summary>Origin (schema, värd och port) för appens adress.</summary>
    public static string Origin(string appUrl)
    {
        if (!Uri.TryCreate(appUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new UsageException($"--app ska vara en http- eller https-adress, inte '{appUrl}'.", "Skriv t.ex. --app http://localhost:5000.");
        return uri.GetLeftPart(UriPartial.Authority);
    }

    /// <summary>Hur många sidor som öppnas samtidigt.</summary>
    internal const int MaxConcurrency = 4;

    private sealed record Item(string Path, int Depth, int? ViewIndex, string? From);

    /// <summary>
    /// Öppnar sidorna nivå för nivå, bredden först: först alla kända vyer, sedan sidorna de länkar till, och så vidare. Sidorna i en nivå
    /// öppnas högst <see cref="MaxConcurrency"/> åt gången, men resultaten slås ihop i nivåns ordning när alla är klara. Då blir katalogen,
    /// varningarna och vilka sidor som hinner med före <see cref="ProbeOptions.MaxPages"/> desamma som om sidorna öppnats en i taget.
    /// </summary>
    internal static async Task<ViewCatalog> ProbeAsync(ViewCatalog catalog, string origin, ProbeOptions options, IPageSource source, CancellationToken cancellationToken)
    {
        var matcher = new RouteMatcher(catalog.Views.Select(v => v.Route));
        var views = catalog.Views.OrderBy(v => v.Route, StringComparer.Ordinal).ThenBy(v => v.Source, StringComparer.Ordinal).ToList();
        var warnings = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var level = new List<Item>();
        for (var i = 0; i < views.Count; i++)
        {
            var path = SnapshotMerger.TrimSlash(RouteExamples.Path(views[i].Route));
            visited.Add(path);
            level.Add(new Item(path, 0, i, null));
        }

        // Slår ihop en sida i katalogen och lägger sidorna den länkar till (som inte är kända) i nästa nivå. Anropas i nivåns ordning.
        void Merge(Item item, PageResult result, List<Item> next)
        {
            var kept = item.ViewIndex is not null ? "Vyn behålls oförändrad." : "Sidan lades inte till som vy.";
            var from = item.From is null ? "" : $" (länk från {item.From})";

            if (result.TimedOut) { warnings.Add($"Sonderingen: {item.Path}{from} laddades inte inom {Seconds(options.TimeoutMs)} s. {kept}"); return; }
            if (result.Error is not null) { warnings.Add($"Sonderingen: {item.Path}{from} gick inte att öppna ({result.Error}). {kept}"); return; }
            if (result.Status >= 400) { warnings.Add($"Sonderingen: {item.Path}{from} svarade med status {result.Status}. {kept}"); return; }
            if (result.Snapshot is not { } snapshot) { warnings.Add($"Sonderingen: {item.Path}{from} gick inte att läsa. {kept}"); return; }
            if (!SamePath(result.FinalPath, item.Path))
            {
                warnings.Add($"Sonderingen: {item.Path}{from} omdirigerades till {result.FinalPath}. {kept}");
                return;
            }

            if (item.ViewIndex is { } index) views[index] = SnapshotMerger.Merge(views[index], snapshot, matcher, origin, item.Path);
            else views.Add(SnapshotMerger.NewView(item.Path, snapshot, matcher, origin));

            if (item.Depth >= options.MaxDepth) return;
            foreach (var link in SnapshotMerger.LocalLinks(snapshot, origin, item.Path))
                if (matcher.Match(link.Path) is null && visited.Add(link.Path))
                    next.Add(new Item(link.Path, item.Depth + 1, null, item.Path));
        }

        var pages = 0;
        while (level.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = level.Take(Math.Max(0, options.MaxPages - pages)).ToList();
            var results = await OpenAllAsync(source, batch, cancellationToken);
            pages += batch.Count;

            var next = new List<Item>();
            for (var n = 0; n < batch.Count; n++) Merge(batch[n], results[n], next);

            var left = level.Count - batch.Count + next.Count;
            if (left > 0 && pages >= options.MaxPages)
            {
                warnings.Add($"Sonderingen stoppade efter {options.MaxPages} sidor. {left} sidor sondrades inte. Höj gränsen med --max-pages.");
                break;
            }
            level = next;
        }

        return ViewDiscovery.Finish(views, catalog.Warnings.Concat(warnings));
    }

    /// <summary>Öppnar sidorna högst <see cref="MaxConcurrency"/> åt gången. Resultaten ligger i samma ordning som sidorna.</summary>
    private static async Task<PageResult[]> OpenAllAsync(IPageSource source, IReadOnlyList<Item> items, CancellationToken cancellationToken)
    {
        var results = new PageResult[items.Count];
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrency, CancellationToken = cancellationToken };
        await Parallel.ForEachAsync(Enumerable.Range(0, items.Count), parallel,
            async (i, token) => results[i] = await source.OpenAsync(items[i].Path, token));
        return results;
    }

    /// <summary>Sekunder med svenskt decimaltecken, oberoende av datorns språkinställning (varningarna ska vara desamma överallt).</summary>
    private static string Seconds(int ms) => (ms / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',');

    private static bool SamePath(string a, string b) =>
        string.Equals(SnapshotMerger.TrimSlash(a), SnapshotMerger.TrimSlash(b), StringComparison.OrdinalIgnoreCase);
}
