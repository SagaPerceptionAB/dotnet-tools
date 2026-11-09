namespace TestGen.Core.Run;

/// <summary>Ett rapportformat utöver de inbyggda (html, json, junit). Registreras med <see cref="ReportWriter.Register"/>.</summary>
public interface IReportFormat
{
    /// <summary>Filändelsen med punkt, i gemener (".xlsx").</summary>
    string Extension { get; }

    /// <summary>Vad filen är, för bekräftelsen "Skrev rapport.xlsx (Excel-rapport)".</summary>
    string Description { get; }

    /// <summary>Är den befintliga filen en rapport från testgen (så att den får skrivas över)?</summary>
    bool IsReport(string fullPath);

    void Write(string fullPath, ReportInfo info, IReadOnlyList<ReportEntry> entries, HeaderMask mask);

    /// <summary>Rapport om en körning som inte kunde genomföras.</summary>
    void WriteFailure(string fullPath, ReportInfo info, string what, string action, int finishedTests, HeaderMask mask);
}
