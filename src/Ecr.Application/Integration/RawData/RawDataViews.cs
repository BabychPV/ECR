// src/Ecr.Application/Integration/RawData/RawDataViews.cs
namespace Ecr.Application.Integration.RawData;

/// <summary>Сутність збору і її теги (<c>GET /sources/{id}/raw-data/tags</c>).</summary>
/// <param name="SourceEntityId">Сутність.</param>
/// <param name="Code">Код у джерелі.</param>
/// <param name="DisplayName">Підпис; <c>null</c> — не задано.</param>
/// <param name="IsActive">Чи сутність діє.</param>
/// <param name="Tags">Теги: поля мапінгів; без мапінгів — шлях самої сутності.</param>
/// <param name="IsTruncated">Тегів більше за стелю.</param>
/// <param name="EventMapCount">
/// D-465: мапінгів подій (Event Frames) у сутності; більше нуля — сутність-шаблон подій, і екран показує «Події».
/// </param>
public sealed record RawDataTagsView(
    int SourceEntityId,
    string Code,
    string? DisplayName,
    bool IsActive,
    IReadOnlyList<RawDataTag> Tags,
    bool IsTruncated,
    int EventMapCount);

/// <summary>Точка для таблиці «Дані з PI».</summary>
/// <param name="Timestamp">Мітка часу точки (UTC).</param>
/// <param name="ValueNumeric">Число в одиниці ДЖЕРЕЛА (ФВ-16.10); конверсія — лише при записі в документ.</param>
/// <param name="ValueString">Текст для нечислових атрибутів.</param>
/// <param name="UnitCode">Код одиниці джерела; <c>null</c> — не повідомлена.</param>
/// <param name="Quality">Якість за класифікацією джерела; <c>null</c> — не повідомлена.</param>
/// <param name="RetrievedAt">Коли точку отримано (UTC).</param>
/// <param name="CollectionRunId">Прогін збору, що її приніс.</param>
public sealed record RawPointView(
    DateTime Timestamp,
    decimal? ValueNumeric,
    string? ValueString,
    string? UnitCode,
    string? Quality,
    DateTime RetrievedAt,
    long CollectionRunId);

/// <summary>Згорнутий ряд для графіка: не більше заданого числа кошиків незалежно від довжини вікна.</summary>
/// <param name="FromUtc">Початок вікна.</param>
/// <param name="ToUtc">Кінець вікна (виключно).</param>
/// <param name="BucketSeconds">Ширина кошика.</param>
/// <param name="TotalPoints">Усіх точок у вікні.</param>
/// <param name="NumericPoints">З них числових — лише вони на графіку.</param>
/// <param name="Buckets">Непорожні кошики за зростанням часу.</param>
public sealed record RawSeriesView(
    DateTime FromUtc,
    DateTime ToUtc,
    long BucketSeconds,
    long TotalPoints,
    long NumericPoints,
    IReadOnlyList<RawSeriesBucket> Buckets);

/// <summary>Кошик графіка.</summary>
/// <param name="StartUtc">Початок кошика.</param>
/// <param name="Points">Усіх точок.</param>
/// <param name="Min">Мінімум числових; <c>null</c> — числових немає.</param>
/// <param name="Max">Максимум числових.</param>
/// <param name="Avg">Середнє арифметичне числових.</param>
public sealed record RawSeriesBucket(DateTime StartUtc, long Points, decimal? Min, decimal? Max, decimal? Avg);

/// <summary>Покриття вікна збором: що зібрано, де прогалини, коли збирали востаннє.</summary>
/// <param name="FromUtc">Початок вікна.</param>
/// <param name="ToUtc">Кінець вікна (виключно).</param>
/// <param name="Covered">Злиті покриті інтервали, обрізані вікном.</param>
/// <param name="Gaps">Прогалини вікна — часу, за який збір не відзвітував успіхом.</param>
/// <param name="IsTruncated">Інтервалів більше за стелю: прогалини після останнього не обчислено.</param>
/// <param name="LastCollection">Останній прогін і останній успіх.</param>
public sealed record RawCoverageView(
    DateTime FromUtc,
    DateTime ToUtc,
    IReadOnlyList<RawInterval> Covered,
    IReadOnlyList<RawInterval> Gaps,
    bool IsTruncated,
    RawCollectionStatus LastCollection);
