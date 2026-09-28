// src/Ecr.Application/Calculations/RecalculationApprovalHandlers.cs
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;

namespace Ecr.Application.Calculations;

/// <summary>Погодження перерахунку закритого періоду — як його бачить клієнт.</summary>
/// <param name="Id">Ідентифікатор; його передає <c>POST …/recalculate</c> як <c>approvalId</c>.</param>
/// <param name="PeriodKey">Єдиний період, який погодження відкриває.</param>
/// <param name="Reason">Причина.</param>
/// <param name="RequestedByUserId">Ініціатор — єдиний, хто може ним скористатися.</param>
/// <param name="RequestedByName">Ім'я ініціатора.</param>
/// <param name="RequestedAt">Коли створено, UTC.</param>
/// <param name="ExpiresAt">Після цього моменту погодження мертве, UTC.</param>
/// <param name="ConfirmedByUserId">Хто підтвердив; <c>null</c> — ще чекає.</param>
/// <param name="ConfirmedByName">Ім'я того, хто підтвердив.</param>
/// <param name="ConfirmedAt">Коли підтверджено, UTC.</param>
public sealed record RecalculationApprovalDto(
    long Id,
    int PeriodKey,
    string Reason,
    int RequestedByUserId,
    string? RequestedByName,
    DateTime RequestedAt,
    DateTime ExpiresAt,
    int? ConfirmedByUserId,
    string? ConfirmedByName,
    DateTime? ConfirmedAt);

