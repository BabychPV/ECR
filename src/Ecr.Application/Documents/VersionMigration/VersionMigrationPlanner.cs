using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents.VersionMigration;

/// <summary>Режим переносу документів на нову версію шаблону (ФВ-7.5).</summary>
public enum VersionMigrationMode
{
    /// <summary>
    /// Нова версія може додавати й прибирати структуру, але жодне введене
    /// значення не зникає і не змінює тлумачення. Інакше — відмова.
    /// </summary>
    Safe = 0,

    /// <summary>
    /// Нова версія відрізняється лише презентаційним шаром (підписи,
    /// порядок, формати, видимість). Будь-яка структурна різниця — відмова.
    /// </summary>
    Presentation = 1,
}

/// <summary>Скільки непорожніх значень лежить у колонці вихідної версії.</summary>
/// <param name="ColumnDefId">Колонка вихідної версії.</param>
/// <param name="RowDefId">Опис рядка, до якого прив'язаний рядок (<c>null</c> — рядок без опису).</param>
/// <param name="Values">Кількість непорожніх комірок.</param>
public sealed record VersionMigrationCellUsage(int ColumnDefId, int? RowDefId, long Values);

/// <summary>Одна відмінність між версіями, як її бачить перенос.</summary>
/// <param name="Path">Шлях за кодами: <c>SHEET.TABLE.COLUMN</c>, <c>SHEET.TABLE#ROW</c>, <c>header.CODE</c>.</param>
/// <param name="Kind">
/// <c>Added</c> — нове в цільовій версії; <c>Removed</c> — зникає без даних;
/// <c>Lost</c> — зникає разом із введеними значеннями; <c>Modified</c> —
/// змінюється тлумачення (тип, одиниця, довідник, обов'язковість);
/// <c>Presentation</c> — лише вигляд.
/// </param>
/// <param name="ChangeClass">Клас зміни (ФВ-7.3).</param>
/// <param name="Values">Скільки введених значень зачіпає відмінність.</param>
/// <param name="Field">Змінене поле для <c>Modified</c>/<c>Presentation</c>.</param>
/// <param name="OldValue">Було.</param>
/// <param name="NewValue">Стане.</param>
public sealed record VersionMigrationItem(
    string Path, string Kind, ChangeClass ChangeClass, long Values,
    string? Field = null, string? OldValue = null, string? NewValue = null);

/// <summary>Колонка вихідної версії → колонка й таблиця цільової.</summary>
public sealed record VersionMigrationColumn(int SourceColumnDefId, int TargetColumnDefId, int TargetTableDefId);

/// <summary>Рядок фіксованої таблиці, якого вихідна версія не мала: його треба завести в наявних екземплярах.</summary>
public sealed record VersionMigrationNewRow(int TargetTableDefId, int TargetRowDefId, string RowKey, int Ordinal);

/// <summary>План переносу: відповідності ідентифікаторів і звіт про наслідки.</summary>
/// <remarks>
/// ⚠ Відповідність — лише за КОДАМИ (аркуш, таблиця, колонка, ключ рядка,
/// поле шапки), ніколи за позицією: ідентифікатори в кожної версії свої, а
/// <c>Ordinal</c> — презентація (ФВ-7.2).
/// </remarks>
public sealed record VersionMigrationPlan(
    IReadOnlyDictionary<int, int> Sheets,
    IReadOnlyDictionary<int, int> Tables,
    IReadOnlyList<VersionMigrationColumn> Columns,
    IReadOnlyDictionary<int, int> Rows,
    IReadOnlyDictionary<int, int> HeaderFields,
    IReadOnlyList<VersionMigrationNewRow> NewRows,
    IReadOnlyList<VersionMigrationItem> Items,
    long TransferredValues,
    long LostValues,
    long GuardedValues)
{
    /// <summary>Чи є серед відмінностей бодай одна непрезентаційна.</summary>
    public bool HasStructuralChanges => Items.Any(i => i.ChangeClass != ChangeClass.Presentation);

    /// <summary>
    /// Причини, з яких режим відмовить; порожньо — перенос дозволений.
    /// </summary>
    /// <remarks>
    /// <c>dataLoss</c> — зникли б введені значення; <c>guardedWithData</c> —
    /// змінилося б тлумачення введених значень, а стратегії перетворення
    /// режим не має; <c>structural</c> — режим <c>Presentation</c>, а версії
    /// відрізняються структурою.
    /// </remarks>
    public IReadOnlyList<string> RefusalsFor(VersionMigrationMode mode)
    {
        var reasons = new List<string>();

        if (mode == VersionMigrationMode.Presentation && HasStructuralChanges)
        {
            reasons.Add("structural");
        }

        if (LostValues > 0)
        {
            reasons.Add("dataLoss");
        }

        if (GuardedValues > 0)
        {
            reasons.Add("guardedWithData");
        }

        return reasons;
    }
}

