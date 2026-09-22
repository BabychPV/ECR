using System.Globalization;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;

namespace Ecr.Domain.Entities.Documents;

/// <summary>Правило стану для зміни бізнес-ключа документа (ФВ-3.9).</summary>
/// <remarks>
/// Поданий чи погоджений аркуш уже бачили погоджувачі під старим ключем: зміна ключа
/// тоді переписала б те, що вони підписали. Чернетка й відхилений аркуш — у роботі.
/// </remarks>
public static class DocumentKeyChange
{
    /// <summary>Кидає, якщо хоч один аркуш документа поданий або погоджений.</summary>
    /// <exception cref="DomainException"><c>ECR-DOC-0409</c>, <c>rekeyLocked</c>.</exception>
    public static void EnsureChangeable(IReadOnlyCollection<ApprovalState> sheetStates)
    {
        ArgumentNullException.ThrowIfNull(sheetStates);

        var locked = sheetStates
            .Where(s => s.Status is DocumentStatus.Submitted or DocumentStatus.Approved)
            .OrderByDescending(s => s.Status == DocumentStatus.Approved)
            .FirstOrDefault();

        if (locked is not null)
        {
            throw new DomainException(
                ErrorCodes.DocumentSubmitted,
                $"Ключ не змінюється: аркуш {locked.SheetDefId} за період {locked.PeriodKey} — {locked.Status}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-DOC-0409.rekeyLocked",
                    ["reason"] = locked.Status.ToString(),
                    ["sheetDefId"] = locked.SheetDefId.ToString(CultureInfo.InvariantCulture),
                    ["periodKey"] = locked.PeriodKey.ToString(CultureInfo.InvariantCulture),
                });
        }
    }
}
