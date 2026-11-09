using System.Diagnostics;
using System.Runtime.InteropServices;
using SystemMap;
using TestGen.Core;
using TestGen.Core.Projects;
using TestGen.Core.Run;

namespace TestGen.Cli;

public static class Program
{
    public static int Main(string[] args)
    {
        ExcelSupport.Register();
        var ctx = new ErrorContext();
        var terminal = Terminal.Default;
        return Command.Run(args, () => Dispatch(args, ctx, terminal), e => FriendlyErrors.Translate(e, ctx), terminal);
    }

    private static int Dispatch(string[] args, ErrorContext ctx, Terminal terminal)
    {
        if (args.Length == 0) return StatusView.Run(null, terminal);
        if (args is ["--solution", var solutionArg]) return StatusView.Run(solutionArg, terminal);
        if (args[0] is "-h" or "--help")
        {
            terminal.Out.Write(HelpText.General);
            return ExitCodes.Ok;
        }
        var cmd = args[0];
        if (cmd == "help")
        {
            terminal.Out.Write((args.Length > 1 ? HelpText.For(args[1]) : null) ?? HelpText.General);
            return ExitCodes.Ok;
        }
        if (cmd.StartsWith('-'))
            throw new UsageException($"Kommando saknas före {cmd}.",
                $"Skriv ett kommando först, t.ex. testgen run {cmd} ... Kommandon: {string.Join(", ", HelpText.Commands)}.");
        if (!HelpText.Commands.Contains(cmd))
        {
            var hint = Suggest.Closest(cmd, HelpText.Commands) is { } m ? $" Menade du '{m}'?" : "";
            throw new UsageException($"Okänt kommando: {cmd}.{hint}", $"Giltiga kommandon: {string.Join(", ", HelpText.Commands)}. Se: testgen --help");
        }
        var rest = args[1..];
        if (rest.Any(a => a is "-h" or "--help")) { terminal.Out.Write(HelpText.For(cmd)); return ExitCodes.Ok; }
        return cmd switch
        {
            "run" => RunAsync(rest, ctx, terminal).GetAwaiter().GetResult(),
            "template" => Template(rest, ctx, terminal),
            "web" => WebAsync(rest, ctx, terminal).GetAwaiter().GetResult(),
            "map" => MapAsync(rest, ctx, terminal).GetAwaiter().GetResult(),
            "mock" => MockCommand.RunAsync(rest, ctx, terminal).GetAwaiter().GetResult(),
            "init" => InitCommand.RunAsync(rest, terminal).GetAwaiter().GetResult(),
            "views" => ViewsCommand.RunAsync(rest, ctx, terminal).GetAwaiter().GetResult(),
            _ => throw new UnreachableException(),
        };
    }

    /// <summary>Exitkoden om en signal tagits emot (130 SIGINT, 143 SIGTERM), annars 0. Sätts av signalhanteraren.</summary>
    private static int _signalExit;

    private static async Task<int> RunAsync(string[] args, ErrorContext ctx, Terminal terminal)
    {
        var a = CommandArgs.Parse("run", args, new[] { "spec", "api", "excel", "seed", "only", "config", "header", "token", "timeout", "env", "report", "scenario", "test", "fail-on", "baseline", "max-time", "solution" },
            "force", "verbose", "explain", "excel-only", "fail-on-new", "allow-empty");
        ctx.Excel = a.Get("excel"); ctx.Config = a.Get("config"); ctx.Report = a.GetAll("report").FirstOrDefault(); ctx.Api = a.Get("api");
        var seedChoice = a.Get("seed") is { } sv ? SeedSpec.Parse(sv) : null;
        var failOn = a.Get("fail-on") is { } fo ? CauseCodes.ParseFailOn(fo) : null;
        int? maxTime = null;
        if (a.Get("max-time") is { } mt)
            maxTime = int.TryParse(mt.Trim(), out var secs) && secs > 0 ? secs
                : throw new UsageException($"--max-time måste vara ett positivt antal sekunder, inte '{mt}'.", "Skriv t.ex. --max-time 600 för att avbryta körningen efter 10 minuter.");
        if (a.Get("config") is { } c && !File.Exists(c)) throw new MissingSourceException(SourceKind.Config, Path.GetFullPath(c));
        if (a.Get("excel") is { } x && !File.Exists(x)) throw new MissingSourceException(SourceKind.Excel, Path.GetFullPath(x));
        var target = RunTarget.Resolve(a.Get("solution"), a.Get("config"), a.Get("api"), a.Get("spec"), a.Get("env"), a.Get("token"), a.GetAll("header"), a.Get("timeout"));
        var (spec, settings) = (target.Spec, target.Settings);
        var excel = a.Get("excel") ?? target.Excel;
        ctx.Excel = excel;
        ctx.Config = target.ConfigPath;
        ctx.Spec = spec;
        ctx.Api = settings.Api;

        using var cts = new CancellationTokenSource();
        using var signals = ListenForSignals(cts, terminal);
        var exit = await new TestRunner(new RunOptions
        {
            Spec = spec,
            Api = settings.Api,
            Headers = settings.Headers,
            Token = settings.Token,
            SecretHeaderNames = settings.SecretHeaderNames,
            TimeoutSeconds = settings.TimeoutSeconds,
            EnvName = settings.EnvName,
            Excel = excel,
            Source = ExcelSupport.Source,
            Project = target.Project,
            Seed = seedChoice?.Seed,
            Only = a.Get("only"),
            Scenarios = a.GetAll("scenario"),
            ExcelOnly = a.Flag("excel-only"),
            Config = a.Get("config"),
            Generator = target.Generator,
            ReadOnly = settings.ReadOnly,
            Verbose = a.Flag("verbose"),
            PlainText = a.Flag("explain"),
            ReportPaths = a.GetAll("report"),
            ForceReport = a.Flag("force"),
            Tests = a.GetAll("test"),
            SeedRandom = seedChoice?.Random ?? false,
            SeedNote = seedChoice?.Note,
            HeaderArgs = a.GetAll("header"),
            FailOn = failOn,
            BaselinePath = a.Get("baseline"),
            FailOnNew = a.Flag("fail-on-new"),
            AllowEmpty = a.Flag("allow-empty"),
            MaxTimeSeconds = maxTime,
            HandleInterrupt = true,
            InterruptExitCode = () => Volatile.Read(ref _signalExit) is var sig and not 0 ? sig : ExitCodes.Interrupted,
        }, terminal).RunAsync(cts.Token);
        return exit;
    }

