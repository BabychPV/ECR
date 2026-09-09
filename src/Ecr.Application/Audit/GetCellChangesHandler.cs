using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Errors;

namespace Ecr.Application.Audit;

/// <summary>Історія змін комірок. Право <c>Security.ViewAudit</c>.</summary>
public sealed class GetCellChangesHandler(
    IAuditReader audit, IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Право, без якого журнал не віддається.</summary>
    public const string Permission = "Security.ViewAudit";

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

    /// <summary>Повертає сторінку змін.</summary>
    /// <param name="from">Початок вікна в UTC.</param>
    /// <param name="to">Кінець вікна в UTC.</param>
    /// <param name="documentId">Фільтр за документом; <c>null</c> — усі.</param>
    /// <param name="page">Курсорна пагінація.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<PagedResult<CellChangeView>> HandleAsync(
        DateTime from, DateTime to, long? documentId, CursorRequest page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);

        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException("ECR-AUTH-0401", "Потрібна автентифікація.");

        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);
        if (!profile.Has(Permission))
        {
            throw new AccessDeniedException("ECR-AUTH-0403", $"Потрібне право {Permission}.");
        }

        // ⛔ Родина REQ, а не CELL: суб'єкт відмови — ПАРАМЕТР ЗАПИТУ, а не
        // комірка документа. З `ECR-CELL-0422` відмова журналу аудиту
        // приходила клієнтові в обробник помилок сітки, якої на цьому екрані
        // немає взагалі (`P-25`, рядок 1).
        if (to <= from)
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid, "Кінець вікна аудиту має бути пізнішим за початок.");
        }

        if (to - from > MaxWindow)
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"Вікно аудиту ширше за {MaxWindow.TotalDays:F0} днів: запит пішов би по всіх партиціях.",
                new Dictionary<string, object?> { ["maxDays"] = MaxWindow.TotalDays });
        }

        if (!page.IsValid)
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid, $"Розмір сторінки поза межами 1..{CursorRequest.MaxLimit}.");
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
        if (documentId is { } id)
        {
            var read = await access.CanReadDocumentAsync(profile, id, ct).ConfigureAwait(false);
            if (!read.IsAllowed)
            {
                throw new AccessDeniedException(
                    "ECR-AUTH-0403", $"Немає доступу до документа {id}: {read.Reason}.");
            }
        }

        return await audit.ReadCellChangesAsync(from, to, documentId, page, ct).ConfigureAwait(false);
    }
}
