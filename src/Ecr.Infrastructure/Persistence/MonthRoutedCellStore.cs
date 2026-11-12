using System.Runtime.CompilerServices;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
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
/// екземпляра перегляду за <c>RowKey</c>. Обчислені комірки (<c>IsCalculated = 1</c>) беруться з екземпляра
/// перегляду — ✎ LAND-YEAR L1: крім обчислюваних колонок версій <c>YearWithMonthColumns</c>: там власник колонки
/// той самий, що й для введеної (місячна <c>C_Mm</c> — період <c>m</c>, не-місячна — якір), копія перегляду затінена,
/// а значення (обчислене чи ручне D-461) береться з власника тим самим перенесенням за <c>RowKey</c>.
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
    EcrDbContext db) : ICellStore, IOwnerReadSession
{
    private static readonly ConditionalWeakTable<TemplateVersionSnapshot, Dictionary<int, TableDef>> TablesBySnapshot = new();

    // Q-17.4: кеш введених комірок власників на одну задачу перерахунку (див. IOwnerReadSession). null — сесії немає.
    private Dictionary<(long DocumentId, int OwnerPeriod, int TableDefId), OwnerSlot>? _ownerCache;
    private readonly HashSet<int> _sessionAnchors = [];
    private int _sessionDepth;

    /// <inheritdoc />
    public IDisposable BeginOwnerReadSession()
    {
        _sessionDepth++;
        _ownerCache ??= [];
        return new OwnerSession(this);
    }

    /// <summary>Скільки разів сесія ходила до бази за комірками власників (для тестів/діагностики).</summary>
    public int OwnerQueryCount { get; private set; }

    /// <summary>✎ LAND-YEAR L1: скільки разів читалися обчислені комірки власників (поза річною розкладкою — 0).</summary>
    public int OwnerComputedQueryCount { get; private set; }

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
    /// <param name="anchorPeriods">
    /// Підмножина <paramref name="ownerPeriods"/>, що є ЯКОРЕМ року (власник не-місячних введених колонок).
    /// </param>
    /// <returns>Незавершений запит без відстеження.</returns>
    /// <remarks>
    /// ⛔ Ключ партиції — у предикаті КОЖНОЇ з трьох таблиць (<c>WR-05</c>, <c>PartitionKeyQueryTests</c>). Лише
    /// введені комірки (<c>IsCalculated = 0</c>): обчислені належать екземпляру перегляду. Видалені рядки відсікаються.
    /// <para>
    /// ⚠ Q-17 перф (замір на копії Land, GetTableSlice 5–9× повільніший за прапора): відбір колонок списком
    /// <c>ColumnDefId IN (≈200 параметрів)</c> коштував двічі — оптимізатор розкладав його у 200 пошуків на КОЖЕН
    /// рядок власника (~616 тис. логічних читань), а решта CPU йшла на лінійну перевірку 200 значень на кожну комірку
    /// (+ ~0,5 с компіляції плану на таке число параметрів). Тому власника колонки визначає сам запит через
    /// <c>cfg.ColumnDef</c> (поєднання за PK): місячна колонка читається лише в періоді свого місяця
    /// (<c>MonthNumber = PeriodKey % 100</c>), не-місячна — лише в якорі. Це НАДМНОЖИНА того, що потрібно роутеру
    /// (календар року може зробити власником сам період перегляду); остаточний відбір — точний, у пам'яті,
    /// за <see cref="ForeignColumn.OwnerPeriod"/> (<c>OverlayDocumentAsync</c>).
    /// </para>
    /// </remarks>
    public static IQueryable<MonthOwnerCellRow> OwnerCellsQuery(
        EcrDbContext db,
        long documentId,
        IReadOnlyCollection<int> ownerPeriods,
        IReadOnlyCollection<int> tableDefIds,
        IReadOnlyCollection<int> anchorPeriods)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(ownerPeriods);
        ArgumentNullException.ThrowIfNull(tableDefIds);
        ArgumentNullException.ThrowIfNull(anchorPeriods);

        // ⛔ L6 перф (замір на копії Land, правка комірки січня у звіті S07: 12 читачів «місяці в колонках»).
        // Колишній відбір `anchor OR EXISTS(ColumnDef …)` на кожній комірці ВСІХ таблиць × 12 періодів SQL Server
        // виконував через Table Spool (~111 тис. логічних читань робочої таблиці, 125 тис. разом, 150–3400 мс на запит,
        // паралельний план DOP 12 — під навантаженням машини хвіст найповільнішого потоку). Тепер дві гілки UNION ALL:
        // якір — усі введені комірки періоду-якоря; решта — ЗВИЧАЙНЕ з'єднання за PK `cfg.ColumnDef` (місячна колонка
        // лише в періоді свого місяця). Гілки не перетинаються (друга виключає якорі), тож рядки ті самі, що й з OR
        // (`OwnerCellsQuery…EquivalenceTests`), а читань ~12 тис. і план послідовний.
        var instancesRows =
            from instance in db.TableInstances.AsNoTracking().OriginalOnly()   // RS: Q-17 — лише оригінали
            where instance.DocumentId == documentId
                  && ownerPeriods.Contains(instance.PeriodKeyValue)
                  && tableDefIds.Contains(instance.TableDefId)
            join row in db.TableRows.AsNoTracking().Where(r => ownerPeriods.Contains(r.PeriodKeyValue) && !r.IsDeleted)
                on new { P = instance.PeriodKeyValue, I = instance.Id }
                equals new { P = row.PeriodKeyValue, I = row.TableInstanceId }
            select new { row.PeriodKeyValue, row.Id, row.RowKeyValue };

        var inputCells = db.CellValues.AsNoTracking()
            .Where(c => ownerPeriods.Contains(c.PeriodKeyValue) && !c.IsCalculated);

        var anchorBranch =
            from row in instancesRows
            join cell in inputCells.Where(c => anchorPeriods.Contains(c.PeriodKeyValue))
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

        var monthBranch =
            from row in instancesRows
            join cell in inputCells.Where(c => !anchorPeriods.Contains(c.PeriodKeyValue))
                on new { P = row.PeriodKeyValue, R = row.Id }
                equals new { P = cell.PeriodKeyValue, R = cell.TableRowId }
            join column in db.ColumnDefs.AsNoTracking()
                on cell.ColumnDefId equals column.Id
            where column.IsMonthColumn && column.MonthNumber == cell.PeriodKeyValue % 100
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

        return anchorBranch.Concat(monthBranch);
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
                    // ✎ LAND-YEAR L1: обчислювані колонки бувають чужими лише у версії YearWithMonthColumns
                    // (`IMonthColumnRouter.RoutesComputedVersion`); для решти версій `ForeignComputed` порожній.
                    plan.Add(new Entry(
                        instance,
                        foreign.ToDictionary(f => f.Column.Id),
                        foreign.Where(f => !f.Column.IsComputed).ToDictionary(f => f.Column.Id),
                        foreign.Where(f => f.Column.IsComputed).ToDictionary(f => f.Column.Id)));
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
            // ✎ LAND-YEAR L1: у чужій ОБЧИСЛЮВАНІЙ колонці (лише YearWithMonthColumns) затінено ВСЕ — і обчислену
            // копію перегляду (старий результат «по періодах» лишається в базі, D-67, але не видно).
            result[entry.Instance.TableInstanceId] =
            [
                .. cells.Where(c => !entry.ForeignColumns.TryGetValue(c.Address.ColumnDefId, out var foreign)
                                    || (c.Value.IsCalculated && !foreign.Column.IsComputed)),
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

        // ✎ LAND-YEAR L1: записи без чужих ВВЕДЕНИХ колонок у запит введених комірок не йдуть (у спадковому режимі
        // таких записів немає — набір і запит ті самі, що до L1).
        var inputEntries = entries.Where(e => e.ForeignInputs.Count > 0).ToList();
        var ownerCells = inputEntries.Count == 0
            ? []
            : await LoadOwnerCellsAsync(documentId, inputEntries, ct).ConfigureAwait(false);

        await OverlayComputedAsync(documentId, entries, viewRows, result, ct).ConfigureAwait(false);

        if (ownerCells.Count == 0)
        {
            return;
        }

        var byTable = ownerCells.ToLookup(c => c.TableDefId);
        foreach (var entry in inputEntries)
        {
            if (!viewRows.TryGetValue(entry.Instance.TableInstanceId, out var rowIds))
            {
                continue;
            }

            var target = result[entry.Instance.TableInstanceId];

            // Q-17 (дзеркало вʼюхи MonthsInColumns): значення = COALESCE(власна обчислена, що НЕСЕ значення у слоті типу
            // колонки; власник). Обчислена зі значенням приховує власника; порожня обчислена (IsEmpty / NULL у слоті,
            // у т.ч. помилка в чужому слоті) — ні: її замінює комірка власника. Завжди ОДНА комірка на адресу.
            var calculatedAt = new Dictionary<(long Row, int Column), CellRecord>();
            foreach (var c in target.Where(c => c.Value.IsCalculated))
            {
                calculatedAt[(c.Address.TableRowId, c.Address.ColumnDefId)] = c;
            }

            foreach (var cell in byTable[entry.Instance.TableDefId])
            {
                if (!entry.ForeignInputs.TryGetValue(cell.ColumnDefId, out var foreign)
                    || foreign.OwnerPeriod.Value != cell.OwnerPeriodKey
                    || !rowIds.TryGetValue(cell.RowKey, out var viewRowId))
                {
                    continue;
                }

                if (calculatedAt.TryGetValue((viewRowId, cell.ColumnDefId), out var own))
                {
                    if (CarriesValue(own.Value, foreign.Column.DataType))
                    {
                        continue;
                    }

                    target.Remove(own);
                    calculatedAt.Remove((viewRowId, cell.ColumnDefId));
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

    /// <summary>
    /// ✎ LAND-YEAR L1: комірки чужих ОБЧИСЛЮВАНИХ колонок (лише версії YearWithMonthColumns) — з екземпляра-власника,
    /// за <c>RowKey</c>, з тим самим <c>IsCalculated</c>, що у власника (ручне значення D-461 лишається ручним).
    /// </summary>
    /// <remarks>
    /// Немає жодної такої колонки (усі версії, крім річної) — НУЛЬ запитів. Є — ОДИН запит на документ, без кешу сесії:
    /// прохід перерахунку пише ці комірки сам (період <c>m</c> — <c>C_Mm</c>), і наступний період-читач мусить бачити
    /// щойно записане. Видимість власної копії перегляду (затінення) — у <see cref="RouteAsync"/>.
    /// </remarks>
    private async Task OverlayComputedAsync(
        long documentId,
        List<Entry> entries,
        Dictionary<long, Dictionary<string, long>> viewRows,
        Dictionary<long, List<CellRecord>> result,
        CancellationToken ct)
    {
        var computedEntries = entries.Where(e => e.ForeignComputed.Count > 0).ToList();
        if (computedEntries.Count == 0)
        {
            return;
        }

        OwnerComputedQueryCount++;
        var rows = await OwnerComputedCellsQuery(
                db,
                documentId,
                computedEntries.SelectMany(e => e.ForeignComputed.Values.Select(f => f.OwnerPeriod.Value)).Distinct().ToList(),
                computedEntries.Select(e => e.Instance.TableDefId).Distinct().ToList(),
                computedEntries.SelectMany(e => e.ForeignComputed.Values.Where(f => !f.Column.IsMonthColumn)
                    .Select(f => f.OwnerPeriod.Value)).Distinct().ToList())
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var byTable = rows.ToLookup(c => c.TableDefId);
        foreach (var entry in computedEntries)
        {
            if (!viewRows.TryGetValue(entry.Instance.TableInstanceId, out var rowIds))
            {
                continue;
            }

            var target = result[entry.Instance.TableInstanceId];
            foreach (var cell in byTable[entry.Instance.TableDefId])
            {
                if (!entry.ForeignComputed.TryGetValue(cell.ColumnDefId, out var foreign)
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
                        IsCalculated = cell.IsCalculated,
                        IsEmpty = cell.IsEmpty,
                    }));
            }
        }
    }

    /// <summary>
    /// ✎ LAND-YEAR L1: комірки ОБЧИСЛЮВАНИХ колонок (<c>Formula</c>/<c>Calculated</c>) у їхніх власниках — ОДИН запит на
    /// документ: місячна колонка — лише в періоді свого місяця, не-місячна — лише в якорі.
    /// </summary>
    /// <param name="db">Контекст.</param>
    /// <param name="documentId">Документ власників.</param>
    /// <param name="ownerPeriods">Періоди-власники (≤ 13 партицій).</param>
    /// <param name="tableDefIds">Таблиці, що читаються.</param>
    /// <param name="anchorPeriods">Підмножина <paramref name="ownerPeriods"/> — якорі року (власники не-місячних).</param>
    /// <returns>Незавершений запит без відстеження.</returns>
    /// <remarks>
    /// ⛔ Ключ партиції — у предикаті кожної з трьох таблиць документа (як <see cref="OwnerCellsQuery"/>). Власника
    /// колонки визначає з'єднання за PK <c>cfg.ColumnDef</c>, а не список колонок (див. перф-примітку там). Надмножина
    /// (календар може зробити власником сам перегляд) — остаточний відбір у пам'яті за <see cref="ForeignColumn.OwnerPeriod"/>.
    /// </remarks>
    public static IQueryable<MonthOwnerCellRow> OwnerComputedCellsQuery(
        EcrDbContext db,
        long documentId,
        IReadOnlyCollection<int> ownerPeriods,
        IReadOnlyCollection<int> tableDefIds,
        IReadOnlyCollection<int> anchorPeriods)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(ownerPeriods);
        ArgumentNullException.ThrowIfNull(tableDefIds);
        ArgumentNullException.ThrowIfNull(anchorPeriods);

        return from instance in db.TableInstances.AsNoTracking().OriginalOnly()
               where instance.DocumentId == documentId
                     && ownerPeriods.Contains(instance.PeriodKeyValue)
                     && tableDefIds.Contains(instance.TableDefId)
               join row in db.TableRows.AsNoTracking().Where(r => ownerPeriods.Contains(r.PeriodKeyValue) && !r.IsDeleted)
                   on new { P = instance.PeriodKeyValue, I = instance.Id }
                   equals new { P = row.PeriodKeyValue, I = row.TableInstanceId }
               join cell in db.CellValues.AsNoTracking().Where(c => ownerPeriods.Contains(c.PeriodKeyValue))
                   on new { P = row.PeriodKeyValue, R = row.Id }
                   equals new { P = cell.PeriodKeyValue, R = cell.TableRowId }
               join column in db.ColumnDefs.AsNoTracking()
                   on cell.ColumnDefId equals column.Id
               where (column.DataType == CellDataType.Formula || column.DataType == CellDataType.Calculated)
                     && ((column.IsMonthColumn && column.MonthNumber == cell.PeriodKeyValue % 100)
                         || (!column.IsMonthColumn && anchorPeriods.Contains(cell.PeriodKeyValue)))
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
                   IsCalculated = cell.IsCalculated,
               };
    }

    /// <summary>Чи несе обчислена комірка значення у слоті типу колонки (так само, як COALESCE у вʼюсі).</summary>
    private static bool CarriesValue(CellValueData v, CellDataType type) => type switch
    {
        CellDataType.String => v.ValueString is not null,
        CellDataType.Bool => v.ValueBool is not null,
        CellDataType.Date => v.ValueDate is not null,
        CellDataType.Lookup => v.ValueRegistryEntryId is not null,
        CellDataType.Unit => v.ValueUnitId is not null,
        _ => v.ValueNumeric is not null,
    };

    /// <summary>
    /// Введені комірки власників для записів плану: без сесії — один запит на документ (як і було); у сесії
    /// перерахунку — лише те, чого ще немає в кеші (документ × період-власник × таблиця).
    /// </summary>
    private async Task<IReadOnlyList<MonthOwnerCellRow>> LoadOwnerCellsAsync(
        long documentId, List<Entry> entries, CancellationToken ct)
    {
        if (_ownerCache is null)
        {
            OwnerQueryCount++;
            return await OwnerCellsQuery(
                    db,
                    documentId,
                    entries.SelectMany(e => e.ForeignInputs.Values.Select(f => f.OwnerPeriod.Value)).Distinct().ToList(),
                    entries.Select(e => e.Instance.TableDefId).Distinct().ToList(),
                    entries.SelectMany(e => e.ForeignInputs.Values.Where(f => !f.Column.IsMonthColumn).Select(f => f.OwnerPeriod.Value))
                        .Distinct().ToList())
                .ToListAsync(ct)
                .ConfigureAwait(false);
        }

        // Що потрібно: (період-власник, таблиця) → колонки з цим власником.
        var needs = new Dictionary<(int OwnerPeriod, int TableDefId), HashSet<int>>();
        foreach (var entry in entries)
        {
            foreach (var foreign in entry.ForeignInputs.Values)
            {
                if (!foreign.Column.IsMonthColumn)
                {
                    _sessionAnchors.Add(foreign.OwnerPeriod.Value);
                }

                var key = (foreign.OwnerPeriod.Value, entry.Instance.TableDefId);
                if (!needs.TryGetValue(key, out var columns))
                {
                    needs[key] = columns = [];
                }

                columns.Add(foreign.Column.Id);
            }
        }

        var missing = needs
            .Where(n => !_ownerCache.TryGetValue((documentId, n.Key.OwnerPeriod, n.Key.TableDefId), out var slot)
                        || !n.Value.IsSubsetOf(slot.Columns))
            .ToList();

        if (missing.Count > 0)
        {
            // Колонки ключа = потрібні ∪ вже завантажені, щоб оновлений слот не втратив старих.
            var wanted = missing.ToDictionary(
                m => m.Key,
                m => _ownerCache.TryGetValue((documentId, m.Key.OwnerPeriod, m.Key.TableDefId), out var old)
                    ? new HashSet<int>(m.Value.Union(old.Columns))
                    : m.Value);

            OwnerQueryCount++;
            var fetched = await OwnerCellsQuery(
                    db,
                    documentId,
                    wanted.Keys.Select(k => k.OwnerPeriod).Distinct().ToList(),
                    wanted.Keys.Select(k => k.TableDefId).Distinct().ToList(),
                    [.. _sessionAnchors])
                .ToListAsync(ct)
                .ConfigureAwait(false);

            var byKey = fetched.ToLookup(c => (c.OwnerPeriodKey, c.TableDefId));
            foreach (var (key, columns) in wanted)
            {
                _ownerCache[(documentId, key.OwnerPeriod, key.TableDefId)] = new OwnerSlot(
                    columns,
                    [.. byKey[(key.OwnerPeriod, key.TableDefId)].Where(c => columns.Contains(c.ColumnDefId))]);
            }
        }

        var result = new List<MonthOwnerCellRow>();
        foreach (var (key, columns) in needs)
        {
            result.AddRange(_ownerCache[(documentId, key.OwnerPeriod, key.TableDefId)].Rows
                .Where(c => columns.Contains(c.ColumnDefId)));
        }

        return result;
    }

    private void EndOwnerReadSession()
    {
        if (--_sessionDepth <= 0)
        {
            _sessionDepth = 0;
            _ownerCache = null;
            _sessionAnchors.Clear();
        }
    }

    private sealed record OwnerSlot(HashSet<int> Columns, List<MonthOwnerCellRow> Rows);

    private sealed class OwnerSession(MonthRoutedCellStore store) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                store.EndOwnerReadSession();
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

    /// <param name="Instance">Екземпляр перегляду.</param>
    /// <param name="ForeignColumns">Усі колонки з чужим власником (затінення).</param>
    /// <param name="ForeignInputs">З них введені — значення з введених комірок власника (кешуються сесією).</param>
    /// <param name="ForeignComputed">
    /// ✎ LAND-YEAR L1: з них обчислювані (лише YearWithMonthColumns) — значення з комірок власника будь-якого виду
    /// (<see cref="OwnerComputedCellsQuery"/>), НЕ кешуються: перерахунок пише їх у тому самому проході.
    /// </param>
    private sealed record Entry(
        MonthRoutingInstance Instance,
        Dictionary<int, ForeignColumn> ForeignColumns,
        Dictionary<int, ForeignColumn> ForeignInputs,
        Dictionary<int, ForeignColumn> ForeignComputed);
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

    /// <summary>
    /// ✎ LAND-YEAR L1: обчислена комірка власника (лише <see cref="MonthRoutedCellStore.OwnerComputedCellsQuery"/>;
    /// запит введених комірок читає тільки <c>IsCalculated = 0</c> і поле не заповнює).
    /// </summary>
    public bool IsCalculated { get; init; }
}
