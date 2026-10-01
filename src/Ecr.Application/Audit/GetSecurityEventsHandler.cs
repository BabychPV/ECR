using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Errors;

namespace Ecr.Application.Audit;

/// <summary>
/// Журнал подій безпеки (<c>aud.SecurityEvent</c>): права, ролі, відмови в доступі
/// (<c>AccessDenied</c>, ФВ-5.24), підтвердження зауважень подання. Право <c>Security.ViewAudit</c>.
/// </summary>
/// <remarks>
/// ⚠ Правила ті самі, що в <see cref="GetStructureChangesHandler"/>: право ПЕРЕД будь-яким
/// читанням, вікно обов'язкове й обмежене <see cref="MaxWindow"/>, розмір сторінки в межах.
/// Тих самих messageKey (<c>err.ECR-REQ-0422.audit*</c>) досить — окремих текстів немає.
/// </remarks>
public sealed class GetSecurityEventsHandler(
    IAuditReader audit, IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Право, без якого журнал не віддається.</summary>
    public const string Permission = GetCellChangesHandler.Permission;

    /// <summary>Максимальна ширина вікна — спільна для всіх журналів аудиту.</summary>
    public static readonly TimeSpan MaxWindow = GetCellChangesHandler.MaxWindow;

    /// <summary>Повертає сторінку подій безпеки.</summary>
    /// <param name="filter">Вікно й звуження журналу.</param>
    /// <param name="page">Курсорна пагінація.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<PagedResult<SecurityEventView>> HandleAsync(
        SecurityEventFilter filter, CursorRequest page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(page);

        await ListTemplatesHandler
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        if (filter.To <= filter.From)
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid, "Кінець вікна аудиту має бути пізнішим за початок.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-REQ-0422.auditWindowOrder" });
        }

        if (filter.To - filter.From > MaxWindow)
        {
            var maxDays = MaxWindow.TotalDays.ToString("F0", CultureInfo.InvariantCulture);
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
            var max = CursorRequest.MaxLimit.ToString(CultureInfo.InvariantCulture);

            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"Розмір сторінки поза межами 1..{max}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422.pageSizeOutOfRange",
                    ["max"] = max,
                });
        }

        return await audit.ReadSecurityEventsAsync(filter, page, ct).ConfigureAwait(false);
    }
}
