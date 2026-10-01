// src/Ecr.Application/Ports/ISourceEventSyncJob.cs
namespace Ecr.Application.Ports;

/// <summary>
/// Маркер задачі синхронізації подій джерела в рядки таблиць (HSE301 A5b,
/// FEATURE-HSE301-VIEW §4.7.4, <c>SourceEventSyncJob</c>).
/// </summary>
/// <remarks>
/// ⚠ Потрібен із тієї самої причини, що й решта маркерів: планувальник приймає
/// ТИП, а прикладний шар не бачить реалізацій з інфраструктури. Ціль злиття —
/// <see cref="SourceEventSyncTarget.Of"/>: дві постановки на ту саму сутність — це
/// одна робота (<see cref="IBackgroundJobScheduler.EnqueueCoalescedAsync{TJob}"/>).
/// </remarks>
public interface ISourceEventSyncJob : IBackgroundJob;

/// <summary>Завдання на синхронізацію подій сутності-шаблону.</summary>
/// <param name="SourceEntityId">Сутність джерела — шаблон подій.</param>
/// <param name="FromUtc">Початок вікна; <c>null</c> — за <c>LookbackDays</c> розкладу.</param>
/// <param name="ToUtc">Кінець вікна, виключно; <c>null</c> — «зараз».</param>
/// <param name="ConfirmRemoval">
/// Ручне підтвердження масового видалення подій, зниклих з джерела (ліміт «повної звірки»): ставить лише обробник з
/// правом <c>Integration.Manage</c>; розклад і збір його не ставлять.
/// </param>
public sealed record SourceEventSyncRequest(
    int SourceEntityId, DateTime? FromUtc = null, DateTime? ToUtc = null, bool ConfirmRemoval = false)
{
    private static readonly System.Text.Json.JsonSerializerOptions Options =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>Розбирає завдання черги: типізоване або JSON.</summary>
    /// <param name="payload">Завдання.</param>
    /// <returns>Завдання з ненульовою сутністю.</returns>
    public static SourceEventSyncRequest Parse(object? payload)
    {
        if (payload is SourceEventSyncRequest typed)
        {
            return Validate(typed);
        }

        var json = payload as string ?? System.Text.Json.JsonSerializer.Serialize(payload);
        var request = System.Text.Json.JsonSerializer.Deserialize<SourceEventSyncRequest>(json, Options)
                      ?? throw new InvalidOperationException(
                          "Завдання синхронізації подій не розбирається: невідома форма payload.");

        return Validate(request);
    }

    // ⛔ Нульова сутність — не «синхронізувати всі»: задача без адресата мовчки не
    // робила б нічого, і побачити це можна було б лише за порожнім реєстром.
    private static SourceEventSyncRequest Validate(SourceEventSyncRequest request)
        => request.SourceEntityId > 0
            ? request
            : throw new InvalidOperationException(
                "Завдання синхронізації подій не називає сутності джерела: синхронізувати нічого.");
}

/// <summary>Ціль злиття завдань синхронізації подій.</summary>
public static class SourceEventSyncTarget
{
    /// <summary>Ціль «сутність-шаблон подій».</summary>
    /// <param name="sourceEntityId">Сутність джерела.</param>
    /// <returns>Ключ цілі.</returns>
    public static string Of(int sourceEntityId)
        => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"source-events-e{sourceEntityId}");

    /// <summary>
    /// Ціль підтвердженого масового видалення: власна, щоб злиття з уже поставленою звичайною синхронізацією не
    /// губило прапор підтвердження.
    /// </summary>
    /// <param name="sourceEntityId">Сутність джерела.</param>
    /// <returns>Ключ цілі.</returns>
    public static string OfConfirmed(int sourceEntityId)
        => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"source-events-e{sourceEntityId}-confirm");
}
