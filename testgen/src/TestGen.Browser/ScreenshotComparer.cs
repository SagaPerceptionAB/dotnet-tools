using System.Globalization;
using Microsoft.Playwright;

namespace TestGen.Browser;

public enum ScreenshotOutcome
{
    /// <summary>Bilden är lika med referensen (inom toleransen).</summary>
    Same,
    /// <summary>Referensen saknades och skapades nu.</summary>
    Created,
    /// <summary>Referensen skrevs om på begäran (<see cref="ScreenshotOptions.Update"/>).</summary>
    Updated,
    /// <summary>Bilden skiljer sig mer än gränsen. Faktisk bild och skillnadsbild är skrivna.</summary>
    Different,
    /// <summary>Bilden har en annan storlek än referensen.</summary>
    SizeMismatch,
    /// <summary>Referensen går inte att läsa.</summary>
    Unreadable,
}

/// <param name="ReferenceDirectory">Katalogen med referensbilderna (<c>&lt;namn&gt;.png</c>).</param>
/// <param name="DiffDirectory">Dit <c>&lt;namn&gt;.actual.png</c> och <c>&lt;namn&gt;.diff.png</c> skrivs när en jämförelse fäller.</param>
/// <param name="ThresholdPercent">Högsta andel avvikande pixlar, i procent.</param>
/// <param name="Tolerance">Högsta skillnad per färgkanal (0-255) som inte räknas som avvikelse.</param>
/// <param name="Update">Skriv om referensen (en oförändrad bild lämnas orörd).</param>
/// <param name="Masks">Element som döljs bakom en ruta, utöver <c>[data-testgen-mask]</c>.</param>
public sealed record ScreenshotOptions(
    string ReferenceDirectory,
    string DiffDirectory,
    double ThresholdPercent = 0.1,
    int Tolerance = 16,
    bool Update = false,
    IReadOnlyList<ILocator>? Masks = null);

/// <param name="Message">Svensk förklaring, med sökvägar till referens, faktisk bild och skillnad.</param>
/// <param name="SharePercent">Andelen avvikande pixlar, när bilderna jämfördes.</param>
public sealed record ScreenshotResult(ScreenshotOutcome Outcome, string Message, string ReferencePath, double? SharePercent = null, string? ActualPath = null, string? DiffPath = null)
{
    /// <summary>Lika med referensen.</summary>
    public bool Passed => Outcome == ScreenshotOutcome.Same;

    /// <summary>Referensen skapades eller uppdaterades: inget är jämfört än, och bilden ska granskas och checkas in.</summary>
    public bool IsNewReference => Outcome is ScreenshotOutcome.Created or ScreenshotOutcome.Updated;
}

/// <summary>Jämför en skärmdump med en referensbild. Första gången skapas referensen; sedan jämförs pixlarna med tolerans.</summary>
public static class ScreenshotComparer
{
    private static readonly CultureInfo Swedish = CultureInfo.GetCultureInfo("sv-SE");

    /// <summary>Tar en skärmdump av hela sidan (animationer av, element med <c>data-testgen-mask</c> dolda) och jämför den med referensen <paramref name="name"/>.</summary>
    public static async Task<ScreenshotResult> CompareAsync(IPage page, string name, ScreenshotOptions options)
    {
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        var mask = new List<ILocator> { page.Locator("[data-testgen-mask]") };
        if (options.Masks is not null) mask.AddRange(options.Masks);
        var png = await page.ScreenshotAsync(new()
        {
            Type = ScreenshotType.Png,
            FullPage = true,
            Animations = ScreenshotAnimations.Disabled,
            Caret = ScreenshotCaret.Hide,
            Scale = ScreenshotScale.Css,
            Mask = mask,
        });
        return Compare(png, name, options);
    }

