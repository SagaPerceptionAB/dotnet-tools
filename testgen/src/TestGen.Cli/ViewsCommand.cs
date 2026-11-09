using TestGen.Browser.Probe;
using TestGen.Core.Projects;
using TestGen.Core.Run;
using TestGen.Core.Views;
using TestGen.Views;

namespace TestGen.Cli;

/// <summary>testgen views: skriver vyerna, formulären och API-nycklarna till views.json.</summary>
internal static class ViewsCommand
{
    public static async Task<int> RunAsync(string[] args, ErrorContext ctx, Terminal terminal, CancellationToken ct = default)
    {
        var a = CommandArgs.Parse("views", args, ["solution", "out", "max-pages"], "no-restore", "probe");
        var maxPages = new ProbeOptions().MaxPages;
        if (a.Get("max-pages") is { } mp && !(int.TryParse(mp, out maxPages) && maxPages > 0))
            throw new UsageException($"--max-pages måste vara ett positivt heltal, inte '{mp}'.", "Skriv t.ex. --max-pages 100");
        var solution = a.Get("solution") ?? SolutionFinder.Find(Directory.GetCurrentDirectory(), "testgen views");
        var facts = RepositoryFacts.Read(solution);
        var path = Path.GetFullPath(a.Get("out") ?? ViewsLocator.Default(facts, Directory.GetCurrentDirectory()));

        var written = await WriteAsync(facts, path, !a.Flag("no-restore"), a.Flag("probe") ? new ProbeOptions(MaxPages: maxPages) : null, terminal, ct);
        Summary(written.File, terminal.Out);
        return ExitCodes.Ok;
    }

    internal sealed record Written(ViewsFile File, bool Changed);

    /// <summary>Läser vyerna (och sonderar dem med <paramref name="probe"/>) och skriver <paramref name="path"/>, men bara om innehållet ändras.</summary>
    internal static async Task<Written> WriteAsync(RepositoryFacts facts, string path, bool restore, ProbeOptions? probe, Terminal terminal, CancellationToken ct)
    {
        var result = await ViewsGenerator.GenerateAsync(facts, Path.GetDirectoryName(path)!, restore, terminal.Err.WriteLine, ct);
        var file = probe is null ? result.File : await ViewProbe.RunAsync(facts, result.File, probe, terminal, ct);
        foreach (var warning in result.Warnings) terminal.Warning(warning);

        var text = ViewsJson.Serialize(file);
        var what = $"vyer: {file.Apps.Sum(x => x.Views.Count)}, appar: {file.Apps.Count}";
        var changed = !File.Exists(path) || await File.ReadAllTextAsync(path, ct) != text;
        if (changed)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, text, ct);
            terminal.Confirm(path, what);
        }
        else terminal.Err.WriteLine($"{Terminal.Relative(path)} är oförändrad ({what})");
        return new Written(file, changed);
    }

    /// <summary>Per app: en rubrikrad med API:erna och sedan en rad per vy i fasta kolumner (vy, sort, formulär, operation).</summary>
    internal static void Summary(ViewsFile file, TextWriter o)
    {
        foreach (var app in file.Apps)
        {
            var calls = app.Apis.Count == 0 ? "" : "   anropar " + string.Join(", ", app.Apis.Select(x => x.ConfigKey is null ? x.Project : $"{x.Project} via {x.ConfigKey}"));
            o.WriteLine($"{app.Project}{calls}");
            var idWidth = app.Views.Max(v => v.Id.Length);
            var kindWidth = app.Views.Max(v => v.Kind.Name().Length);
            foreach (var view in app.Views)
            {
                var forms = view.Forms.Count == 0 ? "-" : $"{view.Forms.Count} formulär";
                var operations = string.Join(", ", view.Forms.Select(f => f.OperationId).OfType<string>());
                o.WriteLine($"  {view.Id.PadRight(idWidth)}  {view.Kind.Name().PadRight(kindWidth)}  {forms.PadRight(11)}{(operations.Length == 0 ? "" : "  " + operations)}".TrimEnd());
            }
        }
    }
}