/// <summary>
/// Будує план переносу документів між двома версіями одного шаблону (ФВ-7.5).
/// </summary>
/// <remarks>
/// Чиста функція над двома знімками структури та підрахунком значень: ані
/// бази, ані часу. Тому звіт сухого прогону й перевірка перед застосуванням
/// рахуються ОДНИМ кодом — розійтися вони не можуть.
/// </remarks>
public static class VersionMigrationPlanner
{
    /// <summary>Будує план.</summary>
    /// <param name="source">Поточна версія проєкту.</param>
    /// <param name="target">Цільова версія.</param>
    /// <param name="cells">Непорожні значення документів за колонками й описами рядків вихідної версії.</param>
    /// <param name="headerValues">Непорожні значення шапки за полями вихідної версії.</param>
    public static VersionMigrationPlan Plan(
        TemplateVersionSnapshot source,
        TemplateVersionSnapshot target,
        IReadOnlyList<VersionMigrationCellUsage> cells,
        IReadOnlyDictionary<int, long> headerValues)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(cells);
        ArgumentNullException.ThrowIfNull(headerValues);

        var items = new List<VersionMigrationItem>();
        var sheets = new Dictionary<int, int>();
        var tables = new Dictionary<int, int>();
        var columns = new List<VersionMigrationColumn>();
        var rows = new Dictionary<int, int>();
        var newRows = new List<VersionMigrationNewRow>();

        var valuesByColumn = cells
            .GroupBy(c => c.ColumnDefId)
            .ToDictionary(g => g.Key, g => g.Sum(c => c.Values));
        var valuesByRow = cells
            .Where(c => c.RowDefId is not null)
            .GroupBy(c => c.RowDefId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(c => c.Values));

        var targetSheets = Live(target.Sheets).ToDictionary(s => s.Code, StringComparer.Ordinal);
        var sourceSheets = Live(source.Sheets).ToList();

        foreach (var sheet in sourceSheets)
        {
            if (!targetSheets.TryGetValue(sheet.Code, out var otherSheet))
            {
                var sheetValues = Live(sheet.Tables).SelectMany(t => Live(t.Columns)).Sum(c => Count(valuesByColumn, c.Id));
                items.Add(Removal(sheet.Code, sheetValues));
                continue;
            }

            sheets[sheet.Id] = otherSheet.Id;
            PresentationDiff(items, sheet.Code, "Name", sheet.NameL10n, otherSheet.NameL10n);

            var targetTables = Live(otherSheet.Tables).ToDictionary(t => t.Code, StringComparer.Ordinal);
            foreach (var table in Live(sheet.Tables))
            {
                var tablePath = $"{sheet.Code}.{table.Code}";
                if (!targetTables.TryGetValue(table.Code, out var otherTable))
                {
                    items.Add(Removal(tablePath, Live(table.Columns).Sum(c => Count(valuesByColumn, c.Id))));
                    continue;
                }

                tables[table.Id] = otherTable.Id;
                PresentationDiff(items, tablePath, "Name", table.NameL10n, otherTable.NameL10n);
                CompareTableShape(items, tablePath, table, otherTable, valuesByColumn);
                CompareColumns(items, columns, tablePath, table, otherTable, valuesByColumn);
                CompareRows(items, rows, newRows, tablePath, table, otherTable, valuesByRow);
            }

            foreach (var added in Live(otherSheet.Tables).Where(t => Live(sheet.Tables).All(s => s.Code != t.Code)))
            {
                items.Add(new($"{sheet.Code}.{added.Code}", "Added", ChangeClass.Safe, 0));
            }
        }

        foreach (var added in targetSheets.Values.Where(t => sourceSheets.All(s => s.Code != t.Code)))
        {
            items.Add(new(added.Code, "Added", ChangeClass.Safe, 0));
        }

        var headerFields = CompareHeader(items, source, target, headerValues);

        // ⚠ Втрата значення рахується ПО КОМІРКАХ, а не сумою відмінностей:
        // значення в прибраній колонці прибраного рядка — одна втрата, а не
        // дві. Інакше звіт казав би «втрачено 2», де людина бачить одне число.
        var mappedColumns = columns.ToDictionary(c => c.SourceColumnDefId);
        long lost = 0;
        long transferred = 0;
        foreach (var usage in cells)
        {
            var rowLost = usage.RowDefId is { } rowDefId && !rows.ContainsKey(rowDefId);
            if (!mappedColumns.ContainsKey(usage.ColumnDefId) || rowLost)
            {
                lost += usage.Values;
            }
            else
            {
                transferred += usage.Values;
            }
        }

