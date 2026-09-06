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
    /// <param name="userId">Хто відкриває.</param>
    /// <param name="reason">Причина; обов'язкова і тут, і в базі (D-67).</param>
    /// <param name="utcNow">Момент операції.</param>
    /// <exception cref="DomainException">Аркуш у <c>Draft</c> або причина порожня.</exception>
    public void Reopen(int userId, string reason, DateTime utcNow)
    {
        if (Status is not (DocumentStatus.Submitted or DocumentStatus.Approved))
        {
            throw new DomainException(
                "ECR-DOC-0409",
                $"Відкривати можна лише поданий або затверджений аркуш; поточний стан — {Status}.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainException("ECR-DOC-0422", "Причина повернення в роботу обов'язкова.");
        }

        Status = DocumentStatus.Draft;
        ReopenedAt = utcNow;
        ReopenedByUserId = userId;
        ReopenReason = reason;

        // ⚠ Маршрут починається спочатку. Лишити крок означало б, що після
        // правки документ підхопить погодження з середини — тобто перші
        // погоджувачі не побачать змін, які внесли після їхнього підпису.
        CurrentStepId = null;

        // ⚠ Перевірку стану ПЕРІОДУ тут не робимо — вона в use-case, бо
        // вимагає UPDLOCK на doc.Period проти гонки з PeriodStateJob
        // (ФВ-1.10a). При Closed періоді use-case поверне ECR-PRD-4223.
    }

    /// <summary>Подає аркуш на погодження.</summary>
    /// <param name="userId">Хто подає.</param>
    /// <param name="utcNow">Момент подання.</param>
    /// <param name="firstStepId">
    /// Перший крок маршруту погодження; <c>null</c> — маршруту немає, і
    /// затвердження одноетапне, як було до <c>ФВ-5.17</c>.
    /// </param>
    /// <exception cref="DomainException">Аркуш не в <c>Draft</c> і не відхилений.</exception>
    /// <remarks>
    /// ⚠ Параметр необов'язковий навмисно: за замовчуванням поведінка
    /// **не змінюється**. Маршрутів у seed немає, і система, у якій їх ніхто
    /// не завів, працює рівно як раніше.
    /// </remarks>
    public void Submit(int userId, DateTime utcNow, int? firstStepId = null)
    {
        // ⚠ Повторне подання поданого — не «нічого не змінилося», а спроба
        // перезаписати момент і автора подання. Саме на них посилається зріз.
        if (Status is not (DocumentStatus.Draft or DocumentStatus.Rejected))
        {
            throw new DomainException(
                "ECR-DOC-0409",
                $"Подати можна лише чернетку або відхилений аркуш; поточний стан — {Status}.");
        }

        Status = DocumentStatus.Submitted;
        SubmittedAt = utcNow;
        SubmittedByUserId = userId;
        RejectedReason = null;
        CurrentStepId = firstStepId;
    }

    /// <summary>Затверджує поданий аркуш.</summary>
    /// <param name="userId">Хто затверджує.</param>
    /// <param name="utcNow">Момент затвердження.</param>
    /// <exception cref="DomainException">Аркуш не в стані <c>Submitted</c>.</exception>
    public void Approve(int userId, DateTime utcNow)
    {
        if (Status != DocumentStatus.Submitted)
        {
            throw new DomainException(
                "ECR-DOC-0409",
                $"Затверджувати можна лише поданий аркуш; поточний стан — {Status}.");
        }

        Status = DocumentStatus.Approved;
        ApprovedAt = utcNow;
        ApprovedByUserId = userId;
    }

    /// <summary>
    /// Затверджує ПОТОЧНИЙ крок маршруту і переходить до наступного.
    /// </summary>
    /// <param name="userId">Хто затверджує.</param>
    /// <param name="utcNow">Момент операції.</param>
    /// <param name="nextStepId">
    /// Наступний крок маршруту; <c>null</c> — цей крок був останнім, і аркуш
    /// стає <c>Approved</c>.
    /// </param>
    /// <exception cref="DomainException">Аркуш не в стані <c>Submitted</c>.</exception>
    /// <remarks>
    /// ⛔ Проміжний крок НЕ робить аркуш затвердженим. Це головна властивість
    /// багатоетапності: підпис першого погоджувача не є підписом останнього, і
    /// документ, який став би <c>Approved</c> після першого кроку, потрапив би
    /// у звітність для регулятора без решти погоджень (<c>ФВ-10.11</c>).
    ///
    /// ⚠ <see cref="ApprovedByUserId"/> заповнюється лише на ОСТАННЬОМУ
    /// кроці: поле означає «хто затвердив документ», а не «хто підписав
    /// останнім». Хто проходив проміжні кроки — питання до аудиту, і саме там
    /// на нього є відповідь.
    /// </remarks>
    public void ApproveStep(int userId, DateTime utcNow, int? nextStepId)
    {
        if (Status != DocumentStatus.Submitted)
        {
            throw new DomainException(
                "ECR-DOC-0409",
                $"Затверджувати можна лише поданий аркуш; поточний стан — {Status}.");
        }

        if (nextStepId is { } next)
        {
            CurrentStepId = next;

            return;
        }

        Approve(userId, utcNow);
        CurrentStepId = null;
    }

    /// <summary>Відхиляє поданий аркуш із коментарем.</summary>
    /// <param name="userId">Хто відхиляє.</param>
    /// <param name="comment">Коментар; обов'язковий.</param>
    /// <param name="utcNow">Момент операції.</param>
    /// <exception cref="DomainException">Аркуш не поданий або коментар порожній.</exception>
    public void Reject(int userId, string comment, DateTime utcNow)
    {
        if (Status != DocumentStatus.Submitted)
        {
            throw new DomainException(
                "ECR-DOC-0409",
                $"Відхилити можна лише поданий аркуш; поточний стан — {Status}.");
        }

        // ⚠ Коментар обов'язковий: відхилення без пояснення повертає роботу
        // тому, хто не знає, що виправляти, — і цикл повторюється.
        if (string.IsNullOrWhiteSpace(comment))
        {
            throw new DomainException("ECR-DOC-0422", "Коментар при відхиленні обов'язковий.");
        }

        Status = DocumentStatus.Rejected;
        RejectedReason = comment;
        ApprovedByUserId = userId;
        ApprovedAt = utcNow;

        // ⚠ Відхилення скидає маршрут: наступне подання йде з першого кроку.
        // Інакше виправлений документ обійшов би тих, хто вже «підписав» до
        // правки, — тобто підпис стосувався б не того тексту.
        CurrentStepId = null;
    }
}
