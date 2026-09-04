// src/Ecr.Application/Workflow/ReopenDocumentHandler.cs
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;

namespace Ecr.Application.Workflow;

/// <summary>
/// Повернення поданого аркуша в <c>Draft</c> для правки (ФВ-5.20a, D-67).
/// </summary>
/// <remarks>
/// Повторне подання створює **новий** зріз; старий лишається `Submitted`
/// назавжди і не перераховується ніколи (ФВ-9.17). Саме тому правка поданої
/// форми — окрема дія з причиною, а не просто редагування.
/// </remarks>
public sealed class ReopenDocumentHandler(
    IAccessDecisionService access, IUnitOfWork uow, ICurrentUser currentUser, IClock clock)
{
    public Task HandleAsync(long documentId, int sheetDefId, int periodKey, string reason, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) право Document.Reopen (небезпечне, ФВ-6.12);\n" +
            "2) взяти doc.Period з UPDLOCK: при Closed → ECR-PRD-4223. " +
            "   Спершу Reopen ПЕРІОДУ, потім аркуша (ФВ-5.20a);\n" +
            "3) reason обов'язковий;\n" +
            "4) ApprovalState.Reopen(...): Submitted/Approved → Draft;\n" +
            "5) ⛔ старий SubmissionSnapshot НЕ чіпати і не позначати недійсним — " +
            "   він доказова база того, що було подано (ФВ-5.7);\n" +
            "6) усі подальші зміни позначати IsLateEdit = 1 (D-70).");
}
