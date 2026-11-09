using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SystemMap;
using TestGen.Core;
using TestGen.Core.Projects;
using TestGen.Core.Run;
using TestGen.Core.Settings;

namespace TestGen.Cli.Web;

/// <summary>
/// Lokalt webbgränssnitt för att köra tester och läsa resultaten. Säkerhet: lyssnar bara på 127.0.0.1, avvisar
/// förfrågningar med fel Host/Origin (DNS rebinding, andra webbplatser), kräver en slumpad sessionsnyckel (header
/// <see cref="KeyHeader"/>) på alla /api-anrop och loggar aldrig. Token tas emot per körning, hålls bara i minnet och
/// skickas aldrig tillbaka.
/// </summary>
public sealed class WebServer : IAsyncDisposable
{
    public const string KeyHeader = "X-Session-Key";
    public const long MaxUploadBytes = 10 * 1024 * 1024;
    private const int MaxUploads = 20;

    /// <summary>JSON till sidan, både svaren och händelserna under en körning.</summary>
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly WebApplication _app;
    private readonly WebOptions _options;
    private readonly string _tempDir = Directory.CreateTempSubdirectory("testgen-web-").FullName;
    private readonly byte[] _key = Encoding.ASCII.GetBytes(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
    private readonly Dictionary<string, UploadedFile> _uploads = new();   // i uppladdningsordning (äldst först vid städning)
    private sealed record UploadedFile(string Path, string Kind, long Seq);
    private long _uploadSeq;
    private readonly Dictionary<string, RunSession> _runs = new();
    private readonly object _gate = new();
    private RunSession? _current;
    // Systemkartan: en analys åt gången, och senaste kartan sparas så länge koden inte ändrats.
    private readonly SemaphoreSlim _mapGate = new(1, 1);
    private CachedMap? _mapCache;

    /// <summary>Adressen, t.ex. http://localhost:5100.</summary>
    public string Url { get; private set; } = "";
    public int Port { get; private set; }
    internal string TempDir => _tempDir;
    /// <summary>Sessionsnyckeln (finns i sidan; tester läser den härifrån).</summary>
    public string SessionKey => Encoding.ASCII.GetString(_key);

    private WebServer(WebOptions options)
    {
        _options = options;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();   // ingen loggning alls: inga token eller sökvägar hamnar i loggar
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = MaxUploadBytes + 1024;
            k.Listen(IPAddress.Loopback, options.Port);   // ENBART 127.0.0.1
        });
        _app = builder.Build();
        Map(_app);
    }

    public static async Task<WebServer> StartAsync(WebOptions options)
    {
        var s = new WebServer(options);
        try { await s._app.StartAsync(); }
        catch (IOException e) when (e.Message.Contains("in use", StringComparison.OrdinalIgnoreCase) || e.InnerException is System.Net.Sockets.SocketException)
        {
            await s.DisposeAsync();
            throw new UsageException($"Porten {options.Port} är upptagen.", $"Välj en annan port med --port, t.ex. testgen web --port {options.Port + 1}.");
        }
        var addr = s._app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        s.Port = new Uri(addr).Port;
        s.Url = $"http://localhost:{s.Port}";
        return s;
    }

    /// <summary>Väntar tills servern stoppas (Ctrl+C).</summary>
    public Task WaitForShutdownAsync() => _app.WaitForShutdownAsync();

    public async ValueTask DisposeAsync()
    {
        lock (_gate) foreach (var r in _runs.Values) r.Cancel();
        try { await _app.StopAsync(); } catch (Exception) { /* redan stoppad */ }
        await _app.DisposeAsync();
        try { Directory.Delete(_tempDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // --- Routing ------------------------------------------------------------------------

    private void Map(WebApplication app)
    {
        app.Use(Guard);

        app.MapGet("/", (HttpContext ctx) =>
        {
            var config = JsonSerializer.Serialize(new { spec = _options.Spec ?? "", excel = _options.Excel ?? "" }, Json)
                .Replace("<", "\\u003c");
            var html = Page().Replace("__SESSION_KEY__", SessionKey).Replace("__PREFILL__", config);
            return Results.Content(html, "text/html; charset=utf-8");
        });
        // Systemkartan: Cytoscape.js, layouten fcose, systemmap.js och systemmap.css (en fast lista, se CodeMapAssets).
        app.MapGet("/lib/{name}", (string name) => CodeMapAssets.Get(name) is { } file
            ? Results.Bytes(file.Bytes, file.ContentType)
            : Results.NotFound());

        app.MapGet("/api/environments", (HttpContext ctx) =>
        {
            try
            {
                var settings = LoadSettings();
                var projects = Projects(settings, Facts());
                return Results.Json(new
                {
                    hasConfig = settings.Path is not null,
                    projects,
                    environments = settings.EnvironmentNames.Select(n => EnvironmentDto(settings, n, projects)).ToList(),   // aldrig token eller headers
                }, Json);
            }
            catch (Exception e)
            {
                return ErrorFrom(e, new ErrorContext());   // samma text som kommandoraden
            }
        });

        // Resultat från båda lagren: körningar i sidan och resultatfiler från dotnet test (TestResults/testgen/*.json).
        app.MapGet("/api/results", () =>
        {
            var rows = new List<ResultRow>();
            foreach (var (id, path) in ResultFiles.Discover(Facts()))
                if (ResultFiles.Read(path) is { } json)
                    rows.Add(ResultFiles.Row(id, "file", json, Path.GetFileNameWithoutExtension(path)));
            lock (_gate) rows.AddRange(_runs.Values.Select(RunRow));
            return Results.Json(new { results = rows.OrderByDescending(r => r.Started ?? "", StringComparer.Ordinal).ThenBy(r => r.Id, StringComparer.Ordinal).ToList() }, Json);
        });
        app.MapGet("/api/results/{id}", (string id) =>
            LoadResult(id) is { Json: { } json, IsFile: true } ? Results.Json(ResultFiles.Open(json), Json) : Error(404, "Resultatet finns inte.", "Ladda om listan."));
        app.MapGet("/api/compare", (string? before, string? after) =>
        {
            if (string.IsNullOrEmpty(before) || string.IsNullOrEmpty(after) || before == after)
                return Error(400, "Välj två olika körningar att jämföra.", "Välj en tidigare och en senare körning.");
            if (LoadResult(before) is not { Json: { } old } || LoadResult(after) is not { Json: { } now })
                return Error(404, "Resultatet finns inte.", "Ladda om listan.");
            if (LayerOf(old) != LayerOf(now))
                return Error(400, "Körningarna är från olika lager.", "Jämför två API-körningar eller två UI-körningar.");
            return Results.Json(ResultFiles.Compare(old, now), Json);
        });

        // Scenarionamnen i bladet Tester, till flervalslistan. Läser bara skrivsättet (inte mot specen).
        app.MapGet("/api/scenarios", (HttpContext ctx) =>
        {
            var excel = ResolveExcel(ctx.Request.Query["excel"], ctx.Request.Query["upload"], out var error);
            if (error is not null) return error;
            if (excel is null) return Results.Json(new { hasSheet = false, scenarios = Array.Empty<string>() }, Json);
            try
            {
                var names = ExcelSupport.Source.ScenarioNames(excel);
                return Results.Json(new { hasSheet = true, scenarios = names }, Json);
            }
            catch (InvalidOperationException e) when (e.Message.Contains("saknar bladet"))
            {
                return Results.Json(new { hasSheet = false, scenarios = Array.Empty<string>() }, Json);
            }
            catch (Exception e)
            {
                return ErrorFrom(e, new ErrorContext { Excel = excel });
            }
        });

        // Delegattyperna anges uttryckligen: annars väljs RequestDelegate och IResult-svaret kastas bort.
        Func<HttpContext, Task<IResult>> upload = Upload, start = StartRun, map = MapAsync, mermaid = MermaidAsync;
        app.MapPost("/api/uploads", upload);
        app.MapPost("/api/map", map);
        app.MapPost("/api/map/mermaid", mermaid);
        app.MapPost("/api/runs", start);
        app.MapGet("/api/runs/{id}/events", async (HttpContext ctx, string id) => await Stream(ctx, id));

        // Sidan frågar vid start om en körning pågår (eller nyss avslutats) så att en omladdad sida kan ansluta igen.
        app.MapGet("/api/runs/latest", () =>
        {
            RunSession? run;
            lock (_gate) run = _current;
            if (run is null || run.Info is not { } i) return Results.Json(new { exists = false }, Json);
            return Results.Json(new
            {
                exists = true,
                id = run.Id,
                running = !run.Finished,
                cancelling = run.CancelRequested && !run.Finished,
                started = run.Started,
                done = run.ResultCount,
                total = run.Total,
                spec = i.Spec,
                specUpload = i.SpecUploadId,
                excel = i.Excel,
                upload = i.UploadId,
                env = i.Env,
                api = i.Api,
                seed = i.Seed,
                only = i.Only,
                timeout = i.Timeout,
                scenarios = i.Scenarios,
                excelOnly = i.ExcelOnly,
                project = i.Project,
            }, Json);
        });

        app.MapPost("/api/runs/{id}/cancel", (string id) =>
        {
            var run = Find(id);
            if (run is null) return Error(404, "Körningen finns inte.", "Starta en ny körning.");
            run.Cancel();
            return Results.Json(new { cancelling = !run.Finished }, Json, statusCode: 202);
        });

        app.MapGet("/api/runs/{id}/report", (string id, string? type) =>
        {
            var run = Find(id);
            if (run is null) return Error(404, "Körningen finns inte.", "Starta en ny körning.");
            if (!run.Finished) return Error(409, "Körningen är inte klar än.", "Vänta tills alla tester är klara och försök igen.");
            var stamp = ReportFileStem(run.Info?.Env, run.Started);
            return type switch
            {
                "html" when run.Html is { } h => Results.File(h, "text/html; charset=utf-8", $"{stamp}.html"),
                "xlsx" when run.Xlsx is { } x => Results.File(x, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"{stamp}.xlsx"),
                "html" or "xlsx" => Error(404, "Det finns ingen rapport eftersom inga tester hann köras.", "Kör testerna igen."),
                _ => Error(400, "Okänd rapporttyp.", "Välj html eller xlsx."),
            };
        });
    }

    // --- Resultat och projekt -----------------------------------------------------------

    /// <summary>Fakta om solutionen (--solution, annars den som hittas från aktuell mapp), eller null om ingen finns.</summary>
    private RepositoryFacts? Facts()
    {
        try
        {
            var solution = _options.Solution ?? SolutionFinder.TryFind(Directory.GetCurrentDirectory(), "testgen web");
            return solution is null ? null : RepositoryFacts.Read(solution);
        }
        catch (Exception e) when (e is UsageException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed record ProjectDto(string Name, string? Spec, string? LocalUrl);

    /// <summary>API-projekten: de i solutionen, annars de som testgen.yaml nämner.</summary>
    private static List<ProjectDto> Projects(TestGenSettings settings, RepositoryFacts? facts)
    {
        var fromSolution = facts?.Apis.Select(a => new ProjectDto(a.Name, settings.SpecOf(a.Name) ?? facts.SpecOf(a), a.LocalUrl)).ToList() ?? [];
        if (fromSolution.Count > 0) return fromSolution;
        return settings.EnvironmentNames.SelectMany(n => settings.Environment(n).ApiNames).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(n => new ProjectDto(n, settings.SpecOf(n), null)).ToList();
    }

    /// <summary>Miljön med adressen för varje API-projekt (utan token och headers). En adress som inte går att läsa utelämnas.</summary>
    private static object EnvironmentDto(TestGenSettings settings, string name, List<ProjectDto> projects)
    {
        var environment = settings.Environment(name);
        var apis = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var project in projects)
        {
            string? url = null;
            try { url = environment.Api(project.Name).Url; }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or UsageException) { /* saknad miljövariabel: ingen adress */ }
            url ??= name.Equals(TestGenSettings.LocalEnvironment, StringComparison.OrdinalIgnoreCase) ? project.LocalUrl : null;
            if (!string.IsNullOrWhiteSpace(url)) apis[project.Name] = url;
        }
        return new { name, apis };
    }

    private sealed record LoadedResult(JsonObject? Json, bool IsFile);

    /// <summary>Resultatet för ett id: en körning i sidan eller en resultatfil. Ett id som inte finns i listan ger null.</summary>
    private LoadedResult? LoadResult(string id)
    {
        lock (_gate)
            if (_runs.TryGetValue(id, out var run)) return new LoadedResult(run.Result, false);
        foreach (var (fileId, path) in ResultFiles.Discover(Facts()))
            if (fileId == id) return new LoadedResult(ResultFiles.Read(path), true);
        return null;
    }

    private static string LayerOf(JsonObject json) => json["layer"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "api";

    private static ResultRow RunRow(RunSession run) =>
        new(run.Id, "page", "api", run.Info?.Project ?? "–", JsonResult.Iso(run.Started), run.Counts.Passed, run.Counts.Failed, run.Counts.Skipped, !run.Finished);

    /// <summary>testrapport-&lt;miljö&gt;-ÅÅÅÅMMDD-TTMMSS (lokal tid); utan miljö lämnas den delen bort.</summary>
    internal static string ReportFileStem(string? env, DateTime started)
    {
        var slug = new string((env ?? "").Normalize(NormalizationForm.FormD)
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            .Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '-').ToArray()).Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return "testrapport-" + (slug.Length > 0 ? slug + "-" : "") + started.ToString("yyyyMMdd-HHmmss");
    }

    private static string Page()
    {
        using var s = typeof(WebServer).Assembly.GetManifestResourceStream("TestGen.Web.index.html")
                      ?? throw new InvalidOperationException("Sidan saknas i programmet (inbäddad resurs TestGen.Web.index.html).");
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    // --- Skydd --------------------------------------------------------------------------

    private async Task Guard(HttpContext ctx, RequestDelegate next)
    {
        var port = ctx.Connection.LocalPort;
        static bool LocalHost(string host) => host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host == "127.0.0.1";

        // Host: bara localhost/127.0.0.1 med vår port (skydd mot DNS rebinding).
        var host = ctx.Request.Host;
        if (!host.HasValue || !LocalHost(host.Host) || host.Port != port)
        {
            ctx.Response.StatusCode = 403;
            await ctx.Response.WriteAsync("Förfrågan avvisades: sidan nås bara via http://localhost:" + port);
            return;
        }
        // Origin (om webbläsaren skickar den): måste vara samma lokala adress, annars kan en annan webbplats ha ställt frågan.
        if (ctx.Request.Headers.Origin.FirstOrDefault() is { } origin &&
            !(Uri.TryCreate(origin, UriKind.Absolute, out var o) && LocalHost(o.Host) && o.Port == port))
        {
            ctx.Response.StatusCode = 403;
            await ctx.Response.WriteAsync("Förfrågan avvisades: okänt ursprung.");
            return;
        }

        var h = ctx.Response.Headers;
        h["Content-Security-Policy"] = "default-src 'none'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; connect-src 'self'; img-src data:; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
        h["X-Content-Type-Options"] = "nosniff";
        h["Referrer-Policy"] = "no-referrer";
        h["Cache-Control"] = "no-store";
        h["X-Frame-Options"] = "DENY";

        if (ctx.Request.Path.StartsWithSegments("/api"))
        {
            var given = Encoding.UTF8.GetBytes(ctx.Request.Headers[KeyHeader].ToString());
            if (!CryptographicOperations.FixedTimeEquals(given, _key))
            {
                ctx.Response.StatusCode = 401;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { message = "Sidan saknar giltig sessionsnyckel.", tip = "Ladda om sidan (F5). Öppna den via adressen som testgen web skrev ut." }, Json));
                return;
            }
        }
        await next(ctx);
    }

    private static IResult Error(int status, string message, string? tip = null, string? technical = null, string? field = null, string? runningId = null,
        IReadOnlyList<string>? items = null, IReadOnlyList<string>? warnings = null) =>
        Results.Json(new { message, tip, technical, field, runningId, items = items ?? Array.Empty<string>(), warnings = warnings ?? Array.Empty<string>() }, Json, statusCode: status);

    /// <summary>Felsvar byggt av den gemensamma översättaren (<see cref="FriendlyErrors"/>); flaggnamn byts mot fältnamn i sidan.</summary>
    private static IResult ErrorFrom(Exception e, ErrorContext ctx, int status = 400)
    {
        var f = WebErrors.Describe(e, ctx);
        return Error(status, f.What, f.Action, f.Technical, items: f.Items, warnings: f.Warnings);
    }

    // --- Filer --------------------------------------------------------------------------

    /// <summary>Excel-sökväg eller uppladdad fil -> sökväg på disk. null om inget angetts.</summary>
    private string? ResolveExcel(string? path, string? uploadId, out IResult? error)
    {
        error = null;
        if (!string.IsNullOrWhiteSpace(uploadId))
        {
            lock (_gate)
                if (_uploads.TryGetValue(uploadId, out var up) && up.Kind == "excel") return up.Path;
            error = Error(400, "Den uppladdade Excel-filen finns inte längre.", "Välj filen igen.");
            return null;
        }
        path = path?.Trim().Trim('"');
        if (string.IsNullOrEmpty(path)) return null;
        if (!File.Exists(path))
        {
            var missing = FriendlyErrors.Translate(new MissingSourceException(SourceKind.Excel, Path.GetFullPath(path)));
            error = Error(400, missing.What, "Kontrollera sökvägen, eller välj filen med \"Välj fil…\".");
            return null;
        }
        return path;
    }

    /// <summary>Uppladdad API-beskrivning -> sökväg på disk, annars null (då gäller sökvägen/URL:en i fältet).</summary>
    private string? ResolveUploadedSpec(string? uploadId, out IResult? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(uploadId)) return null;
        lock (_gate)
            if (_uploads.TryGetValue(uploadId, out var up) && up.Kind == "spec") return up.Path;
        error = Error(400, "Den uppladdade API-beskrivningen finns inte längre.", "Välj filen igen.", field: "spec");
        return null;
    }

    private static string? UploadKind(string name) =>
        name.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ? "excel" :
        name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ? "spec" : null;

    /// <summary>Tar bort äldsta uppladdningen som inte hör till senaste körningen. Anropas med <see cref="_gate"/> låst.</summary>
    private bool EvictOldestUpload()
    {
        var keep = new HashSet<string?> { _current?.Info?.UploadId, _current?.Info?.SpecUploadId };
        var victim = _uploads.Where(u => !keep.Contains(u.Key)).OrderBy(u => u.Value.Seq).Select(u => u.Key).FirstOrDefault();
        if (victim is null) return false;
        var dir = Path.GetDirectoryName(_uploads[victim].Path)!;
        _uploads.Remove(victim);
        try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return true;
    }

    private async Task<IResult> Upload(HttpContext ctx)
    {
        if (ctx.Request.ContentLength is > MaxUploadBytes)
            return Error(413, "Filen är för stor (högst 10 MB).", "En testfil i Excel brukar vara betydligt mindre. Kontrollera att du valt rätt fil.");
        lock (_gate)
            if (_uploads.Count >= MaxUploads && !EvictOldestUpload())
                return Error(429, "För många uppladdade filer i den här sessionen.", "Starta om 'testgen web' eller ange sökvägen till filen i stället.");

        var raw = ctx.Request.Query["name"].ToString();
        var name = new string(Path.GetFileName(raw).Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray());
        var kind = UploadKind(name);
        if (kind is null)
            return Error(400, "Bara Excel-filer (.xlsx) och API-beskrivningar (.json, .yaml, .yml) kan läsas.", "Välj en fil som slutar på .xlsx, .json, .yaml eller .yml.");

        var id = Guid.NewGuid().ToString("N");
        var dir = Directory.CreateDirectory(Path.Combine(_tempDir, id)).FullName;
        var file = Path.Combine(dir, name);
        try
        {
            ctx.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = MaxUploadBytes;
            await using (var fs = new FileStream(file, FileMode.CreateNew, FileAccess.Write))
                await ctx.Request.Body.CopyToAsync(fs, ctx.RequestAborted);
        }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException)
        {
            Directory.Delete(dir, true);
            return Error(413, "Filen är för stor (högst 10 MB).", "Kontrollera att du valt rätt fil.");
        }
        var head = new byte[4096];
        int read;
        using (var fs = File.OpenRead(file)) read = fs.Read(head, 0, head.Length);
        if (kind == "excel" && !(read >= 2 && head[0] == 'P' && head[1] == 'K'))
        {
            Directory.Delete(dir, true);
            return Error(400, "Filen ser inte ut som en Excel-arbetsbok (.xlsx).", "Välj en .xlsx-fil, t.ex. en mall skapad med 'testgen template'.");
        }
        if (kind == "spec" && (read == 0 || Array.IndexOf(head, (byte)0, 0, read) >= 0))
        {
            Directory.Delete(dir, true);
            return Error(400, "Filen ser inte ut som en API-beskrivning (text i JSON eller YAML).", "Välj filen som utvecklaren gav dig, t.ex. openapi.json eller openapi.yaml.");
        }
        lock (_gate) _uploads[id] = new UploadedFile(file, kind, ++_uploadSeq);
        return Results.Json(new { id, name, kind }, Json);
    }

    // --- Systemkarta ---------------------------------------------------------------------

    private sealed class MapRequest
    {
        public string? Solution { get; set; }
        public string? Spec { get; set; }
        public string? SpecUpload { get; set; }
        /// <summary>För Mermaid: projects, classes eller all.</summary>
        public string? Level { get; set; }
        /// <summary>För Mermaid: LR eller TB.</summary>
        public string? Direction { get; set; }
        /// <summary>För Mermaid: bara vägen för anropet, t.ex. <c>POST /orders</c>.</summary>
        public string? Entry { get; set; }
    }

    private sealed record CachedMap(string Key, string Stamp, CodeMapModel Model, object Dto);

    /// <summary>
    /// Läser källkoden till systemet som testas och svarar med systemkartan (samma JSON som <c>testgen map --out</c>, se CodeMapJson).
    /// Specen är valfri: med den kopplas ingångarna till operationerna, så att varje test kan visa sin väg.
    /// </summary>
    private Task<IResult> MapAsync(HttpContext ctx) => WithMapAsync(ctx, (map, _) => Results.Json(map.Dto, Json));

    /// <summary>Kartan som Mermaid (samma som <c>testgen map --mermaid</c>), med nivån, riktningen och anropet som sidan visar.</summary>
    private Task<IResult> MermaidAsync(HttpContext ctx) => WithMapAsync(ctx, (map, req) =>
        Results.Text(CodeMapMermaid.Write(map.Model, MermaidOptions.Parse(req.Level, req.Direction, req.Entry)), "text/plain; charset=utf-8"));

    /// <summary>Kartan för förfrågan: den sparade om koden inte ändrats sedan den lästes, annars läses koden igen.</summary>
    private async Task<IResult> WithMapAsync(HttpContext ctx, Func<CachedMap, MapRequest, IResult> respond)
    {
        MapRequest? req;
        try { req = await ctx.Request.ReadFromJsonAsync<MapRequest>(Json, ctx.RequestAborted); }
        catch (JsonException) { return Error(400, "Förfrågan kunde inte läsas.", "Ladda om sidan och försök igen."); }
        // Solutionen: den sidan skickar, annars den från --solution, annars letas den upp från mappen testgen startades i.
        string solution, full;
        try
        {
            solution = Blank(req?.Solution?.Trim().Trim('"')) ?? _options.Solution ?? SolutionFinder.Find(Directory.GetCurrentDirectory(), "testgen web");
            full = SolutionLoader.Check(solution);
        }
        catch (UsageException e) { return Error(400, e.Message, e.Tip ?? ""); }
        var uploadedSpec = ResolveUploadedSpec(req!.SpecUpload, out var specError);
        if (specError is not null) return specError;
        var spec = uploadedSpec ?? Blank(req.Spec?.Trim().Trim('"'));

        await _mapGate.WaitAsync(ctx.RequestAborted);
        try
        {
            var key = full + "|" + spec;
            var stamp = SourceStamp(full);
            if (_mapCache is { } cached && cached.Key == key && cached.Stamp == stamp) return respond(cached, req!);

            var warnings = new List<string>();
            IReadOnlyList<ApiOperation>? operations = null;
            if (spec is not null)
            {
                try { operations = OpenApiLoader.Load(spec).ForMap(); }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    warnings.Add($"API-beskrivningen gick inte att läsa ({WebErrors.Describe(e, new ErrorContext { Spec = spec }).What}). Kartan visas utan koppling till testerna.");
                }
            }
            using var loaded = await SolutionLoader.LoadAsync(full, restore: true, ct: ctx.RequestAborted);
            var model = await CodeMapper.AnalyzeAsync(loaded.Solution, solution, operations, loaded.Warnings.Concat(warnings), ctx.RequestAborted);
            _mapCache = new CachedMap(key, stamp, model, CodeMapJson.ToDto(model));
            return respond(_mapCache, req!);
        }
        catch (Exception e) when (e is SourceException or MissingSourceException or UsageException)
        {
            return ErrorFrom(e, new ErrorContext { Spec = spec });
        }
        finally
        {
            _mapGate.Release();
        }
    }

    /// <summary>Ändras när någon källkods- eller projektfil under solutionens katalog ändras, läggs till eller tas bort.</summary>
    private static string SourceStamp(string solution)
    {
        var files = SolutionLoader.SourceFiles(Path.GetDirectoryName(solution)!, ".cs", ".csproj", ".sln", ".slnx", ".props", ".targets").ToList();
        return $"{files.Count}:{(files.Count == 0 ? 0 : files.Max(f => f.Written.Ticks))}";
    }

    // --- Körningar ----------------------------------------------------------------------

    private sealed class StartRequest
    {
        public string? Spec { get; set; }
        public string? SpecUpload { get; set; }
        public string? Excel { get; set; }
        public string? Upload { get; set; }
        public string? Env { get; set; }
        public string? Api { get; set; }
        /// <summary>API-projektet (som i solutionen och testgen.yaml); utan värde används det första.</summary>
        public string? Project { get; set; }
        public string? Token { get; set; }
        public string? Seed { get; set; }
        public string? Only { get; set; }
        public string? Timeout { get; set; }
        public List<string>? Scenarios { get; set; }
        public bool ExcelOnly { get; set; }
        /// <summary>Test-id (T-018) att köra om; behåller id och testdata från en full körning.</summary>
        public List<string>? Tests { get; set; }
    }

    private RunSession? Find(string id)
    {
        lock (_gate) return _runs.GetValueOrDefault(id);
    }

    private async Task<IResult> StartRun(HttpContext ctx)
    {
        if (ctx.Request.ContentLength is > 64 * 1024) return Error(413, "Förfrågan är för stor.");
        StartRequest? req;
        try { req = await ctx.Request.ReadFromJsonAsync<StartRequest>(Json, ctx.RequestAborted); }
        catch (JsonException) { return Error(400, "Förfrågan kunde inte läsas.", "Ladda om sidan och försök igen."); }
        if (req is null) return Error(400, "Förfrågan saknas.");

        var uploadedSpec = ResolveUploadedSpec(req.SpecUpload, out var specError);
        if (specError is not null) return specError;
        var spec = uploadedSpec ?? req.Spec?.Trim().Trim('"');
        if (string.IsNullOrEmpty(spec))
            return Error(400, "Ange en API-beskrivning (OpenAPI-fil).", "Skriv sökvägen till filen, eller en webbadress som ofta slutar på /openapi.json, i fältet API-beskrivning.", field: "spec");
        var specIsUrl = uploadedSpec is null && Sources.IsUrl(spec);
        if (!specIsUrl && !File.Exists(spec))
            return Error(400, FriendlyErrors.Translate(new MissingSourceException(SourceKind.Spec, Path.GetFullPath(spec))).What.Replace("Hittar inte specen", "Hittar inte API-beskrivningen"),
                "Kontrollera sökvägen, eller skriv en webbadress som börjar med http:// eller https://.", field: "spec");

        var excel = ResolveExcel(req.Excel, req.Upload, out var excelError);
        if (excelError is not null) return excelError;

        var scenarios = (req.Scenarios ?? new()).Select(n => n?.Trim() ?? "").Where(n => n.Length > 0).Distinct().Take(500).ToList();
        if (excel is null && (scenarios.Count > 0 || req.ExcelOnly))
            return Error(400, req.ExcelOnly ? "\"Bara Excel-testerna\" kräver en Excel-fil." : "Scenarier kräver en Excel-fil (scenarierna finns i bladet Tester).",
                "Välj en Excel-fil, eller ta bort valet under Fler inställningar.", field: "excel");

        int? seed = null;
        var seedRandom = false;
        if (!string.IsNullOrWhiteSpace(req.Seed))
        {
            if (req.Seed.Trim().ToLowerInvariant() is "random") seedRandom = true;
            else if (!int.TryParse(req.Seed.Trim(), out var n)) return Error(400, $"Slumpfrö (seed) måste vara ett heltal eller ordet random, inte '{req.Seed}'.", "Skriv ett heltal, t.ex. 2, skriv random, eller lämna fältet tomt.", field: "seed");
            else seed = n;
        }

        RunSettings settings;
        ProjectDto? project;
        try
        {
            var loaded = LoadSettings();
            var environment = loaded.Environment(Blank(req.Env));
            var known = Projects(loaded, Facts());
            project = Blank(req.Project) is { } named
                ? known.FirstOrDefault(p => p.Name.Equals(named, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"Okänt API-projekt '{named}'.")
                : known.FirstOrDefault();
            settings = RunSettings.Resolve(Blank(req.Api), Array.Empty<string>(), Blank(req.Token), Blank(req.Timeout), environment, project?.Name, project?.LocalUrl);
        }
        catch (ArgumentException e) when (e.Message.StartsWith("API:ets adress saknas"))
        {
            return Error(400, "API-adressen saknas.", "Skriv adressen till systemet som ska testas, t.ex. https://test.exempel.se, eller välj en miljö som har en adress.", field: "api");
        }
        catch (Exception e) when (e is ArgumentException or IOException or InvalidOperationException or ExcelValidationException or UsageException or SourceException)
        {
            return ErrorFrom(e, new ErrorContext { Excel = excel, Api = Blank(req.Api) });
        }

        RunSession run;
        lock (_gate)
        {
            if (_current is { Finished: false })
                return Error(409, "En körning pågår redan.", "Vänta tills den är klar, eller avbryt den, innan du startar en ny.", runningId: _current.Id);
            run = _current = new RunSession();
            _runs[run.Id] = run;
            while (_runs.Count > 5) _runs.Remove(_runs.Values.OrderBy(r => r.Started).First().Id);
        }
        var shownExcel = string.IsNullOrWhiteSpace(req.Upload) ? Blank(req.Excel) : Path.GetFileName(excel);
        var shownSpec = uploadedSpec is null ? spec : Path.GetFileName(uploadedSpec);
        run.Start(settings, new RunInput(spec, excel, seed, Blank(req.Only), scenarios, req.ExcelOnly) { SeedRandom = seedRandom, Tests = (req.Tests ?? new()).Select(t => t?.Trim() ?? "").Where(t => t.Length > 0).Distinct().Take(500).ToList() }, _tempDir, _options.HandlerFactory?.Invoke(),
            new RunInfo(shownSpec, uploadedSpec is null ? null : Blank(req.SpecUpload), shownExcel, Blank(req.Upload), settings.EnvName, settings.Api,
                Blank(req.Seed), Blank(req.Only), Blank(req.Timeout), scenarios, req.ExcelOnly, project?.Name));
        return Results.Json(new { id = run.Id, api = settings.Api, env = settings.EnvName }, Json, statusCode: 202);
    }

    /// <summary>testgen.yaml som hör till solutionen (eller aktuell mapp); utan fil: standardvärden.</summary>
    private TestGenSettings LoadSettings() =>
        _options.Config is { } config ? TestGenSettings.LoadFile(config) : Facts()?.LoadSettings() ?? TestGenSettings.Load(Directory.GetCurrentDirectory());

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private async Task Stream(HttpContext ctx, string id)
    {
        var run = Find(id);
        if (run is null) { await Error(404, "Körningen finns inte.", "Starta en ny körning.").ExecuteAsync(ctx); return; }
        ctx.Response.ContentType = "text/event-stream; charset=utf-8";
        ctx.Response.Headers["Cache-Control"] = "no-store";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

        var abort = ctx.RequestAborted;
        var sent = 0;
        try
        {
            while (true)
            {
                var (batch, complete) = run.Read(sent);
                foreach (var ev in batch)
                    await ctx.Response.WriteAsync($"event: {ev.Name}\ndata: {ev.Json}\n\n", abort);
                sent += batch.Count;
                if (batch.Count > 0) await ctx.Response.Body.FlushAsync(abort);
                if (complete && batch.Count == 0) break;
                if (batch.Count > 0) continue;
                try { await run.WaitForMore(sent, abort).WaitAsync(TimeSpan.FromSeconds(15), abort); }
                catch (TimeoutException)
                {
                    await ctx.Response.WriteAsync(": heartbeat\n\n", abort);   // håller anslutningen vid liv
                    await ctx.Response.Body.FlushAsync(abort);
                }
            }
        }
        catch (OperationCanceledException) { /* webbläsaren stängde sidan; körningen fortsätter oberoende */ }
    }
}
