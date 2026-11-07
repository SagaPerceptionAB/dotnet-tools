using System.Runtime.InteropServices;
using SystemMap;
using TestGen.Core;
using TestGen.Core.Run;

namespace TestGen.Cli;

public static class Program
{
    public static int Main(string[] args)
    {
        var ctx = new ErrorContext();
        return Command.Run(args, () => Dispatch(args, ctx), e => FriendlyErrors.Translate(e, ctx));
    }

    private static int Dispatch(string[] args, ErrorContext ctx)
    {
        if (args.Length == 0)
        {
            Console.WriteLine(HelpText.General);
            return ExitCodes.Usage;
        }
        var cmd = args[0] == "karta" ? "map" : args[0];
        if (cmd is "-h" or "--help") { Console.WriteLine(HelpText.General); return ExitCodes.Ok; }
        if (cmd == "help")
        {
            var text = args.Length > 1 ? HelpText.For(args[1]) : null;
            Console.WriteLine(text ?? HelpText.General);
            return ExitCodes.Ok;
        }
        if (cmd.StartsWith('-'))
            throw new UsageException($"Kommando saknas före {cmd}.",
                cmd is "--out" or "--schema"
                    ? "Menade du: testgen generate " + string.Join(' ', args) + " ?"
                    : $"Skriv ett kommando först, t.ex. testgen run {cmd} ... Kommandon: {string.Join(", ", HelpText.Commands)}.");
        if (!HelpText.Commands.Contains(cmd))
        {
            var hint = Suggest.Closest(cmd, HelpText.Commands) is { } m ? $" Menade du '{m}'?" : "";
            throw new UsageException($"Okänt kommando: {cmd}.{hint}", $"Giltiga kommandon: {string.Join(", ", HelpText.Commands)}. Se: testgen --help");
        }
        var rest = args[1..];
        if (rest.Any(a => a is "-h" or "--help")) { Console.WriteLine(HelpText.For(cmd)); return ExitCodes.Ok; }
        return cmd switch
        {
            "run" => RunAsync(rest, ctx).GetAwaiter().GetResult(),
            "template" => Template(rest, ctx),
            "ui" => UiAsync(rest, ctx).GetAwaiter().GetResult(),
            "map" => MapAsync(rest, ctx).GetAwaiter().GetResult(),
            _ => Generate(rest, ctx),
        };
    }

    private static int Generate(string[] args, ErrorContext ctx)
    {
        var o = CliOptions.Parse(args);
        var configPath = o.Config ?? (File.Exists("testgen.yaml") ? "testgen.yaml" : null);
        ctx.Config = configPath;
        if (o.Config is not null && !File.Exists(o.Config))
            throw new MissingSourceException(SourceKind.Config, Path.GetFullPath(o.Config));
        var cfg = TestGenConfig.Load(configPath);

        var spec = o.Spec ?? (cfg.Spec is null ? null : Sources.IsUrl(cfg.Spec) ? cfg.Spec : cfg.Resolve(cfg.Spec));
        ctx.Spec = spec;
        var generate = o.SchemaFile is null || o.Out is not null;

        if (o.SchemaFile is not null)
        {
            var ids = spec is null ? null : OpenApiLoader.Load(spec).Select(x => x.OperationId);
            var dir = Path.GetDirectoryName(Path.GetFullPath(o.SchemaFile));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(o.SchemaFile, ManualScenarioSchema.Build(ids));
            Console.WriteLine($"Skrev JSON Schema: {o.SchemaFile}");
        }

        if (!generate) return ExitCodes.Ok;
        if (spec is null) throw new UsageException("Ange --spec eller spec i testgen.yaml.", "Se: testgen generate --help");
        var outDir = o.Out ?? (cfg.Output is null ? null : cfg.Resolve(cfg.Output));
        if (outDir is null) throw new UsageException("Ange --out eller output i testgen.yaml.", "Se: testgen generate --help");

        var ops = OpenApiLoader.Load(spec);
        var files = new ScenarioGenerator(ops, cfg).WriteAll(ops, outDir);
        Console.WriteLine($"Skrev {files} scenariofiler till {outDir}");
        return ExitCodes.Ok;
    }

