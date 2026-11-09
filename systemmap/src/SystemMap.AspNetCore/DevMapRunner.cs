using System.Diagnostics;
using System.IO.Enumeration;
using System.Text.Json;

namespace SystemMap.AspNetCore;

internal enum DevMapState { None, Running, Ready, Failed }

/// <summary>
/// Ritar kartan på begäran i utvecklingsläget: startar verktyget i en egen process (en körning i taget), och ritar om när
/// någon källfil blivit nyare än den senaste körningen startade. Ett fel ligger kvar tills användaren försöker igen eller en
/// källfil ändras.
/// </summary>
internal sealed class DevMapRunner(string directory)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private DevMapState _state;
    private DateTime _started;
    private DateTime _finished;
    private DateTime _checked;
    private string _message = "";
    private string? _sourceRoot;

    public string DevJson => Path.Combine(directory, "dev.json");
    public string HtmlFile => Path.Combine(directory, "dev", "systemmap.html");
    public string JsonFile => Path.Combine(directory, "dev", "map.json");

    /// <summary>Tillståndet nu. Startar en körning första gången, eller när en källfil ändrats (kontrolleras högst varannan sekund).</summary>
    public (DevMapState State, int Seconds, string Message) Get(bool startIfNeeded = true)
    {
        lock (_gate)
        {
            if (startIfNeeded)
            {
                if (_state == DevMapState.None) Start();
                else if (_state != DevMapState.Running && DateTime.UtcNow - _checked >= CheckEvery)
                {
                    _checked = DateTime.UtcNow;
                    if (SourceChanged()) Start();
                }
            }
            return Snapshot();
        }
    }

    /// <summary>Försöker igen efter ett fel.</summary>
    public void Retry()
    {
        lock (_gate)
            if (_state == DevMapState.Failed) Start();
    }

    private (DevMapState, int, string) Snapshot()
    {
        var end = _state == DevMapState.Running ? DateTime.UtcNow : _finished;
        return (_state, _state == DevMapState.None ? 0 : (int)(end - _started).TotalSeconds, _message);
    }

    private void Start()
    {
        _state = DevMapState.Running;
        _started = DateTime.UtcNow;
        _checked = _started;
        _message = "";
        _ = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        // Ett oväntat fel får inte lämna tillståndet som Running, för då väntar sidan för alltid.
        bool ok; string message;
        try { (ok, message) = await ExecuteAsync(); }
        catch (Exception e) { (ok, message) = (false, e.Message); }
        lock (_gate)
        {
            _state = ok ? DevMapState.Ready : DevMapState.Failed;
            _message = message;
            _finished = DateTime.UtcNow;
        }
    }

    private async Task<(bool Ok, string Message)> ExecuteAsync()
    {
        try
        {
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(DevJson));
            string? Read(string name) => json.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var (dotnet, tool, projectDir, solution, spec) = (Read("dotnet"), Read("tool"), Read("projectDir"), Read("solution"), Read("spec"));
            if (dotnet is null || tool is null || projectDir is null) return (false, $"{DevJson} saknar dotnet, tool eller projectDir.");
            lock (_gate) _sourceRoot = SourceRoot(projectDir, solution);

            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(HtmlFile)!);
            var start = new ProcessStartInfo(dotnet)
            {
                WorkingDirectory = projectDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var arg in new[] { "exec", tool }) start.ArgumentList.Add(arg);
            if (spec is not null) { start.ArgumentList.Add("--spec"); start.ArgumentList.Add(spec); }
            if (solution is not null) { start.ArgumentList.Add("--solution"); start.ArgumentList.Add(solution); }
            foreach (var arg in new[] { "--html", HtmlFile, "--json", JsonFile, "--no-restore", "--no-browser" }) start.ArgumentList.Add(arg);

            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(Timeout);
            try { await process.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return (false, "Kartan blev inte klar på 5 minuter.");
            }
            // Ett barnbarn som ärvt rören (t.ex. en MSBuild-nod) får inte hålla oss kvar: det hängde CI i 15 minuter.
            // Vi tar det som hunnit komma.
            var (output, error) = (await ReadOrPartial(stdout), await ReadOrPartial(stderr));
            if (process.ExitCode == 0)
                return File.Exists(HtmlFile) && File.Exists(JsonFile) ? (true, "") : (false, "Verktyget skrev ingen karta.");
            var errors = Lines(error);
            return (false, string.Join("\n", errors.Count > 0 ? errors.Take(10) : Lines(output).TakeLast(10)));
        }
        catch (Exception e) when (e is IOException or JsonException or System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            return (false, e.Message);
        }
    }

    private static async Task<string> ReadOrPartial(Task<string> read)
    {
        try { return await read.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException) { return ""; }
    }

    private static List<string> Lines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>Solutionens katalog; annars första katalogen uppåt med en solution (stopp vid .git); annars projektets.</summary>
    private static string SourceRoot(string projectDir, string? solution)
    {
        if (solution is not null) return Path.GetDirectoryName(Path.GetFullPath(solution, projectDir))!;
        for (var dir = new DirectoryInfo(projectDir); dir is not null; dir = dir.Parent)
        {
            // Filändelsen jämförs själv: i Windows träffar mönstret *.sln även .slnx.
            if (dir.GetFiles().Any(f => f.Extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)))
                return dir.FullName;
            if (Path.Exists(Path.Combine(dir.FullName, ".git"))) break;
        }
        return projectDir;
    }

    private bool SourceChanged() =>
        _sourceRoot is not null && System.IO.Directory.Exists(_sourceRoot) && SourceFiles(_sourceRoot, ".cs", ".csproj").Any(written => written > _started);

    /// <summary>Ändringstiderna för filerna under mappen, utan att gå in i bin, obj, .git och node_modules.</summary>
    private static IEnumerable<DateTime> SourceFiles(string dir, params string[] extensions) =>
        new FileSystemEnumerable<DateTime>(dir, (ref FileSystemEntry e) => e.LastWriteTimeUtc.UtcDateTime,
            new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
        {
            ShouldRecursePredicate = (ref FileSystemEntry e) => e.FileName is not ("bin" or "obj" or ".git" or "node_modules"),
            ShouldIncludePredicate = (ref FileSystemEntry e) =>
            {
                if (e.IsDirectory) return false;
                foreach (var x in extensions)
                    if (e.FileName.EndsWith(x, StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            },
        };
}
