using Ecr.Application.Common;
using Ecr.Application.Localization;
using Ecr.Application.Ports;

namespace Ecr.Api.Health;

/// <summary>
/// Резолвить текст health-перевірки каталогом рядків (<c>Q-304</c>).
/// </summary>
/// <remarks>
/// ⛔ До цієї картки `DatabaseHealthCheck`/`JobsHealthCheck`/`SourcesHealthCheck`
/// будували `HealthCheckResult.Description` одразу готовим українським
/// реченням: `Ecr.Expressions` мала ту саму хворобу (`Q-303`) через
/// відсутність DI, але ТУТ причина інша — DI є (перевірки резолвяться в
/// scope запиту, той самий, з якого `DatabaseHealthCheck` уже бере
/// `EcrDbContext`), просто нею не скористались. Український рядок доїжджав
/// до `/admin/health` незалежно від мови інтерфейсу — знайдено живим
/// відкриттям сторінки, не тестом.
///
/// ⚠ `ICurrentUser.Language` працює й для анонімних запитів
/// (`/health/ready`, `/health/live`, D-139) — падає на `Accept-Language`,
/// далі на `en` (`CurrentUser.cs`), тобто виклик тут ніколи не вимагає
/// входу.
///
/// ⛔ Будь-який збій резолву (каталог/база недоступні — саме той стан, який
/// ця перевірка й діагностує) ковтається: health-перевірка звітує про
/// власну відмову, а не падає вдруге під час спроби це сказати гарною
/// мовою. Той самий принцип, що й `ExceptionHandlingMiddleware.
/// LocalizedTitleAsync`.
/// </remarks>
internal static class HealthCatalogText
{
    /// <summary>Резолвить один ключ каталогу; на будь-який збій — англійський запасний варіант.</summary>
    /// <param name="catalog">Каталог рядків.</param>
    /// <param name="currentUser">Поточний користувач (мова); анонімний — теж має мову.</param>
    /// <param name="key">Ключ каталогу, префікс <c>health.</c>.</param>
    /// <param name="fallback">
    /// Англійський текст на випадок відсутності ключа в каталозі чи збою
    /// самого резолву — НЕ на випадок відсутності перекладу мовою користувача
    /// (для цього каталог сам підставляє мову за замовчуванням).
    /// </param>
    /// <param name="parameters">Підстановки <c>{name}</c>; <c>null</c> — без змінних частин.</param>
    /// <param name="ct">Токен скасування.</param>
    public static async Task<string> ResolveAsync(
        IUiStringCatalog catalog,
        ICurrentUser currentUser,
        string key,
        string fallback,
        IReadOnlyDictionary<string, string>? parameters,
        CancellationToken ct)
    {
        try
        {
            var strings = await catalog.GetScopedAsync(currentUser.Language, UiStringScope.Private, ct)
                .ConfigureAwait(false);

            var resolved = UiStringResolver.Resolve(strings, key);

            // Ключа немає в каталозі — `Resolve` повертає сам ключ (D-138-подібний
            // інваріант): це не «переклад відсутній», а «запис поки не заведений»,
            // і англійський fallback тут чесніший за голий `health.db.available`.
            var text = string.Equals(resolved, key, StringComparison.Ordinal) ? fallback : resolved;
            return UiStringResolver.Format(text, parameters);
        }
#pragma warning disable CA1031 // Причина — у ⛔ вище: друга відмова тут гірша за fallback.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return UiStringResolver.Format(fallback, parameters);
        }
    }
}