    /// <summary>Jämför en PNG-bild med referensen. Skild från webbläsaren, så att den går att pröva utan den.</summary>
    public static ScreenshotResult Compare(byte[] actualPng, string name, ScreenshotOptions options)
    {
        var file = FileName(name);
        var referencePath = Path.Combine(options.ReferenceDirectory, file + ".png");
        var exists = File.Exists(referencePath);
        if (exists && !options.Update) return Match(actualPng, File.ReadAllBytes(referencePath), referencePath, file, options);
        if (exists && ImageComparer.SamePng(actualPng, File.ReadAllBytes(referencePath)))
            return new(ScreenshotOutcome.Same, $"Referensbilden {referencePath} är oförändrad.", referencePath);
        Directory.CreateDirectory(options.ReferenceDirectory);
        File.WriteAllBytes(referencePath, actualPng);
        return exists
            ? new(ScreenshotOutcome.Updated, $"Referensbilden {referencePath} uppdaterades. Granska den och checka in den.", referencePath)
            : new(ScreenshotOutcome.Created, $"Referensbilden {referencePath} saknades och skapades nu. Granska den och checka in den; nästa körning jämför mot den.", referencePath);
    }

    /// <summary>Namnet som filnamn: allt utom bokstäver, siffror, punkt, understreck och bindestreck blir bindestreck.</summary>
    public static string FileName(string name)
    {
        var safe = new string(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '-').ToArray()).Trim('-', '.');
        return safe.Length == 0 ? "page" : safe;
    }

    private static ScreenshotResult Match(byte[] actualPng, byte[] referencePng, string referencePath, string file, ScreenshotOptions options)
    {
        if (actualPng.AsSpan().SequenceEqual(referencePng)) return new(ScreenshotOutcome.Same, "Lika med referensbilden.", referencePath, 0);
        RgbaImage expected;
        RgbaImage actual;
        try
        {
            expected = ImageComparer.DecodePng(referencePng);
            actual = ImageComparer.DecodePng(actualPng);
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException)
        {
            return new(ScreenshotOutcome.Unreadable, $"Referensbilden {referencePath} går inte att läsa: {e.Message} Skapa den på nytt.", referencePath);
        }
        if (expected.Width != actual.Width || expected.Height != actual.Height)
        {
            var sizePath = Save(options, file + ".actual.png", actualPng);
            return new(ScreenshotOutcome.SizeMismatch,
                $"Sidan har en annan storlek än referensbilden: {actual.Width}x{actual.Height} mot {expected.Width}x{expected.Height} pixlar.\nReferens: {referencePath}\nFaktisk: {sizePath}",
                referencePath, null, sizePath);
        }

        var result = ImageComparer.Compare(expected, actual, options.Tolerance);
        var share = result.Share * 100;
        if (share <= options.ThresholdPercent) return new(ScreenshotOutcome.Same, "Lika med referensbilden.", referencePath, share);
        var actualPath = Save(options, file + ".actual.png", actualPng);
        var diffPath = Save(options, file + ".diff.png", ImageComparer.EncodePng(ImageComparer.RenderDiff(expected, actual, options.Tolerance)));
        return new(ScreenshotOutcome.Different,
            $"Sidan skiljer sig från referensbilden: {share.ToString("0.###", Swedish)} % av pixlarna ({result.DifferingPixels.ToString("N0", Swedish)} av {result.TotalPixels.ToString("N0", Swedish)}) "
            + $"avviker mer än {options.Tolerance} per färgkanal; gränsen är {options.ThresholdPercent.ToString("0.###", Swedish)} %.\n"
            + $"Referens: {referencePath}\nFaktisk: {actualPath}\nSkillnad (rött): {diffPath}",
            referencePath, share, actualPath, diffPath);
    }

    private static string Save(ScreenshotOptions options, string fileName, byte[] png)
    {
        var path = Path.Combine(options.DiffDirectory, fileName);
        Directory.CreateDirectory(options.DiffDirectory);
        File.WriteAllBytes(path, png);
        return path;
    }
}
