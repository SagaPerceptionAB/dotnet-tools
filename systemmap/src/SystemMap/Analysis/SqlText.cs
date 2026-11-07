using System.Text.RegularExpressions;

namespace SystemMap;

/// <summary>Läser ut vad en SQL-sträng gör och vilka tabeller den rör, för att kunna skriva "läsa Orders".</summary>
internal static partial class SqlText
{
    /// <summary>Till exempel "läsa Orders", "lägga till i Customers" eller "köra proceduren dbo.Spara".</summary>
    public static string Describe(string sql)
    {
        var trimmed = sql.TrimStart();
        var verb = FirstWord().Match(trimmed).Value.ToUpperInvariant();
        if (verb is "EXEC" or "EXECUTE")
            return Procedure().Match(trimmed) is { Success: true } p ? $"köra proceduren {p.Groups[1].Value}" : "köra en procedur";
        var tables = string.Join(", ", Tables(sql));
        var action = verb switch
        {
            "SELECT" or "WITH" => "läsa",
            "INSERT" => "lägga till i",
            "UPDATE" => "ändra i",
            "DELETE" => "ta bort ur",
            "MERGE" => "skriva till",
            _ => "SQL mot",
        };
        return tables.Length > 0 ? $"{action} {tables}" : action == "SQL mot" ? "SQL-fråga" : $"{action} databasen";
    }

    /// <summary>Tabellerna efter FROM, JOIN, INTO, UPDATE och MERGE, i ordning och utan dubbletter.</summary>
    public static IReadOnlyList<string> Tables(string sql) =>
        TableNames().Matches(sql)
            .Select(m => m.Groups[1].Value.Split('.').Last().Trim('[', ']', '"', '`'))
            .Where(t => t.Length > 0 && !t.Equals("SET", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    [GeneratedRegex(@"^\w+")]
    private static partial Regex FirstWord();

    [GeneratedRegex(@"^EXEC(?:UTE)?\s+([\w.\[\]]+)", RegexOptions.IgnoreCase)]
    private static partial Regex Procedure();

    [GeneratedRegex(@"\b(?:FROM|JOIN|INTO|UPDATE|MERGE)\s+((?:[\[""`]?\w+[\]""`]?\.)*[\[""`]?\w+[\]""`]?)", RegexOptions.IgnoreCase)]
    private static partial Regex TableNames();
}
