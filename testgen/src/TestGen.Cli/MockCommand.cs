using System.Runtime.InteropServices;
using TestGen.Core;
using TestGen.Core.Projects;
using TestGen.Core.Run;
using TestGen.Mock;

namespace TestGen.Cli;

/// <summary>testgen mock: startar en mock per spec och skriver varje anrop på stdout tills Ctrl+C.</summary>
internal static class MockCommand
{
    public static async Task<int> RunAsync(string[] args, ErrorContext ctx, Terminal terminal)
    {
        var a = CommandArgs.Parse("mock", args, ["spec", "port", "seed"]);
        var sources = a.GetAll("spec").Select(s => (Spec: s, Name: (string?)null)).ToList();
        if (sources.Count == 0) sources = FromSolution(RepositoryContext.Load(null, null, needFacts: true), terminal);
        var specs = sources.Select(s => s.Spec).ToList();
        ctx.Spec = specs[0];

        var port = 0;
        if (a.Get("port") is { } pv)
        {
            if (!(int.TryParse(pv, out port) && port is >= 1 and <= 65535))
                throw new UsageException($"--port måste vara ett tal mellan 1 och 65535, inte '{pv}'.", "Skriv t.ex. --port 5080");
            if (specs.Count > 1)
                throw new UsageException("--port går bara att använda med en spec.", "Med flera specar väljer systemet en port per spec. Ta bort --port.");
        }
        var seed = new MockOptions().Seed;
        if (a.Get("seed") is { } sv && !int.TryParse(sv, out seed))
            throw new UsageException($"--seed måste vara ett heltal, inte '{sv}'.", "Skriv t.ex. --seed 42");

        var servers = new List<MockServer>();
        try
        {
            foreach (var (spec, projectName) in sources)
            {
                ctx.Spec = spec;
                servers.Add(projectName is { } name
                    ? await MockServer.StartAsync(OpenApiLoader.LoadSpec(spec), name, new MockOptions { Port = port, Seed = seed })
                    : await MockServer.StartAsync(spec, new MockOptions { Port = port, Seed = seed }));
            }

            var nameWidth = servers.Max(s => s.Name.Length);
            for (var i = 0; i < servers.Count; i++)
                terminal.Out.WriteLine($"{servers[i].Name.PadRight(nameWidth)}  {servers[i].Url}  ({(Sources.IsUrl(specs[i]) ? specs[i] : Terminal.Relative(specs[i]))})");
            terminal.Err.WriteLine($"Peka appen hit: sätt dess API-adress (till exempel ShopApi__BaseUrl) till {servers[0].Url}");
            terminal.Err.WriteLine("Avsluta med Ctrl+C. Mocken går bara att nå från den här datorn.");

            var gate = new object();
            foreach (var server in servers)
                server.CallLogged += call =>
                {
                    var line = Format(call, servers.Count > 1 ? server.Name.PadRight(nameWidth) + "  " : "");
                    lock (gate) terminal.Out.WriteLine(line);
                };

            using var stop = new CancellationTokenSource();
            ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
            Console.CancelKeyPress += onCancel;
            PosixSignalRegistration? term = null;
            try { term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; stop.Cancel(); }); }
            catch (PlatformNotSupportedException) { /* Ctrl+C räcker */ }
            try { await Task.Delay(Timeout.Infinite, stop.Token); }
            catch (OperationCanceledException) { /* avslutad */ }
            finally
            {
                Console.CancelKeyPress -= onCancel;
                term?.Dispose();
            }
            return ExitCodes.Ok;   // en server avslutas med Ctrl+C: det är hur den används, inte ett avbrott (som i testgen web)
        }
        finally
        {
            foreach (var server in servers) await server.DisposeAsync();
        }
    }

    /// <summary>Utan --spec: en mock per API-projekt i solutionen, med projektets namn.</summary>
    internal static List<(string Spec, string? Name)> FromSolution(RepositoryContext repo, Terminal terminal)
    {
        var found = new List<(string, string?)>();
        foreach (var api in repo.Facts?.Apis ?? [])
        {
            try { found.Add((repo.SpecFor(api, null), api.Name)); }
            catch (UsageException e) { terminal.Warning(e.Message); }
        }
        if (found.Count == 0)
            throw new UsageException("Hittar ingen spec att starta en mock för.", "Ange en med --spec, t.ex. testgen mock --spec samples/shop.openapi.yaml. Se: testgen mock --help");
        return found;
    }

    /// <summary><c>POST /products   201  createProduct</c> eller <c>POST /products   400  name: saknas (...)</c>.</summary>
    internal static string Format(MockCall call, string prefix = "")
    {
        var what = call.Problems.Count > 0
            ? call.Problems[0] + (call.Problems.Count > 1 ? $" (+{call.Problems.Count - 1})" : "")
            : call.OperationId ?? "";
        return $"{prefix}{$"{call.Method} {call.Path}".PadRight(28)} {call.Status}  {what}".TrimEnd();
    }
}
