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
