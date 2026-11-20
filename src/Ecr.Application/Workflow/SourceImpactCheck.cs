// src/Ecr.Application/Workflow/SourceImpactCheck.cs
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Errors;

namespace Ecr.Application.Workflow;

/// <summary>
/// B80 N5 (D-554): сервер сам вимагає підтвердження впливу, коли затвердження чи повернення із затвердженого аркуша
/// документа-джерела зачепить затверджені місяці залежних документів — не лише клієнт.
/// </summary>
/// <remarks>
/// <para>
/// Політика зв'язки: <c>None</c> — без запиту; <c>Warn</c> — потрібне підтвердження (відбиток впливу); <c>ConfirmWithReason</c> —
/// підтвердження з причиною (в аудит); <c>Block</c> — відмова. Підтвердження діє лише на актуальний вплив: відбиток
/// звіряється з тим, що сервер рахує ЗАРАЗ, у транзакції дії.
/// </para>
/// <para>
/// ⚠ Розглядаються лише затверджені залежні періоди (<see cref="SourceImpact.Required"/>): незатверджені перерахуються
/// самі, затверджені перерахунок не чіпає — вони тихо застаріли б.
/// </para>
/// </remarks>
public static class SourceImpactCheck
{
    /// <summary>Тип події журналу безпеки: людина підтвердила вплив.</summary>
    public const string ConfirmedEventType = "SourceImpactConfirmed";

    /// <summary>Ключ каталогу: політика <c>Block</c>.</summary>
    public const string BlockedMessageKey = "err.ECR-SRC-0422.sourceImpactBlocked";

    /// <summary>Ключ каталогу: за <c>ConfirmWithReason</c> немає причини.</summary>
    public const string ReasonRequiredMessageKey = "err.ECR-SRC-0422.impactReasonRequired";

    /// <summary>Ключ каталогу: підтвердження немає.</summary>
    public const string ConfirmationRequiredMessageKey = "err.ECR-SRC-0409.impactConfirmationRequired";

    /// <summary>Ключ каталогу: підтвердження на застарілий вплив.</summary>
    public const string ImpactChangedMessageKey = "err.ECR-SRC-0409.impactChanged";

    /// <summary>Ключ каталогу: «перерахувати затверджені» без причини.</summary>
    public const string RecalculateApprovedReasonRequiredMessageKey = "err.ECR-SRC-0422.recalculateApprovedReasonRequired";

    /// <summary>Право на явний перерахунок і ЗАТВЕРДЖЕНИХ залежних (рішення людини 19.11; лише SystemAdministrator за умовчанням).</summary>
    public const string RecalculateApprovedPermission = "Calculation.RecalculateApprovedDependents";

    /// <summary>Перевіряє підтвердження; кидає <see cref="BusinessRuleException"/> (409/422) чи пише аудит підтвердження.</summary>
    /// <param name="assessor">Оцінювач; <c>null</c> (тестові фабрики обробників) — перевірки немає.</param>
    /// <param name="audit">Журнал безпеки (запис підтвердження); <c>null</c> — без запису.</param>
    /// <param name="documentId">Документ-джерело.</param>
    /// <param name="sheetDefId">Аркуш.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="change">Дія над джерелом.</param>
    /// <param name="confirmation">Підтвердження клієнта; <c>null</c> — немає.</param>
    /// <param name="userId">Хто діє.</param>
    /// <param name="now">Момент (UTC).</param>
    /// <param name="correlationId">Кореляція запиту.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <param name="mayRecalculateApproved">Чи має викликач право <see cref="RecalculateApprovedPermission"/>.</param>
    /// <returns>
    /// Перекриття «перерахувати й затверджені» (хто й чому) — коли воно запитане, дозволене й має сенс (є затверджені
    /// залежні); інакше <c>null</c>.
    /// </returns>
    public static async Task<ApprovedRecalculationOverride?> EnsureConfirmedAsync(
        ISourceImpactAssessor? assessor,
        IAuditWriter? audit,
        long documentId,
        int sheetDefId,
        int periodKey,
        SourceApprovalChange change,
        SourceImpactConfirmation? confirmation,
        int userId,
        DateTime now,
        string? correlationId,
        CancellationToken ct,
        bool mayRecalculateApproved = false)
    {
        if (assessor is null)
        {
            return null;
        }

        var impact = await assessor.AssessAsync(documentId, sheetDefId, periodKey, change, ct).ConfigureAwait(false);
        var overrideRequested = confirmation?.RecalculateApproved == true && impact.ApprovedCount > 0;
        if (!impact.Required && !overrideRequested)
        {
            return null;
        }

        var action = change == SourceApprovalChange.Approved ? "Затвердження" : "Повернення в роботу";
        if (impact.Policy == DocumentSourcePolicy.Block)
        {
            throw new BusinessRuleException(
                ErrorCodes.SourceImpactRefused,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{action} неможливе: політика зв'язки забороняє, доки затверджено {impact.ApprovedCount} залежних періодів."),
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-SRC-0422.sourceImpactBlocked",
                    ["impactHash"] = impact.Hash,
                    ["policy"] = impact.Policy.ToString(),
                    ["approvedDependents"] = impact.ApprovedCount.ToString(CultureInfo.InvariantCulture),
                    ["dependents"] = impact.Consumers.Count.ToString(CultureInfo.InvariantCulture),
                });
        }

