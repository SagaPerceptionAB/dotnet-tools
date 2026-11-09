using System.Text;
using Microsoft.Extensions.Logging;
using TestGen.Core.Run;

namespace SagaPerception.TestGen.NUnit;

/// <summary>Texten i ett NUnit-meddelande: samma förklaring, anrop, svar och curl som i terminalen, plus appens utgående anrop och logg. Hemligheter maskade.</summary>
internal static class FailureText
{
    private const int MaxLogLines = 20;

    public static string Build(TestOutcome outcome, HeaderMask mask, IReadOnlyList<HostCall> hostCalls, IReadOnlyList<CapturedLog> logs)
    {
        var sb = new StringBuilder();
        sb.AppendLine(outcome.Text.Title);
        sb.AppendLine(outcome.Text.Explanation);
        if (outcome.Text.Hint is { } hint) sb.AppendLine("Tolkning: " + hint);
        if (outcome.TechnicalLines.Count > 0)
        {
            sb.AppendLine();
            foreach (var line in outcome.TechnicalLines) sb.AppendLine(line);
        }
        if (outcome.Curl is { } curl)
        {
            sb.AppendLine();
            sb.AppendLine("curl för anropet:");
            sb.AppendLine(curl);
        }
        AppendAppContext(sb, hostCalls, logs);
        return mask.Scrub(sb.ToString().TrimEnd());
    }

    /// <summary>Appens utgående anrop (värdpolicyn) och dess varningar och fel i loggen. Delas av API- och UI-testerna.</summary>
    public static void AppendAppContext(StringBuilder sb, IReadOnlyList<HostCall> hostCalls, IReadOnlyList<CapturedLog> logs)
    {
        if (hostCalls.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Appens utgående anrop (värdpolicyn i testgen.yaml):");
            foreach (var call in hostCalls) sb.AppendLine("  " + call.Text);
        }
        var important = logs.Where(l => l.Level >= LogLevel.Warning).ToList();
        if (important.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Appens logg (varningar och fel):");
            foreach (var log in important.Take(MaxLogLines))
                sb.AppendLine($"  [{log.Level}] {log.Category}: {log.Message}{(log.Exception is null ? "" : " – " + log.Exception.GetType().Name + ": " + log.Exception.Message)}");
            if (important.Count > MaxLogLines) sb.AppendLine($"  … och {important.Count - MaxLogLines} till.");
        }
    }
}
