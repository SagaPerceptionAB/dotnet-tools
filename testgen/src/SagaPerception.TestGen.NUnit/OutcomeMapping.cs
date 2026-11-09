using NUnit.Framework.Internal;
using TestGen.Core.Run;

namespace SagaPerception.TestGen.NUnit;

/// <summary>Vad ett testgen-utfall blir i NUnit.</summary>
internal enum Verdict
{
    Pass,
    Fail,
    Inconclusive,
    Ignore,
}

/// <summary>
/// Översätter motorns utfall (OK, FEL, HOPPAD och orsak) till NUnit: bara fel i systemet och förväntan som inte stämmer fäller testet.
/// Osäkra orsaker (inloggning, testdata, miljö, verktyget, följdfel) blir Inconclusive, och skrivskyddad miljö blir Ignore.
/// </summary>
internal static class OutcomeMapping
{
    public static Verdict Decide(string outcome, FailureCause? cause) => outcome switch
    {
        "OK" => Verdict.Pass,
        "FEL" => cause is null or FailureCause.System or FailureCause.Expectation ? Verdict.Fail : Verdict.Inconclusive,
        _ => cause is null or FailureCause.ReadOnly ? Verdict.Ignore : Verdict.Inconclusive,
    };

    /// <summary>Kastar NUnits undantag för utfallet. Pass gör ingenting.</summary>
    public static void Apply(Verdict verdict, string message)
    {
        switch (verdict)
        {
            case Verdict.Fail: throw new AssertionException(message);
            case Verdict.Inconclusive: Assert.Inconclusive(message); break;
            case Verdict.Ignore: Assert.Ignore(message); break;
        }
    }

    /// <summary>Lägger orsakskoden som kategori på testet som körs, så att den går att filtrera på.</summary>
    public static void AddCategory(string code)
    {
        try { TestExecutionContext.CurrentContext.CurrentTest.Properties.Add(PropertyNames.Category, code); }
        catch (Exception e) when (e is InvalidOperationException or NullReferenceException) { /* kategorin är en bonus */ }
    }

    public static bool IsMutating(string method) => method.ToUpperInvariant() is "POST" or "PUT" or "PATCH" or "DELETE";
}
