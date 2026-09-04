// src/Ecr.Application/Workflow/ApproveSheetHandler.cs
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;

namespace Ecr.Application.Workflow;

/// <summary>Затвердження або відхилення аркуша за маршрутом (ФВ-5.13…ФВ-5.17).</summary>
public sealed class ApproveSheetHandler(
    IAccessDecisionService access, IUnitOfWork uow, ICurrentUser currentUser, IClock clock)
{
    public Task HandleAsync(long documentId, int sheetDefId, int periodKey, bool approved, string? reason, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) доступ рівня Approve; поточний крок маршруту має відповідати " +
            "   ролі користувача (ФВ-5.17);\n" +
            "2) approved = false → Status = Draft, reason ОБОВ'ЯЗКОВИЙ (ФВ-5.15);\n" +
            "3) approved = true → якщо є наступний крок, перейти до нього, інакше " +
            "   Status = Approved;\n" +
            "4) при Approved дані стають дійсними (ФВ-5.14) — оновити статус зрізу rpt.*;\n" +
            "5) ⛔ статус документа не чіпати: його немає (D-93).");
}
