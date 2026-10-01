// src/Ecr.Infrastructure/Jobs/SourceEventSyncJob.cs
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Errors;
using Ecr.Application.Integration.SourceEvents;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Entities.Integration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Синхронізація подій джерела (PI Event Frames) у рядки динамічних таблиць
/// (HSE301 A5b, FEATURE-HSE301-VIEW §4.7.4).
/// </summary>
/// <remarks>
/// ⛔ Що вирішує ПЛАНУВАЛЬНИК (<see cref="SourceEventSyncPlanner"/>), а що ВИКОНАВЕЦЬ: план — чиста
/// функція (природний ключ, кореневі події, закритий період, <c>Missing</c> лише без <c>Truncated</c>),
/// тут — читання джерела, запис рядків тим самим шляхом, що й ручна правка
/// (<see cref="ICellPatcher.ApplyIntegrationRowsAsync"/> від <c>svc-integration</c>: аудит, валідація,
/// D-118, <c>MaxDynamicRows</c>) і стани <see cref="SourceEventLink"/>.
/// <para>
/// ⚠ Два етапи на мапінг: спершу ВЕСЬ запис рядків, потім усі зміни зв'язків одним збереженням. Патчер
/// зберігає контекст у власній транзакції — зв'язок, доданий ДО нього, поїхав би в ній і відкотився б
/// разом із відмовленим записом.
/// </para>
/// <para>
/// ⚠ Зв'язок кладеться лише для рядка, який ІСНУЄ після запису (перевірка читанням, не віра в
/// <c>Applied</c>): подія без рядка не повинна виглядати синхронізованою. Збій між записом рядків і
/// збереженням зв'язків не лишає дубля: наступний прогін бачить рядок за ключем <c>EF-…</c>
/// і оновлює його.
/// </para>
/// <para>
/// ⚠ Крок 7 §4.7.4 (A4): записане (<c>Applied &gt; 0</c>) ставить автоперерахунок документа за
/// період (<see cref="ICalculationTrigger"/>) — один раз на зачеплений період за прогін мапінгу.
/// <c>recalculation</c> необов'язковий лише для прямого конструювання в тестах; контейнер його
/// завжди передає.
/// </para>
/// </remarks>
public sealed partial class SourceEventSyncJob(
    EcrDbContext db,
    IEnumerable<IExternalDataSource> sources,
    ICellPatcher patcher,
    ICoverageJournal coverage,
    IntegrationActor actor,
    IClock clock,
    ICalculationTrigger? recalculation = null,
    ILogger<SourceEventSyncJob>? logger = null) : ISourceEventSyncJob
{
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    /// <summary>Код задачі в черзі.</summary>
    public static string Code => "source-event-sync";

    /// <summary>Скільки днів перекривати, коли розкладу немає (як у <c>CollectionJob</c>).</summary>
    public const int DefaultLookbackDays = 7;

    /// <summary>Ключ каталогу відмови «рядок не вмістився в <c>MaxDynamicRows</c>».</summary>
    public const string RowLimitKey = "err.ECR-ROW-0409.dynamicRowLimit";

    private const string SourceUnavailable = "ECR-INT-0503";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var request = SourceEventSyncRequest.Parse(payload);

        // ⛔ Автор — svc-integration, як у матеріалізації: патчер без автора відмовляє ECR-AUTH-0401.
        using var author = await actor.EnterAsync(ct).ConfigureAwait(false);

        var entity = await db.SourceEntities
                         .AsNoTracking()
                         .FirstOrDefaultAsync(e => e.Id == request.SourceEntityId && e.IsActive, ct)
                         .ConfigureAwait(false)
                     ?? throw Unavailable(
                         $"Сутність джерела {request.SourceEntityId} не існує або вимкнена: синхронізувати нічого.",
                         "err.ECR-INT-0503.sourceEntityUnavailable",
                         ("sourceEntityId", request.SourceEntityId.ToString(CultureInfo.InvariantCulture)));

        var dataSource = await db.DataSources
                             .AsNoTracking()
                             .FirstOrDefaultAsync(s => s.Id == entity.DataSourceId && s.IsActive, ct)
                             .ConfigureAwait(false)
                         ?? throw Unavailable(
                             $"Джерело {entity.DataSourceId} не існує або вимкнене.",
                             "err.ECR-INT-0503.sourceMissing",
                             ("dataSourceId", entity.DataSourceId.ToString(CultureInfo.InvariantCulture)));

        var adapter = sources.FirstOrDefault(s => s.Transport == dataSource.Transport)
                      ?? throw Unavailable(
                          $"Транспорт {dataSource.Transport} не зареєстровано.",
                          "err.ECR-INT-0503.transportNotRegistered",
                          ("transport", dataSource.Transport.ToString()));

        var maps = await db.SourceEventMaps
            .AsNoTracking()
            .Include(m => m.Fields)
            .ThenInclude(f => f.Values)
            .Where(m => m.SourceEntityId == entity.Id && m.IsActive)
            .OrderBy(m => m.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (maps.Count == 0)
        {
            await progress.ReportKeyAsync(100, "jobs.sourceEventsNoMaps", ct).ConfigureAwait(false);
            return;
        }

        var now = clock.UtcNow;
        var to = request.ToUtc ?? now;

        // ⚠ Початок — з LookbackDays розкладу, як у збору: пізні правки EF у PI доходять самі
        // (ER-I-03), а незакрита подія, що тягнеться довше, перетинає вікно й перечитується.
        var lookback = await db.CollectionSchedules
            .AsNoTracking()
            .Where(s => s.SourceEntityId == entity.Id && s.IsEnabled)
            .Select(s => (int?)s.LookbackDays)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        var from = request.FromUtc ?? to.AddDays(-(lookback ?? DefaultLookbackDays));

        await progress.ReportKeyAsync(10, "jobs.sourceEventsReading", ct).ConfigureAwait(false);

        // ⛔ Одне читання на сутність: мапінги (документи ділянок) ділять відповідь, а Truncated —
        // ознака саме цього читання. Відмова джерела — виняток адаптера, задача стає Failed.
        var read = await adapter
            .ReadEventsAsync(
                new SourceEventQuery(dataSource.Id, entity.Id, entity.Code, from, to, AttributesOf(maps)), ct)
            .ConfigureAwait(false);

        await progress.ReportKeyAsync(40, "jobs.sourceEventsWriting", ct).ConfigureAwait(false);

        var totals = new Totals();
        foreach (var map in maps)
        {
            await SyncMapAsync(map, read, from, to, now, totals, ct).ConfigureAwait(false);
        }

        await progress
            .ReportKeyAsync(
                100,
                "jobs.sourceEventsDone",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["created"] = Text(totals.Created),
                    ["updated"] = Text(totals.Updated),
                    ["keptManual"] = Text(totals.KeptManual),
                    ["missing"] = Text(totals.Missing),
                    ["open"] = Text(totals.Open),
                    ["unmapped"] = Text(totals.Unmapped),
                    ["closed"] = Text(totals.Closed),
                    ["pending"] = Text(totals.Pending),
                },
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>Атрибути для запиту: усі поля мапінгів і атрибути звуження, без зарезервованих.</summary>
    private static List<SourceEventAttributeRef> AttributesOf(IReadOnlyList<SourceEventMap> maps)
        => [.. maps
            .SelectMany(m => m.Fields
                .Where(f => !f.SourceAttribute.StartsWith('$'))
                .Select(f => new SourceEventAttributeRef(f.SourceAttribute, f.AttributeScope))
                .Concat(m.FilterAttribute is { } name && m.FilterScope is { } scope
                    ? [new SourceEventAttributeRef(name, scope)]
                    : []))
            .DistinctBy(a => (a.Name.ToUpperInvariant(), a.Scope))];

    private async Task SyncMapAsync(
        SourceEventMap map,
        SourceEventResult read,
        DateTime from,
        DateTime to,
        DateTime now,
        Totals totals,
        CancellationToken ct)
    {
        var projectId = await db.Documents
            .AsNoTracking()
            .Where(d => d.Id == map.DocumentId)
            .Select(d => (int?)d.ProjectId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (projectId is null)
        {
            // Документа мапінгу немає: писати нікуди, а події лишаються нечитаними цим мапінгом.
            return;
        }

        var timeZoneId = await db.Projects
            .AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => p.TimeZoneId)
            .FirstAsync(ct)
            .ConfigureAwait(false);
        var tz = SiteTimeZone.Create(timeZoneId).ToTimeZoneInfo();

        var periods = (await db.Periods
                .AsNoTracking()
                .Where(p => p.ProjectId == projectId)
                .ToListAsync(ct)
                .ConfigureAwait(false))
            .Select(p => new SourceEventPeriod(
                p.PeriodKeyValue, p.State, Domain.Entities.Documents.Period.UtcBounds(p.PeriodStart, p.PeriodEnd, tz)))
            .ToList();

        var instances = await db.TableInstances
            .AsNoTracking()
            .Where(t => t.DocumentId == map.DocumentId && t.TableDefId == map.TableDefId)
            .ToDictionaryAsync(t => t.PeriodKeyValue, t => t.Id, ct)
            .ConfigureAwait(false);

        // Зв'язки — відстежувані: змінюємо їх на місці у другому етапі.
        var links = await db.SourceEventLinks
            .Where(l => l.SourceEventMapId == map.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var linkByEventId = links.ToDictionary(l => l.SourceEventId, StringComparer.OrdinalIgnoreCase);

        var plan = SourceEventSyncPlanner.Plan(new SourceEventSyncInput(
            from,
            to,
            read.Events,
            read.Truncated,
            read.ErrorCode,
            [.. links.Select(State)],
            start =>
            {
                var period = SourceEventPeriods.Locate(start, periods);
                return period is null
                    ? null
                    : new SourceEventPeriodTarget(
                        period.PeriodKey,
                        period.State,
                        instances.TryGetValue(period.PeriodKey, out var instanceId) ? instanceId : null);
            },
            map.FilterAttribute,
            map.FilterScope,
            map.FilterValue));

        var fields = await FieldPlansAsync(map, ct).ConfigureAwait(false);
        UnitCatalogSnapshot? units = fields.Any(f => f.SourceUnitId is not null && f.TargetUnitId is not null)
            ? await new UnitCatalog(db).GetAsync(ct).ConfigureAwait(false)
            : null;

        // ── Етап 1: запис рядків.
        var appliedPeriods = new HashSet<int>();
        var writes = await WriteRowsAsync(map, plan, fields, tz, units, appliedPeriods, ct).ConfigureAwait(false);

        // ── Етап 2: зв'язки.
        var events = new List<CoverageEvent>();
        foreach (var item in plan.Items)
        {
            var link = item.Link is { } state ? linkByEventId[state.SourceEventId] : null;
            ApplyItem(map, item, link, writes.GetValueOrDefault(item.RowKey), now, totals, events, links, linkByEventId);
        }

        foreach (var lost in plan.Missing)
        {
            linkByEventId[lost.SourceEventId].MarkMissing(now);
            totals.Missing++;
        }

        // ⛔ Борг перерахунку (enterprise-2 P2): рядки вже ЗАКОМІЧЕНО патчером у етапі 1, тож збій
        // збереження зв'язків чи журналу покриття не повинен лишити документ без перерахунку — наступний
        // прогін бачить рядок без змін і нічого не запише, тобто перерахунок уже ніхто б не поставив.
        // ⛔ Збій постановки не маскує початковий виняток: є початковий — постановка лише логується й
        // летить початковий; початкового немає — збій постановки летить сам (задача має впасти видимо).
        Exception? initial = null;
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            if (events.Count > 0)
            {
                await coverage.RecordManyAsync(events, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            initial = ex;
            throw;
        }
        finally
        {
            try
            {
                await RequestRecalculationAsync(map.DocumentId, appliedPeriods, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (initial is not null)
            {
                LogRecalculationNotRequested(_logger, map.DocumentId, map.Id, ex);
            }
        }
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "SourceEventSyncJob: перерахунок документа {DocumentId} (мапінг {MapId}) не поставлено після збою збереження; пробрасується початковий збій.")]
    private static partial void LogRecalculationNotRequested(ILogger logger, long documentId, int mapId, Exception exception);

    /// <summary>
    /// ⛔ Автоперерахунок (§4.7.4 крок 7, A4): ОДИН виклик на зачеплений період за прогін мапінгу,
    /// не на подію й не на групу; нуль записаного — нуль викликів. Черга зливає дублікати без
    /// витіснення, тригер сам не ставить для закритого, Scheduled чи поданого періоду.
    /// </summary>
    private async Task RequestRecalculationAsync(long documentId, HashSet<int> appliedPeriods, CancellationToken ct)
    {
        if (recalculation is null)
        {
            return;
        }

        foreach (var periodKey in appliedPeriods.Order())
        {
            await recalculation.RequestAsync(documentId, new PeriodKey(periodKey), ct).ConfigureAwait(false);
        }
    }

    private void ApplyItem(
        SourceEventMap map,
        SourceEventPlanItem item,
        SourceEventLink? link,
        RowOutcome? outcome,
        DateTime now,
        Totals totals,
        List<CoverageEvent> events,
        List<SourceEventLink> links,
        Dictionary<string, SourceEventLink> linkByEventId)
    {
        var ev = item.Event;
        var observation = new SourceEventObservation(ev.EventId, ev.Name, ev.StartUtc, ev.EndUtc, ev.ModifiedUtc, item.ElementToStore);

        if (link is not null && item.IsRekey)
        {
            linkByEventId.Remove(link.SourceEventId);
            link.RekeyTo(ev.EventId);
            linkByEventId[link.SourceEventId] = link;
        }

        switch (item.Action)
        {
            case SourceEventAction.KeepAsIs:
                totals.Open++;
                return;

            case SourceEventAction.Open:
                Unwritten(map, link, observation, SourceEventLinkStatus.Open, now, links, linkByEventId);
                totals.Open++;
                return;

            case SourceEventAction.PeriodNotOpen:
                Unwritten(map, link, observation, SourceEventLinkStatus.PeriodNotOpen, now, links, linkByEventId);
                totals.Pending++;
                return;

            case SourceEventAction.PeriodClosed:
                // ⛔ Нуль записів: лише позначка в зв'язку.
                Unwritten(map, link, observation, SourceEventLinkStatus.PeriodClosed, now, links, linkByEventId);
                totals.Closed++;
                return;

            case SourceEventAction.PeriodChanged:
                link!.RecordPeriodChanged(observation, now);
                totals.Pending++;
                return;
        }

        // Write.
        outcome ??= new RowOutcome();
        var periodKey = new PeriodKey(item.Target!.PeriodKey);

        if (outcome.WriteConflict || outcome.Awaiting || outcome.Failure is not null)
        {
            events.Add(new CoverageEvent(
                map.SourceEntityId,
                periodKey,
                outcome.Awaiting && !outcome.WriteConflict && outcome.Failure is null
                    ? CollectionCoverage.SkippedNeedsConfirmation
                    : CollectionCoverage.SkippedWriteConflict,
                outcome.Failure is { } failure
                    ? CoverageDetails.EventWriteFailed(ev.EventId, failure)
                    : CoverageDetails.EventWritePartial(ev.EventId, item.RowKey)));
        }

        if (!outcome.RowExists)
        {
            if (outcome.RowLimit)
            {
                Unwritten(map, link, observation, SourceEventLinkStatus.RowLimit, now, links, linkByEventId);
                totals.Pending++;
            }
            else
            {
                // Рядка немає й винен не ліміт (усе відхилено чи запис відмовив): зв'язок не створюється.
                if (outcome.Failure is null && !outcome.WriteConflict && !outcome.Awaiting)
                {
                    events.Add(new CoverageEvent(
                        map.SourceEntityId,
                        periodKey,
                        CollectionCoverage.SkippedWriteConflict,
                        CoverageDetails.EventRowNotCreated(ev.EventId, item.RowKey)));
                }

                totals.Pending++;
            }

            return;
        }

        var row = new SourceEventRowRef(item.Target.PeriodKey, item.Target.TableInstanceId!.Value, item.RowKey);
        var kept = outcome.KeptManual.Count > 0 ? JsonSerializer.Serialize(outcome.KeptManual, JsonOptions) : null;
        var unmapped = outcome.Unmapped.Count > 0 ? JsonSerializer.Serialize(outcome.Unmapped, JsonOptions) : null;

        if (link is null)
        {
            var created = SourceEventLink.FirstSeenWritten(map.Id, observation, row, kept, unmapped, now);
            links.Add(created);
            db.SourceEventLinks.Add(created);
            linkByEventId[created.SourceEventId] = created;
        }
        else
        {
            link.RecordWritten(observation, row, kept, unmapped, now);
        }

        if (outcome.RowExistedBefore)
        {
            totals.Updated++;
        }
        else
        {
            totals.Created++;
        }

        if (kept is not null)
        {
            totals.KeptManual++;
        }

        if (unmapped is not null)
        {
            totals.Unmapped++;
        }
    }

    private void Unwritten(
        SourceEventMap map,
        SourceEventLink? link,
        SourceEventObservation observation,
        SourceEventLinkStatus status,
        DateTime now,
        List<SourceEventLink> links,
        Dictionary<string, SourceEventLink> linkByEventId)
    {
        if (link is not null)
        {
            link.RecordUnwritten(observation, status, now);
            return;
        }

        var created = SourceEventLink.FirstSeenUnwritten(map.Id, observation, status, now);
        links.Add(created);
        db.SourceEventLinks.Add(created);
        linkByEventId[created.SourceEventId] = created;
    }

    /// <summary>Етап 1: пише рядки подій по екземплярах таблиць і збирає наслідок на кожен ключ рядка.</summary>
    private async Task<Dictionary<string, RowOutcome>> WriteRowsAsync(
        SourceEventMap map,
        SourceEventSyncPlan plan,
        IReadOnlyList<SourceEventFieldPlan> fields,
        TimeZoneInfo tz,
        UnitCatalogSnapshot? units,
        HashSet<int> appliedPeriods,
        CancellationToken ct)
    {
        var outcomes = new Dictionary<string, RowOutcome>(StringComparer.OrdinalIgnoreCase);
        var columnIds = fields.ToDictionary(f => f.ColumnCode, f => f.ColumnDefId, StringComparer.Ordinal);

        var groups = plan.Items
            .Where(i => i.Action == SourceEventAction.Write)
            .GroupBy(i => (Instance: i.Target!.TableInstanceId!.Value, i.Target.PeriodKey));

        foreach (var group in groups)
        {
            var rows = new List<IntegrationRowUpsert>();
            var cellsByRow = new Dictionary<string, IReadOnlyList<IntegrationRowCell>>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in group)
            {
                if (outcomes.ContainsKey(item.RowKey))
                {
                    continue;
                }

                var built = SourceEventRowBuilder.Build(item.Event, fields, tz, units);
                var outcome = new RowOutcome();
                foreach (var miss in built.Unmapped)
                {
                    outcome.Unmapped.Add(new UnmappedEntry(miss.Column, miss.Value));
                }

                outcomes[item.RowKey] = outcome;
                cellsByRow[item.RowKey] = built.Cells;

                if (built.Cells.Count > 0)
                {
                    rows.Add(new IntegrationRowUpsert(item.RowKey, built.Cells));
                }
            }

            if (rows.Count == 0)
            {
                continue;
            }

            var keys = rows.Select(r => r.RowKey).ToList();
            var before = await ExistingRowsAsync(group.Key.Instance, group.Key.PeriodKey, keys, ct).ConfigureAwait(false);

            var applied = await WriteGroupAsync(
                    map.DocumentId, group.Key.Instance, group.Key.PeriodKey, rows, outcomes, cellsByRow, columnIds, ct)
                .ConfigureAwait(false);
            if (applied > 0)
            {
                appliedPeriods.Add(group.Key.PeriodKey);
            }

            var after = await ExistingRowsAsync(group.Key.Instance, group.Key.PeriodKey, keys, ct).ConfigureAwait(false);
            foreach (var key in keys)
            {
                outcomes[key].RowExists = after.Contains(key);
                outcomes[key].RowExistedBefore = before.Contains(key);
            }
        }

        // ⚠ Рядки з подій зв'язку, що вже має рядок і НЕ пишеться цього разу, сюди не входять.
        return outcomes;
    }

    /// <summary>
    /// Пише групу одним пакетом; якщо обробник відхилив пакет (стеля рядків, валідація) — по одному рядку,
    /// щоб знати, котрий саме винен, і щоб один поганий рядок не блокував решту.
    /// </summary>
    /// <returns>Скільки комірок записано (для автоперерахунку).</returns>
    private async Task<int> WriteGroupAsync(
        long documentId,
        long tableInstanceId,
        int periodKey,
        List<IntegrationRowUpsert> rows,
        Dictionary<string, RowOutcome> outcomes,
        Dictionary<string, IReadOnlyList<IntegrationRowCell>> cellsByRow,
        Dictionary<string, int> columnIds,
        CancellationToken ct)
    {
        try
        {
            var result = await patcher
                .ApplyIntegrationRowsAsync(documentId, tableInstanceId, new PeriodKey(periodKey), rows, ct)
                .ConfigureAwait(false);
            Distribute(result, outcomes, cellsByRow, columnIds);
            return result.Applied;
        }
        catch (AccessDeniedException ex)
        {
            // Період закрили між планом і записом (чи екземпляр недоступний): усі рядки групи однаково
            // заборонені — по одному не розводимо; наступний прогін побачить закритий період у плані.
            foreach (var row in rows)
            {
                outcomes[row.RowKey].Failure = ex.Message;
            }

            return 0;
        }
        catch (Exception ex) when (ex is BusinessRuleException or DomainException)
        {
            // Пакет відхилено цілком (нічого не записано): розводимо по одному.
        }

        var applied = 0;
        foreach (var row in rows)
        {
            try
            {
                var result = await patcher
                    .ApplyIntegrationRowsAsync(documentId, tableInstanceId, new PeriodKey(periodKey), [row], ct)
                    .ConfigureAwait(false);
                Distribute(result, outcomes, cellsByRow, columnIds);
                applied += result.Applied;
            }
            catch (BusinessRuleException ex) when (ex.Details?.GetValueOrDefault("messageKey") as string == RowLimitKey)
            {
                outcomes[row.RowKey].RowLimit = true;
            }
            catch (Exception ex) when (ex is BusinessRuleException or DomainException)
            {
                outcomes[row.RowKey].Failure = ex.Message;
            }
        }

        return applied;
    }

    /// <summary>Розкладає результат патчера («ключ:колонка») по подіях.</summary>
    private static void Distribute(
        IntegrationWriteResult result,
        Dictionary<string, RowOutcome> outcomes,
        Dictionary<string, IReadOnlyList<IntegrationRowCell>> cellsByRow,
        Dictionary<string, int> columnIds)
    {
        foreach (var kept in result.KeptManual)
        {
            if (Split(kept) is { } parts && outcomes.TryGetValue(parts.RowKey, out var o))
            {
                o.KeptManual.Add(parts.Column);
            }
        }

        foreach (var rejected in result.Rejected ?? [])
        {
            if (Split(rejected) is { } parts && outcomes.TryGetValue(parts.RowKey, out var o))
            {
                // Значення патчер не повертає — беремо те, що послали (для Lookup це Id запису).
                o.Unmapped.Add(new UnmappedEntry(parts.Column, RejectedValue(cellsByRow, parts.RowKey, columnIds.GetValueOrDefault(parts.Column, -1))));
            }
        }

        foreach (var conflict in result.WriteConflicts ?? [])
        {
            if (Split(conflict) is { } parts && outcomes.TryGetValue(parts.RowKey, out var o))
            {
                o.WriteConflict = true;
            }
        }

        foreach (var awaiting in result.AwaitingConfirmation ?? [])
        {
            if (Split(awaiting) is { } parts && outcomes.TryGetValue(parts.RowKey, out var o))
            {
                o.Awaiting = true;
            }
        }
    }

    // Код колонки — ідентифікатор, ключ рядка двокрапки не містить (RowKey.Pattern): ділимо по першій.
    private static (string RowKey, string Column)? Split(string entry)
    {
        var at = entry.IndexOf(':', StringComparison.Ordinal);
        return at <= 0 ? null : (entry[..at], entry[(at + 1)..]);
    }

    private static string? RejectedValue(
        Dictionary<string, IReadOnlyList<IntegrationRowCell>> cellsByRow, string rowKey, int columnDefId)
        => cellsByRow.TryGetValue(rowKey, out var cells)
           && cells.FirstOrDefault(c => c.ColumnDefId == columnDefId)?.Value.Raw is { } raw
            ? Convert.ToString(raw, CultureInfo.InvariantCulture)
            : null;

    private async Task<HashSet<string>> ExistingRowsAsync(long tableInstanceId, int periodKey, List<string> keys, CancellationToken ct)
        => (await db.TableRows
                .AsNoTracking()
                .Where(r => r.PeriodKeyValue == periodKey && r.TableInstanceId == tableInstanceId && !r.IsDeleted && keys.Contains(r.RowKeyValue))
                .Select(r => r.RowKeyValue)
                .ToListAsync(ct)
                .ConfigureAwait(false))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Поля мапінгу з колонками, записами довідників і явними відповідностями.</summary>
    private async Task<List<SourceEventFieldPlan>> FieldPlansAsync(SourceEventMap map, CancellationToken ct)
    {
        var columnIds = map.Fields.Select(f => f.TargetColumnDefId).ToList();
        var columns = await db.ColumnDefs
            .AsNoTracking()
            .Where(c => columnIds.Contains(c.Id) && !c.IsDeleted)
            .ToDictionaryAsync(c => c.Id, ct)
            .ConfigureAwait(false);

        var registryIds = columns.Values
            .Where(c => c.DataType == CellDataType.Lookup && c.LookupRegistryDefId is not null)
            .Select(c => c.LookupRegistryDefId!.Value)
            .Distinct()
            .ToList();

        var entries = (await db.RegistryEntries
                .AsNoTracking()
                .Where(e => registryIds.Contains(e.RegistryDefId) && !e.IsDeleted && e.IsActive)
                .ToListAsync(ct)
                .ConfigureAwait(false))
            .ToLookup(
                e => e.RegistryDefId,
                e => new SourceEventLookupEntry(e.Id, e.Code, [.. e.DisplayL10n.Values.Values]));

        return [.. map.Fields
            .Where(f => columns.ContainsKey(f.TargetColumnDefId))
            .Select(f =>
            {
                var column = columns[f.TargetColumnDefId];
                return new SourceEventFieldPlan(
                    column.Id,
                    column.Code,
                    column.DataType,
                    f.SourceAttribute,
                    f.AttributeScope,
                    f.ValueKind,
                    f.SourceUnitId,
                    f.TargetUnitId,
                    column.LookupRegistryDefId is { } registryId ? [.. entries[registryId]] : [],
                    f.Values.ToDictionary(v => v.SourceValue, v => v.RegistryEntryId, StringComparer.OrdinalIgnoreCase));
            })];
    }

    private static SourceEventLinkState State(SourceEventLink link)
        => new(
            link.SourceEventId,
            link.EventName,
            DateTime.SpecifyKind(link.StartUtc, DateTimeKind.Utc),
            link.Status,
            link.PeriodKey,
            link.TableInstanceId,
            link.RowKey,
            link.PrimaryElement);

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Cut(string text)
        => text.Length > CollectionCoverage.MaxDetailsLength ? text[..CollectionCoverage.MaxDetailsLength] : text;

    private static BusinessRuleException Unavailable(string message, string messageKey, (string Key, string Value) extra)
        => new(
            SourceUnavailable,
            message,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["messageKey"] = messageKey,
                [extra.Key] = extra.Value,
            });

    /// <summary>Значення без відповідника в довіднику — елемент <c>UnmappedJson</c>.</summary>
    private sealed record UnmappedEntry(string Column, string? Value);

    /// <summary>Наслідок запису одного рядка події.</summary>
    private sealed class RowOutcome
    {
        public bool RowExists { get; set; }

        public bool RowExistedBefore { get; set; }

        public bool RowLimit { get; set; }

        public bool WriteConflict { get; set; }

        public bool Awaiting { get; set; }

        public string? Failure { get; set; }

        public List<string> KeptManual { get; } = [];

        public List<UnmappedEntry> Unmapped { get; } = [];
    }

    /// <summary>Лічильники прогону (прогрес <c>jobs.sourceEventsDone</c>).</summary>
    private sealed class Totals
    {
        public int Created { get; set; }

        public int Updated { get; set; }

        public int KeptManual { get; set; }

        public int Missing { get; set; }

        public int Open { get; set; }

        public int Unmapped { get; set; }

        public int Closed { get; set; }

        public int Pending { get; set; }
    }
}