        if (string.IsNullOrWhiteSpace(confirmation?.Hash))
        {
            throw new BusinessRuleException(
                ErrorCodes.SourceImpactConflict,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{action} зачепить {impact.ApprovedCount} затверджених залежних періодів: потрібне підтвердження."),
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-SRC-0409.impactConfirmationRequired",
                    ["impactHash"] = impact.Hash,
                    ["policy"] = impact.Policy.ToString(),
                    ["approvedDependents"] = impact.ApprovedCount.ToString(CultureInfo.InvariantCulture),
                    ["dependents"] = impact.Consumers.Count.ToString(CultureInfo.InvariantCulture),
                });
        }

        if (!string.Equals(confirmation.Hash, impact.Hash, StringComparison.Ordinal))
        {
            throw new BusinessRuleException(
                ErrorCodes.SourceImpactConflict,
                "Вплив змінився після підтвердження: перегляньте його ще раз.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-SRC-0409.impactChanged",
                    ["impactHash"] = impact.Hash,
                    ["policy"] = impact.Policy.ToString(),
                    ["approvedDependents"] = impact.ApprovedCount.ToString(CultureInfo.InvariantCulture),
                    ["dependents"] = impact.Consumers.Count.ToString(CultureInfo.InvariantCulture),
                });
        }

        var reason = confirmation.Reason?.Trim();

        // ✎ B80 N5: «перерахувати також затверджені» — лише з правом, причина обов'язкова незалежно від політики.
        if (overrideRequested)
        {
            if (!mayRecalculateApproved)
            {
                throw new AccessDeniedException(
                    ErrorCodes.AccessDenied,
                    $"Потрібне право {RecalculateApprovedPermission}.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-ACCS-0403.recalculateApproved",
                        ["permission"] = RecalculateApprovedPermission,
                    });
            }

            if (string.IsNullOrEmpty(reason))
            {
                throw new BusinessRuleException(
                    ErrorCodes.SourceImpactRefused,
                    "Потрібна причина перерахунку затверджених залежних.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-SRC-0422.recalculateApprovedReasonRequired",
                        ["impactHash"] = impact.Hash,
                        ["policy"] = impact.Policy.ToString(),
                        ["approvedDependents"] = impact.ApprovedCount.ToString(CultureInfo.InvariantCulture),
                        ["dependents"] = impact.Consumers.Count.ToString(CultureInfo.InvariantCulture),
                    });
            }
        }

        if (impact.Policy == DocumentSourcePolicy.ConfirmWithReason && string.IsNullOrEmpty(reason))
        {
            throw new BusinessRuleException(
                ErrorCodes.SourceImpactRefused,
                "Потрібна причина підтвердження.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-SRC-0422.impactReasonRequired",
                    ["impactHash"] = impact.Hash,
                    ["policy"] = impact.Policy.ToString(),
                    ["approvedDependents"] = impact.ApprovedCount.ToString(CultureInfo.InvariantCulture),
                    ["dependents"] = impact.Consumers.Count.ToString(CultureInfo.InvariantCulture),
                });
        }

        if (audit is not null)
        {
            await audit.WriteSecurityEventAsync(
                new SecurityEventRecord(
                    now,
                    ConfirmedEventType,
                    TargetUserId: null,
                    TargetRoleId: null,
                    JsonSerializer.Serialize(new
                    {
                        action = change == SourceApprovalChange.Approved ? "approve" : "reopen",
                        sourceDocumentId = documentId,
                        sheetDefId,
                        periodKey,
                        policy = impact.Policy.ToString(),
                        impactHash = impact.Hash,
                        approvedDependents = impact.ApprovedCount,
                        dependents = impact.Consumers.Count,
                        recalculateApproved = overrideRequested,
                        reason,
                    }),
                    userId,
                    correlationId),
                ct).ConfigureAwait(false);
        }

        return overrideRequested ? new ApprovedRecalculationOverride(userId, reason!) : null;
    }
}