    /// <summary>Exitkoden om en signal tagits emot (130 SIGINT, 143 SIGTERM), annars 0. Sätts av signalhanteraren.</summary>
    private static int _signalExit;

    private static async Task<int> RunAsync(string[] args, ErrorContext ctx)
    {
        var a = CommandArgs.Parse("run", args, new[] { "spec", "api", "excel", "seed", "only", "config", "header", "token", "timeout", "env", "report", "scenario", "test", "fail-on", "jamfor", "baseline", "max-time" },
            "force", "verbose", "klartext", "bara-excel", "excel-only", "fail-on-new", "bara-nya-fel", "allow-empty");
        var spec = a.Require("spec");
        ctx.Spec = spec; ctx.Excel = a.Get("excel"); ctx.Config = a.Get("config"); ctx.Report = a.GetAll("report").FirstOrDefault(); ctx.Api = a.Get("api");
        var seedChoice = a.Get("seed") is { } sv ? SeedSpec.Parse(sv) : null;
        var failOn = a.Get("fail-on") is { } fo ? CauseCodes.ParseFailOn(fo) : null;
        int? maxTime = null;
        if (a.Get("max-time") is { } mt)
            maxTime = int.TryParse(mt.Trim(), out var secs) && secs > 0 ? secs
                : throw new UsageException($"--max-time måste vara ett positivt antal sekunder, inte '{mt}'.", "Skriv t.ex. --max-time 600 för att avbryta körningen efter 10 minuter.");
        var baseline = a.Get("jamfor") ?? a.Get("baseline");
        if (a.Get("jamfor") is not null && a.Get("baseline") is not null && a.Get("jamfor") != a.Get("baseline"))
            throw new UsageException("--jamfor och --baseline är samma flagga men har olika värden.", "Ange bara en av dem.");
        if (a.Get("config") is { } c && !File.Exists(c)) throw new MissingSourceException(SourceKind.Config, Path.GetFullPath(c));
        if (a.Get("excel") is { } x && !File.Exists(x)) throw new MissingSourceException(SourceKind.Excel, Path.GetFullPath(x));
        var settings = RunSettings.Resolve(a.Get("api"), a.GetAll("header"), a.Get("token"), a.Get("timeout"), a.Get("excel"), a.Get("env"));
        ctx.Api = settings.Api;

        using var cts = new CancellationTokenSource();
        using var signals = ListenForSignals(cts);
        var exit = await new TestRunner(new RunOptions
        {
            Spec = spec, Api = settings.Api, Headers = settings.Headers, Token = settings.Token, SecretHeaderNames = settings.SecretHeaderNames,
            TimeoutSeconds = settings.TimeoutSeconds, EnvName = settings.EnvName, Excel = a.Get("excel"), Seed = seedChoice?.Seed, Only = a.Get("only"), Scenarios = a.GetAll("scenario"), ExcelOnly = a.Flag("bara-excel") || a.Flag("excel-only"),
            Warnings = settings.Warnings, Config = a.Get("config"),
            Verbose = a.Flag("verbose"), PlainText = a.Flag("klartext"), ReportPaths = a.GetAll("report"), ForceReport = a.Flag("force"),
            Tests = a.GetAll("test"), SeedRandom = seedChoice?.Random ?? false, SeedNote = seedChoice?.Note, HeaderArgs = a.GetAll("header"),
            FailOn = failOn, BaselinePath = baseline, FailOnNew = a.Flag("fail-on-new") || a.Flag("bara-nya-fel"), AllowEmpty = a.Flag("allow-empty"),
            MaxTimeSeconds = maxTime, HandleInterrupt = true, InterruptExitCode = () => Volatile.Read(ref _signalExit) is var sig and not 0 ? sig : ExitCodes.Interrupted,
        }, Console.Out).RunAsync(cts.Token);
        return exit;
    }

