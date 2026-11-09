namespace SagaPerception.TestGen.NUnit.Tests;

/// <summary>Titeln i views.json är sidans egen del; layouten får lägga text runt den.</summary>
public class UiRunnerTitleTests
{
    [Test]
    public void A_layout_may_add_text_around_the_title()
    {
        Assert.That(UiRunner.TitleProblem("Home page - DemoWeb", "Home page"), Is.Null);
        Assert.That(UiRunner.TitleProblem("Home page", "Home page"), Is.Null);
    }

    [Test]
    public void A_different_or_differently_cased_title_is_reported()
    {
        Assert.That(UiRunner.TitleProblem("Privacy - DemoWeb", "Home page"),
            Is.EqualTo("Sidans titel 'Privacy - DemoWeb' innehåller inte 'Home page' från views.json. Kör testgen views om sidan ändrats."));
        Assert.That(UiRunner.TitleProblem("home page", "Home page"), Is.Not.Null);
    }
}
