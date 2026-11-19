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
    public static async Task EnsureConfirmedAsync(
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
        CancellationToken ct)
    {
        if (assessor is null)
        {
            return;
        }

        var impact = await assessor.AssessAsync(documentId, sheetDefId, periodKey, change, ct).ConfigureAwait(false);
        if (!impact.Required)
        {
            return;
        }

        var action = change == SourceApprovalChange.Approved ? "Затвердження" : "Повернення в роботу";
        if (impact.Policy == DocumentSourcePolicy.Block)
        {
            throw new BusinessRuleException(
                ErrorCodes.SourceImpactRefused,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{action} неможливе: політика зв'язки забороняє, доки затверджено {impact.ApprovedCount} залежних періодів."),
                Details(impact, BlockedMessageKey));
        }

        if (string.IsNullOrWhiteSpace(confirmation?.Hash))
        {
            throw new BusinessRuleException(
                ErrorCodes.SourceImpactConflict,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{action} зачепить {impact.ApprovedCount} затверджених залежних періодів: потрібне підтвердження."),
                Details(impact, ConfirmationRequiredMessageKey));
        }

        if (!string.Equals(confirmation.Hash, impact.Hash, StringComparison.Ordinal))
        {
            throw new BusinessRuleException(
                ErrorCodes.SourceImpactConflict,
                "Вплив змінився після підтвердження: перегляньте його ще раз.",
                Details(impact, ImpactChangedMessageKey));
        }

        var reason = confirmation.Reason?.Trim();
        if (impact.Policy == DocumentSourcePolicy.ConfirmWithReason && string.IsNullOrEmpty(reason))
        {
            throw new BusinessRuleException(
                ErrorCodes.SourceImpactRefused,
                "Потрібна причина підтвердження.",
                Details(impact, ReasonRequiredMessageKey));
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
                        reason,
                    }),
                    userId,
                    correlationId),
                ct).ConfigureAwait(false);
        }
    }

    /// <summary>Тіло відмови: ключ, поточний відбиток (клієнт підтверджує саме його) і числа впливу.</summary>
    private static Dictionary<string, object?> Details(SourceImpact impact, string messageKey)
        => new()
        {
            ["messageKey"] = messageKey,
            ["impactHash"] = impact.Hash,
            ["policy"] = impact.Policy.ToString(),
            ["approvedDependents"] = impact.ApprovedCount.ToString(CultureInfo.InvariantCulture),
            ["dependents"] = impact.Consumers.Count.ToString(CultureInfo.InvariantCulture),
        };
}
