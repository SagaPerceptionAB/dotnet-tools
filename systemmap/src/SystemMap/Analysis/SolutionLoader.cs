using System.Diagnostics;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace SystemMap;

/// <summary>En inläst solution. Håll den öppen medan kartan byggs: MSBuild-processen bakom stängs med <see cref="Dispose"/>.</summary>
public sealed class LoadedSolution(MSBuildWorkspace workspace, Solution solution, IReadOnlyList<string> warnings) : IDisposable
{
    public Solution Solution { get; } = solution;
    public IReadOnlyList<string> Warnings { get; } = warnings;
    public void Dispose() => workspace.Dispose();
}

/// <summary>
/// Läser in en solution (<c>.sln</c>, <c>.slnx</c>) eller ett projekt (<c>.csproj</c>) med MSBuild, som Visual Studio gör.
/// Projektfilerna utvärderas av MSBuild precis som vid <c>dotnet build</c>.
/// </summary>
public static class SolutionLoader
{
    /// <summary>Den fullständiga sökvägen, om den pekar på en solution eller ett projekt som finns. Annars ett fel att visa.</summary>
    public static string Check(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw UsageException.MissingFile("solutionen", full);
        var extension = Path.GetExtension(full);
        if (!SolutionFinder.SolutionExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) && !extension.Equals(SolutionFinder.ProjectExtension, StringComparison.OrdinalIgnoreCase))
            throw new UsageException($"{full} är ingen solution eller projektfil.",
                "Ange en .sln- eller .slnx-fil (eller en .csproj), t.ex. --solution samples/Shop.slnx.");
        return full;
    }

    /// <param name="restore">
    /// Kör <c>dotnet restore</c> först så att paketens typer går att läsa (t.ex. EF Core, Service Bus). Hoppas över när
    /// paketen redan är hämtade för alla projekt under solutionens katalog.
    /// </param>
    /// <param name="progress">Får en rad om vad som händer, t.ex. "Läser solutionen ...".</param>
    public static async Task<LoadedSolution> LoadAsync(string path, bool restore, Action<string>? progress = null, CancellationToken ct = default)
    {
        var full = Check(path);

        var warnings = new List<string>();
        if (restore && RestoreNeeded(full))
        {
            progress?.Invoke("Hämtar paketen (dotnet restore) ...");
            var (ok, output) = await RestoreAsync(full, ct);
            if (!ok)
                warnings.Add("dotnet restore misslyckades, så typer från paket kan saknas. Integrationer känns då igen på namnet. " +
                              $"Kör dotnet restore själv för att se felet{(output is null ? "" : $" ({output})")}.");
        }

        progress?.Invoke("Läser solutionen ...");
        var workspace = MSBuildWorkspace.Create();
        workspace.RegisterWorkspaceFailedHandler(e =>
        {
            if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure) lock (warnings) warnings.Add("MSBuild: " + e.Diagnostic.Message);
        });
        try
        {
            var solution = Path.GetExtension(full).Equals(SolutionFinder.ProjectExtension, StringComparison.OrdinalIgnoreCase)
                ? (await workspace.OpenProjectAsync(full, cancellationToken: ct)).Solution
                : await workspace.OpenSolutionAsync(full, cancellationToken: ct);
            if (!solution.Projects.Any())
                throw new UsageException($"Solutionen {full} innehåller inga projekt som gick att läsa.",
                    "Kontrollera att dotnet build fungerar för solutionen.", string.Join(" ", warnings));
            return new LoadedSolution(workspace, solution, warnings);
        }
        catch (Exception e) when (e is not (UsageException or OperationCanceledException))
        {
            workspace.Dispose();
            throw new UsageException($"Kunde inte läsa solutionen {full}.",
                "Kontrollera att dotnet build fungerar för solutionen och att .NET SDK är installerat.", e.Message);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Källfilerna under <paramref name="dir"/> med någon av ändelserna, med senaste ändringstid. Kataloger för byggresultat och
    /// verktyg (bin, obj, .git, node_modules) hoppas över utan att gås igenom.
    /// </summary>
    public static IEnumerable<(string Path, DateTime Written)> SourceFiles(string dir, params string[] extensions) =>
        new FileSystemEnumerable<(string, DateTime)>(dir, (ref FileSystemEntry e) => (e.ToFullPath(), e.LastWriteTimeUtc.UtcDateTime),
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

    /// <summary>Sant om något projekt under solutionens katalog saknar obj/project.assets.json eller har ändrats sedan den skrevs.</summary>
    private static bool RestoreNeeded(string solution)
    {
        var projects = Path.GetExtension(solution).Equals(SolutionFinder.ProjectExtension, StringComparison.OrdinalIgnoreCase)
            ? [solution]
            : SourceFiles(Path.GetDirectoryName(solution)!, ".csproj").Select(f => f.Path).ToList();
        return projects.Count == 0 || projects.Any(project =>
        {
            var assets = Path.Combine(Path.GetDirectoryName(project)!, "obj", "project.assets.json");
            return !File.Exists(assets) || File.GetLastWriteTimeUtc(assets) < File.GetLastWriteTimeUtc(project);
        });
    }

    private static async Task<(bool Ok, string? Output)> RestoreAsync(string path, CancellationToken ct)
    {
        var start = new ProcessStartInfo(DotnetPath()) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        // Inga byggservrar som lever kvar och ärver stdout/stderr (se väntan på utskriften nedan).
        foreach (var arg in new[] { "restore", path, "--nologo", "-v", "q", "--disable-build-servers" }) start.ArgumentList.Add(arg);
        try
        {
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            try { await process.WaitForExitAsync(ct); }
            catch (OperationCanceledException) { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } throw; }
            // Ett barnbarn som ärvt rören (t.ex. en MSBuild-nod) får inte hålla oss kvar: det hängde CI i 15 minuter.
            // Vi tar det som hunnit komma.
            var text = await ReadOrPartial(stdout) + "\n" + await ReadOrPartial(stderr);
            var output = string.Join(" ", text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)).Take(2));
            return (process.ExitCode == 0, output.Length > 0 ? output : null);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            return (false, e.Message);
        }
    }

    private static async Task<string> ReadOrPartial(Task<string> read)
    {
        try { return await read.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException) { return ""; }
    }

    /// <summary>dotnet-programmet: det som startade oss (DOTNET_HOST_PATH), det i runtimens katalog, eller "dotnet" i PATH.</summary>
    private static string DotnetPath()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host)) return host;
        var exe = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var runtimeRoot = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        var candidate = Path.Combine(runtimeRoot, exe);
        return File.Exists(candidate) ? candidate : "dotnet";
    }
}
