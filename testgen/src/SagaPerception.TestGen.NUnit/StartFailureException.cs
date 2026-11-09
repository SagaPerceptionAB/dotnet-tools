namespace SagaPerception.TestGen.NUnit;

/// <summary>
/// Appen startar inte. Fäller testet utan stack: NUnit visar annars samma stack under varje test i fixturen, och meddelandet säger redan allt.
/// </summary>
internal sealed class StartFailureException(string message) : AssertionException(message)
{
    public override string? StackTrace => null;
}
