// src/Ecr.Application/Workflow/SourceCascadePendingCheck.cs
using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Errors;

namespace Ecr.Application.Workflow;

/// <summary>
/// F5 (рішення людини «Заборонити, поки йде перерахунок»): відмова подання й затвердження аркуша споживача, поки його
/// місяць чекає перерахунку в каскаді від зміни документа-джерела (<see cref="ISourceCascadePendingGate"/>) — той самий
/// код <c>ECR-SUB-4221</c>, що й інші ділові відмови подання (B80 <c>sourceMonthNotApproved</c>), свій <c>messageKey</c>.
/// </summary>
/// <remarks>
/// Відхилення, відкликання й повернення в роботу не блокуються: вони не закріплюють чисел.
/// </remarks>
public static class SourceCascadePendingCheck
{
    /// <summary>Ключ каталогу відмови.</summary>
    public const string MessageKey = "err.ECR-SUB-4221.sourceCascadePending";

    /// <summary>Кидає <see cref="BusinessRuleException"/>, якщо місяць документа ще чекає каскаду від зміни джерела.</summary>
    /// <param name="gate">Ворота; <c>null</c> (тестові фабрики обробників) — перевірки немає.</param>
    /// <param name="documentId">Документ-споживач.</param>
    /// <param name="periodKey">Період аркуша.</param>
    /// <param name="action">Назва дії для тексту в журналі («Подання», «Затвердження»).</param>
    /// <param name="ct">Токен скасування.</param>
    public static async Task EnsureNotPendingAsync(
        ISourceCascadePendingGate? gate, long documentId, int periodKey, string action, CancellationToken ct)
    {
        if (gate is null || !await gate.IsPendingAsync(documentId, periodKey, ct).ConfigureAwait(false))
        {
            return;
        }

        throw new BusinessRuleException(
            ErrorCodes.SubmitBlocked,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{action} неможливе: період {periodKey} документа {documentId} ще перераховується після зміни документа-джерела."),
            new Dictionary<string, object?>
            {
                ["messageKey"] = MessageKey,
                ["periodKey"] = periodKey.ToString(CultureInfo.InvariantCulture),
            });
    }
}
