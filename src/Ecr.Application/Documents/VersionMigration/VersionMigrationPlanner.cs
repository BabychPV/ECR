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
    long GuardedValues,
    IReadOnlyList<string>? RenameConflicts = null,
    IReadOnlySet<int>? TouchedSourceSheetIds = null)
{
    /// <summary>
    /// LP-32: аркуші вихідної версії, які план ЗАЧІПАЄ так, що подане/затверджене на них переносити не можна: втрата чи
    /// видалення (аркуш, таблиця, колонка, рядок), зміна тлумачення (клас Guarded і вище), перейменування таблиці чи
    /// колонки, нові рядки фіксованої таблиці. Додані колонки/таблиці/аркуші, розширення Precision/Scale, презентація
    /// й формули не зачіпають. <c>null</c> — не обчислювалось (як порожній).
    /// </summary>
    public IReadOnlySet<int> TouchedSheets => TouchedSourceSheetIds ?? new HashSet<int>();

    /// <summary>
    /// LP-41: шляхи (<c>SHEET.TABLE</c>) таблиць, де цільова версія ДОДАЄ або ЗМІНЮЄ формулу (нова формула,
    /// змінений вираз/діалект, нова обчислювана колонка). Непорожній — після переносу значення цих формул
    /// без перерахунку лишаться порожніми, тож обробник ставить один перерахунок проєкту.
    /// </summary>
    public IReadOnlyList<string> FormulaChanges { get; init; } = [];

    /// <summary>
    /// ACC-OW: комірки, на які цільова версія кладе НОВУ комірочну формулу і де вже лежать введені значення —
    /// єдиний клас <c>guardedWithData</c>, який знімає прапор <c>acceptGuardedOverwrite</c> (їх, як похідні підсумки,
    /// замінить перерахунок). Те саме число входить у <see cref="GuardedValues"/>.
    /// </summary>
    public IReadOnlyList<GuardedOverwriteItem> GuardedOverwrites { get; init; } = [];

    /// <summary>ACC-OW: скільки значень лежить у <see cref="GuardedOverwrites"/>.</summary>
    public long GuardedOverwritableValues => GuardedOverwrites.Sum(g => g.Values);

    /// <summary>
    /// Мапи Lookup шапки: що саме перепише SQL (поле вихідної версії, старий і новий запис). Лише для полів, де мапа
    /// покриває ВСІ вжиті значення; порожньо — SQL шапки лишається без змін.
    /// </summary>
    public IReadOnlyList<VersionMigrationLookupRemapRow> LookupRemaps { get; init; } = [];

    /// <summary>Скільки значень шапки перепише <see cref="LookupRemaps"/>.</summary>
    public long LookupRemappedValues { get; init; }

    /// <summary>Частина <see cref="VersionMigrationPlan.GuardedValues"/>, що лишилась через неповну мапу Lookup.</summary>
    public long LookupGuardedValues { get; init; }

    /// <summary>Скільки значень шапки не мають придатного відповідника в мапі (відмова <c>lookupValuesNotMapped</c>).</summary>
    public long LookupUnmappedValues { get; init; }

    /// <summary>Звіт мап Lookup по цілях, яких стосується пара версій.</summary>
    public IReadOnlyList<LookupRemapReportItem> LookupReport { get; init; } = [];

    /// <summary>Скільки пунктів мапи перейменувань суперечать цій парі версій (<c>renameConflict</c>).</summary>
    public int RenameConflictCount => RenameConflicts?.Count ?? 0;

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
        => RefusalsFor(mode, acceptGuardedOverwrite: false);

    /// <summary>
    /// Причини відмови з урахуванням згоди на перезапис похідних значень (ACC-OW).
    /// </summary>
    /// <param name="mode">Режим.</param>
    /// <param name="acceptGuardedOverwrite">
    /// <c>true</c> — з <c>guardedWithData</c> віднімаються ЛИШЕ значення під новою комірочною формулою
    /// (<see cref="GuardedOverwritableValues"/>); решта guarded (звуження, тип, одиниця, довідник, обов'язковість,
    /// режим рядків, рядок «лише читання») відмовляє як і раніше. <c>dataLoss</c>, <c>structural</c> і конфлікти
    /// перейменувань прапор не торкається.
    /// </param>
    public IReadOnlyList<string> RefusalsFor(VersionMigrationMode mode, bool acceptGuardedOverwrite)
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

        var blockingGuarded = acceptGuardedOverwrite ? GuardedValues - GuardedOverwritableValues : GuardedValues;
        if (blockingGuarded > 0)
        {
            reasons.Add("guardedWithData");
        }

        // Мапа Lookup задана, але не покриває всі вжиті значення (або ціль мапи непридатна): нічого не пишемо.
        if (LookupUnmappedValues > 0)
        {
            reasons.Add("lookupValuesNotMapped");
        }

        // ⛔ Мапа веде в таблицю/колонку, що вже має відповідник за кодом (або двоє ведуть в одну): мовчки
        // вибрати котрусь означало б злити чужі дані в одне місце. Відмова, а не вгадування.
        if (RenameConflictCount > 0)
        {
            reasons.Add("renameConflict");
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
    /// <param name="renames">
    /// Мапа «було → стало» кодів таблиць (і колонок) для структури, перейменованої між версіями; <c>null</c> —
    /// без мапи (відповідність лише за однаковим кодом, як і раніше).
    /// </param>
    /// <param name="lookup">
    /// Мапи записів Lookup шапки з даними їхнього використання; <c>null</c> — без мап (план і SQL як раніше).
    /// </param>
    public static VersionMigrationPlan Plan(
        TemplateVersionSnapshot source,
        TemplateVersionSnapshot target,
        IReadOnlyList<VersionMigrationCellUsage> cells,
        IReadOnlyDictionary<int, long> headerValues,
        IReadOnlyList<VersionMigrationTableRename>? renames = null,
        VersionMigrationLookupInput? lookup = null)
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
        var conflicts = new List<string>();
        var formulaChanges = new List<string>();
        var overwrites = new List<GuardedOverwriteItem>();

        var valuesByColumn = cells
            .GroupBy(c => c.ColumnDefId)
            .ToDictionary(g => g.Key, g => g.Sum(c => c.Values));
        var valuesByRow = cells
            .Where(c => c.RowDefId is not null)
            .GroupBy(c => c.RowDefId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(c => c.Values));

        // WW-02: скільки непорожніх значень лежить у КОМІРЦІ (колонка × опис рядка) вихідної версії.
        var valuesByCell = cells
            .Where(c => c.RowDefId is not null)
            .GroupBy(c => (c.ColumnDefId, RowDefId: c.RowDefId!.Value))
            .ToDictionary(g => g.Key, g => g.Sum(c => c.Values));

        var targetSheets = Live(target.Sheets).ToDictionary(s => s.Code, StringComparer.Ordinal);
        var sourceSheets = Live(source.Sheets).ToList();

        var touched = new HashSet<int>();

        foreach (var sheet in sourceSheets)
        {
            var itemsBefore = items.Count;
            var newRowsBefore = newRows.Count;

            if (!targetSheets.TryGetValue(sheet.Code, out var otherSheet))
            {
                var sheetValues = Live(sheet.Tables).SelectMany(t => Live(t.Columns)).Sum(c => Count(valuesByColumn, c.Id));
                items.Add(Removal(sheet.Code, sheetValues));
                touched.Add(sheet.Id);
                continue;
            }

            sheets[sheet.Id] = otherSheet.Id;
            PresentationDiff(items, sheet.Code, "Name", sheet.NameL10n, otherSheet.NameL10n);

            var targetTables = Live(otherSheet.Tables).ToDictionary(t => t.Code, StringComparer.Ordinal);
            var sourceTableCodes = Live(sheet.Tables).Select(t => t.Code).ToHashSet(StringComparer.Ordinal);
            var renamedTargets = new HashSet<int>();
            foreach (var table in Live(sheet.Tables))
            {
                var tablePath = $"{sheet.Code}.{table.Code}";
                var rename = FindRename(renames, sheet.Code, table.Code);
                string? renamedFrom = null;
                IReadOnlyList<VersionMigrationColumnRename>? columnRenames = null;

                if (!targetTables.TryGetValue(table.Code, out var otherTable))
                {
                    // Мапа спрацьовує лише там, де відповідника за кодом немає: однаковий код завжди першим.
                    if (rename is not null
                        && !string.Equals(rename.ToTableCode, table.Code, StringComparison.Ordinal)
                        && targetTables.TryGetValue(rename.ToTableCode, out var renamedTable))
                    {
                        if (sourceTableCodes.Contains(rename.ToTableCode))
                        {
                            conflicts.Add($"{tablePath} -> {rename.ToTableCode}: цільова таблиця вже має відповідник за кодом у вихідній версії");
                            items.Add(new(tablePath, "RenameConflict", ChangeClass.Breaking, 0, "Code", table.Code, rename.ToTableCode));
                        }
                        else if (!renamedTargets.Add(renamedTable.Id))
                        {
                            conflicts.Add($"{tablePath} -> {rename.ToTableCode}: у цю таблицю вже веде інший запис мапи");
                            items.Add(new(tablePath, "RenameConflict", ChangeClass.Breaking, 0, "Code", table.Code, rename.ToTableCode));
                        }
                        else
                        {
                            otherTable = renamedTable;
                            renamedFrom = table.Code;
                            columnRenames = rename.Columns;
                        }
                    }

                    if (otherTable is null)
                    {
                        items.Add(Removal(tablePath, Live(table.Columns).Sum(c => Count(valuesByColumn, c.Id))));
                        continue;
                    }
                }
                else if (rename is not null && string.Equals(rename.ToTableCode, table.Code, StringComparison.Ordinal))
                {
                    columnRenames = rename.Columns;
                }

                tables[table.Id] = otherTable.Id;
                var columnsBefore = columns.Count;
                PresentationDiff(items, tablePath, "Name", table.NameL10n, otherTable.NameL10n);
                CompareTableShape(items, tablePath, table, otherTable, valuesByColumn);
                CompareColumns(items, columns, conflicts, tablePath, table, otherTable, valuesByColumn, columnRenames);
                CompareRows(items, rows, newRows, tablePath, table, otherTable, valuesByRow);
                CompareFormulas(formulaChanges, tablePath, table, otherTable, columns, rows);
                CompareCellFormulas(items, overwrites, sheet.Code.ToString(), table.Code.ToString(), tablePath, table, otherTable, valuesByCell);

                if (renamedFrom is not null)
                {
                    // Скільки значень переїздить під новим кодом: лише колонки, що мають відповідника.
                    var moved = columns.Skip(columnsBefore).Sum(c => Count(valuesByColumn, c.SourceColumnDefId));
                    items.Add(new(tablePath, "Renamed", ChangeClass.Safe, moved, "Code", renamedFrom, otherTable.Code));
                }
            }

            foreach (var added in Live(otherSheet.Tables)
                         .Where(t => Live(sheet.Tables).All(s => s.Code != t.Code) && !renamedTargets.Contains(t.Id)))
            {
                items.Add(new($"{sheet.Code}.{added.Code}", "Added", ChangeClass.Safe, 0));
                AddedTableFormulas(formulaChanges, $"{sheet.Code}.{added.Code}", added);
            }

            // LP-32: усе, що додано до списку під час розбору цього аркуша, належить йому.
            if (newRows.Count > newRowsBefore || items.Skip(itemsBefore).Any(TouchesLockedSheet))
            {
                touched.Add(sheet.Id);
            }
        }

        foreach (var added in targetSheets.Values.Where(t => sourceSheets.All(s => s.Code != t.Code)))
        {
            items.Add(new(added.Code, "Added", ChangeClass.Safe, 0));
            foreach (var table in Live(added.Tables))
            {
                AddedTableFormulas(formulaChanges, $"{added.Code}.{table.Code}", table);
            }
        }

        var lookupOutcome = new LookupOutcome();
        var headerFields = CompareHeader(items, source, target, headerValues, lookup, lookupOutcome);

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
            sheets, tables, columns, rows, headerFields, newRows, items, transferred, lost, guarded,
            conflicts.Count == 0 ? null : conflicts, touched)
        {
            FormulaChanges = [.. formulaChanges.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            LookupRemaps = lookupOutcome.Rows,
            LookupRemappedValues = lookupOutcome.Remapped,
            LookupGuardedValues = lookupOutcome.Guarded,
            LookupUnmappedValues = lookupOutcome.Unmapped,
            LookupReport = lookupOutcome.Report,
            GuardedOverwrites =
            [
                .. overwrites
                    .OrderBy(o => o.SheetCode, StringComparer.Ordinal)
                    .ThenBy(o => o.TableCode, StringComparer.Ordinal)
                    .ThenBy(o => o.RowKey, StringComparer.Ordinal)
                    .ThenBy(o => o.ColumnCode, StringComparer.Ordinal),
            ],
        };
    }

    /// <summary>
    /// LP-41: чи додає/змінює цільова таблиця формули відносно вихідної. Порівняння — за ЦІЛЬОВИМИ ідентифікаторами
    /// колонки/рядка (вихідні проходять крізь мапу плану), тож перейменована колонка з незмінним виразом не рахується
    /// зміною. Видалена формула зміною не вважається: перерахунок не прибере вже записане значення.
    /// </summary>
    private static void CompareFormulas(
        List<string> changed, string tablePath, TableDef from, TableDef to,
        IReadOnlyList<VersionMigrationColumn> columnMap, Dictionary<int, int> rowMap)
    {
        var columns = columnMap.ToDictionary(c => c.SourceColumnDefId, c => c.TargetColumnDefId);

        var before = new Dictionary<(FormulaScope, int?, int?), (string Expression, ExpressionDialect Dialect)>();
        foreach (var formula in from.Formulas.Where(f => !f.IsDeleted))
        {
            int? column = null;
            int? row = null;
            if (formula.ColumnDefId is { } sourceColumn)
            {
                if (!columns.TryGetValue(sourceColumn, out var mapped))
                {
                    continue;
                }

                column = mapped;
            }

            if (formula.RowDefId is { } sourceRow)
            {
                if (!rowMap.TryGetValue(sourceRow, out var mapped))
                {
                    continue;
                }

                row = mapped;
            }

            before[(formula.Scope, column, row)] = (formula.Expression.Trim(), formula.Dialect);
        }

        foreach (var formula in to.Formulas.Where(f => !f.IsDeleted))
        {
            if (!before.TryGetValue((formula.Scope, formula.ColumnDefId, formula.RowDefId), out var old)
                || !string.Equals(old.Expression, formula.Expression.Trim(), StringComparison.Ordinal)
                || old.Dialect != formula.Dialect)
            {
                changed.Add(tablePath);
                return;
            }
        }

        // Обчислювана колонка без формули в таблиці (методологія/Calculated) теж лишилась би порожньою:
        // нова колонка або колонка, що стала обчислюваною.
        var sourceComputed = Live(from.Columns).ToDictionary(c => c.Id, c => c.IsComputed);
        var targetToSource = columnMap.ToDictionary(c => c.TargetColumnDefId, c => c.SourceColumnDefId);
        foreach (var column in Live(to.Columns).Where(c => c.IsComputed))
        {
            if (!targetToSource.TryGetValue(column.Id, out var source) || !sourceComputed.GetValueOrDefault(source))
            {
                changed.Add(tablePath);
                return;
            }
        }
    }

    /// <summary>Нова таблиця (чи аркуш): будь-яка її формула й обчислювана колонка — нова.</summary>
    private static void AddedTableFormulas(List<string> changed, string tablePath, TableDef table)
    {
        if (table.Formulas.Any(f => !f.IsDeleted) || Live(table.Columns).Any(c => c.IsComputed))
        {
            changed.Add(tablePath);
        }
    }

    /// <summary>
    /// LP-32: чи не можна переносити подане/затверджене на аркуші з такою відмінністю. Клас Guarded і вище — втрата,
    /// видалення, зміна тлумачення; <c>Renamed</c> — код таблиці/колонки змінюється (клас Safe, але адреса інша).
    /// <c>Added</c>/<c>Widened</c>/<c>Presentation</c> (клас Safe і нижче) дані подання не міняють.
    /// </summary>
    private static bool TouchesLockedSheet(VersionMigrationItem item)
        => item.ChangeClass >= ChangeClass.Guarded || string.Equals(item.Kind, "Renamed", StringComparison.Ordinal);

    /// <summary>
    /// Запис мапи для таблиці: з вказаним аркушем має перевагу над записом «на будь-якому аркуші».
    /// </summary>
    private static VersionMigrationTableRename? FindRename(
        IReadOnlyList<VersionMigrationTableRename>? renames, string sheetCode, string tableCode)
    {
        if (renames is null || renames.Count == 0)
        {
            return null;
        }

        return renames.FirstOrDefault(r => string.Equals(r.SheetCode, sheetCode, StringComparison.Ordinal)
                                           && string.Equals(r.FromTableCode, tableCode, StringComparison.Ordinal))
               ?? renames.FirstOrDefault(r => r.SheetCode is null
                                              && string.Equals(r.FromTableCode, tableCode, StringComparison.Ordinal));
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
        List<VersionMigrationItem> items, List<VersionMigrationColumn> map, List<string> conflicts, string tablePath,
        TableDef from, TableDef to, IReadOnlyDictionary<int, long> valuesByColumn,
        IReadOnlyList<VersionMigrationColumnRename>? columnRenames)
    {
        var targetColumns = Live(to.Columns).ToDictionary(c => c.Code, StringComparer.Ordinal);
        var sourceColumnCodes = Live(from.Columns).Select(c => c.Code).ToHashSet(StringComparer.Ordinal);
        var renamedTargets = new HashSet<int>();

        foreach (var column in Live(from.Columns))
        {
            var path = $"{tablePath}.{column.Code}";
            var values = Count(valuesByColumn, column.Id);

            if (!targetColumns.TryGetValue(column.Code, out var other))
            {
                // Той самий принцип, що й для таблиць: мапа діє лише там, де відповідника за кодом немає.
                var rename = columnRenames?.FirstOrDefault(r => string.Equals(r.FromColumnCode, column.Code, StringComparison.Ordinal));
                if (rename is not null && targetColumns.TryGetValue(rename.ToColumnCode, out var renamedColumn))
                {
                    if (sourceColumnCodes.Contains(rename.ToColumnCode))
                    {
                        conflicts.Add($"{path} -> {rename.ToColumnCode}: цільова колонка вже має відповідник за кодом у вихідній таблиці");
                        items.Add(new(path, "RenameConflict", ChangeClass.Breaking, 0, "Code", column.Code, rename.ToColumnCode));
                    }
                    else if (!renamedTargets.Add(renamedColumn.Id))
                    {
                        conflicts.Add($"{path} -> {rename.ToColumnCode}: у цю колонку вже веде інший запис мапи");
                        items.Add(new(path, "RenameConflict", ChangeClass.Breaking, 0, "Code", column.Code, rename.ToColumnCode));
                    }
                    else
                    {
                        other = renamedColumn;
                        items.Add(new(path, "Renamed", ChangeClass.Safe, values, "Code", column.Code, renamedColumn.Code));
                    }
                }

                if (other is null)
                {
                    items.Add(Removal(path, values));
                    continue;
                }
            }

            map.Add(new VersionMigrationColumn(column.Id, other.Id, to.Id));

            Guarded(items, path, values, nameof(ColumnDef.DataType), column.DataType, other.DataType);
            NumericFormat(items, path, values, column, other);
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

        foreach (var added in targetColumns.Values
                     .Where(c => Live(from.Columns).All(s => s.Code != c.Code) && !renamedTargets.Contains(c.Id)))
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

            // WW-02: рядок став «лише читання» — людина більше не редагує його, а введене лишається: зміна
            // тлумачення, тому Guarded зі значеннями рядка (як і IsRequired). Зняття «лише читання» лише
            // розширює права й нічого не ламає (клас Safe, Kind не «Modified», тож у guarded не рахується).
            if (!row.IsReadOnly && other.IsReadOnly)
            {
                items.Add(new(path, "Modified", ChangeClass.Guarded, Count(valuesByRow, row.Id),
                    nameof(RowDef.IsReadOnly), "editable", "readOnly"));
            }
            else if (row.IsReadOnly && !other.IsReadOnly)
            {
                items.Add(new(path, "Relaxed", ChangeClass.Safe, 0,
                    nameof(RowDef.IsReadOnly), "readOnly", "editable"));
            }
        }

        foreach (var added in targetRows.Values.Where(r => !sourceKeys.Contains(r.RowKeyValue)))
        {
            items.Add(new($"{tablePath}#{added.RowKeyValue}", "Added", ChangeClass.Safe, 0));
            newRows.Add(new VersionMigrationNewRow(to.Id, added.Id, added.RowKeyValue, added.Ordinal));
        }
    }

    /// <summary>
    /// WW-02: комірочні формули цільової таблиці, яких у вихідній версії не було. Така комірка стає обчислюваною:
    /// перший перерахунок перезапише введене, а редагування закрите. Тому нова комірочна формула на комірці з
    /// непорожнім значенням — <c>Modified</c>/Guarded зі скількістю значень (відмова <c>guardedWithData</c>, аркуш
    /// «зачеплений» для LP-32); на порожній — безпечне додавання (одним підсумковим рядком на таблицю).
    /// </summary>
    /// <remarks>
    /// Комірка, яку вихідна версія й так рахувала (обчислювана колонка, формула колонки/рядка/комірки), нової
    /// відмінності не дає — ні відмови, ні пункту. Відповідність — за кодом колонки й ключем рядка.
    /// </remarks>
    private static void CompareCellFormulas(
        List<VersionMigrationItem> items, List<GuardedOverwriteItem> overwrites, string sheetCode, string tableCode,
        string tablePath, TableDef from, TableDef to,
        IReadOnlyDictionary<(int ColumnDefId, int RowDefId), long> valuesByCell)
    {
        var targetFormulas = to.Formulas
            .Where(f => !f.IsDeleted && f.Scope == FormulaScope.Cell && f.RowDefId is not null && f.ColumnDefId is not null)
            .ToList();

        if (targetFormulas.Count == 0)
        {
            return;
        }

        var targetRows = Live(to.Rows).ToDictionary(r => r.Id);
        var targetColumns = Live(to.Columns).ToDictionary(c => c.Id);
        var sourceRows = Live(from.Rows).ToDictionary(r => r.RowKeyValue, StringComparer.Ordinal);
        var sourceColumns = Live(from.Columns).ToDictionary(c => c.Code, StringComparer.Ordinal);
        var sourceFormulas = from.Formulas.Where(f => !f.IsDeleted).ToList();

        var added = 0;
        foreach (var formula in targetFormulas)
        {
            if (!targetRows.TryGetValue(formula.RowDefId!.Value, out var targetRow)
                || !targetColumns.TryGetValue(formula.ColumnDefId!.Value, out var targetColumn))
            {
                continue;
            }

            if (!sourceRows.TryGetValue(targetRow.RowKeyValue, out var sourceRow)
                || !sourceColumns.TryGetValue(targetColumn.Code, out var sourceColumn))
            {
                // Комірки у вихідній версії не було — значень там бути не може.
                added++;
                continue;
            }

            var alreadyComputed = sourceColumn.IsComputed
                || sourceFormulas.Any(f =>
                    (f.Scope == FormulaScope.Column && f.ColumnDefId == sourceColumn.Id)
                    || (f.Scope == FormulaScope.Cell && f.RowDefId == sourceRow.Id && f.ColumnDefId == sourceColumn.Id)
                    || (f.Scope == FormulaScope.Row && f.RowDefId == sourceRow.Id
                        && (f.ColumnDefId is null || f.ColumnDefId == sourceColumn.Id)));

            if (alreadyComputed)
            {
                continue;
            }

            added++;
            var values = valuesByCell.GetValueOrDefault((sourceColumn.Id, sourceRow.Id));
            if (values > 0)
            {
                items.Add(new($"{tablePath}#{targetRow.RowKeyValue}.{targetColumn.Code}", "Modified",
                    ChangeClass.Guarded, values, "CellFormula", "input", "formula"));

                // ACC-OW: той самий пункт, але структурно — для переліку «що перезапише формула».
                overwrites.Add(new GuardedOverwriteItem(
                    sheetCode, tableCode, targetRow.RowKeyValue, targetColumn.Code.ToString(), values));
            }
        }

        if (added > 0)
        {
            items.Add(new(tablePath, "Added", ChangeClass.Safe, 0, "CellFormulas", null, added.ToString(
                System.Globalization.CultureInfo.InvariantCulture)));
        }
    }

    private static Dictionary<int, int> CompareHeader(
        List<VersionMigrationItem> items, TemplateVersionSnapshot source, TemplateVersionSnapshot target,
        IReadOnlyDictionary<int, long> headerValues, VersionMigrationLookupInput? lookup, LookupOutcome lookupOutcome)
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
            if (!TryRemapLookup(items, path, field, other, values, lookup, lookupOutcome))
            {
                Guarded(items, path, values, nameof(HeaderFieldDef.LookupRegistryDefId), field.LookupRegistryDefId, other.LookupRegistryDefId);
            }

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

    /// <summary>Накопичувач результатів мап Lookup під час розбору шапки.</summary>
    private sealed class LookupOutcome
    {
        public List<VersionMigrationLookupRemapRow> Rows { get; } = [];

        public List<LookupRemapReportItem> Report { get; } = [];

        public long Remapped { get; set; }

        public long Guarded { get; set; }

        public long Unmapped { get; set; }
    }

    /// <summary>
    /// Мапа Lookup для поля шапки, чий довідник змінився. Повна мапа замінює пункт <c>Guarded</c> безпечним
    /// <c>Remapped</c> (значення переписує SQL); неповна лишає <c>Guarded</c> і рахує значення без відповідника.
    /// </summary>
    /// <returns><c>true</c> — мапа повна, <c>Guarded</c> за довідник додавати не треба.</returns>
    private static bool TryRemapLookup(
        List<VersionMigrationItem> items, string path, HeaderFieldDef field, HeaderFieldDef other, long values,
        VersionMigrationLookupInput? lookup, LookupOutcome outcome)
    {
        if (lookup is null
            || field.LookupRegistryDefId == other.LookupRegistryDefId
            || !lookup.Fields.TryGetValue(field.Id, out var data))
        {
            return false;
        }

        var mapped = new List<VersionMigrationLookupRemapRow>();
        var unmappedIds = new List<long>();
        long mappedValues = 0;
        long unmappedValues = data.ValuesWithoutEntry;
        foreach (var (entryId, count) in data.UsedEntries.OrderBy(e => e.Key))
        {
            if (data.Mappings.TryGetValue(entryId, out var newId) && !data.UnusableTargets.ContainsKey(newId))
            {
                mapped.Add(new VersionMigrationLookupRemapRow(field.Id, field.Code, entryId, newId));
                mappedValues += count;
            }
            else
            {
                unmappedIds.Add(entryId);
                unmappedValues += count;
            }
        }

        var complete = unmappedValues == 0;
        outcome.Report.Add(new LookupRemapReportItem(
            new VersionMigrationLookupTarget(VersionMigrationLookupRemaps.HeaderKind, field.Code),
            Applied: true,
            MappedValues: mappedValues,
            UnmappedValues: unmappedValues,
            MappedEntries: mapped.Count,
            UnmappedEntries: unmappedIds.Count,
            UnmappedEntryIds: [.. unmappedIds.Take(VersionMigrationLookupRemaps.MaxListed)],
            UnmappedTruncated: unmappedIds.Count > VersionMigrationLookupRemaps.MaxListed,
            TargetNotUsable:
            [
                .. data.UnusableTargets
                    .OrderBy(t => t.Key)
                    .Take(VersionMigrationLookupRemaps.MaxListed)
                    .Select(t => new LookupRemapNotUsable(t.Key, t.Value)),
            ]));

        if (!complete)
        {
            outcome.Unmapped += unmappedValues;
            outcome.Guarded += values;
            return false;
        }

        if (mappedValues == 0)
        {
            // Значень немає: переписувати нічого, але зміна довідника без даних нічого не псує.
            return false;
        }

        outcome.Rows.AddRange(mapped);
        outcome.Remapped += mappedValues;
        items.Add(new(path, "Remapped", ChangeClass.Safe, mappedValues,
            nameof(HeaderFieldDef.LookupRegistryDefId), Text(field.LookupRegistryDefId), Text(other.LookupRegistryDefId)));
        return true;
    }

    /// <summary>
    /// Precision/Scale колонки: РОЗШИРЕННЯ (<c>Widened</c>, клас Safe) переносить значення без перетворення;
    /// будь-що інше — <c>Modified</c> (Guarded), як і раніше.
    /// </summary>
    /// <remarks>
    /// Значення завжди лежать у <c>decimal(34,16)</c> (<c>CellValue.ValueNumeric</c>) і від Precision/Scale
    /// колонки не залежать — змінюється лише обмеження валідації. Тому розширення нічого не перетворює й не
    /// округлює: кожне значення, що проходило в старій колонці, проходить і в новій. <c>null</c> — «без межі».
    /// </remarks>
    private static void NumericFormat(
        List<VersionMigrationItem> items, string path, long values, ColumnDef from, ColumnDef to)
    {
        if (from.Precision == to.Precision && from.Scale == to.Scale)
        {
            return;
        }

        var widened = IsWidening(from.Precision, from.Scale, to.Precision, to.Scale);
        void Report(string field, byte? before, byte? after)
        {
            if (before == after)
            {
                return;
            }

            items.Add(new(path, widened ? "Widened" : "Modified", widened ? ChangeClass.Safe : ChangeClass.Guarded, values,
                field, Text(before), Text(after)));
        }

        Report(nameof(ColumnDef.Precision), from.Precision, to.Precision);
        Report(nameof(ColumnDef.Scale), from.Scale, to.Scale);
    }

    /// <summary>
    /// Нова пара не звужує стару: Precision не менша, Scale не менша і цілих розрядів
    /// (<c>Precision − Scale</c>) не менше. <c>null</c> = без межі: «стало null» завжди розширення,
    /// «було null, стало число» — звуження.
    /// </summary>
    private static bool IsWidening(byte? precisionBefore, byte? scaleBefore, byte? precisionAfter, byte? scaleAfter)
    {
        var precisionOk = precisionAfter is null || (precisionBefore is { } pb && precisionAfter >= pb);
        var scaleOk = scaleAfter is null || (scaleBefore is { } sb && scaleAfter >= sb);
        if (!precisionOk || !scaleOk)
        {
            return false;
        }

        // Є межа Scale в новій колонці (отже й у старій): цілих розрядів не меншає.
        // Precision у новій без межі, а Scale з межею — довільно багато цілих розрядів, також ок.
        if (precisionAfter is { } pa && scaleAfter is { } sa)
        {
            return precisionBefore is { } p0 && scaleBefore is { } s0 && pa - sa >= p0 - s0;
        }

        return true;
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
