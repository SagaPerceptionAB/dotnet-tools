namespace SagaPerception.Testing.NUnit.Tests;

/// <summary>Assert.ThrowsAsync räknar ett AssertionException som ett fel i det egna testet, så det fångas för hand.</summary>
internal static class Caught
{
    public static async Task<AssertionException> Failure(Func<Task> action)
    {
        try { await action(); }
        catch (AssertionException e) { return e; }
        throw new InvalidOperationException("Testet gick igenom, men skulle ha fallerat.");
    }
}
