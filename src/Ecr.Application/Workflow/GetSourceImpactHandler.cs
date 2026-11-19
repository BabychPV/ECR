// src/Ecr.Application/Workflow/GetSourceImpactHandler.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Workflow;

/// <summary>Залежний документ × період у відповіді оцінки впливу.</summary>
/// <param name="BindingCode">Код зв'язки.</param>
/// <param name="TemplateCode">Код шаблону залежного документа.</param>
/// <param name="DocumentId">Залежний документ.</param>
/// <param name="ProjectCode">Код проєкту.</param>
/// <param name="DocumentKey">Бізнес-ключ документа.</param>
/// <param name="PeriodKey">Період залежного документа.</param>
/// <param name="Approved">Період затверджено (перерахунок його не чіпає, він застаріє).</param>
public sealed record SourceImpactConsumerDto(
    string BindingCode, string TemplateCode, long DocumentId, string ProjectCode, string DocumentKey, int PeriodKey, bool Approved);

/// <summary>Вплив дії над документом-джерелом на залежні документи (B80 N5, D-554).</summary>
/// <param name="Action"><c>approve</c> | <c>reopen</c>.</param>
/// <param name="Policy">Політика: <c>None</c> | <c>Warn</c> | <c>ConfirmWithReason</c> | <c>Block</c>.</param>
/// <param name="Required">Дія потребує підтвердження (чи відмовляє): політика не <c>None</c> і є затверджені залежні.</param>
/// <param name="Blocked">Дію відхилить сервер (<c>Block</c> і є затверджені залежні).</param>
/// <param name="ReasonRequired">Підтвердження потребує причини (<c>ConfirmWithReason</c>).</param>
/// <param name="ImpactHash">Відбиток впливу: його клієнт повертає в <c>confirmedImpact.hash</c>.</param>
/// <param name="SourceMonths">Зачеплені місяці джерела <c>yyyyMM</c>.</param>
/// <param name="DependentCount">Усього залежних періодів.</param>
/// <param name="ApprovedCount">З них затверджених.</param>
/// <param name="Consumers">Залежні, видимі користувачеві.</param>
/// <param name="HiddenCount">Залежних, яких користувач не бачить (лише число).</param>
public sealed record SourceImpactDto(
    string Action,
    string Policy,
    bool Required,
    bool Blocked,
    bool ReasonRequired,
    string ImpactHash,
    IReadOnlyList<int> SourceMonths,
    int DependentCount,
    int ApprovedCount,
    IReadOnlyList<SourceImpactConsumerDto> Consumers,
    int HiddenCount);

/// <summary>
/// B80 N5 (D-554): <c>GET /documents/{id}/source-impact</c> — що зачепить затвердження чи повернення в роботу аркуша
/// документа-джерела: залежні документи й місяці, які вже затверджено, політика зв'язки і відбиток впливу.
/// </summary>
/// <remarks>
/// ⚠ Читання, без змін. Видимість — як у самої дії (документ і аркуш, <c>404</c> інакше); залежні документи, яких читач
/// не бачить, лише лічаться (<see cref="SourceImpactDto.HiddenCount"/>), без назв. Відбиток рахується з повного переліку.
/// </remarks>
public sealed class GetSourceImpactHandler(
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IDocumentStore documents,
    IWorkflowStore workflow,
    ISourceImpactAssessor assessor)
{
    /// <summary>Скільки залежних віддає відповідь найбільше (решта лічиться).</summary>
    public const int MaxConsumers = 200;

    /// <summary>Вплив дії.</summary>
    /// <param name="documentId">Документ-джерело.</param>
    /// <param name="sheetDefId">Аркуш.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="action"><c>approve</c> чи <c>reopen</c>.</param>
    /// <param name="sheetCopyId">Копія аркуша; не <c>null</c> — впливу немає (копія залежних не годує).</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<SourceImpactDto> HandleAsync(
        long documentId, int sheetDefId, int periodKey, string action, long? sheetCopyId, CancellationToken ct)
    {
        var change = action?.ToUpperInvariant() switch
        {
            "APPROVE" => SourceApprovalChange.Approved,
            "REOPEN" => SourceApprovalChange.Reopened,
            _ => throw new BusinessRuleException(
                Ecr.Domain.Errors.ErrorCodes.RequestInvalid,
                "action: approve | reopen.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-REQ-0422.sourceImpactActionInvalid" }),
        };

        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException(
                         "ECR-AUTH-0401", "Потрібна автентифікація.",
                         new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.signInRequired" });
        var key = PeriodKey.Parse(periodKey);
        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);
        await Documents.DocumentVisibility.RequireVisibleAsync(access, profile, documentId, ct).ConfigureAwait(false);
        await Documents.DocumentVisibility
            .RequireSheetVisibleAsync(documents, access, profile, documentId, sheetDefId, key, ct).ConfigureAwait(false);

        // Дія, що джерела не змінює, впливу не має: копія аркуша; повернення НЕ із затвердженого; проміжний крок маршруту
        // (лише остаточне затвердження міняє те, що бачать залежні) — ті самі умови, що й у самої дії.
        var name = change == SourceApprovalChange.Approved ? "approve" : "reopen";
        if (sheetCopyId is not null
            || !await ActionChangesSourceAsync(documentId, sheetDefId, key, change, ct).ConfigureAwait(false))
        {
            return new SourceImpactDto(name, nameof(DocumentSourcePolicy.None), false, false, false, string.Empty, [], 0, 0, [], 0);
        }

        var impact = await assessor.AssessAsync(documentId, sheetDefId, periodKey, change, ct).ConfigureAwait(false);

        var visible = new List<SourceImpactConsumerDto>();
        var hidden = 0;
        var seen = new Dictionary<long, bool>();
        foreach (var c in impact.Consumers)
        {
            if (!seen.TryGetValue(c.DocumentId, out var canRead))
            {
                canRead = (await access.CanReadDocumentAsync(profile, c.DocumentId, ct).ConfigureAwait(false)).IsAllowed;
                seen[c.DocumentId] = canRead;
            }

            if (canRead && visible.Count < MaxConsumers)
            {
                visible.Add(new SourceImpactConsumerDto(
                    c.BindingCode, c.TemplateCode, c.DocumentId, c.ProjectCode, c.DocumentKey, c.PeriodKey, c.Approved));
            }
            else
            {
                hidden++;
            }
        }

        return new SourceImpactDto(
            name,
            impact.Policy.ToString(),
            impact.Required,
            impact.Required && impact.Policy == DocumentSourcePolicy.Block,
            impact.Required && impact.Policy == DocumentSourcePolicy.ConfirmWithReason,
            impact.Hash,
            impact.SourceMonths,
            impact.Consumers.Count,
            impact.ApprovedCount,
            visible,
            hidden);
    }

    private async Task<bool> ActionChangesSourceAsync(
        long documentId, int sheetDefId, PeriodKey key, SourceApprovalChange change, CancellationToken ct)
    {
        var sheets = await workflow.GetSheetsAsync(documentId, key, ct).ConfigureAwait(false);
        var state = sheets.FirstOrDefault(s => s.SheetDefId == sheetDefId && s.SheetCopyId is null);
        if (state is null)
        {
            return false;
        }

        if (change == SourceApprovalChange.Reopened)
        {
            return state.Status == Domain.Enums.DocumentStatus.Approved;
        }

        if (state.Status != Domain.Enums.DocumentStatus.Submitted)
        {
            return false;
        }

        var step = await access.CurrentApprovalStepOfAsync(documentId, sheetDefId, key, null, ct).ConfigureAwait(false);
        return step is not { NextStepId: not null };
    }
}
