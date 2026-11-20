using System.Globalization;
using Ecr.MethodologyImport.Model;

namespace Ecr.MethodologyImport.Reading;

/// <summary>Рядок дзеркала <c>AF_Formulas</c> старої системи (лише поля, потрібні моделі імпорту).</summary>
public sealed record LegacyFormulaRow(
    string? Methodology,
    string? MethodologyVersion,
    string? Name,
    string? Version,
    string? Arguments,
    string? Text,
    DateTime? StartDate,
    DateTime? EndDate,
    bool IsAvailable,
    string? Report,
    string RowId);

/// <summary>Рядок дзеркала <c>AF_Constants</c>: одне значення константи (<c>Категорія × Версія</c>).</summary>
public sealed record LegacyConstantRow(
    string? Methodology,
    string? MethodologyVersion,
    string? Name,
    string? Category,
    string? Version,
    string? Value,
    string? Parameter,
    DateTime? StartDate,
    DateTime? EndDate,
    string? Unit,
    string RowId);

/// <summary>Рядок дзеркала <c>AF_EmissionCalculationWork_Rules</c>: правило відбору рядків таблиці для методології.</summary>
public sealed record LegacyRuleRow(
    string? Methodology,
    string? MethodologyVersion,
    string? Name,
    string? Version,
    string? UniqueName,
    bool IsAvailable,
    DateTime? StartDate,
    DateTime? EndDate,
    string? Table,
    string? Parameters,
    string? Values,
    string RowId);

/// <summary>Лічильники читання дзеркал (що пропущено й чому).</summary>
public sealed class LegacyMirrorStats
{
    public long FormulaRows { get; internal set; }

    public long ConstantRows { get; internal set; }

    public long RuleRows { get; internal set; }

    /// <summary>Рядки без коду методології/версії/імені — не потрапляють у модель.</summary>
    public long SkippedRows { get; internal set; }

    public long TrimmedFields { get; internal set; }
}

/// <summary>
/// Шлях «дзеркала SQL старої системи → модель імпорту» (B13 §7, кроки 1–4): <c>AF_Formulas</c> і <c>AF_Constants</c>
/// читаються в ТУ САМУ <see cref="MethodologyModel"/>, що й AF XML (далі працюють ті самі аналіз, нормалізація й
/// пакет: нічого не дублюється). Відмінності від XML — лише в джерелі:
/// <list type="bullet">
/// <item>імена й версії методології — готові поля <c>MInfo_Name/MInfo_Version</c> (шляху з <c>\</c> немає);</item>
/// <item>дати в дзеркалі — ЛОКАЛЬНІ (без пояса); у моделі дати AF — UTC-рядки (як у XML), тож локальний час
/// майданчика переводиться в UTC (<c>timeZone</c>), а імпортер повертає його в дні майданчика;</item>
/// <item>значення констант — текст як є (<c>CInfo_Value</c>), без перетворень.</item>
/// </list>
/// Читання з SQL окремо (<see cref="LegacySqlMirrorSource"/>): ця частина працює з рядками й тестується без БД.
/// </summary>
public static class LegacyMirrorReader
{
    /// <summary>Пояс майданчика за замовчуванням (як <c>timeZone</c> у <c>POST /methodologies/import</c> репетицій).</summary>
    public const string DefaultTimeZone = "Asia/Atyrau";

