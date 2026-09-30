using System.Globalization;
using System.Text;

namespace Ecr.Bootstrap.Excel;

/// <summary>Вага зауваження звіту.</summary>
public enum IssueSeverity
{
    /// <summary>Імпорт не може бути застосований, доки це не виправлено в книзі.</summary>
    Error,

    /// <summary>
    /// Потрібне рішення людини. Імпорт не вгадує: відповідне поле лишається
    /// порожнім (одиниця, формула), а місце названо тут (ФВ-16.12).
    /// </summary>
    ManualReview,

    /// <summary>Довідка: що і за яким правилом вирішено автоматично.</summary>
    Info,
}

/// <summary>Одне зауваження звіту.</summary>
/// <param name="Severity">Вага.</param>
/// <param name="Location">Місце в книзі (<c>Аркуш!C5</c>) або в плані.</param>
/// <param name="Message">Що саме і чому.</param>
public sealed record ImportIssue(IssueSeverity Severity, string Location, string Message);

/// <summary>
/// Звіт про відповідність (ФВ-2.10): що імпортовано автоматично, що потребує
/// ручного рішення, і чек-лист таблиць для звірки бізнесом.
/// </summary>
public sealed class ImportReport
{
    private readonly List<ImportIssue> _issues = [];

    /// <summary>Усі зауваження в порядку виявлення.</summary>
    public IReadOnlyList<ImportIssue> Issues => _issues;

    /// <summary>Чи є бодай одна помилка — тоді запис заборонено.</summary>
    public bool HasErrors => _issues.Any(i => i.Severity == IssueSeverity.Error);

    /// <summary>Додає помилку.</summary>
    /// <param name="location">Місце.</param>
    /// <param name="message">Опис.</param>
    public void Error(string location, string message) => _issues.Add(new(IssueSeverity.Error, location, message));

    /// <summary>Додає пункт для ручного рішення.</summary>
    /// <param name="location">Місце.</param>
    /// <param name="message">Опис.</param>
    public void Manual(string location, string message) => _issues.Add(new(IssueSeverity.ManualReview, location, message));

    /// <summary>Додає довідку.</summary>
    /// <param name="location">Місце.</param>
    /// <param name="message">Опис.</param>
    public void Info(string location, string message) => _issues.Add(new(IssueSeverity.Info, location, message));

    /// <summary>Кількість зауважень заданої ваги.</summary>
    /// <param name="severity">Вага.</param>
    /// <returns>Кількість.</returns>
    public int Count(IssueSeverity severity) => _issues.Count(i => i.Severity == severity);

    /// <summary>Звіт у Markdown.</summary>
    /// <param name="plan">Вичитана структура.</param>
    /// <param name="outcome">Рядок про результат запису: сухий прогін, чернетка N чи збій.</param>
    /// <returns>Текст звіту.</returns>
    public string ToMarkdown(StructurePlan plan, string outcome)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        var tables = plan.Tables.ToList();

        sb.AppendLine(c, $"# Звіт імпорту структури: {plan.SourceFile}");
        sb.AppendLine();
        sb.AppendLine(c, $"**Результат:** {outcome}");
        sb.AppendLine();
        sb.AppendLine(c, $"- Аркушів: {plan.Sheets.Count}; таблиць: {tables.Count}; колонок: {tables.Sum(t => t.Columns.Count)}; фіксованих рядків: {tables.Sum(t => t.Rows.Count)}.");
        sb.AppendLine(c, $"- Помилок: {Count(IssueSeverity.Error)}; потребує ручного рішення: {Count(IssueSeverity.ManualReview)}; довідок: {Count(IssueSeverity.Info)}.");
        sb.AppendLine("- Версія створюється лише **чернеткою**: публікує її людина після звірки за чек-листом нижче (ФВ-2.9).");
        sb.AppendLine();

        Section(sb, "Помилки — запис заборонено, доки їх не виправлено", IssueSeverity.Error);
        Section(sb, "Потребує ручного рішення — імпорт не вгадує", IssueSeverity.ManualReview);

        sb.AppendLine("## Чек-лист звірки таблиць");
        sb.AppendLine();
        foreach (var sheet in plan.Sheets)
        {
            foreach (var t in sheet.Tables)
            {
                sb.AppendLine(c, $"- [ ] **{Escape(sheet.Name)} / {Escape(t.Name)}** (`{t.Address}`, {OriginText(t.Origin)}): {t.Columns.Count} колонок, {t.Rows.Count} рядків, {t.RowMode}, {t.LayoutKind}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Імпортовано автоматично");
        foreach (var sheet in plan.Sheets)
        {
            foreach (var t in sheet.Tables)
            {
                sb.AppendLine();
                sb.AppendLine(c, $"### {Escape(sheet.Name)} / {Escape(t.Name)} — `{sheet.Code}.{t.Code}`");
                sb.AppendLine();
                sb.AppendLine("| # | Код | Заголовок | Тип | Одиниця | Формула |");
                sb.AppendLine("|---|---|---|---|---|---|");
                foreach (var col in t.Columns)
                {
                    var type = col.Scale is { } s ? $"{col.DataType}({s})" : col.DataType.ToString();
                    if (col.IsReadOnly)
                    {
                        type += ", лише читання";
                    }

                    sb.AppendLine(c, $"| {col.Ordinal} | `{col.Code}` | {Escape(col.Header)} | {type} | {col.UnitCode ?? "—"} | {(col.Formula is null ? "—" : $"`{Escape(col.Formula)}`")} |");
                }

                if (t.Rows.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("| # | RowKey | Підпис | Вид | Батько |");
                    sb.AppendLine("|---|---|---|---|---|");
                    foreach (var row in t.Rows)
                    {
                        sb.AppendLine(c, $"| {row.Ordinal} | `{row.Key}` | {Escape(row.Label)} | {row.Kind} | {row.ParentKey ?? "—"} |");
                    }
                }
            }
        }

        sb.AppendLine();
        Section(sb, "Довідка: вирішено автоматично", IssueSeverity.Info);
        return sb.ToString();
    }

    private void Section(StringBuilder sb, string title, IssueSeverity severity)
    {
        var items = _issues.Where(i => i.Severity == severity).ToList();
        sb.AppendLine(CultureInfo.InvariantCulture, $"## {title} ({items.Count})");
        sb.AppendLine();
        if (items.Count == 0)
        {
            sb.AppendLine("Немає.");
        }

        foreach (var i in items)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- `{i.Location}` — {Escape(i.Message)}");
        }

        sb.AppendLine();
    }

    private static string OriginText(TableOrigin origin) => origin switch
    {
        TableOrigin.ExcelTable => "таблиця Excel",
        TableOrigin.DefinedName => "іменований діапазон",
        _ => "межі вгадано за порожніми рядками",
    };

    /// <summary>Екранує символи, що ламають рядок таблиці Markdown.</summary>
    private static string Escape(string text) => text.Replace("|", "\\|", StringComparison.Ordinal)
        .Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
