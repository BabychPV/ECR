using System.Globalization;
using ClosedXML.Excel;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Adapters.Excel;

/// <summary>
/// Будує diff між книгою <c>.xlsx</c> і поточними даними документа.
/// </summary>
/// <remarks>
/// ⚠ Виділений з <see cref="ExcelImporter"/> навмисно: порівняння — це те, що
/// має бути перевірним без книги, без бази і без прав. Обчислення diff і його
/// застосування, змішані в одному методі, дають код, у якому неможливо
/// перевірити, що саме буде відхилено, не застосувавши це.
/// </remarks>
public sealed class ImportDiffBuilder(ICellStore cellStore, IRowStore rowStore)
{
    /// <summary>
    /// Скільки змін має сенс показати в одному перегляді.
    /// </summary>
    /// <remarks>
    /// ⚠ Не оптимізація. Десять тисяч змін — це не імпорт правок, а підміна
    /// документа: переглянути такий diff людина не може, а «підтвердити не
    /// дивлячись» — саме те, від чого перегляд і захищає (ФВ-4.3).
    /// </remarks>
    public const int MaxChanges = 5_000;

    /// <summary>Порівнює блок книги з поточними даними.</summary>
    /// <param name="worksheet">Аркуш книги.</param>
    /// <param name="block">Блок таблиці з карти книги.</param>
    /// <param name="periodKey">Період книги; він же ключ партиції.</param>
    /// <param name="table">Опис таблиці зі знімка.</param>
    /// <param name="decisions">Рішення про доступ на комірки зрізу.</param>
    /// <param name="lookups">Коди записів довідників: <c>RegistryDefId</c> → код → <c>Id</c>.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<TableDiff> BuildAsync(
        IXLWorksheet worksheet,
        ExcelTableBlock block,
        int periodKey,
        TableDef table,
        IReadOnlyDictionary<CellAddress, EditDecision> decisions,
        IReadOnlyDictionary<int, IReadOnlyDictionary<string, long>> lookups,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(worksheet);
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(decisions);

        var period = new PeriodKey(periodKey);

        var rowIds = await rowStore
            .GetRowIdsAsync(block.TableInstanceId, period, ct)
            .ConfigureAwait(false);

        var versions = await rowStore
            .GetRowVersionsAsync(block.TableInstanceId, period, ct)
            .ConfigureAwait(false);

        var current = await cellStore
            .ReadSliceAsync(block.TableInstanceId, ct)
            .ConfigureAwait(false);

        var byRowId = rowIds.ToDictionary(p => p.Value, p => p.Key);

        // ⚠ Комірки рядків поза переліком ключів відкидаються, а не зводяться
        // до спільного ключа з порожнім RowKey: два такі рядки дали б
        // однаковий ключ і `ToDictionary` упав би на дублікаті — посеред
        // перегляду імпорту, на даних, які виглядають звичайними.
        var values = current
            .Where(c => byRowId.ContainsKey(c.Address.TableRowId))
            .ToDictionary(c => (byRowId[c.Address.TableRowId], c.Address.ColumnDefId));

        var columnsById = table.Columns.ToDictionary(c => c.Id);

        var changes = new List<ImportChange>();
        var rejected = new List<ImportRejection>();

        foreach (var row in block.Rows)
        {
            foreach (var column in block.Columns)
            {
                if (changes.Count >= MaxChanges)
                {
                    break;
                }

                if (!columnsById.TryGetValue(column.ColumnDefId, out var definition))
                {
                    continue;
                }

                var cell = worksheet.Cell(row.Number, column.Number);
                var incoming = Read(cell, definition, column, lookups);
                var existing = values.GetValueOrDefault((row.RowKey, column.ColumnDefId))?.Value;

                if (Same(incoming, existing, definition))
                {
                    continue;
                }

                // ⛔ Обчислена комірка відхиляється ЗАВЖДИ і першою — навіть
                // якщо права дозволяють. Записане поверх формули значення
                // зникне при найближчому перерахунку, і користувач вирішить,
                // що система «загубила» його правку (ECR-CELL-4221).
                if (column.IsCalculated)
                {
                    rejected.Add(new ImportRejection(
                        row.RowKey, column.Code, "ECR-CELL-4221",
                        "Комірка обчислюється системою: значення з файлу не застосовується."));

                    continue;
                }

                if (!rowIds.TryGetValue(row.RowKey, out var rowId))
                {
                    rejected.Add(new ImportRejection(
                        row.RowKey, column.Code, "ECR-ROW-0404",
                        "Рядка з таким ключем у документі немає: імпорт рядків не створює."));

                    continue;
                }

                var address = new CellAddress(period, rowId, column.ColumnDefId);

                // ⚠ Заборонені комірки НЕ застосовуються і показуються
                // переліком (ФВ-4.4). Мовчазне пропускання виглядало б як
                // успішний імпорт, після якого частина чисел не змінилася.
                if (decisions.TryGetValue(address, out var decision) && !decision.IsAllowed)
                {
                    rejected.Add(new ImportRejection(
                        row.RowKey, column.Code, "ECR-ACCS-0403",
                        decision.Detail ?? $"Змінювати комірку не дозволено: {decision.Reason}."));

                    continue;
                }

                changes.Add(new ImportChange(row.RowKey, column.Code, Display(existing, definition), incoming));
            }
        }

