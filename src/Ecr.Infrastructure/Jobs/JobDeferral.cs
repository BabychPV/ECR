// src/Ecr.Infrastructure/Jobs/JobDeferral.cs
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Ports;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Стеля відкладень (борг O1): задача, що відкладається (<see cref="JobDeferredException"/>)
/// довше за <see cref="MaxDeferral"/> від ПЕРШОГО відкладення, закривається <c>Failed</c>
/// з конвертом <see cref="ExhaustedKey"/>, а не повторюється вічно.
/// </summary>
/// <remarks>
/// ⛔ Без стелі лок, що ніколи не звільниться (зависла сесія власника, забутий
/// <c>sp_getapplock</c>), означав би безкінечні повтори кожні
/// <see cref="RecalculationDocumentLock.DeferDelay"/>: стан вічно «у черзі», жодного
/// сигналу оператору. Відкладення не рахуються спробами — тож і
/// <c>JobRetryPolicy</c> їх не зупинить.
/// <para>
/// Момент першого відкладення: черга в базі — властивість <see cref="PayloadProperty"/>
/// у JSON-об'єкті payload (без міграції; задачі десеріалізують payload у свої
/// типи, і зайву властивість System.Text.Json пропускає); Quartz —
/// <see cref="QuartzSinceKey"/> у <c>JobDataMap</c> триґера. Ретрай після провалу
/// і ручний перезапуск починають відлік заново.
/// </para>
/// <para>
/// ⛔ Д-1 огляду O1: свіжа дельта не успадковує чужий відлік. Злиття, що додає
/// НОВІ комірки в задачу злиття (<c>IFormulaRecalculationJob</c>), знімає момент
/// відкладення (і позначку <see cref="RequeuedProperty"/>); поглинання повернутої
/// задачі задачею позаду свій момент туди не переносить. Інакше правка о t0+29 хв
/// падала б разом із задачею о t0+30 і не рахувалася б до нічного прогону. Другий
/// захист — <see cref="RequeuePayload"/>: вичерпана стеля перепоставляє комірки раз.
/// </para>
/// </remarks>
public static class JobDeferral
{
    /// <summary>Ключ каталогу причини «відкладення вичерпано».</summary>
    public const string ExhaustedKey = "jobs.deferralExhausted";

    /// <summary>Властивість payload черги в базі з моментом першого відкладення (ISO 8601, UTC).</summary>
    public const string PayloadProperty = "ecrDeferredSince";

    /// <summary>Ключ <c>JobDataMap</c> триґера Quartz: момент першого відкладення, тіки UTC.</summary>
    public const string QuartzSinceKey = "ecr.deferredSince";

    /// <summary>
    /// Властивість payload задачі злиття, перепоставленої після вичерпаної стелі
    /// (<see cref="RequeuePayload"/>): друга стеля — вже <c>Failed</c> без перепостановки.
    /// </summary>
    public const string RequeuedProperty = "ecrDeferralRequeued";

    /// <summary>
    /// Найдовший сумарний час відкладень однієї задачі (<c>Jobs:Recalculation:MaxDeferral</c>,
    /// типово 30 хв) — з запасом довший за найдовший законний повний перерахунок документа.
    /// </summary>
    public static readonly TimeSpan MaxDeferral = TimeSpan.FromMinutes(30);

    /// <summary>Чи вичерпано стелю: від першого відкладення минуло БІЛЬШЕ за <paramref name="max"/>.</summary>
    /// <param name="since">Перше відкладення; <c>null</c> — задача ще не відкладалась.</param>
    /// <param name="now">Зараз (UTC).</param>
    /// <param name="max">Стеля.</param>
    public static bool IsExhausted(DateTime? since, DateTime now, TimeSpan max)
        => since is { } first && now - first > max;

    /// <summary>Конверт <see cref="ExhaustedKey"/> для <c>Message</c>.</summary>
    /// <param name="resource">Зайнятий ресурс (ім'я лока); <c>null</c> — невідомий.</param>
    /// <param name="waited">Скільки задача відкладалась.</param>
    public static string Envelope(string? resource, TimeSpan waited)
        => JobProgressMessageCodec.Encode(new JobProgressMessageEnvelope(
            ExhaustedKey,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["resource"] = resource ?? "—",
                ["waited"] = Format(waited),
            }));

    /// <summary>Тривалість для тексту: <c>hh:mm:ss</c> без дробу секунд.</summary>
    public static string Format(TimeSpan waited)
        => TimeSpan.FromSeconds(Math.Floor(Math.Max(0, waited.TotalSeconds))).ToString("c", CultureInfo.InvariantCulture);

    /// <summary>
    /// Payload перепостановки задачі злиття, що вичерпала стелю (Д-1 огляду O1): без моменту
    /// відкладення (нова серія) і з позначкою <see cref="RequeuedProperty"/>.
    /// </summary>
    /// <param name="payloadJson">Payload задачі, що вичерпала стелю.</param>
    /// <returns><c>null</c> — не перепоставляти: вже перепоставлена раз або payload не об'єкт.</returns>
    /// <remarks>
    /// ⛔ Рівно одна перепостановка на серію. Лок, що не звільняється ніколи, інакше
    /// давав би нескінченний ланцюг «стеля → нова задача → стеля» кожні 30 хв. Позначку
    /// знімає лише злиття НОВИХ комірок (свіжа правка — нова серія), тож ланцюг
    /// обмежений правками людей, а не часом.
    /// </remarks>
    public static string? RequeuePayload(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(payloadJson) is not System.Text.Json.Nodes.JsonObject payload
                || payload.ContainsKey(RequeuedProperty))
            {
                return null;
            }

            payload.Remove(PayloadProperty);
            payload[RequeuedProperty] = true;

            return payload.ToJsonString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Момент першого відкладення з payload черги в базі; <c>null</c> — немає або не читається.</summary>
    /// <param name="payloadJson">Payload задачі.</param>
    public static DateTime? SinceOf(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);

            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty(PayloadProperty, out var value)
                   && value.ValueKind == JsonValueKind.String
                   && DateTime.TryParse(
                       value.GetString(), CultureInfo.InvariantCulture,
                       DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var since)
                ? DateTime.SpecifyKind(since, DateTimeKind.Utc)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
