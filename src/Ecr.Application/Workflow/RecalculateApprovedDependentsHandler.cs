// src/Ecr.Application/Workflow/RecalculateApprovedDependentsHandler.cs
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Workflow;

/// <summary>Підсумок явного перерахунку затверджених залежних.</summary>
/// <param name="Queued">Скільки задач поставлено (по зв'язці × року × місяцю); 0 — залежних із такою зв'язкою немає.</param>
/// <param name="ApprovedDependents">Скільки затверджених залежних періодів було на момент запиту.</param>
public sealed record RecalculateApprovedDependentsResult(int Queued, int ApprovedDependents);

/// <summary>
/// B80 N5 (D-554, рішення людини 19.11): <c>POST /documents/{id}/source-impact/recalculate-approved</c> — адміністратор з
/// правом <c>Calculation.RecalculateApprovedDependents</c> явно просить перерахувати і ЗАТВЕРДЖЕНІ залежні документи-споживачі
/// аркуша-джерела (після правки комірок). Затверджені аркуші перерахунок звичайно ПРОПУСКАЄ (D-493/D-510); тут — єдиний
/// шлях їх перерахувати, і все логується.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Видимість — як у самої дії (документ і аркуш, <c>404</c> інакше); право — <c>403</c>; причина — <c>422</c>;
/// відбиток впливу застарів — <c>409 impactChanged</c> (людина підтверджує те, що бачила).</item>
/// <item>Аудит: подія журналу безпеки <c>ApprovedDependentsRecalculationRequested</c> (хто, причина, відбиток, затверджені
/// залежні) у цій дії; після прогону — по запису журналу аркушів <c>RecalculatedApproved</c> на кожен фактично
/// перерахований затверджений аркуш (<c>RecalculationJob</c>).</item>
/// </list>
/// </remarks>
public sealed class RecalculateApprovedDependentsHandler(
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IDocumentStore documents,
    ISourceImpactAssessor assessor,
    ISourceApprovalChangeTrigger trigger,
    IAuditWriter audit,
    IClock clock)
{
    /// <summary>Ставить перерахунок затверджених залежних.</summary>
    /// <param name="documentId">Документ-джерело.</param>
    /// <param name="sheetDefId">Аркуш.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="hash">Відбиток впливу (<c>action=edit</c>), який бачила людина.</param>
    /// <param name="reason">Причина; обов'язкова.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<RecalculateApprovedDependentsResult> HandleAsync(
        long documentId, int sheetDefId, int periodKey, string? hash, string? reason, CancellationToken ct)
    {
        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException(
                         "ECR-AUTH-0401", "Потрібна автентифікація.",
                         new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.signInRequired" });
        var key = PeriodKey.Parse(periodKey);
        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);
        await Documents.DocumentVisibility.RequireVisibleAsync(access, profile, documentId, ct).ConfigureAwait(false);
        await Documents.DocumentVisibility
            .RequireSheetVisibleAsync(documents, access, profile, documentId, sheetDefId, key, ct).ConfigureAwait(false);

        if (!PermissionCheck.IsGranted(profile, SourceImpactCheck.RecalculateApprovedPermission))
        {
            throw new AccessDeniedException(
                ErrorCodes.AccessDenied,
                $"Потрібне право {SourceImpactCheck.RecalculateApprovedPermission}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-ACCS-0403.recalculateApproved",
                    ["permission"] = SourceImpactCheck.RecalculateApprovedPermission,
                });
        }

        var trimmed = reason?.Trim();
        var impact = await assessor.AssessAsync(documentId, sheetDefId, periodKey, SourceApprovalChange.Edited, ct)
            .ConfigureAwait(false);
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new BusinessRuleException(
                ErrorCodes.SourceImpactRefused,
                "Потрібна причина перерахунку затверджених залежних.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-SRC-0422.recalculateApprovedReasonRequired",
                    ["impactHash"] = impact.Hash,
                    ["approvedDependents"] = impact.ApprovedCount.ToString(CultureInfo.InvariantCulture),
                    ["dependents"] = impact.Consumers.Count.ToString(CultureInfo.InvariantCulture),
                });
        }

        if (!string.Equals(hash, impact.Hash, StringComparison.Ordinal))
        {
            throw new BusinessRuleException(
                ErrorCodes.SourceImpactConflict,
                "Вплив змінився після підтвердження: перегляньте його ще раз.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-SRC-0409.impactChanged",
                    ["impactHash"] = impact.Hash,
                    ["approvedDependents"] = impact.ApprovedCount.ToString(CultureInfo.InvariantCulture),
                    ["dependents"] = impact.Consumers.Count.ToString(CultureInfo.InvariantCulture),
                });
        }

        var approved = impact.Consumers.Where(c => c.Approved).ToList();
        await audit.WriteSecurityEventAsync(
            new SecurityEventRecord(
                clock.UtcNow,
                "ApprovedDependentsRecalculationRequested",
                TargetUserId: null,
                TargetRoleId: null,
                JsonSerializer.Serialize(new
                {
                    sourceDocumentId = documentId,
                    sheetDefId,
                    periodKey,
                    impactHash = impact.Hash,
                    reason = trimmed,
                    approvedDependents = approved.Select(c => new { c.DocumentId, c.PeriodKey, c.TemplateCode }),
                }),
                userId,
                currentUser.CorrelationId),
            ct).ConfigureAwait(false);

        var queued = approved.Count == 0
            ? 0
            : await trigger.RequestApprovedDependentsRecalculationAsync(
                    documentId, sheetDefId, periodKey, new ApprovedRecalculationOverride(userId, trimmed), ct)
                .ConfigureAwait(false);
        return new RecalculateApprovedDependentsResult(queued, approved.Count);
    }
}
