using System.Runtime.CompilerServices;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Q-17: декоратор <see cref="ICellStore"/> над <see cref="NormalizedCellStore"/> — читання повертає ЛОГІЧНИЙ зріз
/// таблиць «місяці в колонках» (значення введених комірок беруться з екземпляра-власника).
/// </summary>
/// <remarks>
/// <para>
/// Що саме змінюється: <c>ReadSliceAsync</c>/<c>ReadSlicesAsync</c> (усі чотири форми). Для екземпляра перегляду
/// та колонок, чий власник — інший період (<see cref="IMonthColumnRouter.ForeignColumnsAsync"/>), введені комірки
/// фізичного зрізу відкидаються, а замість них ідуть введені комірки власника, перенесені на <c>TableRow.Id</c>
/// екземпляра перегляду за <c>RowKey</c>. Обчислені комірки (<c>IsCalculated = 1</c>) завжди беруться з екземпляра
/// перегляду.
/// </para>
/// <para>
/// ⛔ <c>ReadCellsAsync</c>, <c>ApplyAsync</c>, <c>ApplyBatchAsync</c>, <c>LimitLockWaitAsync</c> — ФІЗИЧНІ: адреса в
/// них вже фізична (запис маршрутизує викликач, наступний комміт Q-17).
/// </para>
/// <para>
/// ⛔ Прапор вимкнено → кожен метод — прямий виклик внутрішнього сховища: жодного додаткового звернення до бази,
/// жодної зміни результату. Прапор увімкнено, але в пакеті немає таблиць із чужими власниками → платиться лише
/// пошук ідентичності екземплярів (ОДИН запит на пакет, далі кеш scope).
/// </para>
/// <para>
/// Без N+1: на пакет — 1 запит ідентичності екземплярів, ≤ 1 запит календаря року на (проєкт, рік), 1 запит рядків
/// на період перегляду і 1 запит комірок власників на документ (відбір <c>ColumnDefId IN (…)</c>, ≤ 13 партицій).
/// </para>
/// <para>
/// ⚠ Не маршрутизується: заархівований рік (його екземпляри відсутні в гарячій схемі, роутер їх не резолвить →
/// читання фізичне, з архіву) і рядок власника без рядка з тим самим <c>RowKey</c> в екземплярі перегляду (дзеркальні
/// рядки — окремий комміт Q-17; до них такі значення у зрізі перегляду не видно).
/// </para>
/// </remarks>
public sealed class MonthRoutedCellStore(
    ICellStore inner,
    IMonthColumnRouter router,
    IMetadataCache metadata,
    EcrDbContext db) : ICellStore
{
    private static readonly ConditionalWeakTable<TemplateVersionSnapshot, Dictionary<int, TableDef>> TablesBySnapshot = new();

    /// <inheritdoc />
    public async Task<IReadOnlyList<CellRecord>> ReadSliceAsync(long tableInstanceId, CancellationToken ct)
    {
        var physical = await inner.ReadSliceAsync(tableInstanceId, ct).ConfigureAwait(false);
        return await RouteOneAsync(tableInstanceId, physical, period: null, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CellRecord>> ReadSliceAsync(
        long tableInstanceId, PeriodKey periodKey, CancellationToken ct)
    {
        var physical = await inner.ReadSliceAsync(tableInstanceId, periodKey, ct).ConfigureAwait(false);
        return await RouteOneAsync(tableInstanceId, physical, periodKey, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<long, IReadOnlyList<CellRecord>>> ReadSlicesAsync(
        IReadOnlyList<long> tableInstanceIds, CancellationToken ct)
    {
        var physical = await inner.ReadSlicesAsync(tableInstanceIds, ct).ConfigureAwait(false);
        return await RouteAsync(physical, tableInstanceIds, period: null, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<long, IReadOnlyList<CellRecord>>> ReadSlicesAsync(
        IReadOnlyList<long> tableInstanceIds, PeriodKey periodKey, CancellationToken ct)
    {
        var physical = await inner.ReadSlicesAsync(tableInstanceIds, periodKey, ct).ConfigureAwait(false);
        return await RouteAsync(physical, tableInstanceIds, periodKey, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<CellAddress, CellValueData>> ReadCellsAsync(
        IReadOnlyCollection<CellAddress> addresses, CancellationToken ct)
        => inner.ReadCellsAsync(addresses, ct);

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<long, string>> ApplyAsync(CellChangeSet changes, CancellationToken ct)
        => inner.ApplyAsync(changes, ct);

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<long, IReadOnlyDictionary<long, string>>> ApplyBatchAsync(
        IReadOnlyCollection<CellChangeSet> changes, CancellationToken ct)
        => inner.ApplyBatchAsync(changes, ct);

    /// <inheritdoc />
    public Task LimitLockWaitAsync(CancellationToken ct) => inner.LimitLockWaitAsync(ct);

    /// <summary>
    /// Введені комірки власників для колонок із чужим власником — ОДИН запит на документ.
    /// </summary>
    /// <param name="db">Контекст.</param>
    /// <param name="documentId">Документ власників.</param>
    /// <param name="ownerPeriods">Періоди-власники (≤ 13 партицій).</param>
    /// <param name="tableDefIds">Таблиці, що читаються.</param>
    /// <param name="columnDefIds">Колонки з чужим власником.</param>
    /// <returns>Незавершений запит без відстеження.</returns>
    /// <remarks>
    /// ⛔ Ключ партиції — у предикаті КОЖНОЇ з трьох таблиць (<c>WR-05</c>, <c>PartitionKeyQueryTests</c>). Лише
    /// введені комірки (<c>IsCalculated = 0</c>): обчислені належать екземпляру перегляду. Видалені рядки відсікаються.
    /// </remarks>
    public static IQueryable<MonthOwnerCellRow> OwnerCellsQuery(
        EcrDbContext db,
        long documentId,
        IReadOnlyCollection<int> ownerPeriods,
        IReadOnlyCollection<int> tableDefIds,
        IReadOnlyCollection<int> columnDefIds)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(ownerPeriods);
        ArgumentNullException.ThrowIfNull(tableDefIds);
        ArgumentNullException.ThrowIfNull(columnDefIds);

        return from instance in db.TableInstances.AsNoTracking()
               where instance.DocumentId == documentId
                     && ownerPeriods.Contains(instance.PeriodKeyValue)
                     && tableDefIds.Contains(instance.TableDefId)
               join row in db.TableRows.AsNoTracking().Where(r => ownerPeriods.Contains(r.PeriodKeyValue) && !r.IsDeleted)
                   on new { P = instance.PeriodKeyValue, I = instance.Id }
                   equals new { P = row.PeriodKeyValue, I = row.TableInstanceId }
               join cell in db.CellValues.AsNoTracking()
                       .Where(c => ownerPeriods.Contains(c.PeriodKeyValue) && !c.IsCalculated && columnDefIds.Contains(c.ColumnDefId))
                   on new { P = row.PeriodKeyValue, R = row.Id }
                   equals new { P = cell.PeriodKeyValue, R = cell.TableRowId }
               select new MonthOwnerCellRow
               {
                   OwnerPeriodKey = cell.PeriodKeyValue,
                   RowKey = row.RowKeyValue,
                   ColumnDefId = cell.ColumnDefId,
                   TableDefId = cell.TableDefId,
                   ValueString = cell.ValueString,
                   ValueNumeric = cell.ValueNumeric,
                   ValueDate = cell.ValueDate,
                   ValueBool = cell.ValueBool,
                   ValueRegistryEntryId = cell.ValueRegistryEntryId,
                   ValueUnitId = cell.ValueUnitId,
                   IsEmpty = cell.IsEmpty,
               };
    }

    private async Task<IReadOnlyList<CellRecord>> RouteOneAsync(
        long tableInstanceId, IReadOnlyList<CellRecord> physical, PeriodKey? period, CancellationToken ct)
    {
        if (!router.Enabled)
        {
            return physical;
        }

        var routed = await RouteAsync(
                new Dictionary<long, IReadOnlyList<CellRecord>> { [tableInstanceId] = physical },
                [tableInstanceId], period, ct)
            .ConfigureAwait(false);

        return routed.TryGetValue(tableInstanceId, out var cells) ? cells : physical;
    }

    private async Task<IReadOnlyDictionary<long, IReadOnlyList<CellRecord>>> RouteAsync(
        IReadOnlyDictionary<long, IReadOnlyList<CellRecord>> physical,
        IReadOnlyCollection<long> requestedIds,
        PeriodKey? period,
        CancellationToken ct)
    {
        if (!router.Enabled || requestedIds.Count == 0)
        {
            return physical;
        }

        var instances = await router.ResolveInstancesAsync(requestedIds, period, ct).ConfigureAwait(false);
        if (instances.Count == 0)
        {
            return physical;
        }

        // ── План: які екземпляри мають колонки з чужим власником ─────────
        var plan = new List<Entry>();
        foreach (var byVersion in instances.Values.GroupBy(i => i.Document.TemplateVersionId))
        {
            var snapshot = await metadata.GetAsync(byVersion.Key, ct).ConfigureAwait(false);
            var tables = TablesOf(snapshot);

            foreach (var instance in byVersion)
            {
                if (!tables.TryGetValue(instance.TableDefId, out var table) || !router.AppliesToTable(table))
                {
                    continue;
                }

                var foreign = await router
                    .ForeignColumnsAsync(instance.Document, table, instance.PeriodKey, ct)
                    .ConfigureAwait(false);
                if (foreign.Count > 0)
                {
                    plan.Add(new Entry(instance, [.. foreign.Select(f => f.Column.Id)], foreign.ToDictionary(f => f.Column.Id)));
                }
            }
        }

        if (plan.Count == 0)
        {
            return physical;
        }

        var result = new Dictionary<long, List<CellRecord>>();
        foreach (var entry in plan)
        {
            var cells = physical.TryGetValue(entry.Instance.TableInstanceId, out var own) ? own : [];

            // Введені комірки колонок з чужим власником у екземплярі перегляду «затінені» власником.
            result[entry.Instance.TableInstanceId] =
            [
                .. cells.Where(c => c.Value.IsCalculated || !entry.ForeignColumns.ContainsKey(c.Address.ColumnDefId)),
            ];
        }

        foreach (var byDocument in plan.GroupBy(e => e.Instance.Document.DocumentId))
        {
            await OverlayDocumentAsync([.. byDocument], result, ct).ConfigureAwait(false);
        }

        var merged = new Dictionary<long, IReadOnlyList<CellRecord>>(physical);
        foreach (var (id, cells) in result)
        {
            if (cells.Count > 0 || merged.ContainsKey(id))
            {
                merged[id] = cells;
            }
        }

        return merged;
    }

    private async Task OverlayDocumentAsync(
        List<Entry> entries, Dictionary<long, List<CellRecord>> result, CancellationToken ct)
    {
        var documentId = entries[0].Instance.Document.DocumentId;

        // Рядки екземплярів перегляду: RowKey → TableRow.Id — один запит на період перегляду.
        var viewRows = new Dictionary<long, Dictionary<string, long>>();
        foreach (var byPeriod in entries.GroupBy(e => e.Instance.PeriodKey))
        {
            IReadOnlyList<long> ids = [.. byPeriod.Select(e => e.Instance.TableInstanceId)];
            var rows = await RowStore.RowsBatchQuery(db, ids, byPeriod.Key)
                .Select(r => new { r.TableInstanceId, r.Id, r.RowKeyValue })
                .ToListAsync(ct)
                .ConfigureAwait(false);

            foreach (var row in rows)
            {
                if (!viewRows.TryGetValue(row.TableInstanceId, out var map))
                {
                    viewRows[row.TableInstanceId] = map = new Dictionary<string, long>(StringComparer.Ordinal);
                }

                map.TryAdd(row.RowKeyValue, row.Id);
            }
        }

        var ownerPeriods = entries
            .SelectMany(e => e.ForeignColumns.Values.Select(f => f.OwnerPeriod.Value))
            .Distinct()
            .ToList();
        var tableDefIds = entries.Select(e => e.Instance.TableDefId).Distinct().ToList();
        var columnDefIds = entries.SelectMany(e => e.ColumnIds).Distinct().ToList();

        var ownerCells = await OwnerCellsQuery(db, documentId, ownerPeriods, tableDefIds, columnDefIds)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (ownerCells.Count == 0)
        {
            return;
        }

        var byTable = ownerCells.ToLookup(c => c.TableDefId);
        foreach (var entry in entries)
        {
            if (!viewRows.TryGetValue(entry.Instance.TableInstanceId, out var rowIds))
            {
                continue;
            }

            var target = result[entry.Instance.TableInstanceId];
            foreach (var cell in byTable[entry.Instance.TableDefId])
            {
                if (!entry.ForeignColumns.TryGetValue(cell.ColumnDefId, out var foreign)
                    || foreign.OwnerPeriod.Value != cell.OwnerPeriodKey
                    || !rowIds.TryGetValue(cell.RowKey, out var viewRowId))
                {
                    continue;
                }

                target.Add(new CellRecord(
                    new CellAddress(entry.Instance.PeriodKey, viewRowId, cell.ColumnDefId),
                    cell.TableDefId,
                    new CellValueData
                    {
                        ValueString = cell.ValueString,
                        ValueNumeric = cell.ValueNumeric,
                        ValueDate = cell.ValueDate,
                        ValueBool = cell.ValueBool,
                        ValueRegistryEntryId = cell.ValueRegistryEntryId,
                        ValueUnitId = cell.ValueUnitId,
                        IsCalculated = false,
                        IsEmpty = cell.IsEmpty,
                    }));
            }
        }
    }

    private static Dictionary<int, TableDef> TablesOf(TemplateVersionSnapshot snapshot)
        => TablesBySnapshot.GetValue(
            snapshot,
            s =>
            {
                var index = new Dictionary<int, TableDef>();
                foreach (var sheet in s.Sheets.Where(sh => !sh.IsDeleted))
                {
                    foreach (var table in sheet.Tables)
                    {
                        index.TryAdd(table.Id, table);
                    }
                }

                return index;
            });

    private sealed record Entry(
        MonthRoutingInstance Instance, IReadOnlyList<int> ColumnIds, Dictionary<int, ForeignColumn> ForeignColumns);
}

/// <summary>Введена комірка екземпляра-власника — рядок результату <see cref="MonthRoutedCellStore.OwnerCellsQuery"/>.</summary>
public sealed class MonthOwnerCellRow
{
    /// <summary>Період власника (ключ партиції).</summary>
    public int OwnerPeriodKey { get; init; }

    /// <summary>Бізнес-ключ рядка — за ним значення переносяться на рядок екземпляра перегляду.</summary>
    public string RowKey { get; init; } = string.Empty;

    /// <summary>Колонка.</summary>
    public int ColumnDefId { get; init; }

    /// <summary>Таблиця.</summary>
    public int TableDefId { get; init; }

    /// <summary>Рядкове значення.</summary>
    public string? ValueString { get; init; }

    /// <summary>Числове значення.</summary>
    public decimal? ValueNumeric { get; init; }

    /// <summary>Значення-дата.</summary>
    public DateTime? ValueDate { get; init; }

    /// <summary>Логічне значення.</summary>
    public bool? ValueBool { get; init; }

    /// <summary>Запис реєстру.</summary>
    public long? ValueRegistryEntryId { get; init; }

    /// <summary>Одиниця вимірювання.</summary>
    public int? ValueUnitId { get; init; }

    /// <summary>Явна порожнеча.</summary>
    public bool IsEmpty { get; init; }
}
