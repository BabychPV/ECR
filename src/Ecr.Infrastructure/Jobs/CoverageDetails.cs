// src/Ecr.Infrastructure/Jobs/CoverageDetails.cs
using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Integration;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Подробиці події журналу покриття (<c>itg.CollectionCoverage.Details</c>) КОНВЕРТОМ
/// <c>{"k":…,"p":{…}}</c> із ключем <c>coverageEvents.*</c>, а не готовою фразою.
/// </summary>
/// <remarks>
/// ⛔ Мова читача в момент запису невідома, тож готова українська фраза в базі показується
/// українською і в англійському інтерфейсі. Форма та сама, що в <c>CollectionRunner</c>
/// (<c>coverageEvents.sourceDataRefused</c>); клієнт розгортає її в
/// <c>collectionRunMessage.ts</c>. Старі рядки без конверта клієнт показує як є.
///
/// ⚠ Контракт не змінено: <c>Details</c> лишається рядком. Конверт мусить вміститися в
/// <see cref="CollectionCoverage.MaxDetailsLength"/> — обрізаний JSON не читається, тож
/// вільний текст (<c>reason</c>) скорочується ДО кодування, а не після.
/// </remarks>
public static class CoverageDetails
{
    /// <summary>Стеля вільного тексту причини; решта конверта — ключ і короткі параметри.</summary>
    private const int MaxReasonLength = 300;

    public const string PeriodClosedKey = "coverageEvents.periodClosed";
    public const string PeriodMissingKey = "coverageEvents.periodMissing";
    public const string PointCeilingKey = "coverageEvents.pointCeiling";
    public const string KeptManualKey = "coverageEvents.keptManual";
    public const string WriteConflictKey = "coverageEvents.writeConflict";
    public const string NeedsConfirmationKey = "coverageEvents.needsConfirmation";
    public const string EventWriteFailedKey = "coverageEvents.eventWriteFailed";
    public const string EventWritePartialKey = "coverageEvents.eventWritePartial";
    public const string EventRowNotCreatedKey = "coverageEvents.eventRowNotCreated";

    public const string EventRemovalSourceEmptyKey = "coverageEvents.eventRemovalSourceEmpty";
    public const string EventRemovalKeptManualKey = "coverageEvents.eventRemovalKeptManual";

    public const string EventRemovalLimitKey = "coverageEvents.eventRemovalLimit";
    public const string EventRemovalSheetSubmittedKey = "coverageEvents.eventRemovalSheetSubmitted";

    public const string EventTemplateOverlapKey = "coverageEvents.eventTemplateOverlap";

    public const string EventsTruncatedKey = "coverageEvents.eventsTruncated";

    /// <summary>
    /// Подій у вікні більше, ніж дочитали сторінками (L3-07): пізніші за <paramref name="after"/> цим прогоном не
    /// синхронізовано, видалення зниклих пропущено.
    /// </summary>
    public static string EventsTruncated(int count, int pages, object after)
        => Encode(EventsTruncatedKey, ("count", Text(count)), ("pages", Text(pages)), ("after", Text(after)));

    /// <summary>Масове видалення зниклих подій перевищило ліміт: за мапінгом нічого не видалено до ручного підтвердження.</summary>
    public static string EventRemovalLimit(int candidates, int limit, int linked)
        => Encode(
            EventRemovalLimitKey,
            ("count", Text(candidates)),
            ("limit", Text(limit)),
            ("linked", Text(linked)));

    /// <summary>Події немає в джерелі, але аркуш рядка поданий чи затверджений: рядок не видалено.</summary>
    public static string EventRemovalSheetSubmitted(object eventId, object rowKey, object status)
        => Encode(
            EventRemovalSheetSubmittedKey,
            ("eventId", Text(eventId)),
            ("rowKey", Text(rowKey)),
            ("status", Text(status)));

    /// <summary>Одна подія в двох братніх шаблонах з різними значеннями атрибутів; береться перший за порядком.</summary>
    public static string EventTemplateOverlap(object eventId, object template, object otherTemplate)
        => Encode(
            EventTemplateOverlapKey,
            ("eventId", Text(eventId)),
            ("template", Text(template)),
            ("other", Text(otherTemplate)));

    /// <summary>Джерело віддало нуль подій при N прив'язаних у БД: видалення пропущено (гард «повної звірки»).</summary>
    public static string EventRemovalSourceEmpty(int linked)
        => Encode(EventRemovalSourceEmptyKey, ("count", Text(linked)));

    /// <summary>Події немає в джерелі, але рядок має правку людини: рядок не видалено.</summary>
    public static string EventRemovalKeptManual(object eventId, object rowKey)
        => Encode(EventRemovalKeptManualKey, ("eventId", Text(eventId)), ("rowKey", Text(rowKey)));

    /// <summary>Період не відкритий для запису (або невідомий — <c>null</c>).</summary>
    public static string PeriodNotOpen(object? state)
        => state is null
            ? Encode(PeriodMissingKey)
            : Encode(PeriodClosedKey, ("state", Text(state)));

    /// <summary>Поле має більше точок за період, ніж стеля згортки.</summary>
    public static string PointCeiling(object field, int limit)
        => Encode(PointCeilingKey, ("field", Text(field)), ("limit", Text(limit)));

    /// <summary>Комірка має правку людини.</summary>
    public static string KeptManual(object cell) => Encode(KeptManualKey, ("cell", Text(cell)));

    /// <summary>Рядок змінювали під час запису, повтори вичерпано.</summary>
    public static string WriteConflict(object cell) => Encode(WriteConflictKey, ("cell", Text(cell)));

    /// <summary>Правило періоду вимагає підтвердження людини.</summary>
    public static string NeedsConfirmation(object cell) => Encode(NeedsConfirmationKey, ("cell", Text(cell)));

    /// <summary>Подію не записано; <paramref name="reason"/> — текст відмови (дані, не наше формулювання).</summary>
    public static string EventWriteFailed(object eventId, string reason)
        => Encode(
            EventWriteFailedKey,
            ("eventId", Text(eventId)),
            ("reason", JobProgressMessageCodec.Shorten(reason, MaxReasonLength)));

    /// <summary>Подію записано не повністю: конфлікт запису чи підтвердження людини.</summary>
    public static string EventWritePartial(object eventId, object rowKey)
        => Encode(EventWritePartialKey, ("eventId", Text(eventId)), ("rowKey", Text(rowKey)));

    /// <summary>Рядок події не створено, значення відхилено.</summary>
    public static string EventRowNotCreated(object eventId, object rowKey)
        => Encode(EventRowNotCreatedKey, ("eventId", Text(eventId)), ("rowKey", Text(rowKey)));

    private static string Encode(string key, params (string Name, string Value)[] parameters)
    {
        var envelope = new JobProgressMessageEnvelope(
            key,
            parameters.Length == 0
                ? null
                : parameters.ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal));

        return JobProgressMessageCodec.Encode(envelope);
    }

    private static string Text(object? value)
        => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
}
