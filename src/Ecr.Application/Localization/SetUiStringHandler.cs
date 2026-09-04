// src/Ecr.Application/Localization/SetUiStringHandler.cs
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;

namespace Ecr.Application.Localization;

/// <summary>Зміна рядка каталогу; право <c>System.ManageLocalization</c>.</summary>
public sealed class SetUiStringHandler(
    IUiStringCatalog catalog,
    Security.IAccessDecisionService access,
    IUnitOfWork uow,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Право, без якого каталог не змінюється.</summary>
    public const string Permission = "System.ManageLocalization";

    /// <summary>Записує рядок і повертає нову версію каталогу.</summary>
    /// <param name="key">Ключ.</param>
    /// <param name="languageCode">Мова.</param>
    /// <param name="value">Текст.</param>
    /// <param name="scope">Область: 0 — публічна, 1 — приватна.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Нова версія каталогу; слугує <c>ETag</c>.</returns>
    public async Task<int> HandleAsync(
        string key, string languageCode, string value, byte scope, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(languageCode);
        ArgumentNullException.ThrowIfNull(value);

        // Каталог кодів закритий, і коду «невідоме значення enum» у ньому немає —
        // і не має бути: до цього місця таке значення не доходить (контролер приймає
        // UiStringScope, і модельне зв’язування відсіває чуже число 400-ю).
        // Тут — захист від нового викликача, а не обробка вводу.
        ArgumentOutOfRangeException.ThrowIfGreaterThan(scope, (byte)UiStringScope.Private);

        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException(
                         "ECR-AUTH-0401", "Анонімний запит не може змінювати каталог.");

        // Право перевіряється ТУТ, а не лише політикою на контролері: публічна
        // область каталогу віддається анонімно, тому чужий текст у ній — це текст
        // на сторінці входу для всіх відвідувачів.
        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);
        if (!profile.Has(Permission))
        {
            throw new AccessDeniedException("ECR-AUTH-0403", $"Потрібне право {Permission}.");
        }

        var now = clock.UtcNow;
        var target = (UiStringScope)scope;

        // Запис і інкремент версії — одна операція сховища (R-B7). Розділити їх
        // означало б, що два одночасні записи отримають ту саму версію: другий
        // ETag збігся б із першим при різному вмісті, і клієнт лишився б із
        // застарілими підписами до наступної правки.
        var result = await catalog.SetAsync(
            new UiStringWrite(key, languageCode, value, target, userId, now), ct).ConfigureAwait(false);

        // ⚠ Перехід Private → Public — це рішення про ВИДИМІСТЬ: рядок починає
        // віддаватися анонімно. Такі переходи мають бути видимі з автором
        // (ФВ-14.2), решта правок перекладу — рутина і в аудиті безпеки зайва.
        if (result.PreviousScope == UiStringScope.Private && target == UiStringScope.Public)
        {
            await audit.WriteSecurityEventAsync(
                new SecurityEventRecord(
                    now,
                    "UiStringScopeWidened",
                    TargetUserId: null,
                    TargetRoleId: null,
                    DetailsJson: JsonSerializer.Serialize(new { key, languageCode }),
                    ChangedByUserId: userId,
                    CorrelationId: currentUser.CorrelationId),
                ct).ConfigureAwait(false);
        }

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        return result.Revision;
    }
}
