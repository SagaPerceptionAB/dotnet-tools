namespace CommandLine;

/// <summary>Ramen runt Main: UTF-8 i konsolen, och fel skrivs på enkel svenska till stderr med rätt exitkod.</summary>
public static class Command
{
    public static int Run(string[] args, Func<int> run, Func<Exception, FriendlyError> translate)
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
            var f = translate(e);
            foreach (var line in f.Lines(verbose)) Console.Error.WriteLine(line);
            if (verbose) Console.Error.WriteLine(e.ToString());   // stackspår bara med --verbose
            return f.ExitCode;
        }
    }
}
