// src/Ecr.Application/Localization/GetUiStringsHandler.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;

namespace Ecr.Application.Localization;

/// <summary>
/// Каталог рядків інтерфейсу за мовою і областю (ФВ-14.9, D-95, D-114).
/// </summary>
/// <remarks>
/// Область `Public` віддається **анонімно** — сторінка входу потребує підписів
/// кнопок раніше, ніж хтось автентифікований. `Private` — після входу, бо
/// назви адміністративних областей не мають бути видимі невідомому
/// відвідувачу (ФВ-14.2).
/// </remarks>
public sealed class GetUiStringsHandler(IUiStringCatalog catalog, ICurrentUser currentUser)
{
    /// <summary>Повертає каталог області з розгорнутим fallback.</summary>
    /// <param name="languageCode">Мова інтерфейсу.</param>
    /// <param name="publicOnly">Чи потрібна лише публічна область.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="AccessDeniedException">Приватна область для анонімного запиту.</exception>
    public async Task<UiStringCatalog> HandleAsync(string languageCode, bool publicOnly, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(languageCode);

        var scope = publicOnly ? UiStringScope.Public : UiStringScope.Private;

        // ⚠ Перевірка стоїть ТУТ, а не лише в контролері. Контролер вирішує,
        // яку область просили; правило «приватне — лише після входу» (ФВ-14.2)
        // не має залежати від того, чи не забули атрибут на новому маршруті.
        if (scope == UiStringScope.Private && currentUser.UserId is null)
        {
            throw new AccessDeniedException(
                "ECR-AUTH-0401",
                "Приватна область каталогу доступна лише після входу.");
        }

        // Кеш за ключем {lang}:{scope}:{revision} і сам fallback — у сховищі:
        // тут немає ані з'єднання, ані пам'яті процесу, і не має бути.
        return await catalog.GetScopedAsync(languageCode, scope, ct).ConfigureAwait(false);
    }

    /// <summary><c>ETag</c> області для умовного запиту.</summary>
    /// <param name="publicOnly">Чи публічна область.</param>
    /// <param name="languageCode">Мова.</param>
    /// <param name="revision">Версія каталогу.</param>
    public static string ETag(bool publicOnly, string languageCode, int revision)
        => UiStringResolver.ETag(
            publicOnly ? UiStringScope.Public : UiStringScope.Private, languageCode, revision);
}
