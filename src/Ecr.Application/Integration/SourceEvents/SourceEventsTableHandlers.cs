// src/Ecr.Application/Integration/SourceEvents/SourceEventsTableHandlers.cs
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Integration.SourceEvents;

/// <summary>Значення події, яке не вдалося зіставити із записом довідника (<c>UnmappedJson</c>).</summary>
/// <param name="Column">Код колонки.</param>
/// <param name="Value">Значення джерела.</param>
public sealed record SourceEventUnmappedItem(string Column, string? Value);

/// <summary>Рядок таблиці подій: зв'язок «подія джерела ↔ рядок документа» зі станом (HSE301 A6).</summary>
/// <param name="Id">Ідентифікатор зв'язку.</param>
/// <param name="SourceEventMapId">Мапінг подій.</param>
/// <param name="SourceEventId">ID події в джерелі.</param>
/// <param name="EventName">Назва події.</param>
/// <param name="PrimaryElement">Первинний елемент події (Location/Equipment) у ключовому вигляді — верхній регістр; <c>null</c> — не зберігся.</param>
/// <param name="Status">Стан синхронізації.</param>
/// <param name="StartUtc">Початок події, UTC.</param>
/// <param name="StartLocal">Початок у поясі проєкту (зі зсувом).</param>
/// <param name="EndUtc">Кінець події, UTC; <c>null</c> — подія триває.</param>
/// <param name="EndLocal">Кінець у поясі проєкту.</param>
/// <param name="TimeZoneId">Пояс проєкту.</param>
/// <param name="SourceModifiedUtc">Остання зміна події в джерелі.</param>
/// <param name="DocumentId">Документ мапінгу.</param>
/// <param name="DocumentKey">Бізнес-ключ документа.</param>
/// <param name="PeriodKey">Період рядка; <c>null</c> — рядка немає.</param>
/// <param name="TableInstanceId">Екземпляр таблиці рядка.</param>
/// <param name="RowKey">Ключ рядка (<c>EF-…</c>).</param>
/// <param name="KeptManual">Коди колонок, лишених за людиною.</param>
/// <param name="Unmapped">Значення без відповідника в довіднику.</param>
/// <param name="FirstSeenAt">Коли подію вперше побачено.</param>
/// <param name="LastSeenAt">Коли її востаннє повернуло джерело.</param>
/// <param name="LastSyncAt">Останній прогін синхронізації.</param>
public sealed record SourceEventRowDto(
    long Id,
    int SourceEventMapId,
    string SourceEventId,
    string? EventName,
    string? PrimaryElement,
    SourceEventLinkStatus Status,
    DateTime StartUtc,
    DateTimeOffset StartLocal,
    DateTime? EndUtc,
    DateTimeOffset? EndLocal,
    string TimeZoneId,
    DateTime? SourceModifiedUtc,
    long DocumentId,
    string DocumentKey,
    int? PeriodKey,
    long? TableInstanceId,
    string? RowKey,
    IReadOnlyList<string> KeptManual,
    IReadOnlyList<SourceEventUnmappedItem> Unmapped,
    DateTime FirstSeenAt,
    DateTime LastSeenAt,
    DateTime LastSyncAt);

/// <summary>Фільтри таблиці подій.</summary>
/// <param name="SourceEntityId">Сутність-шаблон подій.</param>
/// <param name="MapId">Лише цей мапінг.</param>
/// <param name="DocumentId">Лише цей документ.</param>
/// <param name="Statuses">Лише ці стани; порожньо — усі.</param>
/// <param name="FromUtc">Початок події не раніше (включно).</param>
/// <param name="ToUtc">Початок події раніше (виключно).</param>
/// <param name="PeriodKey">Лише цей період.</param>
public sealed record SourceEventsFilter(
    int SourceEntityId,
    int? MapId,
    long? DocumentId,
    IReadOnlyCollection<SourceEventLinkStatus>? Statuses,
    DateTime? FromUtc,
    DateTime? ToUtc,
    int? PeriodKey);

