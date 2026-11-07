using NUnit.Framework;
using TestGen.Core.Run;

namespace TestGen.Tests;

/// <summary>Maskning av hemliga värden i fri text: korta hemligheter får inte fräta sönder vanliga ord, långa döljs överallt.</summary>
public class HeaderMaskScrubTests
{
    [Test]
    public void Short_secret_is_hidden_only_as_whole_word_and_does_not_break_words()
    {
        var mask = new HeaderMask(null, new[] { "kas" });
        Assert.That(mask.Scrub("Fältet saknas i svaret"), Is.EqualTo("Fältet saknas i svaret"));
        Assert.That(mask.Scrub("token kas här"), Is.EqualTo("token *** här"));
        Assert.That(mask.Scrub("url=/x?k=kas&y=1"), Is.EqualTo("url=/x?k=***&y=1"));
        Assert.That(mask.Scrub("{\"key\":\"kas\"}"), Is.EqualTo("{\"key\":\"***\"}"));
        Assert.That(mask.Scrub("kas"), Is.EqualTo("***"));
        Assert.That(mask.Scrub("kas och kas-bil"), Is.EqualTo("*** och ***-bil"));
    }

    [Test]
    public void Long_secret_is_hidden_everywhere_even_inside_other_characters()
    {
        var mask = new HeaderMask(null, new[] { "s3cr3t-t0ken-value" });
        Assert.That(mask.Scrub("a=s3cr3t-t0ken-value&b=1"), Is.EqualTo("a=***&b=1"));
        Assert.That(mask.Scrub("xs3cr3t-t0ken-valuey"), Is.EqualTo("x***y"));
        Assert.That(mask.Scrub("curl -H 'X-Api-Key: s3cr3t-t0ken-value' s3cr3t-t0ken-value"), Is.EqualTo("curl -H 'X-Api-Key: ***' ***"));
    }

    [Test]
    public void Header_values_are_always_fully_masked_regardless_of_length()
    {
        var mask = HeaderMask.For(new Dictionary<string, string> { ["X-Api-Key"] = "ab", ["Authorization"] = "Bearer abc" });
        Assert.That(mask.Value("X-Api-Key", "ab"), Is.EqualTo("***"));
        Assert.That(mask.Value("Authorization", "Bearer abc"), Is.EqualTo("Bearer ***"));
        Assert.That(mask.Scrub("Authorization: Bearer abc; nyckel ab; abc123"), Is.EqualTo("Authorization: ***; nyckel ***; abc123"));
    }

    [Test]
    public void Warning_for_secret_under_8_characters_without_revealing_it()
    {
        var shortMask = HeaderMask.For(new Dictionary<string, string> { ["X-Api-Key"] = "abc12" }, null, new[] { "tok" });
        Assert.That(shortMask.Warnings, Has.Count.EqualTo(2));
        Assert.That(shortMask.Warnings, Has.Some.Contain("headern X-Api-Key").And.Contain("5 tecken").And.Contain("minst 8"));
        Assert.That(shortMask.Warnings, Has.Some.Contain("3 tecken"));
        Assert.That(string.Concat(shortMask.Warnings), Does.Not.Contain("abc12").And.Not.Contain("tok "));
        Assert.That(HeaderMask.For(new Dictionary<string, string> { ["X-Api-Key"] = "abcdefgh" }).Warnings, Is.Empty);
        Assert.That(HeaderMask.For(new Dictionary<string, string> { ["Authorization"] = "Bearer abcdefgh" }).Warnings, Is.Empty);
        Assert.That(HeaderMask.For(new Dictionary<string, string> { ["Authorization"] = "Bearer abc" }).Warnings, Has.Count.EqualTo(1));
    }
}
