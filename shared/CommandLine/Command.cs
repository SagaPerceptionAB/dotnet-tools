using System.Reflection;
using System.Runtime.InteropServices;

namespace SagaPerception.CommandLine;

/// <summary>Ramen runt Main: UTF-8 i konsolen, och fel skrivs på enkel svenska till stderr med rätt exitkod.</summary>
public static class Command
{
    /// <param name="terminal">Dit felen skrivs (stderr). Utan den används konsolen.</param>
    public static int Run(string[] args, Func<int> run, Func<Exception, FriendlyError> translate, Terminal? terminal = null)
    {
        // Windows-loggar (och CI-system) läser UTF-8; utan detta blir å, ä, ö och tankstreck fel tecken i konsolen. Utan BOM så att loggen inte börjar med ett konstigt tecken.
        try { Console.OutputEncoding = new System.Text.UTF8Encoding(false); }
        catch (Exception e) when (e is IOException or PlatformNotSupportedException or UnauthorizedAccessException) { /* ingen konsol att ställa in */ }
        // Verktygen startar dotnet (restore och Roslyns BuildHost). Inget av det ska skicka användningsdata till Microsoft, så
        // .NET SDK:ns telemetri stängs av för hela processen och allt den startar, om användaren inte själv har valt.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_CLI_TELEMETRY_OPTOUT")))
            Environment.SetEnvironmentVariable("DOTNET_CLI_TELEMETRY_OPTOUT", "1");
        var verbose = args.Contains("--verbose");
        try
        {
            return run();
        }
        catch (Exception e)
        {
            var err = (terminal ?? Terminal.Default).Err;
            var f = translate(e);
            foreach (var line in f.Lines(verbose)) err.WriteLine(line);
            if (f.ExitCode == ExitCodes.Internal) foreach (var line in Diagnostics(args)) err.WriteLine(line);
            if (verbose) err.WriteLine(e.ToString());   // stackspår bara med --verbose
            return f.ExitCode;
        }
    }

    /// <summary>Det som behövs för att felsöka ett internt fel: verktygets version, systemet, runtime och kommandoraden (hemligheterna maskade).</summary>
    public static IEnumerable<string> Diagnostics(IReadOnlyList<string> args)
    {
        var entry = Assembly.GetEntryAssembly();
        var version = entry?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? entry?.GetName().Version?.ToString() ?? "okänd";
        var plus = version.IndexOf('+');
        if (plus > 0) version = version[..plus];
        var tool = entry?.GetName().Name ?? "verktyget";
        yield return $"Version: {tool} {version}";
        yield return $"System: {RuntimeInformation.OSDescription}";
        yield return $"Runtime: {RuntimeInformation.FrameworkDescription}";
        yield return $"Kommandorad: {tool} {Secrets.RedactCommandLine(args)}".TrimEnd();
    }
}
