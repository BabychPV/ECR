// src/Ecr.Application/Integration/RawData/PiEventDataHandler.cs
using System.Globalization;
using System.Text;
using Ecr.Application.Audit;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Integration.SourceEvents;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Integration.RawData;

/// <summary>Мапінги подій сутності для перемикача «Дані з PI → Події».</summary>
/// <param name="SourceEntityId">Сутність-шаблон.</param>
/// <param name="Code">Код шаблону.</param>
/// <param name="DisplayName">Підпис.</param>
/// <param name="Maps">Мапінги (документ — лише видимий читачеві).</param>
/// <param name="Periods">Періоди проєктів мапінгів, що перетинають вікно, — для журналу покриття за період.</param>
public sealed record PiEventMapsView(
    int SourceEntityId,
    string Code,
    string? DisplayName,
    IReadOnlyList<PiEventMapView> Maps,
    IReadOnlyList<PiEventPeriodView> Periods);

/// <summary>Мапінг подій.</summary>
/// <param name="MapId">Мапінг.</param>
/// <param name="IsActive">Діє.</param>
/// <param name="DocumentVisible">Читач бачить документ (і таблицю) мапінгу.</param>
/// <param name="DocumentId">Документ — лише якщо видимий.</param>
/// <param name="DocumentKey">Бізнес-ключ — лише якщо видимий.</param>
/// <param name="TableCode">Код таблиці.</param>
/// <param name="Fields">Поля.</param>
/// <param name="HasMonthColumnFields">Є поля на колонки місяця (D-465: варто прибрати).</param>
public sealed record PiEventMapView(
    int MapId,
    bool IsActive,
    bool DocumentVisible,
    long? DocumentId,
    string? DocumentKey,
    string TableCode,
    IReadOnlyList<PiEventMapField> Fields,
    bool HasMonthColumnFields);

/// <summary>Період проєкту, що перетинає вікно.</summary>
public sealed record PiEventPeriodView(int PeriodKey, string State, DateTime FromUtc, DateTime ToUtc);

/// <summary>Рядок документа, у який лягла подія (лише для того, хто документ бачить).</summary>
public sealed record PiEventRowView(long DocumentId, string DocumentKey, int PeriodKey, long TableInstanceId, string RowKey);

/// <summary>Подія PI з порівнянням атрибутів.</summary>
/// <param name="LinkId">Зв'язок.</param>
/// <param name="MapId">Мапінг.</param>
/// <param name="EventId">ID події в PI.</param>
/// <param name="EventName">Назва.</param>
/// <param name="StartUtc">Початок, UTC.</param>
/// <param name="StartLocal">Початок у поясі проєкту.</param>
/// <param name="EndUtc">Кінець, UTC; <c>null</c> — триває.</param>
/// <param name="EndLocal">Кінець у поясі проєкту.</param>
/// <param name="TimeZoneId">Пояс проєкту.</param>
/// <param name="PrimaryElement">Первинний елемент (ключовий вигляд).</param>
/// <param name="SourceModifiedUtc"><c>Modified</c> події в PI.</param>
/// <param name="Status">Стан зв'язку.</param>
/// <param name="Reason">Причина на рівні події (<see cref="PiValueReason"/>).</param>
/// <param name="PeriodKey">Період, у який лягає початок події.</param>
/// <param name="Row">Рядок документа — лише якщо видимий.</param>
/// <param name="RowHidden">Рядок є, але документ читачеві не видно.</param>
/// <param name="FirstSeenAt">Уперше побачено.</param>
/// <param name="LastSeenAt">Востаннє повернуто джерелом.</param>
/// <param name="LastSyncAt">Останній прогін синку.</param>
/// <param name="Attributes">Атрибути з порівнянням.</param>
public sealed record PiEventView(
    long LinkId,
    int MapId,
    string EventId,
    string? EventName,
    DateTime StartUtc,
    DateTimeOffset StartLocal,
    DateTime? EndUtc,
    DateTimeOffset? EndLocal,
    string TimeZoneId,
    string? PrimaryElement,
    DateTime? SourceModifiedUtc,
    string Status,
    string? Reason,
    int? PeriodKey,
    PiEventRowView? Row,
    bool RowHidden,
    DateTime FirstSeenAt,
    DateTime LastSeenAt,
    DateTime LastSyncAt,
    IReadOnlyList<PiEventAttributeView> Attributes);

