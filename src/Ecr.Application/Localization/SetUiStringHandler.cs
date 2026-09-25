// src/Ecr.Application/Localization/SetUiStringHandler.cs
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Errors;

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

    /// <summary>
    /// Стеля довжини тексту (<c>sys_ecr.UiString.Value nvarchar(1000)</c>,
    /// <c>08-system-tables.sql</c>). Той самий ключ каталогу, що й CSV-імпорт
    /// (<c>UiStringCsvHandlers</c>) — той самий факт «довше за N символів»,
    /// незалежно від того, яким шляхом текст дійшов до сервера.
    /// </summary>
    private const int MaxValueLength = 1000;

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
            throw new AccessDeniedException(
                "ECR-AUTH-0403", $"Потрібне право {Permission}.",
                new Dictionary<string, object?> { ["permission"] = Permission });
        }

        // ⛔ Без цих двох перевірок обидва випадки доїжджали до бази: задовге
        // значення на `Value nvarchar(1000)` давало `String or binary data
        // would be truncated`, а невідома мова — порушення `FK_UiString_Lang`.
        // Обидва — необроблений `SqlException`, тобто гола 500-ка замість
        // пояснення, яке поле і чому (B-03).
        if (value.Length > MaxValueLength)
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"Текст перекладу {key} ({languageCode}) довший за {MaxValueLength} символів.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    // Той самий ключ, що й CSV-імпорт того самого поля.
                    ["messageKey"] = "err.ECR-REQ-0422.uiStringTooLong",
                    ["key"] = key,
                    ["maxLength"] = MaxValueLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        if (!await catalog.LanguageExistsAsync(languageCode, ct).ConfigureAwait(false))
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"Мови {languageCode} немає в довіднику.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["messageKey"] = "err.ECR-REQ-0422.uiStringUnknownLanguage",
                    ["lang"] = languageCode,
                });
        }

        await RequireSamePlaceholdersAsync(key, languageCode, value, ct).ConfigureAwait(false);

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

    /// <summary>Переклад зобов'язаний нести ті самі плейсхолдери, що й оригінал (<c>BE-13</c>).</summary>
    /// <remarks>
    /// ⚠ Три випадки перевірку минають, і кожен свідомо. Мова за замовчуванням —
    /// вона сама є еталоном. Порожнє значення — це «зняти переклад»
    /// (<see cref="UiStringResolver.Compose"/> читає його як відсутнє), а не
    /// переклад без плейсхолдерів. Ключ, якого в еталоні немає, звіряти нема з чим.
    /// </remarks>
    private async Task RequireSamePlaceholdersAsync(
        string key, string languageCode, string value, CancellationToken ct)
    {
        if (value.Length == 0
            || string.Equals(languageCode, UiStringResolver.DefaultLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var reference = await catalog.GetAsync(UiStringResolver.DefaultLanguage, ct).ConfigureAwait(false);
        if (!reference.Strings.TryGetValue(key, out var source)
            || UiStringResolver.SamePlaceholders(source, value))
        {
            return;
        }

        // ⚠ Код — наявний `ECR-REQ-0422` з власним messageKey, а не нова родина
        // `ECR-L10N`: нова родина — це рядок таблиці `02-contracts.md` §7 і
        // константа каталогу, тобто спільні файли поза цією підзадачею.
        throw new BusinessRuleException(
            ErrorCodes.RequestInvalid,
            $"Плейсхолдери перекладу {key} ({languageCode}) не збігаються з оригіналом.",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["messageKey"] = "err.ECR-REQ-0422.placeholderMismatch",
                ["key"] = key,
                ["expected"] = string.Join(", ", UiStringResolver.Placeholders(source)),
                ["actual"] = string.Join(", ", UiStringResolver.Placeholders(value)),
            });
    }
}
