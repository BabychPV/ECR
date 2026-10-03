// src/Ecr.Infrastructure/Jobs/RowWindowFetchJob.cs
using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Підтягує значення PI за вікном рядка динамічної таблиці (HSE301 A1, FEATURE-HSE301-VIEW §4.4).
/// </summary>
/// <remarks>
/// ⛔ Задача — на ЕКЗЕМПЛЯР таблиці й перераховує всі його рядки, вікно яких змінилося чи ще не
/// дочитане (<see cref="RowWindowFetch.NeedsFetch"/>): черга зливає постановки на екземпляр, тож ключів
/// рядків у payload немає, а повторний прогін нічого не робить зайвого.
/// <para>
/// ⛔ Вікно — з комірок у ЧАСІ ПРОЄКТУ, у UTC воно лише для запиту (<see cref="RowWindowFetch.TryResolveWindow"/>).
/// Запис — тим самим <see cref="ICellPatcher"/>, що й ручна правка й решта інтеграцій (D-118): ручна правка цілі
/// не перезаписується, статус <see cref="RowWindowValueStatus.KeptManual"/>. Після запису — один
/// <see cref="ICalculationTrigger"/> на екземпляр.
/// </para>
/// <para>
/// ⚠ Провенанс (<c>ext.RowWindowValue</c>) додається, а не переписується: попередній чинний запис комірки
/// знімається (<c>IsCurrent = 0</c>) окремим збереженням ДО вставки нового — унікальний індекс «чинний запис —
/// один» не пробачає їхнього порядку в одному пакеті. «Немає джерела для селектора» й недійсне вікно прив'язки
/// без жодного джерела лише лічаться: <c>RowWindowValue</c> має ключ на сутність джерела, якої тут немає.
/// </para>
/// </remarks>
public sealed class RowWindowFetchJob(
    EcrDbContext db,
    IEnumerable<IExternalDataSource> sources,
    ICellPatcher patcher,
    IntegrationActor actor,
    IClock clock,
    IBackgroundJobScheduler? jobs = null,
    ICalculationTrigger? recalculation = null) : IRowWindowFetchJob
{
    /// <summary>Код задачі в черзі.</summary>
    public static string Code => "row-window-fetch";

    /// <summary>Скільки рядків за один прогін; решту доганяє продовження, поставлене в кінці.</summary>
    public const int MaxRowsPerRun = 500;

    private const string WriteConflictCode = "ECR-CELL-0409";
    private const string NeedsConfirmationCode = "ECR-CELL-0422";
    private const string TransportMissingCode = "ECR-INT-0503";

    /// <inheritdoc />
    public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var request = RowWindowFetchRequest.Parse(payload);

        using var author = await actor.EnterAsync(ct).ConfigureAwait(false);

        var instance = await db.TableInstances
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == request.TableInstanceId && t.PeriodKeyValue == request.PeriodKey, ct)
            .ConfigureAwait(false);
        if (instance is null)
        {
            await progress.ReportKeyAsync(100, "jobs.rowWindowSkipped", ct).ConfigureAwait(false);
            return;
        }

        var project = await (
                from d in db.Documents.AsNoTracking()
                join p in db.Projects.AsNoTracking() on d.ProjectId equals p.Id
                where d.Id == instance.DocumentId
                select new { p.Id, p.TimeZoneId })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        var state = project is null
            ? (PeriodState?)null
            : await db.Periods
                .AsNoTracking()
                .Where(p => p.ProjectId == project.Id && p.PeriodKeyValue == request.PeriodKey)
                .Select(p => (PeriodState?)p.State)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

        // ⛔ Пишемо лише в Open/Grace: закритий період не змінюється, а Scheduled ще не має що рахувати
        // (так само, як матеріалізація збору й перерахунок).
        if (project is null || state is not (PeriodState.Open or PeriodState.Grace))
        {
            await progress.ReportKeyAsync(100, "jobs.rowWindowSkipped", ct).ConfigureAwait(false);
            return;
        }

        var maps = await db.RowWindowMaps
            .AsNoTracking()
            .Include(m => m.Sources)
            .Where(m => m.TableDefId == instance.TableDefId && m.IsActive)
            .OrderBy(m => m.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (maps.Count == 0)
        {
            await progress.ReportKeyAsync(100, "jobs.rowWindowSkipped", ct).ConfigureAwait(false);
            return;
        }

        var zone = SiteTimeZone.Create(project.TimeZoneId).ToTimeZoneInfo();
        var now = clock.UtcNow;
        var periodKey = new PeriodKey(request.PeriodKey);

        var context = await LoadRowsAsync(instance.Id, request.PeriodKey, maps, ct).ConfigureAwait(false);
        var units = await new UnitCatalog(db).GetAsync(ct).ConfigureAwait(false);

        await progress.ReportKeyAsync(20, "jobs.rowWindowReading", ct).ConfigureAwait(false);

        var totals = new Totals();
        var budget = MaxRowsPerRun;
        var applied = 0;
        var truncated = false;

        foreach (var map in maps)
        {
            var (done, wasTruncated) = await FetchMapAsync(
                    map, instance, periodKey, zone, now, units, context, budget, totals, ct)
                .ConfigureAwait(false);
            applied += done.Applied;
            budget -= done.Fetched;
            truncated |= wasTruncated;
        }

        // ⛔ Стеля рядків не губить решту: продовження в черзі (злиття з чергою — та сама ціль).
        if (truncated && jobs is not null)
        {
            await jobs
                .EnqueueCoalescedAsync<IRowWindowFetchJob>(
                    RowWindowFetchTarget.Of(instance.Id), request, ct, createdByUserId: null)
                .ConfigureAwait(false);
        }

        // ⛔ Крок 5 §4.4: нові числа в комірках — перерахунок методологій; ОДИН виклик на прогін.
        if (applied > 0 && recalculation is not null)
        {
            await recalculation.RequestAsync(instance.DocumentId, periodKey, ct).ConfigureAwait(false);
        }

        await progress
            .ReportKeyAsync(
                100,
                "jobs.rowWindowDone",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["fetched"] = Text(totals.Fetched),
                    ["partial"] = Text(totals.Partial),
                    ["noData"] = Text(totals.NoData),
                    ["keptManual"] = Text(totals.KeptManual),
                    ["failed"] = Text(totals.Failed),
                    ["invalid"] = Text(totals.Invalid),
                    ["notApplicable"] = Text(totals.NotApplicable),
                },
                ct)
            .ConfigureAwait(false);
    }

    private async Task<RowContext> LoadRowsAsync(
        long tableInstanceId, int periodKey, IReadOnlyList<RowWindowMap> maps, CancellationToken ct)
    {
        var columnIds = maps
            .SelectMany(m => new int?[] { m.StartColumnDefId, m.EndColumnDefId, m.SelectorColumnDefId })
            .OfType<int>()
            .Distinct()
            .ToList();

        var cells = await (
                from c in db.CellValues.AsNoTracking()
                join r in db.TableRows.AsNoTracking()
                    on new { c.PeriodKeyValue, Id = c.TableRowId } equals new { r.PeriodKeyValue, r.Id }
                where r.TableInstanceId == tableInstanceId && r.PeriodKeyValue == periodKey && !r.IsDeleted
                      && columnIds.Contains(c.ColumnDefId)
                select new CellRead(r.RowKeyValue, c.ColumnDefId, c.ValueDate, c.ValueString, c.ValueRegistryEntryId))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var entryIds = cells.Where(c => c.EntryId is not null).Select(c => c.EntryId!.Value).Distinct().ToList();
        var entryCodes = entryIds.Count == 0
            ? new Dictionary<long, string>()
            : await db.RegistryEntries
                .AsNoTracking()
                .Where(e => entryIds.Contains(e.Id))
                .ToDictionaryAsync(e => e.Id, e => e.Code, ct)
                .ConfigureAwait(false);

        // ⛔ L3-03: НЕ відстежувані. Патчер ділить із задачею scoped-контекст і на гонці за
        // рядок робить ChangeTracker.Clear(): відчеплений Supersede() не зберігався, і вставка
        // нового чинного запису падала на UX_RowWindowValue_Current. Чинні знімаються запитом.
        var current = await db.RowWindowValues
            .AsNoTracking()
            .Where(v => v.PeriodKey == periodKey && v.TableInstanceId == tableInstanceId && v.IsCurrent)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new RowContext(
            [.. cells.GroupBy(c => c.RowKey, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Key: g.Key, Cells: g.ToDictionary(c => c.ColumnId)))
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new RowCells(g.Key, g.Cells))],
            entryCodes,
            current.ToDictionary(v => (v.RowKey.ToUpperInvariant(), v.ColumnDefId)));
    }

    private async Task<(MapRun Run, bool Truncated)> FetchMapAsync(
        RowWindowMap map,
        TableInstance instance,
        PeriodKey periodKey,
        TimeZoneInfo zone,
        DateTime now,
        UnitCatalogSnapshot units,
        RowContext context,
        int budget,
        Totals totals,
        CancellationToken ct)
    {
        var entities = await db.SourceEntities
            .AsNoTracking()
            .Where(e => map.Sources.Select(s => s.SourceEntityId).Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, ct)
            .ConfigureAwait(false);
        var dataSources = await db.DataSources
            .AsNoTracking()
            .Where(s => entities.Values.Select(e => e.DataSourceId).Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, ct)
            .ConfigureAwait(false);

        var items = new List<Item>();
        var truncated = false;

        foreach (var row in context.Rows)
        {
            row.Cells.TryGetValue(map.StartColumnDefId, out var startCell);
            row.Cells.TryGetValue(map.EndColumnDefId, out var endCell);
            context.Current.TryGetValue((row.Key.ToUpperInvariant(), map.TargetColumnDefId), out var current);

            var valid = RowWindowFetch.TryResolveWindow(startCell?.Date, endCell?.Date, zone, out var span);
            var source = PickSource(map, row, context);

            if (!valid)
            {
                if (source is not null && !IsSameInvalid(current, startCell?.Date, endCell?.Date))
                {
                    items.Add(Item.Invalid(row.Key, source, startCell?.Date, endCell?.Date));
                }

                totals.Invalid++;
                continue;
            }

            if (source is null)
            {
                totals.NotApplicable++;
                continue;
            }

            if (!RowWindowFetch.NeedsFetch(current, span, map.RefetchWithinDays, now))
            {
                continue;
            }

            if (items.Count(i => i.Reads) >= budget)
            {
                truncated = true;
                break;
            }

            items.Add(await ReadAsync(map, row.Key, source, span, units, entities, dataSources, ct).ConfigureAwait(false));
        }

        if (items.Count == 0)
        {
            return (new MapRun(0, 0), truncated);
        }

        var applied = await WriteAsync(map, instance, periodKey, items, ct).ConfigureAwait(false);
        await RecordAsync(map, instance, items, context, now, ct).ConfigureAwait(false);

        foreach (var item in items)
        {
            switch (item.Status)
            {
                case RowWindowValueStatus.Fetched: totals.Fetched++; break;
                case RowWindowValueStatus.Partial: totals.Partial++; break;
                case RowWindowValueStatus.NoData: totals.NoData++; break;
                case RowWindowValueStatus.KeptManual: totals.KeptManual++; break;
                case RowWindowValueStatus.InvalidWindow: break;
                default: totals.Failed++; break;
            }
        }

        return (new MapRun(applied, items.Count(i => i.Reads)), truncated);
    }

    /// <summary>Джерело рядка: за значенням селектора, інакше «для всіх» (§4.4 крок 2).</summary>
    private static RowWindowSource? PickSource(RowWindowMap map, RowCells row, RowContext context)
    {
        string? selector = null;
        if (map.SelectorColumnDefId is { } selectorColumn && row.Cells.TryGetValue(selectorColumn, out var cell))
        {
            selector = cell.EntryId is { } entryId && context.EntryCodes.TryGetValue(entryId, out var code)
                ? code
                : cell.Text;
            selector = selector?.Trim();
        }

        if (!string.IsNullOrEmpty(selector))
        {
            var exact = map.Sources.FirstOrDefault(
                s => s.SelectorValue is not null && string.Equals(s.SelectorValue, selector, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact;
            }
        }

        return map.Sources.FirstOrDefault(s => s.SelectorValue is null);
    }

    private static bool IsSameInvalid(RowWindowValue? current, DateTime? start, DateTime? end)
        => current is { Status: RowWindowValueStatus.InvalidWindow }
           && current.FromUtc == (start ?? end ?? Item.Placeholder)
           && current.ToUtc == (end ?? start ?? Item.Placeholder);

    private async Task<Item> ReadAsync(
        RowWindowMap map,
        string rowKey,
        RowWindowSource source,
        RowWindowSpan span,
        UnitCatalogSnapshot units,
        Dictionary<int, SourceEntity> entities,
        Dictionary<int, DataSource> dataSources,
        CancellationToken ct)
    {
        if (!entities.TryGetValue(source.SourceEntityId, out var entity)
            || !dataSources.TryGetValue(entity.DataSourceId, out var dataSource)
            || sources.FirstOrDefault(s => s.Transport == dataSource.Transport) is not { } adapter)
        {
            return Item.Failed(rowKey, source, span, TransportMissingCode);
        }

        WindowResult result;
        try
        {
            result = await adapter
                .ReadWindowAsync(
                    new WindowRequest(
                        dataSource.Id,
                        entity.Id,
                        source.SourceField,
                        span.FromUtc,
                        span.ToUtc,
                        (SourceSummaryKind)(byte)map.Summary,
                        map.IsStep,
                        map.MaxGap),
                    ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (EcrException ex)
        {
            // Відмова джерела рядка не валить решту рядків: статус SourceError, повтор — за RefetchWithinDays.
            return Item.Failed(rowKey, source, span, ex.ErrorCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // ⛔ L3-02: адаптери на останній спробі віддають сирі HttpRequestException /
            // OdbcException. Без цього один такий рядок обривав усю задачу, і прочитане
            // для інших рядків не записувалося — задача падала на кожному тригері, поки
            // джерело лежить. Для рядка це та сама відмова транспорту.
            return Item.Failed(rowKey, source, span, TransportMissingCode);
        }

        var fold = RowWindowFetch.Fold(
            map.Summary, result, map.MinPercentGood, source.SourceUnitId, map.TargetUnitId, units);

        return new Item(rowKey, source, span.FromUtc, span.ToUtc, fold, result)
        {
            Reads = true,
        };
    }

    /// <summary>Записує значення в цільову колонку й позначає рядки, де людина була першою.</summary>
    private async Task<int> WriteAsync(
        RowWindowMap map, TableInstance instance, PeriodKey periodKey, List<Item> items, CancellationToken ct)
    {
        var cells = items
            .Where(i => i.Fold is { Status: RowWindowValueStatus.Fetched or RowWindowValueStatus.Partial, ValueTarget: not null })
            .Select(i => new IntegrationCellValue(i.RowKey, map.TargetColumnDefId, i.Fold!.ValueTarget!.Value))
            .ToList();
        if (cells.Count == 0)
        {
            return 0;
        }

        IntegrationWriteResult written;
        try
        {
            written = await patcher
                .ApplyIntegrationAsync(instance.DocumentId, instance.Id, periodKey, cells, ct)
                .ConfigureAwait(false);
        }
        catch (AccessDeniedException)
        {
            // Період закрили між перевіркою й записом: нічого не пишемо й не журналюємо — наступний прогін побачить стан.
            foreach (var item in items)
            {
                item.Abandoned = true;
            }

            return 0;
        }
        catch (Exception ex) when (cells.Count > 1 && ex is BusinessRuleException or DomainException)
        {
            // ⛔ L3-02: пакет відхилено цілком (ECR-CELL-0422 validationBlocked, ECR-CALC-0437 …) —
            // одна комірка не валить решту рядків: розводимо по одному, як SourceEventSyncJob.WriteGroupAsync.
            return await WriteOneByOneAsync(instance, periodKey, items, cells, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is BusinessRuleException or DomainException)
        {
            MarkRejected(items, cells[0].RowKey, ex);
            return 0;
        }

        Distribute(items, written);
        return written.Applied;
    }

    private async Task<int> WriteOneByOneAsync(
        TableInstance instance, PeriodKey periodKey, List<Item> items, List<IntegrationCellValue> cells, CancellationToken ct)
    {
        var applied = 0;
        foreach (var cell in cells)
        {
            try
            {
                var written = await patcher
                    .ApplyIntegrationAsync(instance.DocumentId, instance.Id, periodKey, [cell], ct)
                    .ConfigureAwait(false);
                Distribute(items, written);
                applied += written.Applied;
            }
            catch (AccessDeniedException)
            {
                foreach (var item in items)
                {
                    item.Abandoned = true;
                }

                return applied;
            }
            catch (Exception ex) when (ex is BusinessRuleException or DomainException)
            {
                MarkRejected(items, cell.RowKey, ex);
            }
        }

        return applied;
    }

    private static void Distribute(List<Item> items, IntegrationWriteResult written)
    {
        Mark(items, written.KeptManual, RowWindowValueStatus.KeptManual, null);
        Mark(items, written.WriteConflicts ?? [], RowWindowValueStatus.SourceError, WriteConflictCode);
        Mark(items, written.AwaitingConfirmation ?? [], RowWindowValueStatus.SourceError, NeedsConfirmationCode);
    }

    private static void MarkRejected(List<Item> items, string rowKey, Exception ex)
        => Mark(
            items,
            [rowKey],
            RowWindowValueStatus.SourceError,
            ex switch
            {
                EcrException ecr => ecr.ErrorCode,
                DomainException domain => domain.ErrorCode,
                _ => TransportMissingCode,
            });

    private static void Mark(List<Item> items, IReadOnlyList<string> entries, RowWindowValueStatus status, string? code)
    {
        foreach (var entry in entries)
        {
            var at = entry.IndexOf(':', StringComparison.Ordinal);
            var key = at > 0 ? entry[..at] : entry;
            foreach (var item in items.Where(i => string.Equals(i.RowKey, key, StringComparison.OrdinalIgnoreCase)))
            {
                item.Override(status, code);
            }
        }
    }

    /// <summary>Знімає чинні записи запитом, потім додає нові (унікальний індекс «чинний — один»).</summary>
    private async Task RecordAsync(
        RowWindowMap map, TableInstance instance, List<Item> items, RowContext context, DateTime now, CancellationToken ct)
    {
        var recorded = items.Where(i => !i.Abandoned).ToList();
        if (recorded.Count == 0)
        {
            return;
        }

        var superseded = new List<long>();
        foreach (var item in recorded)
        {
            if (context.Current.Remove((item.RowKey.ToUpperInvariant(), map.TargetColumnDefId), out var previous))
            {
                superseded.Add(previous.Id);
            }
        }

        // Запитом, а не через трекер (L3-03): ChangeTracker.Clear() у патчері між читанням і
        // записом не може загубити зняття «чинного».
        if (superseded.Count > 0)
        {
            await db.RowWindowValues
                .Where(v => v.PeriodKey == instance.PeriodKeyValue && superseded.Contains(v.Id) && v.IsCurrent)
                .ExecuteUpdateAsync(set => set.SetProperty(v => v.IsCurrent, false), ct)
                .ConfigureAwait(false);
        }

        foreach (var item in recorded)
        {
            var value = new RowWindowValue(
                instance.PeriodKeyValue,
                instance.Id,
                item.RowKey,
                map.TargetColumnDefId,
                map.Id,
                item.Source.SourceEntityId,
                item.Source.SourceField,
                item.FromUtc,
                item.ToUtc,
                map.Summary,
                map.TargetUnitId,
                now);

            value.Record(
                item.Status,
                item.Read is { ComputedBy: WindowComputedBy.Server } ? RowWindowComputedBy.Server : RowWindowComputedBy.Local,
                item.Fold?.ValueSource,
                item.Fold?.SourceUnitSymbol,
                item.Fold?.ValueTarget,
                item.Fold?.Factor,
                item.Read?.PointCount ?? 0,
                item.Read?.PercentGood,
                item.ErrorCode);

            db.RowWindowValues.Add(value);
            context.Current[(item.RowKey.ToUpperInvariant(), map.TargetColumnDefId)] = value;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

    private sealed record CellRead(string RowKey, int ColumnId, DateTime? Date, string? Text, long? EntryId);

    private sealed record RowCells(string Key, Dictionary<int, CellRead> Cells);

    private sealed record RowContext(
        List<RowCells> Rows,
        Dictionary<long, string> EntryCodes,
        Dictionary<(string RowKey, int ColumnId), RowWindowValue> Current);

    private sealed record MapRun(int Applied, int Fetched);

    /// <summary>Наслідок одного рядка перед записом.</summary>
    private sealed class Item(
        string rowKey, RowWindowSource source, DateTime fromUtc, DateTime toUtc, RowWindowFold? fold, WindowResult? result)
    {
        /// <summary>Мітка «немає меж» для недійсного вікна: колонки провенансу не приймають <c>NULL</c>.</summary>
        public static readonly DateTime Placeholder = new(1900, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

        public string RowKey { get; } = rowKey;

        public RowWindowSource Source { get; } = source;

        public DateTime FromUtc { get; } = fromUtc;

        public DateTime ToUtc { get; } = toUtc;

        public RowWindowFold? Fold { get; } = fold;

        public WindowResult? Read { get; } = result;

        /// <summary>Звернення до джерела було (рахується проти стелі рядків прогону).</summary>
        public bool Reads { get; init; }

        /// <summary>Не журналювати: запис комірок відмовлено (період закрили).</summary>
        public bool Abandoned { get; set; }

        public RowWindowValueStatus Status { get; private set; } = fold?.Status ?? RowWindowValueStatus.InvalidWindow;

        public string? ErrorCode { get; private set; } = fold?.ErrorCode;

        public void Override(RowWindowValueStatus status, string? code)
        {
            Status = status;
            ErrorCode = code;
        }

        public static Item Failed(string rowKey, RowWindowSource source, RowWindowSpan span, string code)
            => new(rowKey, source, span.FromUtc, span.ToUtc,
                new RowWindowFold(RowWindowValueStatus.SourceError, null, null, null, null, code), null);

        public static Item Invalid(string rowKey, RowWindowSource source, DateTime? start, DateTime? end)
            => new(rowKey, source, start ?? end ?? Placeholder, end ?? start ?? Placeholder, null, null);
    }

    /// <summary>Лічильники прогону (прогрес <c>jobs.rowWindowDone</c>).</summary>
    private sealed class Totals
    {
        public int Fetched { get; set; }

        public int Partial { get; set; }

        public int NoData { get; set; }

        public int KeptManual { get; set; }

        public int Failed { get; set; }

        public int Invalid { get; set; }

        public int NotApplicable { get; set; }
    }
}