/// <summary>
/// Запит, перелік і підтвердження погоджень перерахунку закритого періоду
/// (ФВ-9.7, аудит безпеки S1).
/// </summary>
/// <remarks>
/// ⛔ Друга людина — це <see cref="ICurrentUser"/> ЇЇ запиту, а не число в тілі
/// чужого. Право — те саме <c>Calculation.Recalculate</c>; ініціатору досить
/// гранта Read на проєкт (як і для самого перерахунку), погоджувачу потрібен
/// Manage — погоджує той, хто за проєкт відповідає, а не будь-хто з правом
/// запускати перерахунок.
/// </remarks>
public sealed class RecalculationApprovalHandlers(
    IRecalculationApprovalStore approvals,
    IPeriodStore periods,
    IAccessDecisionService access,
    IUnitOfWork uow,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Подія журналу безпеки: запит створено.</summary>
    public const string RequestedEventType = "RecalculationApprovalRequested";

    /// <summary>Подія журналу безпеки: запит підтверджено.</summary>
    public const string ConfirmedEventType = "RecalculationApprovalConfirmed";

    /// <summary>Подія журналу безпеки: погодження використано перерахунком.</summary>
    public const string UsedEventType = "RecalculationApprovalUsed";

    /// <summary>Створює запит на погодження від імені поточного користувача.</summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="periodKey">Закритий період, який треба перерахувати.</param>
    /// <param name="reason">Причина; обов'язкова.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<RecalculationApprovalDto> RequestAsync(
        int projectId, int periodKey, string? reason, CancellationToken ct)
    {
        var userId = await RequireAsync(projectId, GrantLevel.Read, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > RecalculationApproval.ReasonMaxLength)
        {
            throw new BusinessRuleException(
                "ECR-CALC-4221",
                "Погодження перерахунку закритого періоду без причини не приймається.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-CALC-4221.approvalReasonRequired" });
        }

        var targets = await periods.GetPeriodStatesAsync(projectId, periodKey, ct).ConfigureAwait(false);
        if (targets.Count == 0)
        {
            throw new NotFoundException(
                "ECR-PRD-0404", $"Періоду {periodKey} у проєкті {projectId} немає.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-PRD-0404.periodForProject",
                    ["periodKey"] = periodKey.ToString(CultureInfo.InvariantCulture),
                    ["projectId"] = projectId.ToString(CultureInfo.InvariantCulture),
                });
        }

        var approval = new RecalculationApproval(projectId, periodKey, reason.Trim(), userId, clock.UtcNow);
        approvals.Add(approval);
        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        await WriteAuditAsync(RequestedEventType, approval.Id, projectId, periodKey, userId, ct).ConfigureAwait(false);

        return await approvals.FindAsync(approval.Id, projectId, ct).ConfigureAwait(false)
               ?? throw new InvalidOperationException($"Щойно збережене погодження {approval.Id} не читається.");
    }

    /// <summary>Живі погодження проєкту — те, що бачить друга людина.</summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<IReadOnlyList<RecalculationApprovalDto>> ListAsync(int projectId, CancellationToken ct)
    {
        await RequireAsync(projectId, GrantLevel.Read, ct).ConfigureAwait(false);
        return await approvals.ListActiveAsync(projectId, clock.UtcNow, ct).ConfigureAwait(false);
    }

    /// <summary>Підтверджує чужий запит під сесією поточного користувача.</summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="id">Погодження.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<RecalculationApprovalDto> ConfirmAsync(int projectId, long id, CancellationToken ct)
    {
        var userId = await RequireAsync(projectId, GrantLevel.Manage, ct).ConfigureAwait(false);

        var approval = await approvals.FindAsync(id, projectId, ct).ConfigureAwait(false);
        if (approval is not null && approval.RequestedByUserId == userId)
        {
            throw new BusinessRuleException(
                "ECR-CALC-0409",
                "Погодити власний перерахунок закритого періоду не можна (правило чотирьох очей, D-40).",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-CALC-0409.ownRecalculationApproval" });
        }

        var now = clock.UtcNow;
        if (approval is null || !await approvals.TryConfirmAsync(id, projectId, userId, now, ct).ConfigureAwait(false))
        {
            // ⚠ Одна відмова на «немає», «уже підтверджено», «використано» й
            // «прострочено»: розрізняти їх для чужого проєкту означало б
            // розповідати, які погодження в ньому існують.
            throw new BusinessRuleException(
                "ECR-CALC-0409",
                $"Погодження {id} не чекає підтвердження.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CALC-0409.approvalNotPending",
                    ["approvalId"] = id.ToString(CultureInfo.InvariantCulture),
                });
        }

        await WriteAuditAsync(ConfirmedEventType, id, projectId, approval.PeriodKey, userId, ct).ConfigureAwait(false);

        return await approvals.FindAsync(id, projectId, ct).ConfigureAwait(false) ?? approval;
    }

    /// <summary>Пише подію погодження в журнал безпеки.</summary>
    internal static Task WriteAuditAsync(
        IAuditWriter audit, ICurrentUser currentUser, DateTime at,
        string eventType, long approvalId, int projectId, int periodKey, int byUserId, object? extra, CancellationToken ct)
        => audit.WriteSecurityEventAsync(
            new SecurityEventRecord(
                at, eventType, TargetUserId: null, TargetRoleId: null,
                JsonSerializer.Serialize(new { approvalId, projectId, periodKey, extra }),
                byUserId, currentUser.CorrelationId),
            ct);

    private Task WriteAuditAsync(
        string eventType, long approvalId, int projectId, int periodKey, int byUserId, CancellationToken ct)
        => WriteAuditAsync(audit, currentUser, clock.UtcNow, eventType, approvalId, projectId, periodKey, byUserId, null, ct);

    /// <summary>Право <c>Calculation.Recalculate</c> плюс грант на проєкт не нижче <paramref name="level"/>.</summary>
    private async Task<int> RequireAsync(int projectId, GrantLevel level, CancellationToken ct)
    {
        var profile = await PermissionCheck
            .RequireAsync(access, currentUser, RunCalculationHandler.Permission, ct)
            .ConfigureAwait(false);

        if (profile.LevelFor(ResourceKind.Project, projectId) < level)
        {
            var key = level >= GrantLevel.Manage ? "err.ECR-AUTH-0403.noProjectManageGrant" : "err.ECR-AUTH-0403.noProjectGrant";
            throw new AccessDeniedException(
                "ECR-AUTH-0403", $"Немає гранта {level} на проєкт {projectId}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = key,
                    ["projectId"] = projectId.ToString(CultureInfo.InvariantCulture),
                });
        }

        return currentUser.UserId!.Value;
    }
}
