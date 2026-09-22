using System.Globalization;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;

namespace Ecr.Domain.Entities.Documents;

/// <summary>
/// Правило видалення документа: видаляти можна ЛИШЕ чернетку (рішення людини 2026-09-21).
/// </summary>
/// <remarks>
/// «Чернетка» — кожен рядок <c>wf.ApprovalState</c> документа в <c>Draft</c> І жодного
/// сліду погодження (<c>wf.ApprovalEvent</c>, <c>calc.SubmissionSnapshot</c>). Друга
/// умова не зайва: відкликаний чи повернутий у роботу аркуш знову <c>Draft</c>, але
/// його подання вже бачили погоджувачі й зберіг зріз — такий документ не чернетка.
/// </remarks>
public static class DraftDocumentDeletion
{
    /// <summary>Кидає, якщо документ не чернетка.</summary>
    /// <param name="sheetStates">Усі стани аркуш × період документа.</param>
    /// <param name="hasWorkflowHistory">Чи є в документа події погодження або зрізи подання.</param>
    /// <exception cref="DomainException"><c>ECR-DOC-0409</c> із причиною.</exception>
    public static void EnsureDraft(IReadOnlyCollection<ApprovalState> sheetStates, bool hasWorkflowHistory)
    {
        ArgumentNullException.ThrowIfNull(sheetStates);

        // Найвагоміша причина першою: погоджений аркуш важить більше за поданий.
        var blocking = sheetStates
            .Where(s => s.Status != DocumentStatus.Draft)
            .OrderByDescending(s => Weight(s.Status))
            .FirstOrDefault();

        if (blocking is not null)
        {
            throw new DomainException(
                ErrorCodes.DocumentSubmitted,
                $"Видалити можна лише чернетку; аркуш {blocking.SheetDefId} за період {blocking.PeriodKey} — {blocking.Status}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-DOC-0409.deleteNotDraft",
                    ["reason"] = blocking.Status.ToString(),
                    ["sheetDefId"] = blocking.SheetDefId.ToString(CultureInfo.InvariantCulture),
                    ["periodKey"] = blocking.PeriodKey.ToString(CultureInfo.InvariantCulture),
                });
        }

        if (hasWorkflowHistory)
        {
            throw new DomainException(
                ErrorCodes.DocumentSubmitted,
                "Видалити можна лише чернетку; документ уже проходив погодження.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-DOC-0409.deleteHasHistory",
                    ["reason"] = "WorkflowHistory",
                });
        }
    }

    private static int Weight(DocumentStatus status) => status switch
    {
        DocumentStatus.Approved => 3,
        DocumentStatus.Submitted => 2,
        DocumentStatus.Rejected => 1,
        _ => 0,
    };
}
