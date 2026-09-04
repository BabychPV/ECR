// src/Ecr.Domain/Entities/Workflow/ApprovalState.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;

namespace Ecr.Domain.Entities.Workflow;

/// <summary>
/// Стан робочого процесу на **аркуш × період** (D-38). Це **єдине джерело
/// істини** про статус: скалярного статусу на документі не існує (D-93).
/// </summary>
/// <remarks>
/// 24 аркуші рідко готові одночасно, і чекати найповільніший не має сенсу.
/// Скалярний статус на документі був би другим джерелом, яке рано чи пізно
/// покаже <c>Approved</c> там, де половина аркушів у <c>Draft</c>.
/// </remarks>
public sealed class ApprovalState : Entity<long>
{
    private ApprovalState() { }

    public ApprovalState(long documentId, int sheetDefId, int periodKey)
    {
        DocumentId = documentId;
        SheetDefId = sheetDefId;
        PeriodKey = periodKey;
        Status = DocumentStatus.Draft;
    }

    public long DocumentId { get; private set; }
    public int SheetDefId { get; private set; }
    public int PeriodKey { get; private set; }
    public DocumentStatus Status { get; private set; }
    public int? CurrentStepId { get; private set; }

    public DateTime? SubmittedAt { get; private set; }
    public int? SubmittedByUserId { get; private set; }
    public DateTime? ApprovedAt { get; private set; }
    public int? ApprovedByUserId { get; private set; }
    public string? RejectedReason { get; private set; }

    public DateTime? ReopenedAt { get; private set; }
    public int? ReopenedByUserId { get; private set; }

    /// <summary>Причина `Reopen` — обов'язкова, це перевіряє і база (D-67).</summary>
    public string? ReopenReason { get; private set; }

    /// <summary>Оптимістичне блокування: два подання одного аркуша не змішуються.</summary>
    public byte[] RowVersion { get; private set; } = [];

    /// <summary>
    /// Повернення в <c>Draft</c> для правки поданого. Створює потребу в
    /// **новому** зрізі; старий лишається `Submitted` назавжди (ФВ-5.20a).
    /// </summary>
    public void Reopen(int userId, string reason, DateTime utcNow)
        => throw new NotImplementedException(
            "TODO: 1) Status має бути Submitted або Approved;\n" +
            "2) reason обов'язковий і непорожній;\n" +
            "3) Status = Draft, зафіксувати автора, час і причину;\n" +
            "4) ⚠ перевірку стану ПЕРІОДУ тут НЕ робити — вона в use-case, бо " +
            "вимагає UPDLOCK на doc.Period проти гонки з PeriodStateJob (ФВ-1.10a). " +
            "При Closed періоді use-case поверне ECR-PRD-4223.");
}