    /// <summary>
    /// Ctrl+C (SIGINT) och SIGTERM (CI som avbryter jobbet) avbryter pågående anrop; körningen skriver då en partiell rapport och
    /// avslutas med 130 respektive 143. En andra signal avslutar direkt.
    /// </summary>
    private static IDisposable ListenForSignals(CancellationTokenSource cts, Terminal terminal)
    {
        void Handle(int code)
        {
            if (Interlocked.CompareExchange(ref _signalExit, code, 0) != 0) Environment.Exit(code);   // andra signalen: ge inte upp rapporten för evigt
            terminal.Err.WriteLine(code == ExitCodes.Terminated
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

    private static async Task<int> WebAsync(string[] args, ErrorContext ctx, Terminal terminal)
    {
        var a = CommandArgs.Parse("web", args, new[] { "port", "spec", "excel", "solution", "config" });
        var port = Web.WebOptions.DefaultPort;
        if (a.Get("port") is { } pv && !(int.TryParse(pv, out port) && port is >= 1 and <= 65535))
            throw new UsageException($"--port måste vara ett tal mellan 1 och 65535, inte '{pv}'.", "Skriv t.ex. --port 5100");
        ctx.Spec = a.Get("spec"); ctx.Excel = a.Get("excel");
        await using var server = await Web.WebServer.StartAsync(new Web.WebOptions
        {
            Port = port,
            Spec = a.Get("spec"),
            Excel = a.Get("excel"),
            Solution = a.Get("solution"),
            Config = a.Get("config"),
        });
        terminal.Out.WriteLine($"Öppna {server.Url} i webbläsaren.");
        terminal.Err.WriteLine("Avsluta med Ctrl+C. Sidan går bara att nå från den här datorn.");
        await server.WaitForShutdownAsync();
        return ExitCodes.Ok;
    }

    private static async Task<int> MapAsync(string[] args, ErrorContext ctx, Terminal terminal)
    {
        var a = CommandArgs.Parse("map", args, ["solution", "spec", .. MapFiles.Flags("out")], "no-restore");
        var solution = a.Get("solution")
            ?? throw new UsageException("Ange vilken solution kartan ska ritas för med --solution.", "Skriv t.ex. testgen map --solution samples/Shop.slnx. Se: testgen map --help");
        var files = MapFiles.From(a, "out");
        ctx.Spec = a.Get("spec");
        var operations = a.Get("spec") is { } spec ? OpenApiLoader.Load(spec).ForMap() : null;
        await CodeMapRun.RunAsync(solution, operations, restore: !a.Flag("no-restore"), files, terminal);
        return ExitCodes.Ok;
    }

    private static int Template(string[] args, ErrorContext ctx, Terminal terminal)
    {
        var a = CommandArgs.Parse("template", args, new[] { "spec", "out" }, "force");
        var spec = a.Require("spec");
        var path = a.Require("out");
        ctx.Spec = spec;
        if (File.Exists(path) && !a.Flag("force"))
            throw new UsageException($"{Path.GetFullPath(path)} finns redan.", "Välj ett annat namn med --out, eller lägg till --force för att skriva över.");
        var ops = OpenApiLoader.Load(spec);
        TestGen.Excel.ExcelTemplate.Create(ops, path);
        terminal.Confirm(path, $"Excel-mall med {ops.Count} anrop");
        terminal.Err.WriteLine($"Fyll i bladet Tester i Excel och kör: testgen run --spec {spec} --api <bas-url> --excel {path}");
        return ExitCodes.Ok;
    }
}
