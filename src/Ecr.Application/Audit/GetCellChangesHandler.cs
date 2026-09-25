using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Errors;

namespace Ecr.Application.Audit;

/// <summary>
/// Історія змін комірок. Право <c>Security.ViewAudit</c> — або
/// <c>Document.View</c>, коли запит адресує РІВНО ОДНУ комірку (D15-16).
/// </summary>
public sealed class GetCellChangesHandler(
    IAuditReader audit, IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Право, без якого ЗАГАЛЬНИЙ журнал не віддається.</summary>
    public const string Permission = "Security.ViewAudit";

    /// <summary>
    /// Право, якого достатньо для історії ОДНІЄЇ комірки (D15-16).
    /// </summary>
    /// <remarks>
    /// ⛔ Два рівні доступу в одній дії — рішення, а не недогляд.
    /// <c>Security.ViewAudit</c> — централізоване комплаєнс-право (Q-177):
    /// воно дає наскрізний журнал по ВСІХ проєктах, і роздавати його кожному
    /// операторові заради вкладки History в інспекторі комірки означало б
    /// віддати разом із нею весь журнал системи. Натомість питання «що було з
    /// ЦИМ числом» — це питання про документ, який людина й так бачить, тож
    /// поріг для нього — <c>Document.View</c> плюс той самий грант на проєкт,
    /// що й для читання зрізу.
    ///
    /// ⚠ Межа проходить за <see cref="CellChangeFilter.IsSingleCell"/>, тобто
    /// за ПОВНОЮ адресою комірки. Послабити її до «є documentId» означало б
    /// віддати власникові <c>Document.View</c> увесь журнал документа — це вже
    /// не історія комірки, а журнал аудиту з іншим порогом.
    /// </remarks>
    public const string CellHistoryPermission = "Document.View";

    /// <summary>
    /// Максимальна ширина вікна.
    /// </summary>
    /// <remarks>
    /// ⚠ Обмеження згори, а не лише знизу. Вікно «за десять років» формально
    /// задане, але читає всі партиції — і одна така кнопка в UI кладе базу
    /// в найгірший момент. 92 дні — квартал із запасом: більше за реальний
    /// запит аудитора, менше за одну партицію-рік.
    /// </remarks>
    public static readonly TimeSpan MaxWindow = TimeSpan.FromDays(92);

    /// <summary>
    /// Максимальна ширина вікна для історії ОДНІЄЇ комірки — 13 місяців.
    /// </summary>
    /// <remarks>
    /// ⚠ Ширше за <see cref="MaxWindow"/> НАВМИСНО і без суперечності з нею.
    /// Стеля в 92 дні захищає від запиту «весь журнал за рік», який читає
    /// мільйони рядків; запит за однією коміркою обмежений не вікном, а
    /// адресою: комірку правлять одиниці разів на рік. 13 місяців — рівно те,
    /// що питає людина («а торік у цьому місяці?»), 92 дні на це не вистачає.
    ///
    /// ⚠ 396 днів, а не «13 × 30»: місяці різної довжини, і рік плюс місяць у
    /// найдовшому розкладі — 396 діб.
    /// </remarks>
    public static readonly TimeSpan MaxCellWindow = TimeSpan.FromDays(396);

    /// <summary>Повертає сторінку змін.</summary>
    /// <param name="filter">Вікно й звуження журналу.</param>
    /// <param name="page">Курсорна пагінація.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<PagedResult<CellChangeView>> HandleAsync(
        CellChangeFilter filter, CursorRequest page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(page);

        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException(
                         "ECR-AUTH-0401", "Потрібна автентифікація.",
                         new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.signInRequired" });

        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);

        // ⛔ ДВА РІВНІ ДОСТУПУ в одній дії (D15-16). Без цієї гілки вкладка
        // History в інспекторі комірки була б порожньою для всіх, крім
        // аудиторів: `Security.ViewAudit` має мізерна частка ролей.
        var single = filter.IsSingleCell;
        if (!profile.Has(Permission) && !(single && profile.Has(CellHistoryPermission)))
        {
            // ⚠ Називається право, якого бракує САМЕ ДЛЯ ЦЬОГО запиту: сказати
            // власникові `Document.View` «потрібне Security.ViewAudit» на
            // запиті історії комірки означало б відправити адміністратора
            // видавати комплаєнс-право заради вкладки в інспекторі.
            var required = single ? CellHistoryPermission : Permission;

            throw new AccessDeniedException(
                "ECR-AUTH-0403", $"Потрібне право {required}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-AUTH-0403.permission",
                    ["permission"] = required,
                });
        }

        // ⛔ `RowKey` унікальний у межах екземпляра таблиці, а не системи:
        // «R1» є в кожному документі. Без `documentId` такий запит зібрав би
        // рядки з чужих документів і виглядав би як відповідь — тому це
        // відмова, а не мовчазне ігнорування фільтра.
        if (filter.IsCellAddressWithoutDocument)
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                "Адреса комірки (rowKey/columnDefId) без documentId не є адресою.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-REQ-0422.auditCellNeedsDocument" });
        }

        var from = filter.From;
        var to = filter.To;

        // ⛔ Родина REQ, а не CELL: суб'єкт відмови — ПАРАМЕТР ЗАПИТУ, а не
        // комірка документа. З `ECR-CELL-0422` відмова журналу аудиту
        // приходила клієнтові в обробник помилок сітки, якої на цьому екрані
        // немає взагалі (`P-25`, рядок 1).
        if (to <= from)
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid, "Кінець вікна аудиту має бути пізнішим за початок.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-REQ-0422.auditWindowOrder" });
        }

        var maxWindow = single ? MaxCellWindow : MaxWindow;
        if (to - from > maxWindow)
        {
            var maxDays = maxWindow.TotalDays.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"Вікно аудиту ширше за {maxDays} днів: запит пішов би по всіх партиціях.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422.auditWindowTooWide",
                    ["maxDays"] = maxDays,
                });
        }

        if (!page.IsValid)
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid, $"Розмір сторінки поза межами 1..{CursorRequest.MaxLimit}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422.pageSizeOutOfRange",
                    ["max"] = CursorRequest.MaxLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        // ⛔ Грант на проєкт (Q-177, аудит фази 2) — лише коли `documentId`
        // ЗАДАНО. Без нього запит іде по ВСІХ документах/проєктах — і це
        // НАВМИСНО, підтверджено рішенням людини: `Security.ViewAudit` —
        // централізоване/комплаєнс-право поза межами проєктів, не «бачить
        // лише свої проєкти». Звузити цей випадок до гранта означало б
        // зламати саме призначення права. Доведено сценарієм
        // `DataEntryScenarios.Журнал_аудиту_без_documentId_наскрізний_за_призначенням`:
        // `stranger` без ЖОДНОГО гранта, лише з `Security.ViewAudit`,
        // отримує `200` на запит без `documentId`.
        //
        // Тут — вужчий і однозначний випадок: КОНКРЕТНИЙ `documentId` мусить
        // належати проєкту, на який запитувач має грант, так само як для
        // читання самого документа.
        //
        // ⚠ Для історії ОДНІЄЇ комірки ця сама перевірка — не додаткова, а
        // ЄДИНА ресурсна: право `Document.View` каже «ця людина взагалі
        // працює з документами», грант каже — з якими.
        if (filter.DocumentId is { } id)
        {
            var read = await access.CanReadDocumentAsync(profile, id, ct).ConfigureAwait(false);
            if (!read.IsAllowed)
            {
                throw new AccessDeniedException(
                    "ECR-AUTH-0403", $"Немає доступу до документа {id}: {read.Reason}.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-AUTH-0403.noDocumentAccess",
                        ["documentId"] = id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["reason"] = read.Reason.ToString(),
                    });
            }
        }

        return await audit.ReadCellChangesAsync(filter, page, ct).ConfigureAwait(false);
    }
}
