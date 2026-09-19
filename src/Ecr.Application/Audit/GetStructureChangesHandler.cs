using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Errors;

namespace Ecr.Application.Audit;

/// <summary>
/// Загальний журнал структурних змін (<c>BE-16</c>). Право <c>Security.ViewAudit</c>.
/// </summary>
/// <remarks>
/// ⚠ Один рівень доступу, на відміну від <see cref="GetCellChangesHandler"/>:
/// «історію однієї сутності» вже віддають її власні екрани за власним правом
/// (<c>IAuditReader.ReadStructureChangesAsync</c>), тож тут лишається тільки
/// наскрізний журнал — а він комплаєнс-право (Q-177).
/// </remarks>
public sealed class GetStructureChangesHandler(
    IAuditReader audit, IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Право, без якого журнал не віддається.</summary>
    public const string Permission = GetCellChangesHandler.Permission;

    /// <summary>
    /// Максимальна ширина вікна — та сама, що в журналі комірок.
    /// </summary>
    /// <remarks>
    /// ⚠ <c>aud.StructureChange</c> лежить на тій самій схемі партицій
    /// (<c>ps_AuditByMonth</c>), тож вікно «за десять років» так само читає
    /// кожну партицію. Окрема стеля для двох вкладок одного екрана означала б
    /// одне поле дат із двома різними межами.
    /// </remarks>
    public static readonly TimeSpan MaxWindow = GetCellChangesHandler.MaxWindow;

    /// <summary>Повертає сторінку структурних змін.</summary>
    /// <param name="filter">Вікно й звуження журналу.</param>
    /// <param name="page">Курсорна пагінація.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<PagedResult<StructureChangeView>> HandleAsync(
        StructureChangeFilter filter, CursorRequest page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(page);

        // ⛔ Право ПЕРЕД будь-яким читанням і перед перевіркою параметрів:
        // відмова має бути відмовою, а не «вікно завелике» для чужої людини.
        await ListTemplatesHandler
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        // ⛔ Запит БЕЗ вікна приходить сюди як `from = to = default` і
        // відхиляється цією самою перевіркою: порожнє вікно не «все», а ніщо.
        if (filter.To <= filter.From)
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid, "Кінець вікна аудиту має бути пізнішим за початок.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-REQ-0422.auditWindowOrder" });
        }

        if (filter.To - filter.From > MaxWindow)
        {
            var maxDays = MaxWindow.TotalDays.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
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
            var max = CursorRequest.MaxLimit.ToString(System.Globalization.CultureInfo.InvariantCulture);

            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"Розмір сторінки поза межами 1..{max}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422.pageSizeOutOfRange",
                    ["max"] = max,
                });
        }

        return await audit.ReadStructureJournalAsync(filter, page, ct).ConfigureAwait(false);
    }
}
