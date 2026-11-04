using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Errors;

namespace Ecr.Application.Audit;

/// <summary>
/// Журнал подій погодження аркушів (<c>wf.ApprovalEvent</c>): подання, затвердження, відхилення, повернення,
/// відкликання — з коментарем. Право <c>Security.ViewAudit</c>, як у решти вкладок аудиту.
/// </summary>
/// <remarks>
/// ⚠ Правила ті самі, що в <see cref="GetSecurityEventsHandler"/>: право ПЕРЕД будь-яким читанням, вікно
/// обов'язкове й обмежене, розмір сторінки в межах; ті самі messageKey (<c>err.ECR-REQ-0422.audit*</c>).
/// Коментар — звичайний текст (клієнт рендерить його як текст і обрізає в клітинці).
/// </remarks>
public sealed class GetWorkflowEventsHandler(
    IAuditReader audit, IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Право, без якого журнал не віддається.</summary>
    public const string Permission = GetCellChangesHandler.Permission;

    /// <summary>Максимальна ширина вікна — спільна для всіх журналів аудиту.</summary>
    public static readonly TimeSpan MaxWindow = GetCellChangesHandler.MaxWindow;

    /// <summary>Повертає сторінку подій погодження.</summary>
    /// <param name="filter">Вікно й звуження журналу.</param>
    /// <param name="page">Курсорна пагінація.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<PagedResult<WorkflowEventView>> HandleAsync(
        WorkflowEventFilter filter, CursorRequest page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(page);

        await Validate(filter, page, ct).ConfigureAwait(false);

        return await audit.ReadWorkflowEventsAsync(filter, page, ct).ConfigureAwait(false);
    }

    /// <summary>Кількість подій за фільтром (стеля експорту); ті самі право й перевірки вікна.</summary>
    /// <param name="filter">Вікно й звуження журналу.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<int> CountAsync(WorkflowEventFilter filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);

        await Validate(filter, page: null, ct).ConfigureAwait(false);

        return await audit.CountWorkflowEventsAsync(filter, ct).ConfigureAwait(false);
    }

    private async Task Validate(WorkflowEventFilter filter, CursorRequest? page, CancellationToken ct)
    {
        await ListTemplatesHandler
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        AuditWindow.Require(filter.From, filter.To, MaxWindow, page);
    }
}

/// <summary>Спільна перевірка вікна й розміру сторінки журналів аудиту (однакові відмови й messageKey).</summary>
internal static class AuditWindow
{
    internal static void Require(DateTime from, DateTime to, TimeSpan maxWindow, CursorRequest? page)
    {
        if (to <= from)
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid, "Кінець вікна аудиту має бути пізнішим за початок.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-REQ-0422.auditWindowOrder" });
        }

        if (to - from > maxWindow)
        {
            var maxDays = maxWindow.TotalDays.ToString("F0", CultureInfo.InvariantCulture);
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"Вікно аудиту ширше за {maxDays} днів: запит пішов би по всіх партиціях.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422.auditWindowTooWide",
                    ["maxDays"] = maxDays,
                });
        }

        if (page is { IsValid: false })
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
    }
}
