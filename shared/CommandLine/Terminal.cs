namespace CommandLine;

/// <summary>
/// All utskrift från ett kommando. <see cref="Out"/> (stdout) är resultat och data; <see cref="Err"/> (stderr) är förlopp,
/// varningar, fel och bekräftelser. Förlopp och färg visas bara när stderr är en terminal.
/// </summary>
public sealed class Terminal(TextWriter output, TextWriter error, bool interactive, bool noColor = false)
{
    public TextWriter Out { get; } = output;
    public TextWriter Err { get; } = error;

    /// <summary>Stderr är en terminal (inte omdirigerad till en fil eller pipe).</summary>
    public bool Interactive { get; } = interactive;

    /// <summary>Färg får användas: stderr är en terminal och <c>NO_COLOR</c> är inte satt.</summary>
    public bool Color { get; } = interactive && !noColor;

    /// <summary>Terminalen som den är just nu (Console.Out och Console.Error läses vid anropet).</summary>
    public static Terminal Default =>
        new(Console.Out, Console.Error, !Console.IsErrorRedirected, !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")));

    /// <summary>"Skrev rapport.html (HTML-rapport)" på stderr. Sökvägen skrivs relativt aktuell katalog.</summary>
    public void Confirm(string path, string what) => Err.WriteLine($"Skrev {Relative(path)} ({what})");

    public void Warning(string text) => Err.WriteLine($"Varning: {text}");

    /// <summary>"[ 3/12] text" på stderr, bara när stderr är en terminal.</summary>
    public void Progress(int done, int total, string text)
    {
        if (!Interactive) return;
        var width = total.ToString().Length;
        Err.WriteLine($"[{done.ToString().PadLeft(width)}/{total}] {text}");
    }

    /// <summary>Sökvägen relativt aktuell katalog, med snedstreck. Ligger filen utanför katalogen (eller på en annan enhet) används den fulla sökvägen.</summary>
    public static string Relative(string path) => Relative(path, Directory.GetCurrentDirectory());

    /// <summary>Sökvägen relativt <paramref name="root"/>, med snedstreck. Ligger filen utanför (eller på en annan enhet) används den fulla sökvägen.</summary>
    public static string Relative(string path, string root)
    {
        var full = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(Path.GetFullPath(root), full).Replace('\\', '/');
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? full.Replace('\\', '/') : relative;
    }
}
