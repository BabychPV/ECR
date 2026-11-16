using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Workflow;

/// <summary>
/// ✎ B75: подання, затвердження й відкликання аркуша версії без погодження (<see cref="WorkflowMode.None"/>) —
/// <c>422</c> з наявним кодом <c>ECR-SUB-4221</c> і ключем <see cref="MessageKey"/>.
/// </summary>
/// <remarks>
/// ⚠ Перевірка — після видимості документа й аркуша (невидимий документ лишається <c>404</c>), але ДО гранта й стану:
/// дії для такої версії немає зовсім, і причина «немає права» тут вводила б в оману.
/// </remarks>
public static class WorkflowModeGuard
{
    /// <summary>Ключ каталогу відмови.</summary>
    public const string MessageKey = "err.ECR-SUB-4221.workflowDisabled";

    /// <summary>Відмовляє, якщо версія документа за період — без погодження.</summary>
    /// <param name="documents">Сховище документів.</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="key">Період.</param>
    /// <param name="action">Дія для тексту відмови.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="BusinessRuleException"><c>ECR-SUB-4221</c>, <see cref="MessageKey"/>.</exception>
    public static async Task EnsureEnabledAsync(
        IDocumentStore documents, long documentId, PeriodKey key, string action, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(documents);

        if (await documents.GetWorkflowModeForPeriodAsync(documentId, key.Value, ct).ConfigureAwait(false)
            != WorkflowMode.None)
        {
            return;
        }

        throw new BusinessRuleException(
            ErrorCodes.SubmitBlocked,
            $"{action}: шаблон документа працює без подання й затвердження — значення одразу доступні звітності.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = MessageKey,
                ["documentId"] = documentId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["periodKey"] = key.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
    }
}
