using Microsoft.AspNetCore.Builder;

namespace TestGen.Browser.Tests;

public class ScreenshotComparerTests
{
    private string _root = null!;

    [SetUp]
    public void CreateDirectories() => _root = Path.Combine(Path.GetTempPath(), "testgen-shots-" + Guid.NewGuid().ToString("N"));

    [TearDown]
    public void DeleteDirectories()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private ScreenshotOptions Options(bool update = false) => new(Path.Combine(_root, "reference"), Path.Combine(_root, "diff"), Update: update);

    private static byte[] Png(int width, int height, byte red)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = red;
            pixels[i + 3] = 255;
        }
        return ImageComparer.EncodePng(new RgbaImage(width, height, pixels));
    }

    [Test]
    public void First_run_creates_the_reference_then_compares()
    {
        var created = ScreenshotComparer.Compare(Png(10, 10, 0), "start", Options());
        var same = ScreenshotComparer.Compare(Png(10, 10, 0), "start", Options());

        Assert.That(created.Outcome, Is.EqualTo(ScreenshotOutcome.Created));
        Assert.That(created.IsNewReference, Is.True);
        Assert.That(created.Message, Does.Contain("saknades och skapades"));
        Assert.That(File.Exists(created.ReferencePath), Is.True);
        Assert.That(same.Passed, Is.True);
    }

    [Test]
    public void Different_image_fails_and_writes_actual_and_diff()
    {
        ScreenshotComparer.Compare(Png(10, 10, 0), "start", Options());

        var result = ScreenshotComparer.Compare(Png(10, 10, 200), "start", Options());

        Assert.That(result.Outcome, Is.EqualTo(ScreenshotOutcome.Different));
        Assert.That(result.SharePercent, Is.EqualTo(100));
        Assert.That(File.Exists(result.ActualPath), Is.True);
        Assert.That(File.Exists(result.DiffPath), Is.True);
        Assert.That(result.Message, Does.Contain("skiljer sig från referensbilden").And.Contain("100 %"));
    }

    [Test]
    public void Difference_within_tolerance_passes()
    {
        ScreenshotComparer.Compare(Png(10, 10, 100), "start", Options());

        Assert.That(ScreenshotComparer.Compare(Png(10, 10, 110), "start", Options()).Passed, Is.True);
    }

    [Test]
    public void Different_size_fails_directly()
    {
        ScreenshotComparer.Compare(Png(10, 10, 0), "start", Options());

        var result = ScreenshotComparer.Compare(Png(10, 20, 0), "start", Options());

        Assert.That(result.Outcome, Is.EqualTo(ScreenshotOutcome.SizeMismatch));
        Assert.That(result.Message, Does.Contain("10x20 mot 10x10"));
    }

    [Test]
    public void Update_rewrites_a_changed_reference_and_leaves_an_unchanged_one()
    {
        var first = ScreenshotComparer.Compare(Png(10, 10, 0), "start", Options());
        var stamp = File.GetLastWriteTimeUtc(first.ReferencePath);

        var unchanged = ScreenshotComparer.Compare(Png(10, 10, 0), "start", Options(update: true));
        var changed = ScreenshotComparer.Compare(Png(10, 10, 200), "start", Options(update: true));

        Assert.That(unchanged.Passed, Is.True);
        Assert.That(File.GetLastWriteTimeUtc(first.ReferencePath), Is.GreaterThanOrEqualTo(stamp));
        Assert.That(changed.Outcome, Is.EqualTo(ScreenshotOutcome.Updated));
        Assert.That(ScreenshotComparer.Compare(Png(10, 10, 200), "start", Options()).Passed, Is.True);
    }

    [Test]
    public void Unreadable_reference_is_reported()
    {
        var options = Options();
        Directory.CreateDirectory(options.ReferenceDirectory);
        File.WriteAllText(Path.Combine(options.ReferenceDirectory, "start.png"), "inte en bild");

        Assert.That(ScreenshotComparer.Compare(Png(10, 10, 0), "start", options).Outcome, Is.EqualTo(ScreenshotOutcome.Unreadable));
    }

    [Test]
    public void File_names_cannot_leave_the_reference_directory()
    {
        Assert.That(ScreenshotComparer.FileName("/products/create"), Is.EqualTo("products-create"));
        Assert.That(ScreenshotComparer.FileName("../../x"), Is.EqualTo("x").Or.Not.Contain(".."));
        Assert.That(ScreenshotComparer.FileName("/"), Is.EqualTo("page"));
    }

    [Test]
    public void Png_round_trips()
    {
        var image = ImageComparer.DecodePng(Png(3, 2, 77));

        Assert.That((image.Width, image.Height, image.Pixels[0]), Is.EqualTo((3, 2, (byte)77)));
    }

    [Test]
    public async Task Compares_a_page_screenshot_against_its_reference()
    {
        var color = "red";
        await using var site = await TestApp.StartAsync(app => app.MapGet("/shot", () => Microsoft.AspNetCore.Http.Results.Content(
            $"<!doctype html><title>t</title><body style=\"margin:0;background:{color}\"><div data-testgen-mask style=\"height:30px\">{Guid.NewGuid()}</div><p>Hej</p></body>", "text/html")));
        await using var session = await BrowserFixture.Host.NewSessionAsync(new SessionOptions(site.Url));
        await session.OpenAsync("/shot");

        var created = await ScreenshotComparer.CompareAsync(session.Page, "shot", Options());
        await session.OpenAsync("/shot");
        var same = await ScreenshotComparer.CompareAsync(session.Page, "shot", Options());
        color = "blue";
        await session.OpenAsync("/shot");
        var different = await ScreenshotComparer.CompareAsync(session.Page, "shot", Options());

        Assert.That(created.Outcome, Is.EqualTo(ScreenshotOutcome.Created));
        Assert.That(same.Passed, Is.True, same.Message);
        Assert.That(different.Outcome, Is.EqualTo(ScreenshotOutcome.Different));
    }
}