/// <summary>
/// Таблиця подій джерела: що синхронізовано, що чекає, що зникло чи не вмістилось (HSE301 A6,
/// FEATURE-HSE301-VIEW §4.7.4). Читання; права — <c>Read</c> на документ мапінгу.
/// </summary>
/// <remarks>
/// ⛔ Без оракула: мапінг, документ якого користувач не бачить, для нього «не існує» — його події не потрапляють
/// у відповідь і не входять у <c>totalCount</c>, а прямий запит за таким <c>mapId</c> — 404, як за неіснуючим.
/// Функціонального права немає: подію бачить той, хто бачить документ (вкладка «Події з PI» документа, §10.6),
/// а не лише адміністратор інтеграції. Вимкнена чи неіснуюча сутність для користувача без права
/// <c>Integration.View</c>/<c>Integration.Manage</c> — порожня відповідь, а не 404: інакше перебір
/// ідентифікаторів розкривав би, які сутності є.
/// </remarks>
public sealed class ListSourceEventsHandler(
    ISourceEventMapStore store,
    ICollectionStore sources,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Повертає сторінку подій.</summary>
    /// <param name="filter">Фільтри.</param>
    /// <param name="page">Курсорна пагінація.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<PagedResult<SourceEventRowDto>> HandleAsync(
        SourceEventsFilter filter, CursorRequest page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(page);

        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException(
                         ErrorCodes.Unauthorized,
                         "Потрібна автентифікація.",
                         new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.signInRequired" });
        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);

        if (!page.IsValid)
        {
            var max = CursorRequest.MaxLimit.ToString(CultureInfo.InvariantCulture);
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"Розмір сторінки поза межами 1..{max}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422.pageSizeOutOfRange",
                    ["max"] = max,
                });
        }

        var entity = await sources.FindSourceEntityAsync(filter.SourceEntityId, ct).ConfigureAwait(false);
        var integrationReader = PermissionCheck.IsGranted(profile, ListDataSourcesHandler.Permission)
                                || PermissionCheck.IsGranted(profile, SaveDataSourceHandler.Permission);
        if (entity is null)
        {
            if (integrationReader)
            {
                throw NotFound("err.ECR-INT-0404.sourceEntity", $"Сутності джерела {filter.SourceEntityId} немає або вона вимкнена.", "id", filter.SourceEntityId);
            }

            return Empty();
        }

        var maps = await store.ListMapsAsync(filter.SourceEntityId, ct).ConfigureAwait(false);
        var visible = new List<int>();
        foreach (var map in maps.OrderBy(m => m.Id))
        {
            if (filter.MapId is { } only && only != map.Id)
            {
                continue;
            }

            if (filter.DocumentId is { } document && document != map.DocumentId)
            {
                continue;
            }

            var read = await access.CanReadDocumentAsync(profile, map.DocumentId, ct).ConfigureAwait(false);
            if (read.IsAllowed)
            {
                visible.Add(map.Id);
            }
        }

        // Прямий запит за мапінгом, якого немає чи якого не видно, — 404 (однаково в обох випадках).
        if (filter.MapId is not null && visible.Count == 0)
        {
            throw NotFound("err.ECR-INT-0404.eventMap", $"Мапінгу подій {filter.MapId} немає.", "eventMapId", filter.MapId.Value);
        }

        if (visible.Count == 0)
        {
            return Empty();
        }

        var statuses = filter.Statuses?.Where(Enum.IsDefined).Distinct().ToList() ?? [];
        var result = await store
            .ReadLinksAsync(
                new SourceEventLinkFilter(visible, statuses, filter.FromUtc, filter.ToUtc, filter.PeriodKey),
                page.Cursor,
                page.Limit,
                ct)
            .ConfigureAwait(false);

        var hasMore = result.Rows.Count > page.Limit;
        var rows = result.Rows.Take(page.Limit).ToList();

        return new PagedResult<SourceEventRowDto>(
            [.. rows.Select(ToDto)],
            hasMore ? store.NextCursor(rows[^1].Link) : null,
            result.TotalCount);

        static PagedResult<SourceEventRowDto> Empty() => new([], null, 0);
    }

    // `param` — плейсхолдер шаблону ключа ({id}, {eventMapId}): без нього клієнт показав би фігурні дужки.
    private static NotFoundException NotFound(string messageKey, string message, string param, int id)
        => new(
            ErrorCodes.SourceEntityNotFound,
            message,
            new Dictionary<string, object?>
            {
                ["messageKey"] = messageKey,
                [param] = id.ToString(CultureInfo.InvariantCulture),
            });

    private static SourceEventRowDto ToDto(SourceEventLinkRow row)
    {
        var link = row.Link;
        var tz = SiteTimeZone.Create(row.TimeZoneId).ToTimeZoneInfo();

        return new SourceEventRowDto(
            link.Id,
            link.SourceEventMapId,
            link.SourceEventId,
            link.EventName,
            link.PrimaryElement,
            link.Status,
            link.StartUtc,
            Local(link.StartUtc, tz),
            link.EndUtc,
            link.EndUtc is { } end ? Local(end, tz) : null,
            row.TimeZoneId,
            link.SourceModifiedUtc,
            row.DocumentId,
            row.DocumentKey,
            link.PeriodKey,
            link.TableInstanceId,
            link.RowKey,
            Parse<string>(link.KeptManualJson),
            Parse<SourceEventUnmappedItem>(link.UnmappedJson),
            link.FirstSeenAt,
            link.LastSeenAt,
            link.LastSyncAt);
    }

    private static DateTimeOffset Local(DateTime utc, TimeZoneInfo tz)
        => TimeZoneInfo.ConvertTime(new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)), tz);

    // Зіпсований JSON у журналі — порожній перелік, а не 500 на всій сторінці: сам рядок важливіший за примітку.
    private static List<T> Parse<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<T>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
