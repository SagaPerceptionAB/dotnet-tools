using TestGen.Core.Run;

namespace SagaPerception.TestGen.NUnit.Tests;

/// <summary>Vad ett utfall från motorn blir i NUnit: bara det som säger något om systemet fäller testet.</summary>
public class OutcomeMappingTests
{
    [TestCase("OK", null, "Pass")]
    [TestCase("FEL", FailureCause.System, "Fail")]
    [TestCase("FEL", FailureCause.Expectation, "Fail")]
    [TestCase("FEL", FailureCause.Auth, "Inconclusive")]
    [TestCase("FEL", FailureCause.TestData, "Inconclusive")]
    [TestCase("FEL", FailureCause.Environment, "Inconclusive")]
    [TestCase("FEL", FailureCause.Tool, "Inconclusive")]
    [TestCase("HOPPAD", FailureCause.TestUserRejected, "Inconclusive")]
    [TestCase("HOPPAD", FailureCause.RateLimited, "Inconclusive")]
    [TestCase("FEL", FailureCause.Cascade, "Inconclusive")]
    [TestCase("HOPPAD", FailureCause.ReadOnly, "Ignore")]
    [TestCase("HOPPAD", FailureCause.Cascade, "Inconclusive")]
    [TestCase("HOPPAD", FailureCause.Tool, "Inconclusive")]
    [TestCase("HOPPAD", null, "Ignore")]
    public void An_outcome_and_its_cause_decide_the_verdict(string outcome, FailureCause? cause, string expected) =>
        Assert.That(OutcomeMapping.Decide(outcome, cause), Is.EqualTo(Enum.Parse<Verdict>(expected)));

    [Test]
    public void Each_verdict_becomes_the_matching_nunit_result()
    {
        Assert.DoesNotThrow(() => OutcomeMapping.Apply(Verdict.Pass, "x"));
        Assert.That(Assert.Throws<AssertionException>(() => OutcomeMapping.Apply(Verdict.Fail, "fel i systemet"))!.Message, Is.EqualTo("fel i systemet"));
        Assert.That(Assert.Throws<InconclusiveException>(() => OutcomeMapping.Apply(Verdict.Inconclusive, "osäkert"))!.Message, Is.EqualTo("osäkert"));
        Assert.That(Assert.Throws<IgnoreException>(() => OutcomeMapping.Apply(Verdict.Ignore, "skrivskyddad"))!.Message, Is.EqualTo("skrivskyddad"));
    }

    [TestCase("POST", true)]
    [TestCase("put", true)]
    [TestCase("PATCH", true)]
    [TestCase("DELETE", true)]
    [TestCase("GET", false)]
    [TestCase("HEAD", false)]
    [TestCase("OPTIONS", false)]
    public void Only_writing_methods_are_mutating(string method, bool mutating) =>
        Assert.That(OutcomeMapping.IsMutating(method), Is.EqualTo(mutating));
}
