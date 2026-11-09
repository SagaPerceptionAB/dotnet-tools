using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace TestGen.Cli;

/// <summary>
/// En webbapp som körs med <c>dotnet run</c> på localhost, port 0, med inställningar som miljövariabler. Används av <c>testgen views --probe</c> och av <c>testgen init</c> för att hämta specen;
/// UI-testerna kör appen i testprocessen i stället. Processen och dess barn stoppas när objektet kastas.
/// </summary>
internal sealed partial class AppProcess : IAsyncDisposable
{
    private readonly Process _process;

    private AppProcess(Process process, string url)
    {
        _process = process;
        Url = url;
    }

    public string Url { get; }

    /// <summary>Startar appen och väntar tills den lyssnar. Fel om den avslutas eller inte hunnit upp inom <paramref name="timeout"/> (bygget ingår).</summary>
    public static async Task<AppProcess> StartAsync(string projectPath, IReadOnlyDictionary<string, string> settings, string environment, TimeSpan timeout, CancellationToken ct = default)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(projectPath)!,
        };
        foreach (var arg in new[] { "run", "--project", projectPath, "--no-launch-profile" }) info.ArgumentList.Add(arg);
        info.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        info.Environment["ASPNETCORE_ENVIRONMENT"] = environment;
        info.Environment["OTEL_EXPORTER_OTLP_ENDPOINT"] = "";
        foreach (var (key, value) in settings) info.Environment[key.Replace(":", "__", StringComparison.Ordinal)] = value;

        var process = new Process { StartInfo = info };
        var output = new StringBuilder();
        var listening = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnLine(object? _, DataReceivedEventArgs e)
        {
            if (e.Data is null) return;
            lock (output) output.AppendLine(e.Data);
            if (Listening().Match(e.Data) is { Success: true } m) listening.TrySetResult(m.Groups[1].Value);
        }
        process.OutputDataReceived += OnLine;
        process.ErrorDataReceived += OnLine;
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => listening.TrySetException(new InvalidOperationException($"Appen avslutades (exitkod {SafeExitCode(process)}) innan den lyssnade."));
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timer.CancelAfter(timeout);
            var url = await listening.Task.WaitAsync(timer.Token);
            return new AppProcess(process, url);
        }
        catch (Exception e) when (e is OperationCanceledException or InvalidOperationException)
        {
            var tail = string.Join(Environment.NewLine, Tail(output));
            Stop(process);
            if (e is OperationCanceledException && ct.IsCancellationRequested) throw;
            var why = e is OperationCanceledException ? $"Appen lyssnade inte inom {timeout.TotalSeconds:0} s." : e.Message;
            throw new IOException($"{why}{Environment.NewLine}{tail}", e);
        }
    }

    private static int SafeExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch (InvalidOperationException) { return -1; }
    }

    private static IEnumerable<string> Tail(StringBuilder output)
    {
        lock (output) return output.ToString().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).TakeLast(15).ToList();
    }

    private static void Stop(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { /* redan avslutad */ }
        process.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Stop(_process);
        return ValueTask.CompletedTask;
    }

    [GeneratedRegex(@"Now listening on:\s*(http://\S+)")]
    private static partial Regex Listening();
}
