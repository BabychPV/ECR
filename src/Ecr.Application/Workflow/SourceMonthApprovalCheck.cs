// src/Ecr.Application/Workflow/SourceMonthApprovalCheck.cs
using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Errors;

namespace Ecr.Application.Workflow;

/// <summary>
/// B80 (N2b, R3.1 A3): відмова подання й затвердження аркуша споживача, поки місяць документа-джерела не затверджено
/// (<see cref="ISourceMonthApprovalGate"/>) — той самий код <c>ECR-SUB-4221</c>, що й інші ділові відмови подання
/// (валідація, застарілі методології, C-1), свій <c>messageKey</c>.
/// </summary>
public static class SourceMonthApprovalCheck
{
    /// <summary>Ключ каталогу відмови.</summary>
    public const string MessageKey = "err.ECR-SUB-4221.sourceMonthNotApproved";

    /// <summary>Кидає <see cref="BusinessRuleException"/>, якщо ворота називають незатверджений місяць джерела.</summary>
    /// <param name="gate">Ворота; <c>null</c> (тестові фабрики обробників) — перевірки немає.</param>
    /// <param name="documentId">Документ-споживач.</param>
    /// <param name="periodKey">Період аркуша.</param>
    /// <param name="action">Назва дії для тексту в журналі («Подання», «Затвердження»).</param>
    /// <param name="ct">Токен скасування.</param>
    public static async Task EnsureApprovedAsync(
        ISourceMonthApprovalGate? gate, long documentId, int periodKey, string action, CancellationToken ct)
    {
        if (gate is null)
        {
            return;
        }

        var missing = await gate.FindUnapprovedSourceMonthAsync(documentId, periodKey, ct).ConfigureAwait(false);
        if (missing is null)
        {
            return;
        }

        throw new BusinessRuleException(
            ErrorCodes.SubmitBlocked,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{action} неможливе: місяць {missing.SourcePeriodKey} документа-джерела {missing.SourceDocumentId} ще не затверджено."),
            new Dictionary<string, object?>
            {
                ["messageKey"] = MessageKey,
                ["sourceDocumentId"] = missing.SourceDocumentId.ToString(CultureInfo.InvariantCulture),
                ["sourcePeriodKey"] = missing.SourcePeriodKey.ToString(CultureInfo.InvariantCulture),
                ["sourceProjectCode"] = missing.SourceProjectCode,
                ["sourceDocumentKey"] = missing.SourceDocumentKey,
            });
    }
}