/// <summary>Запис журналу видалення рядка події.</summary>
public sealed record PiEventRemovalView(
    DateTime RemovedAt,
    string? EventId,
    string? EventName,
    DateTime? EventStartUtc,
    int? PeriodKey,
    int? MapId,
    PiEventRowView? Row,
    bool RowHidden,
    string? RunId);

/// <summary>Журнал видалень за вікно.</summary>
/// <param name="Items">Записи, новіші першими.</param>
/// <param name="IsTruncated">Записів більше за стелю.</param>
public sealed record PiEventRemovalsView(IReadOnlyList<PiEventRemovalView> Items, bool IsTruncated);

/// <summary>Рядок CSV: подія × поле мапінгу.</summary>
public sealed record PiEventCsvRow(PiEventView Event, PiEventAttributeView Attribute);

/// <summary>
/// «Дані з PI» для подій PI (Event Frames, <c>D-465</c>): мапінги сутності-шаблону, події за вікном (до року) зі
/// значеннями атрибутів за останнім прочитанням і порівнянням із комірками рядка, журнал видалень, CSV.
/// Право <c>Integration.View</c> або <c>Integration.Manage</c> — та сама межа, що в <see cref="RawDataHandler"/>.
/// </summary>
/// <remarks>
/// ⛔ Документ мапінгу, якого читач не бачить (документ чи таблиця): подію й значення PI видно (це дані інтеграції),
/// але ні ключа рядка, ні документа, ні значень комірок — лише «записано в документ, до якого у вас немає доступу»
/// (<see cref="PiEventView.RowHidden"/>). Колонка, закрита Deny у періоді рядка, — <see cref="PiValueComparison.Hidden"/>.
/// <para>
/// ⚠ Лише сховище: у PI наживо обробник не ходить. Звернень до БД на сторінку — сталий набір, не на подію.
/// </para>
/// </remarks>
public sealed class PiEventDataHandler(
    ISourceEventViewStore store,
    IRawDataReader entities,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock,
    IAuditWriter audit)
{
    /// <summary>Тип події журналу безпеки для експорту подій.</summary>
    public const string ExportedEventType = "PiEventDataExported";

    /// <summary>Найбільша сторінка подій (кожна несе атрибути).</summary>
    public const int MaxPageSize = 200;

    /// <summary>Типова сторінка подій.</summary>
    public const int DefaultPageSize = 50;

    /// <summary>Стеля записів журналу видалень у відповіді.</summary>
    public const int MaxRemovals = 200;

    /// <summary>Сторінка експорту (подій).</summary>
    public const int ExportPageSize = 200;

    /// <summary>Найдовший фільтр назви / елемента.</summary>
    public const int MaxFilterLength = 200;

    /// <summary>Заголовок CSV.</summary>
    public static readonly string[] CsvColumns =
    [
        "eventId", "eventName", "startUtc", "endUtc", "primaryElement", "modifiedUtc", "status", "reason",
        "attribute", "scope", "piValueNumeric", "piValueText", "piUnit", "column", "expectedValue", "cellValue",
        "comparison", "comparisonReason",
    ];

    /// <summary>Мапінги сутності й періоди, що перетинають вікно.</summary>
    public async Task<PiEventMapsView> MapsAsync(int sourceEntityId, DateTime? fromUtc, DateTime? toUtc, CancellationToken ct)
    {
        var profile = await RequireAsync(ct).ConfigureAwait(false);
        var (from, to) = RawDataQuery.Window(fromUtc, toUtc, clock.UtcNow);
        var entity = await EntityAsync(sourceEntityId, ct).ConfigureAwait(false);

        var maps = await store.MapsAsync(sourceEntityId, ct).ConfigureAwait(false);
        var views = new List<PiEventMapView>(maps.Count);
        var periods = new Dictionary<int, PiEventPeriodView>();
        foreach (var map in maps)
        {
            var visible = await VisibleAsync(profile, map, ct).ConfigureAwait(false) is not null;
            views.Add(new PiEventMapView(
                map.MapId,
                map.IsActive,
                visible,
                visible ? map.DocumentId : null,
                visible ? map.DocumentKey : null,
                map.TableCode,
                map.Fields,
                map.Fields.Any(f => f.IsMonthColumn)));

            var mapPeriods = await store.PeriodsAsync(map, ct).ConfigureAwait(false);
            foreach (var period in mapPeriods.Where(p => p.Bounds.StartUtc < to && from < p.Bounds.EndUtc))
            {
                periods.TryAdd(period.PeriodKey, new PiEventPeriodView(
                    period.PeriodKey,
                    period.State.ToString(),
                    DateTime.SpecifyKind(period.Bounds.StartUtc, DateTimeKind.Utc),
                    DateTime.SpecifyKind(period.Bounds.EndUtc, DateTimeKind.Utc)));
            }
        }

        return new PiEventMapsView(
            entity.Id, entity.Code, entity.DisplayName, views, [.. periods.Values.OrderBy(p => p.FromUtc)]);
    }

    /// <summary>Сторінка подій мапінгу за вікном, новіші першими.</summary>
    /// <param name="sourceEntityId">Сутність-шаблон.</param>
    /// <param name="mapId">Мапінг; <c>null</c> — перший мапінг сутності.</param>
    /// <param name="fromUtc">Початок вікна.</param>
    /// <param name="toUtc">Кінець вікна (≤ 400 діб).</param>
    /// <param name="statuses">Стани зв'язку.</param>
    /// <param name="element">Первинний елемент починається з.</param>
    /// <param name="name">Назва містить.</param>
    /// <param name="cursor">Курсор.</param>
    /// <param name="limit">1..200; 0 — 50.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<PagedResult<PiEventView>> EventsAsync(
        int sourceEntityId,
        int? mapId,
        DateTime? fromUtc,
        DateTime? toUtc,
        IReadOnlyCollection<SourceEventLinkStatus>? statuses,
        string? element,
        string? name,
        string? cursor,
        int limit,
        CancellationToken ct)
    {
        var profile = await RequireAsync(ct).ConfigureAwait(false);
        var size = PageSize(limit);
        var filter = Filter(fromUtc, toUtc, statuses, element, name);
        var map = await MapOfAsync(sourceEntityId, mapId, ct).ConfigureAwait(false);
        if (map is null)
        {
            return new PagedResult<PiEventView>([], null, 0);
        }

        var total = await store.CountLinksAsync(map.MapId, filter, ct).ConfigureAwait(false);
        var (items, next) = await PageAsync(profile, map, filter, DecodeCursor(cursor), size, ct).ConfigureAwait(false);
        return new PagedResult<PiEventView>(items, next, total);
    }

    /// <summary>Журнал видалень рядків подій сутності за вікно моменту видалення.</summary>
    public async Task<PiEventRemovalsView> RemovalsAsync(
        int sourceEntityId, DateTime? fromUtc, DateTime? toUtc, CancellationToken ct)
    {
        var profile = await RequireAsync(ct).ConfigureAwait(false);
        var (from, to) = RawDataQuery.Window(fromUtc, toUtc, clock.UtcNow);
        await EntityAsync(sourceEntityId, ct).ConfigureAwait(false);

        var rows = await store.RemovalsAsync(sourceEntityId, from, to, MaxRemovals + 1, ct).ConfigureAwait(false);
        var maps = (await store.MapsAsync(sourceEntityId, ct).ConfigureAwait(false)).ToDictionary(m => m.MapId);
        var documents = new Dictionary<(long Document, int? Table), bool>();
        var items = new List<PiEventRemovalView>();
        foreach (var row in rows.Take(MaxRemovals))
        {
            PiEventRowView? shown = null;
            var hidden = false;
            if (row.DocumentId is { } documentId && row.RowKey is { } rowKey && row.PeriodKey is { } periodKey
                && row.TableInstanceId is { } instance)
            {
                int? table = row.MapId is { } id && maps.TryGetValue(id, out var map) ? map.TableDefId : null;
                if (!documents.TryGetValue((documentId, table), out var visible))
                {
                    visible = await DocumentVisibleAsync(profile, documentId, table, ct).ConfigureAwait(false) is not null;
                    documents[(documentId, table)] = visible;
                }

                var key = row.MapId is { } mid && maps.TryGetValue(mid, out var m2) ? m2.DocumentKey : null;
                if (visible && key is not null)
                {
                    shown = new PiEventRowView(documentId, key, periodKey, instance, rowKey);
                }
                else
                {
                    hidden = !visible;
                }
            }

            items.Add(new PiEventRemovalView(
                DateTime.SpecifyKind(row.RemovedAt, DateTimeKind.Utc),
                row.EventId,
                row.EventName,
                row.EventStartUtc is { } start ? DateTime.SpecifyKind(start, DateTimeKind.Utc) : null,
                row.PeriodKey,
                row.MapId,
                shown,
                hidden,
                row.RunId));
        }

        return new PiEventRemovalsView(items, rows.Count > MaxRemovals);
    }

    /// <summary>
    /// Перевіряє право, межі й стелю, пише подію безпеки (без значень) — і лише тоді віддає лінивий потік рядків
    /// «подія × поле».
    /// </summary>
    public async Task<AuditExport<PiEventCsvRow>> PrepareExportAsync(
        int sourceEntityId,
        int? mapId,
        DateTime? fromUtc,
        DateTime? toUtc,
        IReadOnlyCollection<SourceEventLinkStatus>? statuses,
        string? element,
        string? name,
        int maxRows,
        CancellationToken ct)
    {
        var profile = await RequireAsync(ct).ConfigureAwait(false);
        var filter = Filter(fromUtc, toUtc, statuses, element, name);
        var map = await MapOfAsync(sourceEntityId, mapId, ct).ConfigureAwait(false)
                  ?? throw NotFound(sourceEntityId);

        var fields = Math.Max(1, map.Fields.Count);

        // ⛔ Подія безпеки — ДО потоку і без значень (`AuditExportRunner`): хто, сутність, мапінг, вікно, рядків.
        return await AuditExportRunner.PrepareAsync(
            async next =>
            {
                var (events, nextCursor) = await PageAsync(profile, map, filter, DecodeCursor(next), ExportPageSize, ct)
                    .ConfigureAwait(false);
                return new PagedResult<PiEventCsvRow>(
                    [.. events.SelectMany(e => e.Attributes.Where(a => a.ColumnDefId is not null).Select(a => new PiEventCsvRow(e, a)))],
                    nextCursor,
                    TotalCount: null);
            },
            async _ => (int)Math.Min(int.MaxValue, (long)await store.CountLinksAsync(map.MapId, filter, ct).ConfigureAwait(false) * fields),
            maxRows,
            ExportedEventType,
            total => new
            {
                sourceEntityId,
                mapId = map.MapId,
                from = filter.FromUtc,
                to = filter.ToUtc,
                rows = total,
            },
            string.Create(
                CultureInfo.InvariantCulture,
                $"pi-events-{sourceEntityId}-{filter.FromUtc:yyyyMMdd'T'HHmmss'Z'}-{filter.ToUtc:yyyyMMdd'T'HHmmss'Z'}.csv"),
            audit,
            clock,
            currentUser,
            ct).ConfigureAwait(false);
    }

    /// <summary>Рядок CSV; текст чужої системи — через <see cref="CsvFormat.Field"/> (захист від формул).</summary>
    public static string ToCsv(PiEventCsvRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var (e, a) = (row.Event, row.Attribute);
        return string.Join(
                   ',',
                   CsvFormat.Field(e.EventId),
                   CsvFormat.Field(e.EventName),
                   AuditExportRunner.Stamp(e.StartUtc),
                   e.EndUtc is { } end ? AuditExportRunner.Stamp(end) : string.Empty,
                   CsvFormat.Field(e.PrimaryElement),
                   e.SourceModifiedUtc is { } modified ? AuditExportRunner.Stamp(modified) : string.Empty,
                   e.Status,
                   e.Reason ?? string.Empty,
                   CsvFormat.Field(a.Attribute),
                   a.Scope.ToString(),
                   a.PiValueNumeric?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                   CsvFormat.Field(a.PiValueText),
                   CsvFormat.Field(a.PiUnit),
                   CsvFormat.Field(a.ColumnCode),
                   ValueField(a.ExpectedValue),
                   ValueField(a.CellValue),
                   a.Comparison ?? string.Empty,
                   a.Reason ?? string.Empty)
               + CsvFormat.NewLine;
    }

    /// <summary>
    /// Значення комірки / «що записав би синк» у CSV: інваріантне число — як є (апостроф зробив би його текстом у
    /// Excel), решта — через <see cref="CsvFormat.Field"/> (захист від формул).
    /// </summary>
    private static string ValueField(string? value)
        => value is not null
           && decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _)
            ? value
            : CsvFormat.Field(value);

    /// <summary>Розмір сторінки 1..<see cref="MaxPageSize"/>; <c>0</c> — типовий.</summary>
    public static int PageSize(int limit)
    {
        if (limit == 0)
        {
            return DefaultPageSize;
        }

        if (limit is < 1 or > MaxPageSize)
        {
            throw RawDataQuery.Invalid(
                "err.ECR-REQ-0422.pageSizeOutOfRange",
                $"Розмір сторінки поза межами 1..{MaxPageSize}.",
                ("max", MaxPageSize.ToString(CultureInfo.InvariantCulture)));
        }

        return limit;
    }

    /// <summary>Кодує курсор (початок, Id).</summary>
    public static string EncodeCursor(DateTime startUtc, long linkId)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Create(
            CultureInfo.InvariantCulture, $"e:{startUtc.Ticks}:{linkId}")));

    /// <summary>Декодує курсор; зіпсований — <c>null</c> (початок).</summary>
    public static PiEventCursor? DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor) || cursor.Length > 96)
        {
            return null;
        }

        Span<byte> buffer = stackalloc byte[96];
        if (!Convert.TryFromBase64String(cursor, buffer, out var written))
        {
            return null;
        }

        var parts = Encoding.UTF8.GetString(buffer[..written]).Split(':');
        return parts.Length == 3 && parts[0] == "e"
               && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
               && ticks is > 0 and <= 3155378975999999999
               && long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? new PiEventCursor(new DateTime(ticks, DateTimeKind.Utc), id)
            : null;
    }

    /// <summary>Вікно й фільтри; рядки фільтрів — обрізані, не довші за <see cref="MaxFilterLength"/>.</summary>
    public PiEventFilter Filter(
        DateTime? fromUtc,
        DateTime? toUtc,
        IReadOnlyCollection<SourceEventLinkStatus>? statuses,
        string? element,
        string? name)
    {
        var (from, to) = RawDataQuery.Window(fromUtc, toUtc, clock.UtcNow);
        if ((element?.Length ?? 0) > MaxFilterLength || (name?.Length ?? 0) > MaxFilterLength)
        {
            throw RawDataQuery.Invalid(
                "err.ECR-REQ-0422.piEventFilter",
                $"Фільтр назви чи елемента довший за {MaxFilterLength} символів.",
                ("max", MaxFilterLength.ToString(CultureInfo.InvariantCulture)));
        }

        return new PiEventFilter(
            from,
            to,
            statuses?.Where(Enum.IsDefined).Distinct().ToList() ?? [],
            string.IsNullOrWhiteSpace(element) ? null : element.Trim().ToUpperInvariant(),
            string.IsNullOrWhiteSpace(name) ? null : name.Trim());
    }

    private async Task<(List<PiEventView> Items, string? Next)> PageAsync(
        AccessProfile profile, PiEventMap map, PiEventFilter filter, PiEventCursor? before, int size, CancellationToken ct)
    {
        var context = await store.ContextAsync(map, ct).ConfigureAwait(false);
        var scope = await VisibleAsync(profile, map, ct).ConfigureAwait(false);
        var links = await store.LinksAsync(map.MapId, filter, before, size + 1, ct).ConfigureAwait(false);
        var page = links.Take(size).ToList();

        var values = page.Count == 0
            ? []
            : await store.ValuesAsync([.. page.Select(l => l.Id)], ct).ConfigureAwait(false);
        var valuesByLink = values.GroupBy(v => v.LinkId).ToDictionary(g => g.Key, g => (IReadOnlyList<PiObservedValue>)[.. g]);

        // ⛔ Комірки — лише для документа, який читач бачить; інакше значення документа не читаються взагалі.
        IReadOnlyList<PiCellValue> cells = [];
        var rows = page
            .Where(l => l.TableInstanceId is not null && l.PeriodKey is not null && l.RowKey is not null)
            .Select(l => new PiRowAddress(l.PeriodKey!.Value, l.TableInstanceId!.Value, l.RowKey!, map.DocumentId))
            .Distinct()
            .ToList();
        if (scope is not null && rows.Count > 0 && context.Fields.Count > 0)
        {
            cells = await store.CellsAsync(rows, [.. context.Fields.Select(f => f.ColumnDefId)], ct).ConfigureAwait(false);
        }

        var cellIndex = cells.ToDictionary(c => (c.PeriodKey, c.TableInstanceId, c.RowKey.ToUpperInvariant(), c.ColumnDefId));
        var tz = SiteTimeZone.Create(map.TimeZoneId).ToTimeZoneInfo();

        var items = new List<PiEventView>(page.Count);
        foreach (var link in page)
        {
            var hasRow = link.TableInstanceId is not null;
            Func<int, PiCellValue?>? cellOf = scope is null || !hasRow
                ? null
                : column => cellIndex.GetValueOrDefault(
                    (link.PeriodKey!.Value, link.TableInstanceId!.Value, link.RowKey!.ToUpperInvariant(), column));
            var periodScope = scope is not null && link.PeriodKey is { } rowPeriod
                ? scope.InPeriod(new PeriodKey(rowPeriod))
                : null;

            var attributes = PiEventComparison.Compare(
                link,
                context,
                tz,
                valuesByLink.GetValueOrDefault(link.Id) ?? [],
                cellOf,
                column => periodScope?.CanReadColumn(column) == true);

            items.Add(ToView(link, map, context, tz, scope is not null, attributes));
        }

        var next = links.Count > size ? EncodeCursor(page[^1].StartUtc, page[^1].Id) : null;
        return (items, next);
    }

    internal static PiEventView ToView(
        PiEventLink link,
        PiEventMap map,
        PiEventMapContext context,
        TimeZoneInfo tz,
        bool documentVisible,
        IReadOnlyList<PiEventAttributeView> attributes)
    {
        var hasRow = link.TableInstanceId is not null && link.PeriodKey is not null && link.RowKey is not null;
        return new PiEventView(
            link.Id,
            link.MapId,
            link.SourceEventId,
            link.EventName,
            DateTime.SpecifyKind(link.StartUtc, DateTimeKind.Utc),
            Local(link.StartUtc, tz),
            link.EndUtc is { } end ? DateTime.SpecifyKind(end, DateTimeKind.Utc) : null,
            link.EndUtc is { } e2 ? Local(e2, tz) : null,
            map.TimeZoneId,
            link.PrimaryElement,
            link.SourceModifiedUtc is { } m ? DateTime.SpecifyKind(m, DateTimeKind.Utc) : null,
            link.Status.ToString(),
            PiEventComparison.LinkReason(link, context)?.ToString(),
            SourceEventPeriods.Locate(link.StartUtc, context.Periods)?.PeriodKey,
            hasRow && documentVisible
                ? new PiEventRowView(map.DocumentId, map.DocumentKey, link.PeriodKey!.Value, link.TableInstanceId!.Value, link.RowKey!)
                : null,
            hasRow && !documentVisible,
            DateTime.SpecifyKind(link.FirstSeenAt, DateTimeKind.Utc),
            DateTime.SpecifyKind(link.LastSeenAt, DateTimeKind.Utc),
            DateTime.SpecifyKind(link.LastSyncAt, DateTimeKind.Utc),
            attributes);
    }

    private static DateTimeOffset Local(DateTime utc, TimeZoneInfo tz)
        => TimeZoneInfo.ConvertTime(new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)), tz);

    private async Task<PiEventMap?> MapOfAsync(int sourceEntityId, int? mapId, CancellationToken ct)
    {
        await EntityAsync(sourceEntityId, ct).ConfigureAwait(false);
        var maps = await store.MapsAsync(sourceEntityId, ct).ConfigureAwait(false);
        if (mapId is null)
        {
            return maps.Count == 0 ? null : maps[0];
        }

        // ⛔ Мапінг іншої сутності — та сама 404, що й неіснуючий: `mapId` не ключ до подій чужого шаблону.
        return maps.FirstOrDefault(m => m.MapId == mapId)
               ?? throw new NotFoundException(
                   ErrorCodes.SourceEntityNotFound,
                   $"Мапінгу подій {mapId} у сутності {sourceEntityId} немає.",
                   new Dictionary<string, object?>
                   {
                       ["messageKey"] = "err.ECR-INT-0404.eventMap",
                       ["eventMapId"] = mapId.Value.ToString(CultureInfo.InvariantCulture),
                   });
    }

    /// <summary>Область читання документа мапінгу, якщо читач бачить документ і таблицю; інакше <c>null</c>.</summary>
    private Task<DocumentReadScope?> VisibleAsync(AccessProfile profile, PiEventMap map, CancellationToken ct)
        => DocumentVisibleAsync(profile, map.DocumentId, map.TableDefId, ct);

    private async Task<DocumentReadScope?> DocumentVisibleAsync(
        AccessProfile profile, long documentId, int? tableDefId, CancellationToken ct)
    {
        var read = await access.CanReadDocumentAsync(profile, documentId, ct).ConfigureAwait(false);
        if (!read.IsAllowed)
        {
            return null;
        }

        var scope = await access.ReadScopeAsync(profile, documentId, ct).ConfigureAwait(false);
        return tableDefId is { } table && !scope.CanReadTable(table) ? null : scope;
    }

    private Task<AccessProfile> RequireAsync(CancellationToken ct)
        => PermissionCheck.RequireAnyAsync(
            access, currentUser, [RawDataHandler.ViewPermission, RawDataHandler.ManagePermission], ct);

    private async Task<RawSourceEntity> EntityAsync(int sourceEntityId, CancellationToken ct)
        => await entities.FindEntityAsync(sourceEntityId, ct).ConfigureAwait(false) ?? throw NotFound(sourceEntityId);

    private static NotFoundException NotFound(int sourceEntityId)
        => new(
            ErrorCodes.SourceEntityNotFound,
            $"Сутності джерела {sourceEntityId} немає.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-INT-0404.sourceEntity",
                ["id"] = sourceEntityId.ToString(CultureInfo.InvariantCulture),
            });
}