    public static (MethodologyModel Model, AfReadStats ReadStats, LegacyMirrorStats Stats) Build(
        IEnumerable<LegacyFormulaRow> formulas,
        IEnumerable<LegacyConstantRow> constants,
        TimeZoneInfo timeZone,
        string? onlyMethodology = null,
        IEnumerable<LegacyRuleRow>? rules = null)
    {
        ArgumentNullException.ThrowIfNull(formulas);
        ArgumentNullException.ThrowIfNull(constants);
        ArgumentNullException.ThrowIfNull(timeZone);

        var stats = new LegacyMirrorStats();
        var formulaDefs = new List<FormulaDef>();
        var constantDefs = new List<ConstantDef>();
        var buildStats = new ModelBuildStats();

        string T(string? raw)
        {
            var value = raw ?? string.Empty;
            var trimmed = value.Trim();
            if (trimmed.Length != value.Length)
            {
                stats.TrimmedFields++;
            }

            return trimmed;
        }

        foreach (var row in formulas)
        {
            stats.FormulaRows++;
            var (m, mv, name, ver) = (T(row.Methodology), T(row.MethodologyVersion), T(row.Name), T(row.Version));
            if (m.Length == 0 || mv.Length == 0 || name.Length == 0 || ver.Length == 0)
            {
                stats.SkippedRows++;
                continue;
            }

            if (Skip(m, onlyMethodology))
            {
                continue;
            }

            buildStats.FormulaVersionElements++;
            formulaDefs.Add(new FormulaDef(
                m, mv, name, ver,
                T(row.Arguments), T(row.Text),
                Utc(row.StartDate, timeZone), Utc(row.EndDate, timeZone),
                row.IsAvailable ? "True" : "False",
                T(row.Report),
                $"sql:AF_Formulas/{row.RowId}"));
        }

        foreach (var row in constants)
        {
            stats.ConstantRows++;
            var (m, mv, name) = (T(row.Methodology), T(row.MethodologyVersion), T(row.Name));
            if (m.Length == 0 || mv.Length == 0 || name.Length == 0)
            {
                stats.SkippedRows++;
                continue;
            }

            if (Skip(m, onlyMethodology))
            {
                continue;
            }

            buildStats.ConstantValueElements++;
            constantDefs.Add(new ConstantDef(
                m, mv, name,
                T(row.Category), T(row.Version),
                true,
                T(row.Value), T(row.Parameter),
                Utc(row.StartDate, timeZone), Utc(row.EndDate, timeZone),
                T(row.Unit),
                $"sql:AF_Constants/{row.RowId}"));
        }

        var ruleDefs = new List<RuleDef>();
        foreach (var row in rules ?? [])
        {
            stats.RuleRows++;
            var (m, mv, name, ver) = (T(row.Methodology), T(row.MethodologyVersion), T(row.Name), T(row.Version));
            if (m.Length == 0 || mv.Length == 0 || name.Length == 0)
            {
                stats.SkippedRows++;
                continue;
            }

            if (Skip(m, onlyMethodology))
            {
                continue;
            }

            var table = T(row.Table);
            var (conditions, problem) = ParseConditions(T(row.Parameters), T(row.Values), table);
            ruleDefs.Add(new RuleDef(
                m, mv, name, ver.Length == 0 ? "V1" : ver, T(row.UniqueName), table, conditions,
                row.IsAvailable,
                Utc(row.StartDate, timeZone), Utc(row.EndDate, timeZone),
                problem,
                $"sql:AF_EmissionCalculationWork_Rules/{row.RowId}"));
        }

        ruleDefs = ruleDefs
            .OrderBy(r => r.Methodology, StringComparer.Ordinal)
            .ThenBy(r => r.MethodologyVersion, StringComparer.Ordinal)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .ThenBy(r => r.Version, StringComparer.Ordinal)
            .ThenBy(r => r.Path, StringComparer.Ordinal)
            .ToList();

        buildStats.TrimmedFields = stats.TrimmedFields;
        formulaDefs = formulaDefs
            .OrderBy(f => f.Methodology, StringComparer.Ordinal)
            .ThenBy(f => f.MethodologyVersion, StringComparer.Ordinal)
            .ThenBy(f => f.Name, StringComparer.Ordinal)
            .ThenBy(f => f.Version, StringComparer.Ordinal)
            .ThenBy(f => f.Path, StringComparer.Ordinal)
            .ToList();
        constantDefs = constantDefs
            .OrderBy(c => c.Methodology, StringComparer.Ordinal)
            .ThenBy(c => c.MethodologyVersion, StringComparer.Ordinal)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .ThenBy(c => c.Category, StringComparer.Ordinal)
            .ThenBy(c => c.Version, StringComparer.Ordinal)
            .ThenBy(c => c.Path, StringComparer.Ordinal)
            .ToList();
        buildStats.DuplicateFormulaKeys = formulaDefs
            .GroupBy(f => f.Key, StringComparer.Ordinal)
            .Sum(g => g.Count() - 1);

        var model = new MethodologyModel { Formulas = formulaDefs, Constants = constantDefs, Rules = ruleDefs, BuildStats = buildStats };
        var readStats = new AfReadStats { Elements = formulaDefs.Count + constantDefs.Count };
        return (model, readStats, stats);
    }

    /// <summary>Розбір <c>RuleArg_Parameters</c> / <c>RuleArg_Values</c>: списки через «;» (кінцевий «;» порожнього елемента не дає).</summary>
    /// <remarks>
    /// <c>*</c> у параметрах — «уся таблиця» (умов немає). Кількість параметрів і значень мусить збігатися, інакше пари
    /// не відновити (стара система зіставляла їх за позицією): рядок позначається <c>Problem</c>, умови не вгадуються.
    /// </remarks>
    internal static (IReadOnlyList<RuleCondition> Conditions, string? Problem) ParseConditions(string parameters, string values, string table)
    {
        if (table.Length == 0)
        {
            return ([], "noTable");
        }

        if (parameters.Length == 0)
        {
            return ([], "noParameters");
        }

        if (parameters == "*")
        {
            return ([], null);
        }

        var names = SplitList(parameters);
        var vals = SplitList(values);
        if (names.Count != vals.Count)
        {
            return ([], string.Create(CultureInfo.InvariantCulture, $"parameterValueCountMismatch:{names.Count}/{vals.Count}"));
        }

        if (names.Any(n => n.Length == 0 || n == "*"))
        {
            return ([], "emptyOrWildcardParameterInList");
        }

        return ([.. names.Select((n, i) => new RuleCondition(n, vals[i]))], null);
    }

    private static List<string> SplitList(string text)
    {
        var parts = text.Split(';').Select(p => p.Trim()).ToList();
        if (parts.Count > 0 && parts[^1].Length == 0)
        {
            parts.RemoveAt(parts.Count - 1);
        }

        return parts;
    }

    private static bool Skip(string methodology, string? only)
        => only is not null && !string.Equals(methodology, only, StringComparison.Ordinal);

    /// <summary>Локальний час майданчика → UTC-рядок AF (<c>yyyy-MM-ddTHH:mm:ssZ</c>); порожньо, якщо дати немає.</summary>
    internal static string Utc(DateTime? local, TimeZoneInfo timeZone)
    {
        if (local is not { } value)
        {
            return string.Empty;
        }

        var unspecified = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
        DateTime utc;
        try
        {
            utc = TimeZoneInfo.ConvertTimeToUtc(unspecified, timeZone);
        }
        catch (ArgumentException)
        {
            // Неіснуючий локальний час (перехід на літній) або поза діапазоном: стандартний зсув пояса.
            utc = unspecified - timeZone.BaseUtcOffset;
        }

        return utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }
}
