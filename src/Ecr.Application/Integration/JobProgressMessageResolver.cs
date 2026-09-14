// src/Ecr.Application/Integration/JobProgressMessageResolver.cs
using Ecr.Application.Localization;
using Ecr.Application.Ports;

namespace Ecr.Application.Integration;

/// <summary>
/// Резолвить структурований конверт прогресу задачі
/// (<see cref="JobProgressMessageEnvelope"/>) мовою ЧИТАЧА — на відміну від
/// запису, де мова читача ще невідома (детальний контраст —
/// <see cref="JobProgressMessageEnvelope"/>, <c>Q-326</c>).
/// </summary>
public static class JobProgressMessageResolver
{
    /// <summary>
    /// Резолвить <paramref name="rawMessage"/>.
    /// </summary>
    /// <param name="catalog">Каталог рядків.</param>
    /// <param name="languageCode">Мова читача (<c>ICurrentUser.Language</c>).</param>
    /// <param name="rawMessage">Сирий вміст <c>itg.JobProgress.Message</c>; <c>null</c> — без повідомлення.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>
    /// Текст мовою читача — якщо <paramref name="rawMessage"/> є структурованим
    /// конвертом (<c>Q-326</c>). Інакше <paramref name="rawMessage"/> повертається
    /// БЕЗ ЗМІН: це або стара пряма форма запису ДО цієї картки (готовий
    /// український текст — лишається читабельним як є), або дані, а не текст
    /// (<c>exportId</c> у повідомленні <c>ExcelExportJob</c> на 100 %).
    /// </returns>
    /// <remarks>
    /// ⛔ Будь-який збій самого резолву (каталог чи база недоступні) ковтається
    /// — той самий принцип, що й <c>HealthCatalogText</c> (<c>Q-304</c>):
    /// читання стану задачі не має падати вдруге через локалізацію, а сирий
    /// конверт (JSON із ключем) лишається кориснішим за виняток на
    /// <c>GET /api/v1/jobs/{jobId}</c>.
    /// </remarks>
    public static async Task<string?> ResolveAsync(
        IUiStringCatalog catalog, string languageCode, string? rawMessage, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        if (rawMessage is null || !JobProgressMessageCodec.TryDecode(rawMessage, out var envelope))
        {
            return rawMessage;
        }

        try
        {
            var strings = await catalog.GetScopedAsync(languageCode, UiStringScope.Private, ct)
                .ConfigureAwait(false);

            return Resolve(envelope, strings);
        }
#pragma warning disable CA1031 // Причина — у ⛔ вище: друга відмова тут гірша за сирий конверт.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return rawMessage;
        }
    }

    /// <summary>Резолвить один рівень конверта, рекурсивно розгортаючи <see cref="JobProgressMessageEnvelope.Inner"/>.</summary>
    private static string Resolve(JobProgressMessageEnvelope envelope, UiStringCatalog strings)
    {
        var template = UiStringResolver.Resolve(strings, envelope.Key);

        var parameters = envelope.Params is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(envelope.Params, StringComparer.Ordinal);

        if (envelope.Inner is not null)
        {
            // ⚠ `{message}` — зарезервоване ім'я параметра для вкладеного
            // повідомлення нижчого шару композиції. Явний параметр із таким
            // самим ім'ям у `envelope.Params` (якого в жодному наявному
            // ключі каталогу немає) був би тихо перезаписаний — прийнятний
            // компроміс заради простоти рекурсії без окремого механізму.
            parameters["message"] = Resolve(envelope.Inner, strings);
        }

        return UiStringResolver.Format(template, parameters);
    }
}
