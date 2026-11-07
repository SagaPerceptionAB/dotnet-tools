using System.Text.RegularExpressions;
using ClosedXML.Excel;
using TestGen.Scenarios;

namespace TestGen.Core.Run;

/// <summary>
/// En miljö ur bladet Miljöer. Värdena är råa (${env:...} är inte ersatt än). <see cref="Rows"/> säger på vilken rad
/// varje värde står (för felmeddelanden); nycklarna är api, token, timeout och header#0, header#1 ...
/// </summary>
public sealed record EnvironmentConfig(string Name, string? Api, List<string> Headers, string? Token, string? Timeout)
{
    public Dictionary<string, int> Rows { get; init; } = new();
}

/// <summary>Läser bladet "Miljöer" (kolumnerna Miljö | Inställning | Värde). Bladet är valfritt i äldre filer.</summary>
public static partial class EnvironmentSheet
{
    public const string SheetName = "Miljöer";
    public static readonly string[] Headers = { "Miljö", "Inställning", "Värde" };
    public static readonly string[] Settings = { "api", "header", "token", "timeout" };

    [GeneratedRegex(@"(?i)authorization|auth|api[-_ ]?key|token|secret|password|passwd|cookie|credential|bearer")]
    private static partial Regex SecretName();

    /// <summary>Allt som lästs: miljöerna, varningar och kolumnnumret för Värde (efter rubrikmatchningen).</summary>
    public sealed record Sheet(List<EnvironmentConfig> Envs, List<ExcelIssue> Warnings, int ValueColumn);

    /// <summary>Alla miljöer, eller null om arbetsboken saknar bladet. Kastar <see cref="ExcelValidationException"/> med alla fel.</summary>
    public static List<EnvironmentConfig>? Read(string path) => ReadSheet(path)?.Envs;

    /// <summary>Som <see cref="Read"/> men med varningar (t.ex. hemligheter i klartext) och kolumnen för Värde.</summary>
    public static Sheet? ReadSheet(string path)
    {
        if (!File.Exists(path)) throw new MissingSourceException(SourceKind.Excel, Path.GetFullPath(path));
        using var stream = FriendlyErrors.Guard(SourceKind.Excel, path, () => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        using var wb = FriendlyErrors.Guard(SourceKind.Excel, path, () => new XLWorkbook(stream));
        if (!wb.TryGetWorksheet(SheetName, out var ws)) return null;

        var col = new[] { 1, 2, 3 };
        foreach (var c in ws.Row(1).CellsUsed())
        {
            var i = Array.FindIndex(Headers, h => h.Equals(c.GetString().Trim(), StringComparison.OrdinalIgnoreCase));
            if (i >= 0) col[i] = c.Address.ColumnNumber;
        }

        var result = new List<EnvironmentConfig>();
        var errors = new List<ExcelIssue>();
        var warnings = new List<ExcelIssue>();
        var last = ws.LastRowUsed()?.RowNumber() ?? 1;
        for (var r = 2; r <= last; r++)
        {
            var row = ws.Row(r);
            string Cell(int i) => row.Cell(col[i]).GetString().Trim();
            var (env, setting, value) = (Cell(0), Cell(1).ToLowerInvariant(), Cell(2));
            if (env.Length == 0 && setting.Length == 0 && value.Length == 0) continue;
            ExcelIssue Issue(int i, string message, bool error = true) => new(SheetName, r, col[i], error ? Headers[i] : null, message, error, null, env.Length == 0 ? null : env);
            if (env.Length == 0) { errors.Add(Issue(0, "cellen är tom. Skriv miljöns namn, t.ex. lokal.")); continue; }
            if (!Settings.Contains(setting))
            {
                errors.Add(Issue(1, $"okänd inställning \"{Cell(1)}\"." + Suggest.DidYouMean(setting, Settings) + $" Giltiga: {string.Join(", ", Settings)}."));
                continue;
            }
            if (value.Length == 0) { errors.Add(Issue(2, $"cellen är tom för {setting}. Skriv värdet, eller ta bort raden.")); continue; }

            var cfg = result.FirstOrDefault(e => e.Name.Equals(env, StringComparison.OrdinalIgnoreCase));
            if (cfg is null) { cfg = new EnvironmentConfig(env, null, new List<string>(), null, null); result.Add(cfg); }
            var updated = setting switch
            {
                "api" => cfg with { Api = value },
                "token" => cfg with { Token = value },
                "timeout" => cfg with { Timeout = value },
                _ => cfg,
            };
            var key = setting == "header" ? $"header#{cfg.Headers.Count}" : setting;
            cfg.Rows[key] = r;
            if (setting == "header") cfg.Headers.Add(value);
            else result[result.IndexOf(cfg)] = updated;

            if (!EnvVars.HasPlaceholder(value))
            {
                if (setting == "token")
                    warnings.Add(Issue(2, "token står i klartext i Excel-filen. Använd ${env:NAMN} så att den inte sprids när filen delas.", false));
                else if (setting == "header" && value.IndexOf(':') is > 0 and var colon && SecretName().IsMatch(value[..colon]) && value[(colon + 1)..].Trim().Length > 0)
                    warnings.Add(Issue(2, $"headern {value[..colon].Trim()} står i klartext i Excel-filen. Använd ${{env:NAMN}} så att den inte sprids när filen delas.", false));
            }
        }
        if (errors.Count > 0) throw new ExcelValidationException(path, errors, warnings);
        return new Sheet(result, warnings, col[2]);
    }

    /// <summary>Hittar en miljö (versaler/gemener spelar ingen roll). Fel som listar tillgängliga om den saknas.</summary>
    public static EnvironmentConfig Find(string path, string name) => FindWithWarnings(path, name).Env;

    /// <summary>Som <see cref="Find"/> men också varningar som gäller just den miljön, och kolumnnumret för Värde.</summary>
    public static (EnvironmentConfig Env, List<ExcelIssue> Warnings, int ValueColumn) FindWithWarnings(string path, string name)
    {
        var sheet = ReadSheet(path) ?? throw new ArgumentException(
            $"Excel-filen {path} saknar bladet \"{SheetName}\" (kolumnerna Miljö, Inställning, Värde). Skapa en ny mall med: testgen template.");
        var envs = sheet.Envs;
        var env = envs.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ??
                  throw new ArgumentException($"Okänd miljö '{name}' i {Path.GetFileName(path)}." + Suggest.DidYouMean(name, envs.Select(e => e.Name)) + " " +
                      (envs.Count == 0 ? $"Bladet {SheetName} är tomt." : $"Tillgängliga: {string.Join(", ", envs.Select(e => e.Name))}."));
        return (env, sheet.Warnings.Where(w => w.Scope is not null && w.Scope.Equals(env.Name, StringComparison.OrdinalIgnoreCase)).ToList(), sheet.ValueColumn);
    }
}
