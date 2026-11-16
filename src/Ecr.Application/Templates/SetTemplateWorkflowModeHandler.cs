using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.Services;

namespace Ecr.Application.Templates;

/// <summary>Режим погодження версії шаблону у відповіді <c>PUT …/workflow-mode</c>.</summary>
/// <param name="TemplateVersionId">Версія.</param>
/// <param name="WorkflowMode">Чинний режим.</param>
public sealed record TemplateWorkflowModeDto(int TemplateVersionId, WorkflowMode WorkflowMode);

/// <summary>
/// ✎ B75 (рішення людини 13.11): задає режим погодження версії-чернетки — <see cref="WorkflowMode.SubmitApprove"/> або
/// <see cref="WorkflowMode.None"/> (HSE401 лише збирає значення й віддає їх звітності: без подання й затвердження).
/// </summary>
/// <remarks>
/// ⛔ Той самий захист, що й розкладка (<see cref="SetTemplateLayoutHandler"/>): лише чернетка і лише доки у версії немає
/// документів — в опублікованій режим змінюється новою версією. ⚠ Старі стани погодження в документах версії
/// <see cref="WorkflowMode.None"/> однаково можливі: перенос документа на нову версію переносить <c>wf.ApprovalState</c>.
/// Тому їх ігнорують і рішення про запис (знімок <c>WorkflowNone</c>), і стан аркуша під замком (<c>SheetEditGate</c>) —
/// аркуш None редагується як Draft (Q4).
/// <para>
/// ⛔ Q4 (дизайн B75): версія з власним діючим маршрутом погодження (кроки) у <see cref="WorkflowMode.None"/> не
/// переводиться — маршрут без погодження нічого не означав би (<c>ECR-TMPL-0422</c>, <c>workflowModeHasRoute</c>).
/// </para>
/// </remarks>
public sealed class SetTemplateWorkflowModeHandler(
    ITemplateVersionStore store,
    IMetadataCache metadataCache,
    IAuditWriter audit,
    IUnitOfWork uow,
    IClock clock,
    IAccessDecisionService access,
    Common.ICurrentUser currentUser)
{
    /// <summary>Право на редагування структури версії.</summary>
    public const string Permission = SetTemplateLayoutHandler.Permission;

    /// <summary>Задає режим погодження.</summary>
    /// <param name="templateVersionId">Версія-чернетка.</param>
    /// <param name="mode">Новий режим.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Чинний режим версії.</returns>
    /// <exception cref="NotFoundException">Версії немає.</exception>
    /// <exception cref="ConcurrencyConflictException">У версії вже є документи (<c>ECR-SCHM-0409</c>).</exception>
    /// <exception cref="BusinessRuleException">Невідомий режим або версія має маршрут погодження (<c>ECR-TMPL-0422</c>).</exception>
    public async Task<TemplateWorkflowModeDto> HandleAsync(int templateVersionId, WorkflowMode mode, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var userId = currentUser.UserId
            ?? throw new AccessDeniedException(
                ErrorCodes.Unauthorized,
                "Сесія не містить користувача.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.anonymousWrite" });

        var versionText = templateVersionId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!Enum.IsDefined(mode))
        {
            throw new BusinessRuleException(
                ErrorCodes.TemplateInvalid,
                "Невідомий режим погодження версії шаблону.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-TMPL-0422.workflowModeUnknown",
                    ["versionId"] = versionText,
                });
        }

        var version = await store.GetWithStructureAsync(templateVersionId, ct).ConfigureAwait(false);
        version.EnsureStructurallyMutable();

        var old = version.WorkflowMode;
        if (old == mode)
        {
            return new TemplateWorkflowModeDto(templateVersionId, old);
        }

        if (await store.HasDocumentsAsync(templateVersionId, ct).ConfigureAwait(false))
        {
            throw new ConcurrencyConflictException(
                ErrorCodes.BreakingChange,
                "Режим погодження не можна змінити: у версії вже є документи. Клонуйте версію.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-SCHM-0409.workflowModeBreaking",
                    ["versionId"] = versionText,
                });
        }

        if (mode == WorkflowMode.None
            && await store.HasApprovalRouteStepsAsync(templateVersionId, ct).ConfigureAwait(false))
        {
            throw new BusinessRuleException(
                ErrorCodes.TemplateInvalid,
                "Версія має власний маршрут погодження: без погодження він нічого не означав би. Вимкніть маршрут.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-TMPL-0422.workflowModeHasRoute",
                    ["versionId"] = versionText,
                });
        }

        version.TouchDraft(userId, clock.UtcNow);

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            await DraftVersionLock.EnsureDraftUnderLockAsync(store, version, innerCt).ConfigureAwait(false);

            version.SetWorkflowMode(mode);

            await audit.WriteStructureChangeAsync(
                new StructureChangeRecord(
                    clock.UtcNow, templateVersionId, nameof(TemplateVersion), templateVersionId,
                    ChangeClass.Safe, "Update",
                    Describe(old), Describe(mode), ChangeReason: null,
                    ChangedByUserId: userId, CorrelationId: null),
                innerCt).ConfigureAwait(false);

            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        // Знімок версії несе ознаку `WorkflowNone` (рішення про запис) — скидається, як після розкладки.
        await metadataCache.InvalidateAsync(templateVersionId, ct).ConfigureAwait(false);

        return new TemplateWorkflowModeDto(templateVersionId, mode);
    }

    private static string Describe(WorkflowMode mode)
        => System.Text.Json.JsonSerializer.Serialize(new { WorkflowMode = mode.ToString() });
}