        lost += headerValues.Where(h => !headerFields.ContainsKey(h.Key)).Sum(h => h.Value);
        transferred += headerValues.Where(h => headerFields.ContainsKey(h.Key)).Sum(h => h.Value);

        var guarded = items.Where(i => i.Kind == "Modified").Sum(i => i.Values);

        return new VersionMigrationPlan(
            sheets, tables, columns, rows, headerFields, newRows, items, transferred, lost, guarded);
    }

    private static IEnumerable<SheetDef> Live(IEnumerable<SheetDef> sheets) => sheets.Where(s => !s.IsDeleted);

    private static IEnumerable<TableDef> Live(IEnumerable<TableDef> tables) => tables.Where(t => !t.IsDeleted);

    private static IEnumerable<ColumnDef> Live(IEnumerable<ColumnDef> columns) => columns.Where(c => !c.IsDeleted);

    private static IEnumerable<RowDef> Live(IEnumerable<RowDef> rows) => rows.Where(r => !r.IsDeleted);

    private static long Count(IReadOnlyDictionary<int, long> counts, int id) => counts.GetValueOrDefault(id);

    private static VersionMigrationItem Removal(string path, long values)
        => values > 0
            ? new(path, "Lost", ChangeClass.Breaking, values)
            : new(path, "Removed", ChangeClass.Guarded, 0);

    private static void CompareTableShape(
        List<VersionMigrationItem> items, string path, TableDef from, TableDef to,
        IReadOnlyDictionary<int, long> valuesByColumn)
    {
        if (from.RowMode == to.RowMode && from.LayoutKind == to.LayoutKind)
        {
            return;
        }

        // Зміна режиму рядків чи розкладки змінює, ЯК читаються вже введені
        // рядки, — той самий клас, що й зміна типу колонки.
        var values = Live(from.Columns).Sum(c => Count(valuesByColumn, c.Id));
        if (from.RowMode != to.RowMode)
        {
            items.Add(new(path, "Modified", ChangeClass.Guarded, values,
                nameof(TableDef.RowMode), from.RowMode.ToString(), to.RowMode.ToString()));
        }

        if (from.LayoutKind != to.LayoutKind)
        {
            items.Add(new(path, "Modified", ChangeClass.Guarded, values,
                nameof(TableDef.LayoutKind), from.LayoutKind.ToString(), to.LayoutKind.ToString()));
        }
    }

    private static void CompareColumns(
        List<VersionMigrationItem> items, List<VersionMigrationColumn> map, string tablePath,
        TableDef from, TableDef to, IReadOnlyDictionary<int, long> valuesByColumn)
    {
        var targetColumns = Live(to.Columns).ToDictionary(c => c.Code, StringComparer.Ordinal);

        foreach (var column in Live(from.Columns))
        {
            var path = $"{tablePath}.{column.Code}";
            var values = Count(valuesByColumn, column.Id);

            if (!targetColumns.TryGetValue(column.Code, out var other))
            {
                items.Add(Removal(path, values));
                continue;
            }

            map.Add(new VersionMigrationColumn(column.Id, other.Id, to.Id));

            Guarded(items, path, values, nameof(ColumnDef.DataType), column.DataType, other.DataType);
            Guarded(items, path, values, nameof(ColumnDef.Precision), column.Precision, other.Precision);
            Guarded(items, path, values, nameof(ColumnDef.Scale), column.Scale, other.Scale);
            Guarded(items, path, values, nameof(ColumnDef.UnitId), column.UnitId, other.UnitId);
            Guarded(items, path, values, nameof(ColumnDef.LookupRegistryDefId), column.LookupRegistryDefId, other.LookupRegistryDefId);

            // Обов'язковість лише ЗНЯТА нічого не ламає; додана — робить
            // порожні комірки наявних документів помилкою валідації.
            if (!column.IsRequired && other.IsRequired)
            {
                items.Add(new(path, "Modified", ChangeClass.Guarded, values,
                    nameof(ColumnDef.IsRequired), "optional", "required"));
            }

            PresentationDiff(items, path, nameof(ColumnDef.HeaderL10n), column.HeaderL10n, other.HeaderL10n);
            PresentationDiff(items, path, nameof(ColumnDef.Ordinal), column.Ordinal, other.Ordinal);
            PresentationDiff(items, path, nameof(ColumnDef.DisplayFormat), column.DisplayFormat, other.DisplayFormat);
            PresentationDiff(items, path, nameof(ColumnDef.IsHidden), column.IsHidden, other.IsHidden);
        }

        foreach (var added in targetColumns.Values.Where(c => Live(from.Columns).All(s => s.Code != c.Code)))
        {
            items.Add(new($"{tablePath}.{added.Code}", "Added", ChangeClass.Safe, 0));
        }
    }

    private static void CompareRows(
        List<VersionMigrationItem> items, Dictionary<int, int> map, List<VersionMigrationNewRow> newRows,
        string tablePath, TableDef from, TableDef to, IReadOnlyDictionary<int, long> valuesByRow)
    {
        var targetRows = Live(to.Rows).ToDictionary(r => r.RowKeyValue, StringComparer.Ordinal);
        var sourceKeys = Live(from.Rows).Select(r => r.RowKeyValue).ToHashSet(StringComparer.Ordinal);

        foreach (var row in Live(from.Rows))
        {
            var path = $"{tablePath}#{row.RowKeyValue}";
            if (!targetRows.TryGetValue(row.RowKeyValue, out var other))
            {
                items.Add(Removal(path, Count(valuesByRow, row.Id)));
                continue;
            }

            map[row.Id] = other.Id;
            PresentationDiff(items, path, nameof(RowDef.LabelL10n), row.LabelL10n, other.LabelL10n);
            PresentationDiff(items, path, nameof(RowDef.Ordinal), row.Ordinal, other.Ordinal);
        }

        foreach (var added in targetRows.Values.Where(r => !sourceKeys.Contains(r.RowKeyValue)))
        {
            items.Add(new($"{tablePath}#{added.RowKeyValue}", "Added", ChangeClass.Safe, 0));
            newRows.Add(new VersionMigrationNewRow(to.Id, added.Id, added.RowKeyValue, added.Ordinal));
        }
    }

    private static Dictionary<int, int> CompareHeader(
        List<VersionMigrationItem> items, TemplateVersionSnapshot source, TemplateVersionSnapshot target,
        IReadOnlyDictionary<int, long> headerValues)
    {
        var map = new Dictionary<int, int>();
        var targetFields = target.HeaderFields.Where(f => !f.IsDeleted).ToDictionary(f => f.Code, StringComparer.Ordinal);
        var sourceFields = source.HeaderFields.Where(f => !f.IsDeleted).ToList();

        foreach (var field in sourceFields)
        {
            var path = $"header.{field.Code}";
            var values = Count(headerValues, field.Id);
            if (!targetFields.TryGetValue(field.Code, out var other))
            {
                items.Add(Removal(path, values));
                continue;
            }

            map[field.Id] = other.Id;
            Guarded(items, path, values, nameof(HeaderFieldDef.DataType), field.DataType, other.DataType);
            Guarded(items, path, values, nameof(HeaderFieldDef.LookupRegistryDefId), field.LookupRegistryDefId, other.LookupRegistryDefId);
            if (!field.IsRequired && other.IsRequired)
            {
                items.Add(new(path, "Modified", ChangeClass.Guarded, values,
                    nameof(HeaderFieldDef.IsRequired), "optional", "required"));
            }

            PresentationDiff(items, path, nameof(HeaderFieldDef.LabelL10n), field.LabelL10n, other.LabelL10n);
            PresentationDiff(items, path, nameof(HeaderFieldDef.Ordinal), field.Ordinal, other.Ordinal);
        }

        foreach (var added in targetFields.Values.Where(f => sourceFields.All(s => s.Code != f.Code)))
        {
            items.Add(new($"header.{added.Code}", "Added", ChangeClass.Safe, 0));
        }

        return map;
    }

    private static void Guarded<T>(
        List<VersionMigrationItem> items, string path, long values, string field, T from, T to)
    {
        if (EqualityComparer<T>.Default.Equals(from, to))
        {
            return;
        }

        items.Add(new(path, "Modified", ChangeClass.Guarded, values, field, Text(from), Text(to)));
    }

    private static void PresentationDiff<T>(List<VersionMigrationItem> items, string path, string field, T from, T to)
    {
        if (EqualityComparer<T>.Default.Equals(from, to))
        {
            return;
        }

        items.Add(new(path, "Presentation", ChangeClass.Presentation, 0, field, Text(from), Text(to)));
    }

    private static void PresentationDiff(
        List<VersionMigrationItem> items, string path, string field, LocalizedText? from, LocalizedText? to)
    {
        var a = Flatten(from);
        var b = Flatten(to);
        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            return;
        }

        items.Add(new(path, "Presentation", ChangeClass.Presentation, 0, field, a, b));
    }

    /// <summary>Підпис усіма мовами одним рядком у стабільному порядку — для порівняння і звіту.</summary>
    private static string Flatten(LocalizedText? text)
        => text is null
            ? string.Empty
            : string.Join("; ", text.Values
                .Where(v => !string.IsNullOrEmpty(v.Value))
                .OrderBy(v => v.Key, StringComparer.Ordinal)
                .Select(v => $"{v.Key}: {v.Value}"));

    private static string? Text<T>(T value)
        => value switch
        {
            null => null,
            IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };
}
