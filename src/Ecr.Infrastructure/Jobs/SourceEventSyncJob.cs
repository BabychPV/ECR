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

    // Стан ОДНОГО прогону (екземпляр задачі — scoped, на прогін новий).
    private string _runId = string.Empty;
    private bool _confirmRemoval;
    private int _actorUserId;

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

        // Прогін, його автор і підтвердження масового видалення — для журналу складу видалених (Б1) і ліміту (Б3).
        _runId = Guid.NewGuid().ToString("N");
        _confirmRemoval = request.ConfirmRemoval;
        _actorUserId = await db.Users
            .AsNoTracking()
            .Where(u => u.UserName == IntegrationActor.UserName)
            .Select(u => u.Id)
            .FirstAsync(ct)
            .ConfigureAwait(false);

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
        // ⛔ «Повна звірка за період»: без явного вікна читаємо від початку найранішого НЕ закритого періоду
        // (Open/Grace) мапінгів — ID подій у PI нестабільні, тож відсутність події ловиться лише повним
        // прочитанням періоду, а не «останніх N днів».
        var from = request.FromUtc
                   ?? await ReconcileFromAsync(maps, to.AddDays(-(lookback ?? DefaultLookbackDays)), ct).ConfigureAwait(false);

        await progress.ReportKeyAsync(10, "jobs.sourceEventsReading", ct).ConfigureAwait(false);

        // ⛔ Одне читання на сутність: мапінги (документи ділянок) ділять відповідь, а Truncated —
        // ознака саме цього читання. Відмова джерела — виняток адаптера, задача стає Failed.
        var read = await adapter
            .ReadEventsAsync(
                new SourceEventQuery(dataSource.Id, entity.Id, entity.Code, from, to, AttributesOf(maps)), ct)
            .ConfigureAwait(false);

        await progress.ReportKeyAsync(40, "jobs.sourceEventsWriting", ct).ConfigureAwait(false);

        // ⛔ Братні шаблони (Auto/Auto_Day/Manual/Manual_Day) тієї самої таблиці — одна множина подій: дедуплікація
        // за ID, перекриття з різними значеннями — попередження, збій читання будь-якого — пропуск видалення.
        var sibling = await ReadSiblingsAsync(adapter, dataSource.Id, entity, maps, from, to, read, ct).ConfigureAwait(false);
        var own = new SourceEventResult(
            [.. read.Events.Where(e => !sibling.Outranked.Contains(e.EventId))],
            read.Truncated || sibling.Incomplete,
            read.ErrorCode);

        var totals = new Totals();
        foreach (var map in maps)
        {
            await SyncMapAsync(map, own, sibling, from, to, now, totals, ct).ConfigureAwait(false);
        }

        if (totals.Removed > 0 || totals.RemovalSkipped > 0)
        {
            await progress
                .ReportKeyAsync(
                    90,
                    "jobs.sourceEventsRemoved",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["removed"] = Text(totals.Removed),
                        ["skipped"] = Text(totals.RemovalSkipped),
                    },
                    ct)
                .ConfigureAwait(false);
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

    /// <summary>Початок вікна повної звірки: найраніший початок Open/Grace-періоду проєктів мапінгів, не пізніше lookback.</summary>
    private async Task<DateTime> ReconcileFromAsync(
        IReadOnlyList<SourceEventMap> maps, DateTime lookbackFrom, CancellationToken ct)
    {
        var from = lookbackFrom;
        var documentIds = maps.Select(m => m.DocumentId).Distinct().ToList();
        var projects = await db.Documents
            .AsNoTracking()
            .Where(d => documentIds.Contains(d.Id))
            .Select(d => d.ProjectId)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var projectId in projects)
        {
            var timeZoneId = await db.Projects
                .AsNoTracking()
                .Where(p => p.Id == projectId)
                .Select(p => p.TimeZoneId)
                .FirstAsync(ct)
                .ConfigureAwait(false);
            var tz = SiteTimeZone.Create(timeZoneId).ToTimeZoneInfo();

            var live = (await db.Periods
                    .AsNoTracking()
                    .Where(p => p.ProjectId == projectId)
                    .ToListAsync(ct)
                    .ConfigureAwait(false))
                .Where(p => p.State is PeriodState.Open or PeriodState.Grace)
                .Select(p => Domain.Entities.Documents.Period.UtcBounds(p.PeriodStart, p.PeriodEnd, tz).StartUtc);

            foreach (var start in live)
            {
                if (start < from)
                {
                    from = start;
                }
            }
        }

        return DateTime.SpecifyKind(from, DateTimeKind.Utc);
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

    /// <summary>Що відомо про братні шаблони цього прогону.</summary>
    /// <param name="OtherIds">Усі ID подій, прочитані з братніх шаблонів.</param>
    /// <param name="Outranked">ID власних подій, які веде братній шаблон з вищим пріоритетом (дедуплікація).</param>
    /// <param name="Overlaps">Власні події, чий ID є в братньому шаблоні з іншими значеннями атрибутів.</param>
    /// <param name="Incomplete">Братній шаблон не прочитано повністю (збій, відмова, стеля): видалення пропускається.</param>
    private sealed record SiblingContext(
        HashSet<string> OtherIds,
        HashSet<string> Outranked,
        List<(SourceEvent Event, string Template)> Overlaps,
        bool Incomplete)
    {
        public static SiblingContext Empty { get; } = new(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            [],
            false);
    }

    private async Task<SiblingContext> ReadSiblingsAsync(
        IExternalDataSource adapter,
        int dataSourceId,
        SourceEntity entity,
        IReadOnlyList<SourceEventMap> maps,
        DateTime from,
        DateTime to,
        SourceEventResult read,
        CancellationToken ct)
    {
        var pairs = maps.Select(m => (m.DocumentId, m.TableDefId)).ToHashSet();
        var documentIds = pairs.Select(p => p.DocumentId).Distinct().ToList();

        var candidates = (await db.SourceEventMaps
                .AsNoTracking()
                .Include(m => m.Fields)
                .Where(m => m.IsActive && m.SourceEntityId != entity.Id && documentIds.Contains(m.DocumentId))
                .ToListAsync(ct)
                .ConfigureAwait(false))
            .Where(m => pairs.Contains((m.DocumentId, m.TableDefId)))
            .GroupBy(m => m.SourceEntityId)
            .ToList();
        if (candidates.Count == 0)
        {
            return SiblingContext.Empty;
        }

        var ids = candidates.Select(g => g.Key).ToList();
        var entities = await db.SourceEntities
            .AsNoTracking()
            .Where(e => ids.Contains(e.Id) && e.IsActive && e.DataSourceId == entity.DataSourceId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var ownById = read.Events
            .Where(e => string.IsNullOrWhiteSpace(e.ParentId))
            .GroupBy(e => e.EventId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var context = SiblingContext.Empty with
        {
            OtherIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            Outranked = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            Overlaps = [],
        };
        var incomplete = false;

        foreach (var sibling in entities.OrderBy(e => SourceEventTemplateOrder.Rank(e.Code)).ThenBy(e => e.Code, StringComparer.OrdinalIgnoreCase))
        {
            SourceEventResult result;
            try
            {
                result = await adapter
                    .ReadEventsAsync(
                        new SourceEventQuery(
                            dataSourceId, sibling.Id, sibling.Code, from, to,
                            AttributesOf([.. candidates.First(g => g.Key == sibling.Id)])),
                        ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogSiblingReadFailed(_logger, sibling.Code, entity.Code, ex);
                incomplete = true;
                continue;
            }

            incomplete |= result.Truncated || result.ErrorCode is not null;

            foreach (var ev in result.Events.Where(e => string.IsNullOrWhiteSpace(e.ParentId)))
            {
                context.OtherIds.Add(ev.EventId);
                if (!ownById.TryGetValue(ev.EventId, out var mine))
                {
                    continue;
                }

                if (SourceEventTemplateOrder.Outranks(sibling.Code, entity.Code))
                {
                    context.Outranked.Add(ev.EventId);
                }
                else if (SourceEventTemplateOrder.AttributesDiffer(mine, ev))
                {
                    context.Overlaps.Add((mine, sibling.Code));
                }
            }
        }

        return context with { Incomplete = incomplete };
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "SourceEventSyncJob: братній шаблон {Sibling} (сутність {Entity}) не прочитано — видалення подій цього прогону пропущено.")]
    private static partial void LogSiblingReadFailed(ILogger logger, string sibling, string entity, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "SourceEventSyncJob: подія {EventId} є в шаблонах {Template} і {Other} з різними значеннями атрибутів; береться перший за порядком шаблонів.")]
    private static partial void LogTemplateOverlap(ILogger logger, string eventId, string template, string other);

    private async Task SyncMapAsync(
        SourceEventMap map,
        SourceEventResult read,
        SiblingContext sibling,
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
            map.FilterValue,
            sibling.OtherIds));

        var fields = await FieldPlansAsync(map, ct).ConfigureAwait(false);
        UnitCatalogSnapshot? units = fields.Any(f => f.SourceUnitId is not null && f.TargetUnitId is not null)
            ? await new UnitCatalog(db).GetAsync(ct).ConfigureAwait(false)
            : null;

        // ── Етап 1: запис рядків.
        var appliedPeriods = new HashSet<int>();
        var writes = await WriteRowsAsync(map, plan, fields, tz, units, appliedPeriods, ct).ConfigureAwait(false);

        // ⛔ L3-03: патчер ділить із задачею scoped-контекст і на гонці за рядок робить
        // ChangeTracker.Clear() — зв'язки відчеплено, і зміни етапу 2 (Written, Missing, Rekey…)
        // SaveChangesAsync мовчки не бачив. Відчеплені — перечитуємо відстежуваними.
        if (links.Any(l => db.Entry(l).State == EntityState.Detached))
        {
            links = await db.SourceEventLinks
                .Where(l => l.SourceEventMapId == map.Id)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            linkByEventId = links.ToDictionary(l => l.SourceEventId, StringComparer.OrdinalIgnoreCase);
        }

        // ── Етап 2: зв'язки.
        var events = new List<CoverageEvent>();
        foreach (var item in plan.Items)
        {
            var link = item.Link is { } state ? linkByEventId[state.SourceEventId] : null;
            ApplyItem(map, item, link, writes.GetValueOrDefault(item.RowKey), now, totals, events, links, linkByEventId);
        }

        // ── Повна звірка: подія зникла з джерела. Спершу РІШЕННЯ (гарди, ліміт) — лише читання; видалення
        // й журнал — одним ADO-коміттом нижче (атомарно: комірки + рядок + зв'язок + запис складу).
        var decision = await DecideRemovalsAsync(map, plan, periods, links, linkByEventId, from, to, totals, events, ct)
            .ConfigureAwait(false);

        // ⛔ Борг перерахунку (enterprise-2 P2): рядки вже ЗАКОМІЧЕНО патчером у етапі 1, тож збій
        // збереження зв'язків чи журналу покриття не повинен лишити документ без перерахунку — наступний
        // прогін бачить рядок без змін і нічого не запише, тобто перерахунок уже ніхто б не поставив.
        // ⛔ Збій постановки не маскує початковий виняток: є початковий — постановка лише логується й
        // летить початковий; початкового немає — збій постановки летить сам (задача має впасти видимо).
        Exception? initial = null;
        try
        {
            var removed = await ApplyRemovalsAsync(map, decision, appliedPeriods, links, linkByEventId, totals, events, ct)
                .ConfigureAwait(false);

            // Подія переїхала в братній шаблон (чи дублюється в ньому): зв'язок знімаємо, РЯДОК лишається —
            // його веде братній мапінг.
            foreach (var handed in plan.HandedOff ?? [])
            {
                Drop(linkByEventId[handed.SourceEventId], links, linkByEventId, removed);
                totals.HandedOff++;
            }

            // Одна подія в кількох шаблонах з різними значеннями: беремо перший за порядком, а розбіжність — у журнал.
            foreach (var (overlapEvent, otherTemplate) in sibling.Overlaps)
            {
                if (SourceEventPeriods.Locate(overlapEvent.StartUtc, periods) is { } overlapPeriod)
                {
                    events.Add(new CoverageEvent(
                        map.SourceEntityId,
                        new PeriodKey(overlapPeriod.PeriodKey),
                        CollectionCoverage.SourceDataRefused,
                        CoverageDetails.EventTemplateOverlap(overlapEvent.EventId, overlapEvent.TemplateName, otherTemplate)));
                }

                LogTemplateOverlap(_logger, overlapEvent.EventId, overlapEvent.TemplateName, otherTemplate);
            }

            foreach (var lost in plan.Missing)
            {
                if (removed.Contains(lost.SourceEventId))
                {
                    continue;
                }

                linkByEventId[lost.SourceEventId].MarkMissing(now);
                totals.Missing++;
            }

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

    /// <summary>Стеля масового видалення за прогін мапінгу: абсолютна (Б3 рев'ю «Аудита»).</summary>
    public const int MaxRemovalsPerRun = 200;

    /// <summary>Мінімум, який дозволено видалити за прогін мапінгу без підтвердження, хоч би яким малим був відсоток.</summary>
    public const int MinRemovalsAllowed = 10;

    /// <summary>Частка зв'язків-з-рядками у вікні, яку дозволено видалити за прогін без підтвердження.</summary>
    public const double MaxRemovalFraction = 0.2;

    /// <summary>
    /// Чи перевищує масове видалення ліміт: більше за <c>max(10, 20 % зв'язків-з-рядками у вікні)</c> АБО більше за
    /// 200 абсолютно.
    /// </summary>
    /// <param name="candidates">Скільки рядків було б видалено після гардів.</param>
    /// <param name="linkedWithRows">Зв'язків з рядком, чий початок у вікні прогону.</param>
    /// <param name="limit">Дозволена кількість (для повідомлення).</param>
    public static bool ExceedsRemovalLimit(int candidates, int linkedWithRows, out int limit)
    {
        var percent = Math.Max(MinRemovalsAllowed, linkedWithRows * MaxRemovalFraction);
        limit = (int)Math.Min(MaxRemovalsPerRun, Math.Floor(percent));
        return candidates > percent || candidates > MaxRemovalsPerRun;
    }

    /// <summary>Видалення, яке РІШЕНО виконати: подія зникла, гарди пройдено.</summary>
    private sealed record PendingRemoval(SourceEventLinkState State, SourceEventLink Link, bool SharedRow);

    /// <summary>Рішення «повної звірки» за мапінгом: що видаляти, а що лише розчепити.</summary>
    private sealed record RemovalDecision(
        List<PendingRemoval> Rows, List<SourceEventLink> NoRowLinks, bool Blocked);

    /// <summary>
    /// ⛔ «Повна звірка за період» (рішення людини 2026-10-01: ID EventFrame нестабільні, подію можуть видалити чи
    /// перестворити): подія вікна, якої немає в ПОВНІЙ відповіді, видаляється разом із рядком і зв'язком, а період
    /// ставиться на перерахунок. Гарди — видалення не відбувається, коли: читання обрізане/з відмовою (план дає
    /// порожній <c>Gone</c>); джерело віддало нуль подій; період рядка не Open/Grace (закритий, Scheduled);
    /// аркуш рядка поданий чи затверджений (Б4: видалення йде повз <c>PatchCellsHandler</c>, тож гард тут);
    /// рядок має правку людини (D-118) — тоді зв'язок лишається позначкою Missing і пишеться подія покриття;
    /// кількість кандидатів перевищує ліміт (Б3) — тоді мапінг не видаляє нічого до ручного підтвердження.
    /// D-259: подія, що перестала проходити звуження мапінгу чи стала не кореневою, не повертається планувальником
    /// у <c>roots</c>, тож теж потрапляє в <c>Gone</c> і видаляється тут.
    /// </summary>
    /// <remarks>Лише читання (і рішення в пам'яті): жодного запису в БД — його робить <see cref="ApplyRemovalsAsync"/>.</remarks>
    private async Task<RemovalDecision> DecideRemovalsAsync(
        SourceEventMap map,
        SourceEventSyncPlan plan,
        IReadOnlyList<SourceEventPeriod> periods,
        List<SourceEventLink> links,
        Dictionary<string, SourceEventLink> linkByEventId,
        DateTime from,
        DateTime to,
        Totals totals,
        List<CoverageEvent> events,
        CancellationToken ct)
    {
        var none = new RemovalDecision([], [], false);
        if (plan.Gone is not { Count: > 0 } gone)
        {
            return none;
        }

        if (plan.SourceEmpty)
        {
            // Нуль подій при N > 0 прив'язаних — ознака збою читання, а не «усе видалили».
            var withRows = gone.Where(g => g.HasRow).ToList();
            if (withRows.Count > 0)
            {
                events.Add(new CoverageEvent(
                    map.SourceEntityId,
                    new PeriodKey(withRows[0].PeriodKey!.Value),
                    CollectionCoverage.SourceDataRefused,
                    CoverageDetails.EventRemovalSourceEmpty(withRows.Count)));
                totals.RemovalSkipped += withRows.Count;
            }

            return none;
        }

        var sheetDefId = await db.TableDefs
            .AsNoTracking()
            .Where(t => t.Id == map.TableDefId)
            .Select(t => (int?)t.SheetDefId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        var manualByGroup = new Dictionary<(long Instance, int Period), HashSet<string>>();
        var lockedByPeriod = new Dictionary<int, DocumentStatus?>();
        var rows = new List<PendingRemoval>();
        var noRow = new List<SourceEventLink>();

        foreach (var state in gone)
        {
            var link = linkByEventId[state.SourceEventId];
            if (!state.HasRow)
            {
                noRow.Add(link);
                continue;
            }

            var periodKey = state.PeriodKey!.Value;
            var instance = state.TableInstanceId!.Value;
            var period = periods.FirstOrDefault(p => p.PeriodKey == periodKey);
            if (period is null || period.State is not (PeriodState.Open or PeriodState.Grace))
            {
                events.Add(new CoverageEvent(
                    map.SourceEntityId,
                    new PeriodKey(periodKey),
                    CollectionCoverage.SkippedPeriodClosed,
                    CoverageDetails.PeriodNotOpen(period?.State)));
                totals.RemovalSkipped++;
                continue;
            }

            // ⛔ Б4: поданий/затверджений аркуш не змінюється — а видалення йде повз PatchCellsHandler (прямий
            // ADO), тож гард, який там стоїть для правок, тут треба повторити.
            if (!lockedByPeriod.TryGetValue(periodKey, out var sheetStatus))
            {
                sheetStatus = sheetDefId is { } sheet
                    ? await db.ApprovalStates
                        .AsNoTracking()
                        .Where(a => a.DocumentId == map.DocumentId && a.SheetDefId == sheet && a.PeriodKey == periodKey
                                    && (a.Status == DocumentStatus.Submitted || a.Status == DocumentStatus.Approved))
                        .Select(a => (DocumentStatus?)a.Status)
                        .FirstOrDefaultAsync(ct)
                        .ConfigureAwait(false)
                    : null;
                lockedByPeriod[periodKey] = sheetStatus;
            }

            if (sheetStatus is { } locked)
            {
                events.Add(new CoverageEvent(
                    map.SourceEntityId,
                    new PeriodKey(periodKey),
                    CollectionCoverage.SkippedPeriodClosed,
                    CoverageDetails.EventRemovalSheetSubmitted(state.SourceEventId, state.RowKey!, locked)));
                totals.RemovalSkipped++;
                continue;
            }

            if (!manualByGroup.TryGetValue((instance, periodKey), out var manual))
            {
                manual = await ManualRowKeysAsync(instance, periodKey, ct).ConfigureAwait(false);
                manualByGroup[(instance, periodKey)] = manual;
            }

            if (manual.Contains(state.RowKey!) || link.KeptManualJson is not null)
            {
                events.Add(new CoverageEvent(
                    map.SourceEntityId,
                    new PeriodKey(periodKey),
                    CollectionCoverage.ConflictKeptManual,
                    CoverageDetails.EventRemovalKeptManual(state.SourceEventId, state.RowKey!)));
                totals.RemovalSkipped++;
                continue;
            }

            // Рядок веде й зв'язок братнього мапінгу: знімаємо лише свій зв'язок, рядок видалить останній.
            var sharedRow = await db.SourceEventLinks
                .AsNoTracking()
                .AnyAsync(
                    l => l.SourceEventMapId != map.Id && l.TableInstanceId == instance && l.PeriodKey == periodKey
                         && l.RowKey == state.RowKey,
                    ct)
                .ConfigureAwait(false);
            rows.Add(new PendingRemoval(state, link, sharedRow));
        }

        // ⛔ Б3: масове видалення — ознака збою джерела чи зміни шаблону, а не «усе щойно видалили». Кандидати —
        // рядки, які ПІСЛЯ гардів справді були б видалені; база — зв'язки-з-рядками вікна. Перший синк (зв'язків ще
        // немає) Gone не має, тож ліміт його не зачіпає.
        var linkedWithRows = links.Count(l => l.HasRow && l.StartUtc >= from && l.StartUtc < to);
        var exceeds = ExceedsRemovalLimit(rows.Count, linkedWithRows, out var limit);
        if (exceeds && !_confirmRemoval)
        {
            events.Add(new CoverageEvent(
                map.SourceEntityId,
                new PeriodKey(rows[0].State.PeriodKey!.Value),
                CollectionCoverage.SourceDataRefused,
                CoverageDetails.EventRemovalLimit(rows.Count, limit, linkedWithRows)));
            totals.RemovalSkipped += rows.Count;
            totals.RemovalBlocked++;
            LogRemovalLimit(_logger, map.Id, rows.Count, linkedWithRows, MaxRemovalsPerRun);
            return new RemovalDecision([], [], true);
        }

        if (exceeds)
        {
            LogRemovalConfirmed(_logger, map.Id, rows.Count, linkedWithRows);
        }

        return new RemovalDecision(rows, noRow, false);
    }

    /// <summary>
    /// Виконує рішення: зв'язки без рядка знімаються через EF (зберігаються разом з рештою), а рядки видаляються
    /// ОДНІЄЮ ADO-транзакцією на мапінг — запис складу в <c>aud.StructureChange</c>, комірки, рядок і зв'язок.
    /// </summary>
    /// <remarks>
    /// ⛔ Б1/Б2: журнал пишеться ДО видалення і в тій самій транзакції (журнал, що може розійтися з видаленим, не
    /// доказ); відкат скасовує все разом. Свій <c>SqlConnection</c> — а не транзакція контексту EF: стратегія повторів
    /// <c>EnableRetryOnFailure</c> забороняє ручні транзакції поза замиканням, а замикання повторилося б над уже
    /// зміненим трекером. Видалений зв'язок відчіплюється від трекера, щоб <c>SaveChanges</c> його не видаляв удруге.
    /// </remarks>
    private async Task<HashSet<string>> ApplyRemovalsAsync(
        SourceEventMap map,
        RemovalDecision decision,
        HashSet<int> appliedPeriods,
        List<SourceEventLink> links,
        Dictionary<string, SourceEventLink> linkByEventId,
        Totals totals,
        List<CoverageEvent> events,
        CancellationToken ct)
    {
        var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var link in decision.NoRowLinks)
        {
            Drop(link, links, linkByEventId, removed);
            totals.Removed++;
        }

        var physical = decision.Rows.Where(r => !r.SharedRow).ToList();
        foreach (var shared in decision.Rows.Where(r => r.SharedRow))
        {
            Drop(shared.Link, links, linkByEventId, removed);
            appliedPeriods.Add(shared.State.PeriodKey!.Value);
            totals.Removed++;
        }

        if (physical.Count == 0)
        {
            return removed;
        }

        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = (Microsoft.Data.SqlClient.SqlTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        var sheetDefId = await db.TableDefs
            .AsNoTracking()
            .Where(t => t.Id == map.TableDefId)
            .Select(t => (int?)t.SheetDefId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        var locked = new Dictionary<int, bool>();
        var deleted = new List<PendingRemoval>();

        foreach (var item in physical)
        {
            // ⛔ L3-04: рішення (DecideRemovalsAsync) читало стан аркуша й правки людини ПОЗА цією
            // транзакцією — подання чи правка між рішенням і видаленням інакше губилися б. Як правка в
            // PatchCellsHandler: спільне блокування аркуша, далі гарди повторно — у тій самій транзакції.
            var periodKey = item.State.PeriodKey!.Value;
            if (sheetDefId is { } sheet)
            {
                if (!locked.TryGetValue(periodKey, out var taken))
                {
                    taken = await TryLockSheetAsync(connection, tx, map.DocumentId, sheet, periodKey, ct).ConfigureAwait(false);
                    locked[periodKey] = taken;
                }

                if (!taken)
                {
                    totals.RemovalSkipped++;
                    continue;
                }
            }

            if (await RecheckRemovalAsync(connection, tx, map, sheetDefId, item, ct).ConfigureAwait(false) is { } refused)
            {
                events.Add(refused);
                totals.RemovalSkipped++;
                continue;
            }

            await JournalAndDeleteAsync(connection, tx, map, item, ct).ConfigureAwait(false);
            deleted.Add(item);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);

        // Після коміту: трекер і лічильники (до коміту збій не лишає хибного «видалено»).
        foreach (var item in deleted)
        {
            db.Entry(item.Link).State = EntityState.Detached;
            links.Remove(item.Link);
            linkByEventId.Remove(item.Link.SourceEventId);
            removed.Add(item.Link.SourceEventId);
            appliedPeriods.Add(item.State.PeriodKey!.Value);
            totals.Removed++;
            LogEventRemoved(_logger, item.State.SourceEventId, map.Id, item.State.RowKey!, item.State.PeriodKey.Value);
        }

        return removed;
    }

    private void Drop(
        SourceEventLink link,
        List<SourceEventLink> links,
        Dictionary<string, SourceEventLink> linkByEventId,
        HashSet<string> removed)
    {
        db.SourceEventLinks.Remove(link);
        links.Remove(link);
        linkByEventId.Remove(link.SourceEventId);
        removed.Add(link.SourceEventId);
    }

    /// <summary>Спільне блокування аркуша в транзакції видалення (той самий ресурс, що й у <c>SheetEditGate</c>).</summary>
    /// <returns><c>false</c> — аркуш зайнятий поданням довше за таймаут: видалення лишається наступному прогону.</returns>
    private static async Task<bool> TryLockSheetAsync(
        Microsoft.Data.SqlClient.SqlConnection connection,
        Microsoft.Data.SqlClient.SqlTransaction tx,
        long documentId,
        int sheetDefId,
        int periodKey,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandType = System.Data.CommandType.StoredProcedure;
        command.CommandText = "sp_getapplock";
        command.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@Resource", System.Data.SqlDbType.NVarChar, 255)
        {
            Value = SheetEditGate.ResourceOf(documentId, sheetDefId, periodKey),
        });
        command.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@LockMode", System.Data.SqlDbType.VarChar, 32) { Value = "Shared" });
        command.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@LockOwner", System.Data.SqlDbType.VarChar, 32) { Value = "Transaction" });
        command.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@LockTimeout", System.Data.SqlDbType.Int)
        {
            Value = SheetEditGatePolicy.DefaultLockTimeoutSeconds * 1000,
        });
        var result = new Microsoft.Data.SqlClient.SqlParameter("@Result", System.Data.SqlDbType.Int)
        {
            Direction = System.Data.ParameterDirection.ReturnValue,
        };
        command.Parameters.Add(result);
        command.CommandTimeout = SheetEditGatePolicy.DefaultLockTimeoutSeconds + 15;

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return (int)result.Value! >= 0;
    }

    /// <summary>
    /// Гарди видалення ще раз — під блокуванням аркуша і рядка, у транзакції видалення (L3-04).
    /// </summary>
    /// <returns>Подія покриття, якщо рядок видаляти вже не можна; <c>null</c> — можна.</returns>
    /// <remarks>
    /// Рядок береться <c>UPDLOCK, HOLDLOCK</c>: правка людини, що ще не закомітилась, дочекається видалення
    /// (і відмовить), а закомічена — видна наступному оператору (RCSI бере знімок на початку оператора).
    /// </remarks>
    private static async Task<CoverageEvent?> RecheckRemovalAsync(
        Microsoft.Data.SqlClient.SqlConnection connection,
        Microsoft.Data.SqlClient.SqlTransaction tx,
        SourceEventMap map,
        int? sheetDefId,
        PendingRemoval item,
        CancellationToken ct)
    {
        var state = item.State;
        var periodKey = state.PeriodKey!.Value;

        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT COUNT(*) FROM doc.TableRow WITH (UPDLOCK, HOLDLOCK)
             WHERE PeriodKey = @period AND TableInstanceId = @instance AND RowKey = @rowKey;

            SELECT TOP (1) Status FROM wf.ApprovalState
             WHERE @sheet IS NOT NULL AND DocumentId = @document AND SheetDefId = @sheet AND PeriodKey = @period
               AND Status IN (@submitted, @approved);

            WITH last_change AS (
                SELECT c.Origin,
                       ROW_NUMBER() OVER (PARTITION BY c.ColumnDefId ORDER BY c.ChangedAt DESC, c.Id DESC) AS rn
                  FROM aud.CellChange AS c
                  JOIN doc.TableRow  AS r ON r.PeriodKey = c.PeriodKey AND r.Id = c.TableRowId
                 WHERE c.PeriodKey = @period AND r.TableInstanceId = @instance AND c.RowKey = @rowKey
            )
            SELECT COUNT(*) FROM last_change WHERE rn = 1 AND Origin = N'UserEdit';
            """;
        command.Parameters.AddWithValue("@period", periodKey);
        command.Parameters.AddWithValue("@instance", state.TableInstanceId!.Value);
        command.Parameters.AddWithValue("@rowKey", state.RowKey!);
        command.Parameters.AddWithValue("@document", map.DocumentId);
        command.Parameters.Add("@sheet", System.Data.SqlDbType.Int).Value = (object?)sheetDefId ?? DBNull.Value;
        command.Parameters.Add("@submitted", System.Data.SqlDbType.TinyInt).Value = (byte)DocumentStatus.Submitted;
        command.Parameters.Add("@approved", System.Data.SqlDbType.TinyInt).Value = (byte)DocumentStatus.Approved;

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        await reader.NextResultAsync(ct).ConfigureAwait(false);

        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var status = (DocumentStatus)reader.GetByte(0);
            return new CoverageEvent(
                map.SourceEntityId,
                new PeriodKey(periodKey),
                CollectionCoverage.SkippedPeriodClosed,
                CoverageDetails.EventRemovalSheetSubmitted(state.SourceEventId, state.RowKey!, status));
        }

        await reader.NextResultAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        if (reader.GetInt32(0) > 0)
        {
            return new CoverageEvent(
                map.SourceEntityId,
                new PeriodKey(periodKey),
                CollectionCoverage.ConflictKeptManual,
                CoverageDetails.EventRemovalKeptManual(state.SourceEventId, state.RowKey!));
        }

        return null;
    }

    /// <summary>
    /// Жорстке видалення рядка, комірок і зв'язку (ключ <c>UQ_TableRow_Key</c> не знає IsDeleted: м'яке видалення
    /// блокувало б повернення події) з записом складу видаленого в <c>aud.StructureChange</c> ДО видалення.
    /// </summary>
    private async Task JournalAndDeleteAsync(
        Microsoft.Data.SqlClient.SqlConnection connection,
        Microsoft.Data.SqlClient.SqlTransaction tx,
        SourceEventMap map,
        PendingRemoval item,
        CancellationToken ct)
    {
        var state = item.State;
        var periodKey = state.PeriodKey!.Value;
        var instance = state.TableInstanceId!.Value;
        var rowKey = state.RowKey!;

        long? rowId = null;
        var cells = new List<object>();

        await using (var read = connection.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = """
                SELECT r.Id, c.ColumnDefId, c.ValueString, c.ValueNumeric, c.ValueDate, c.ValueBool,
                       c.ValueRegistryEntryId, c.ValueUnitId
                  FROM doc.TableRow AS r
                  LEFT JOIN doc.CellValue AS c ON c.PeriodKey = r.PeriodKey AND c.TableRowId = r.Id
                 WHERE r.PeriodKey = @period AND r.TableInstanceId = @instance AND r.RowKey = @rowKey
                 ORDER BY c.ColumnDefId;
                """;
            read.Parameters.AddWithValue("@period", periodKey);
            read.Parameters.AddWithValue("@instance", instance);
            read.Parameters.AddWithValue("@rowKey", rowKey);

            await using var reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                rowId = reader.GetInt64(0);
                if (reader.IsDBNull(1))
                {
                    continue;
                }

                cells.Add(new
                {
                    column = reader.GetInt32(1),
                    text = reader.IsDBNull(2) ? null : reader.GetString(2),
                    number = reader.IsDBNull(3) ? (decimal?)null : reader.GetDecimal(3),
                    date = reader.IsDBNull(4) ? (DateTime?)null : reader.GetDateTime(4),
                    flag = reader.IsDBNull(5) ? (bool?)null : reader.GetBoolean(5),
                    registryEntryId = reader.IsDBNull(6) ? (long?)null : reader.GetInt64(6),
                    unitId = reader.IsDBNull(7) ? (int?)null : reader.GetInt32(7),
                });
            }
        }

        var now = clock.UtcNow;
        var oldJson = JsonSerializer.Serialize(
            new
            {
                eventId = state.SourceEventId,
                eventName = state.EventName,
                eventStartUtc = state.StartUtc,
                rowKey,
                rowId,
                periodKey,
                tableInstanceId = instance,
                documentId = map.DocumentId,
                mapId = map.Id,
                sourceEntityId = map.SourceEntityId,
                linkId = item.Link.Id,
                runId = _runId,
                cells,
            },
            JsonOptions);

        await using (var audit = connection.CreateCommand())
        {
            audit.Transaction = tx;
            audit.CommandText = """
                INSERT INTO aud.StructureChange
                    (ChangedAt, TemplateVersionId, EntityType, EntityId, ChangeClass, Operation,
                     OldJson, NewJson, ChangeReason, ChangedByUserId, CorrelationId)
                VALUES (@t, 0, N'ext.SourceEventLink', @entity, @cc, N'SourceEventRowRemoved',
                        @old, NULL, @reason, @user, @run);
                """;
            audit.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@t", System.Data.SqlDbType.DateTime2) { Scale = 3, Value = now });
            audit.Parameters.AddWithValue("@entity", (int)Math.Min(item.Link.Id, int.MaxValue));
            audit.Parameters.Add("@cc", System.Data.SqlDbType.TinyInt).Value = (byte)Ecr.Domain.Enums.ChangeClass.Guarded;
            audit.Parameters.Add("@old", System.Data.SqlDbType.NVarChar, -1).Value = oldJson;
            audit.Parameters.Add("@reason", System.Data.SqlDbType.NVarChar, -1).Value =
                $"Повна звірка подій: подію «{state.SourceEventId}» (мапінг {map.Id.ToString(CultureInfo.InvariantCulture)}) "
                + $"немає в джерелі; рядок «{rowKey}» періоду {periodKey.ToString(CultureInfo.InvariantCulture)} видалено разом зі зв'язком. "
                + $"Прогін {_runId}.";
            audit.Parameters.Add("@user", System.Data.SqlDbType.Int).Value = _actorUserId;
            audit.Parameters.Add("@run", System.Data.SqlDbType.NVarChar, 64).Value = _runId;
            await audit.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using var delete = connection.CreateCommand();
        delete.Transaction = tx;
        delete.CommandText = """
            DELETE c
              FROM doc.CellValue AS c
             WHERE c.PeriodKey = @period
               AND c.TableRowId IN (SELECT r.Id FROM doc.TableRow AS r
                                     WHERE r.PeriodKey = @period AND r.TableInstanceId = @instance AND r.RowKey = @rowKey);
            DELETE FROM doc.TableRow
             WHERE PeriodKey = @period AND TableInstanceId = @instance AND RowKey = @rowKey;
            DELETE FROM ext.SourceEventLink WHERE Id = @link;
            -- L3-13: провенанс вікна рядка (ключ — RowKey) знімається з чинних, історія лишається;
            -- інакше повернена подія з тим самим ID дає рядок, який RowWindowFetchJob вважає вже підтягнутим.
            UPDATE ext.RowWindowValue SET IsCurrent = 0
             WHERE PeriodKey = @period AND TableInstanceId = @instance AND RowKey = @rowKey AND IsCurrent = 1;
            """;
        delete.Parameters.AddWithValue("@period", periodKey);
        delete.Parameters.AddWithValue("@instance", instance);
        delete.Parameters.AddWithValue("@rowKey", rowKey);
        delete.Parameters.AddWithValue("@link", item.Link.Id);
        await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "SourceEventSyncJob: мапінг {MapId} — {Candidates} подій до видалення з {Linked} зв'язків-з-рядками у вікні перевищує ліміт (20 %, мін. 10, макс. {Absolute}): нічого не видалено, потрібне підтвердження вручну.")]
    private static partial void LogRemovalLimit(ILogger logger, int mapId, int candidates, int linked, int absolute);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "SourceEventSyncJob: мапінг {MapId} — {Candidates} подій до видалення з {Linked} зв'язків-з-рядками перевищує ліміт, але видалення ПІДТВЕРДЖЕНО вручну.")]
    private static partial void LogRemovalConfirmed(ILogger logger, int mapId, int candidates, int linked);

    /// <summary>Ключі рядків екземпляра, у яких остання зміна хоч однієї комірки — правка людини (як <c>ManualCellsAsync</c> патчера).</summary>
    private async Task<HashSet<string>> ManualRowKeysAsync(long tableInstanceId, int periodKey, CancellationToken ct)
    {
        var manual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH last_change AS (
                SELECT c.RowKey, c.Origin,
                       ROW_NUMBER() OVER (PARTITION BY c.RowKey, c.ColumnDefId
                                              ORDER BY c.ChangedAt DESC, c.Id DESC) AS rn
                  FROM aud.CellChange AS c
                  JOIN doc.TableRow  AS r ON r.PeriodKey = c.PeriodKey AND r.Id = c.TableRowId
                 WHERE c.PeriodKey = @period AND r.TableInstanceId = @instance
            )
            SELECT DISTINCT RowKey FROM last_change WHERE rn = 1 AND Origin = N'UserEdit';
            """;
        command.Parameters.AddWithValue("@period", periodKey);
        command.Parameters.AddWithValue("@instance", tableInstanceId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            manual.Add(reader.GetString(0));
        }

        return manual;
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "SourceEventSyncJob: подію {EventId} (мапінг {MapId}) немає в джерелі — рядок {RowKey} періоду {PeriodKey} і зв'язок видалено.")]
    private static partial void LogEventRemoved(ILogger logger, string eventId, int mapId, string rowKey, int periodKey);

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

        public int Removed { get; set; }

        public int HandedOff { get; set; }

        public int RemovalSkipped { get; set; }

        /// <summary>Скільки мапінгів заблоковано лімітом масового видалення (Б3).</summary>
        public int RemovalBlocked { get; set; }
    }
}
