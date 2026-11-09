using TestGen.Core.Run;

namespace SagaPerception.Testing.NUnit;

/// <summary>En fixtures körning: skriver resultatfilen när testerna är klara.</summary>
internal interface IResultSession : IAsyncDisposable
{
    /// <summary>Skriver resultatfilen och returnerar sökvägen. Null om inget test kördes.</summary>
    string? WriteResult();
}

/// <summary>Resultatfilen som varje fixture skriver: samma JSON som <c>testgen run --report</c>, under testprojektets <c>TestResults/testgen</c>.</summary>
internal static class ResultFile
{
    public static string PathFor(string testProjectDirectory, string project) =>
        Path.Combine(testProjectDirectory, ReportWriter.ResultsDirectory, project + ".json");

    /// <summary>Skriver filen och returnerar sökvägen. Maskar hemligheter som alla andra rapporter.</summary>
    public static string Write(string testProjectDirectory, string project, ReportInfo info, IReadOnlyList<ReportEntry> entries, HeaderMask mask) =>
        ReportWriter.Write(PathFor(testProjectDirectory, project), info, entries.OrderBy(e => e.Id, StringComparer.Ordinal).ToList(), mask);

    /// <summary>Skriver resultatfilen, säger var den hamnade och stänger körningen.</summary>
    public static async Task FinishAsync(IResultSession? session)
    {
        if (session is null) return;
        try
        {
            if (session.WriteResult() is { } path) TestContext.Out.WriteLine($"Skrev {Terminal.Relative(path)} (resultat från testgen)");
        }
        finally { await session.DisposeAsync(); }
    }
}
