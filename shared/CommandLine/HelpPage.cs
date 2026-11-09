using System.Text;

namespace CommandLine;

/// <summary>En rad i hjälpen: ett namn (kommando, flagga, kod) och en förklaring.</summary>
public readonly record struct HelpItem(string Name, string Text);

/// <summary>
/// Hjälptexten. Sektionerna kommer alltid i samma ordning: Användning, Beskrivning, Kommandon, Flaggor, Miljövariabler,
/// Exempel, Exitkoder. Tomma sektioner utelämnas och namnen står i jämna kolumner.
/// </summary>
public sealed class HelpPage
{
    public IReadOnlyList<string> Usage { get; init; } = [];
    public string? Description { get; init; }
    public IReadOnlyList<HelpItem> Commands { get; init; } = [];
    public IReadOnlyList<HelpItem> Flags { get; init; } = [];
    public IReadOnlyList<HelpItem> Environment { get; init; } = [];
    public IReadOnlyList<string> Examples { get; init; } = [];
    public IReadOnlyList<HelpItem> ExitCodes { get; init; } = [];

    /// <summary>Rubrikerna i den ordning de skrivs.</summary>
    public static readonly string[] Headings = ["Användning", "Beskrivning", "Kommandon", "Flaggor", "Miljövariabler", "Exempel", "Exitkoder"];

    public override string ToString()
    {
        var sb = new StringBuilder();
        Section(sb, Headings[0], Usage.Select(Indent));
        Section(sb, Headings[1], Description is { Length: > 0 } d ? d.Split('\n').Select(l => Indent(l.TrimEnd('\r'))) : []);
        Section(sb, Headings[2], Columns(Commands));
        Section(sb, Headings[3], Columns(Flags));
        Section(sb, Headings[4], Columns(Environment));
        Section(sb, Headings[5], Examples.Select(Indent));
        Section(sb, Headings[6], Columns(ExitCodes));
        return sb.ToString().TrimEnd() + "\n";
    }

    private static string Indent(string line) => line.Length == 0 ? "" : "  " + line;

    private static IEnumerable<string> Columns(IReadOnlyList<HelpItem> items)
    {
        var width = items.Count == 0 ? 0 : items.Max(i => i.Name.Length);
        return items.Select(i => $"  {i.Name.PadRight(width)}  {i.Text}".TrimEnd());
    }

    private static void Section(StringBuilder sb, string heading, IEnumerable<string> lines)
    {
        var list = lines.ToList();
        if (list.Count == 0) return;
        if (sb.Length > 0) sb.Append('\n');
        sb.Append(heading).Append(":\n");
        foreach (var l in list) sb.Append(l).Append('\n');
    }
}
