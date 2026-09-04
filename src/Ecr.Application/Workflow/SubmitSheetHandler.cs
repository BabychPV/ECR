// src/Ecr.Application/Workflow/SubmitSheetHandler.cs
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Application.Common;

namespace Ecr.Application.Workflow;

/// <summary>
/// Подання аркуша за період — гранулярність `аркуш × період` (D-38).
/// Створює **іммутабельний зріз** вхідних даних (ФВ-5.7, ФВ-9.4).
/// </summary>
public sealed class SubmitSheetHandler(
    ICellStore cellStore,
    IAccessDecisionService access,
    Validation.ValidationEngine validation,
    IUnitOfWork uow,
    ICurrentUser currentUser,
    IClock clock)
{
    public Task HandleAsync(long documentId, int sheetDefId, int periodKey, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) доступ рівня Submit на аркуш (ФВ-6.13);\n" +
            "2) повна валідація: БУДЬ-ЯКИЙ Error будь-якого рівня блокує (ФВ-5.19), " +
            "   на відміну від запису, де блокує лише комірковий (D-90);\n" +
            "3) рядки з IsOrphaned блокують подання → ECR-SUB-4221 (ФВ-8.13).\n" +
            "   ⚠ До Етапу 4 прапорець ніхто не ставить — перевірка коректна і " +
            "   завжди пропускає; це не несправність;\n" +
            "4) створити SubmissionSnapshot: зліпок значень + версії шаблону, " +
            "   методологій і реєстрів + контрольна сума;\n" +
            "5) wf.ApprovalState: Draft → Submitted, зафіксувати автора і час;\n" +
            "6) оновити статус зрізів rpt.* (ФВ-10.10).");
}