    /// <summary>
    /// Ctrl+C (SIGINT) och SIGTERM (CI som avbryter jobbet) avbryter pågående anrop; körningen skriver då en partiell rapport och
    /// avslutas med 130 respektive 143. En andra signal avslutar direkt.
    /// </summary>
    private static IDisposable ListenForSignals(CancellationTokenSource cts)
    {
        void Handle(int code)
        {
            if (Interlocked.CompareExchange(ref _signalExit, code, 0) != 0) Environment.Exit(code);   // andra signalen: ge inte upp rapporten för evigt
            Console.Error.WriteLine(code == ExitCodes.Terminated
                ? "Avbryter (SIGTERM): pågående anrop stoppas och en partiell rapport skrivs. Skicka signalen igen för att avsluta direkt."
                : "Avbryter (Ctrl+C): pågående anrop stoppas och en partiell rapport skrivs. Tryck Ctrl+C igen för att avsluta direkt.");
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; Handle(ExitCodes.Interrupted); };
        Console.CancelKeyPress += onCancel;
        PosixSignalRegistration? term = null;
        try { term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; Handle(ExitCodes.Terminated); }); }
        catch (PlatformNotSupportedException) { /* t.ex. vissa Windows-miljöer: Ctrl+C räcker */ }
        return new Cleanup(() => { Console.CancelKeyPress -= onCancel; term?.Dispose(); });
    }

    private sealed class Cleanup(Action action) : IDisposable { public void Dispose() => action(); }

    private static async Task<int> UiAsync(string[] args, ErrorContext ctx)
    {
        var a = CommandArgs.Parse("ui", args, new[] { "port", "spec", "excel", "solution", "losning" });
        var port = Ui.UiOptions.DefaultPort;
        if (a.Get("port") is { } pv && !(int.TryParse(pv, out port) && port is >= 1 and <= 65535))
            throw new UsageException($"--port måste vara ett tal mellan 1 och 65535, inte '{pv}'.", "Skriv t.ex. --port 5100");
        ctx.Spec = a.Get("spec"); ctx.Excel = a.Get("excel");
        await using var server = await Ui.UiServer.StartAsync(new Ui.UiOptions
        {
            Port = port, Spec = a.Get("spec"), Excel = a.Get("excel"), Solution = a.Get("solution") ?? a.Get("losning"),
        });
        Console.WriteLine($"Öppna {server.Url} i webbläsaren. Avsluta med Ctrl+C.");
        Console.WriteLine("Sidan går bara att nå från den här datorn.");
        await server.WaitForShutdownAsync();
        return ExitCodes.Ok;
    }

    private static async Task<int> MapAsync(string[] args, ErrorContext ctx)
    {
        var a = CommandArgs.Parse("map", args, ["solution", "losning", "spec", .. MapFiles.Flags("out")], "no-restore");
        if (a.Get("solution") is { } s1 && a.Get("losning") is { } s2 && s1 != s2)
            throw new UsageException("--solution och --losning är samma flagga men har olika värden.", "Ange bara en av dem.");
        var solution = a.Get("solution") ?? a.Get("losning")
            ?? throw new UsageException("Ange vilken solution kartan ska ritas för med --solution.", "Skriv t.ex. testgen map --solution samples/Shop.slnx. Se: testgen map --help");
        var files = MapFiles.From(a, "out");
        ctx.Spec = a.Get("spec");
        var operations = a.Get("spec") is { } spec ? OpenApiLoader.Load(spec).ForMap() : null;
        await CodeMapRun.RunAsync(solution, operations, restore: !a.Flag("no-restore"), files, Console.Out, Console.Error);
        return ExitCodes.Ok;
    }

    private static int Template(string[] args, ErrorContext ctx)
    {
        var a = CommandArgs.Parse("template", args, new[] { "spec", "out" }, "force");
        var spec = a.Require("spec");
        var path = a.Require("out");
        ctx.Spec = spec;
        if (File.Exists(path) && !a.Flag("force"))
            throw new UsageException($"{Path.GetFullPath(path)} finns redan.", "Välj ett annat namn med --out, eller lägg till --force för att skriva över.");
        var ops = OpenApiLoader.Load(spec);
        ExcelTemplate.Create(ops, path);
        Console.WriteLine($"Skrev {path} med {ops.Count} anrop. Öppna i Excel, fyll i bladet Tester och kör:");
        Console.WriteLine($"  testgen run --spec {spec} --api <bas-url> --excel {path}");
        Console.WriteLine($"  (eller fyll i bladet Miljöer och kör: testgen run --spec {spec} --excel {path} --env lokal)");
        return ExitCodes.Ok;
    }
}