        return new TableDiff(block.TableInstanceId, periodKey, changes, rejected, versions);
    }

    /// <summary>Читає значення з книги у формі, придатній для <c>PatchCell</c>.</summary>
    /// <remarks>
    /// ⚠ Тип диктує <c>ColumnDef</c>, а не те, чим Excel вважає вміст комірки.
    /// Excel радо віддає число як текст і навпаки, і довіра до нього
    /// перетворює числову колонку на текстову мовчки.
    /// </remarks>
    private static object? Read(
        IXLCell cell,
        ColumnDef definition,
        ExcelColumnRef column,
        IReadOnlyDictionary<int, IReadOnlyDictionary<string, long>> lookups)
    {
        if (cell.IsEmpty())
        {
            return null;
        }

        var text = cell.GetString().Trim();

        switch (definition.DataType)
        {
            case CellDataType.Int:
                return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)
                    ? integer
                    : text;

            case CellDataType.Decimal:
                return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
                    ? number
                    : text;

            case CellDataType.Bool:
                return bool.TryParse(text, out var flag) ? flag : text;

            case CellDataType.Date:
                return cell.TryGetValue(out DateTime date)
                    ? date
                    : DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                        ? parsed
                        : text;

            case CellDataType.Lookup:
                // Код повертається в ідентифікатор тут: далі по шляху запису
                // код нічого не означає, а нерозпізнаний код має лишитися
                // видимим, а не перетворитися на нуль.
                return column.LookupRegistryDefId is { } registryId
                       && lookups.TryGetValue(registryId, out var entries)
                       && entries.TryGetValue(text, out var entryId)
                    ? entryId
                    : text;

            default:
                return text;
        }
    }

    /// <summary>Чи збігається значення з файлу з тим, що вже записано.</summary>
    private static bool Same(object? incoming, CellValueData? existing, ColumnDef definition)
    {
        if (existing is null || existing.IsEmpty)
        {
            return incoming is null;
        }

        return definition.DataType switch
        {
            CellDataType.Int or CellDataType.Decimal =>
                incoming is decimal d ? existing.ValueNumeric == d
                : incoming is int i && existing.ValueNumeric == i,
            CellDataType.Bool => incoming is bool b && existing.ValueBool == b,
            CellDataType.Date => incoming is DateTime t && existing.ValueDate == t,
            CellDataType.Lookup => incoming is long id && existing.ValueRegistryEntryId == id,
            _ => string.Equals(existing.ValueString, incoming as string, StringComparison.Ordinal),
        };
    }

    /// <summary>Поточне значення у вигляді, придатному для показу в переліку змін.</summary>
    private static object? Display(CellValueData? value, ColumnDef definition)
    {
        if (value is null || value.IsEmpty)
        {
            return null;
        }

        return definition.DataType switch
        {
            CellDataType.Int or CellDataType.Decimal or CellDataType.Formula or CellDataType.Calculated
                => value.ValueNumeric,
            CellDataType.Bool => value.ValueBool,
            CellDataType.Date => value.ValueDate,
            CellDataType.Lookup => value.ValueRegistryEntryId,
            CellDataType.Unit => value.ValueUnitId,
            _ => value.ValueString,
        };
    }
}

/// <summary>Diff однієї таблиці.</summary>
/// <param name="TableInstanceId">Екземпляр таблиці.</param>
/// <param name="PeriodKey">Період.</param>
/// <param name="Changes">Що зміниться.</param>
/// <param name="Rejected">Що відхилено і чому.</param>
/// <param name="RowVersions">
/// Версії рядків на момент перегляду — ними перевіряється, чи не змінив
/// хтось дані між переглядом і застосуванням.
/// </param>
public sealed record TableDiff(
    long TableInstanceId,
    int PeriodKey,
    IReadOnlyList<ImportChange> Changes,
    IReadOnlyList<ImportRejection> Rejected,
    IReadOnlyDictionary<string, string> RowVersions);
