// src/Ecr.Domain/Entities/Workflow/ApprovalEvent.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;

namespace Ecr.Domain.Entities.Workflow;

/// <summary>Дія, що породила запис журналу переходів.</summary>
public enum ApprovalAction : byte
{
    /// <summary>Подання на погодження.</summary>
    Submit = 1,

    /// <summary>Остаточне затвердження (єдиний або останній крок маршруту).</summary>
    Approve = 2,

    /// <summary>Проміжний крок маршруту: стан лишається <c>Submitted</c>.</summary>
    ApproveStep = 3,

    /// <summary>Відхилення з причиною.</summary>
    Reject = 4,

    /// <summary>Повернення в роботу з причиною.</summary>
    Reopen = 5,
}

/// <summary>
/// Запис журналу переходів стану аркуша за період (<c>BE-11</c>).
/// </summary>
/// <remarks>
/// ⛔ <see cref="ApprovalState"/> тримає лише ОСТАННЄ затвердження, відхилення і
/// перевідкриття, а <c>Submit</c> ще й обнуляє <c>RejectedReason</c> — історії з
/// нього не відновити. Журнал пишеться в тій самій транзакції, що й зміна
/// стану, і лише вставкою: методів зміни в класі немає.
///
/// ⚠ Таблиця починається ПОРОЖНЬОЮ: переходи до міграції
/// <c>BE11ApprovalEvent</c> не відновлювалися. Часткова реконструкція з
/// <c>calc.SubmissionSnapshot</c> і <c>aud.SecurityEvent</c> виглядала б як
/// повна історія, не будучи нею.
/// </remarks>
public sealed class ApprovalEvent : Entity<long>
{
    private ApprovalEvent() { }

    /// <summary>Створює запис переходу.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="sheetDefId">Аркуш.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="fromStatus">Стан ДО дії.</param>
    /// <param name="toStatus">Стан після дії.</param>
    /// <param name="action">Дія.</param>
    /// <param name="byUserId">Хто виконав; <c>null</c> — система.</param>
    /// <param name="at">Момент дії, UTC.</param>
    /// <param name="reason">Причина відхилення або повернення в роботу.</param>
    /// <param name="stepOrdinal">Порядковий номер кроку маршруту, якщо маршрут є.</param>
    public ApprovalEvent(
        long documentId,
        int sheetDefId,
        int periodKey,
        DocumentStatus fromStatus,
        DocumentStatus toStatus,
        ApprovalAction action,
        int? byUserId,
        DateTime at,
        string? reason = null,
        int? stepOrdinal = null)
    {
        DocumentId = documentId;
        SheetDefId = sheetDefId;
        PeriodKey = periodKey;
        FromStatus = fromStatus;
        ToStatus = toStatus;
        Action = action;
        ByUserId = byUserId;
        At = at;
        Reason = reason;
        StepOrdinal = stepOrdinal;
    }

    public long DocumentId { get; private set; }
    public int SheetDefId { get; private set; }
    public int PeriodKey { get; private set; }
    public DocumentStatus FromStatus { get; private set; }
    public DocumentStatus ToStatus { get; private set; }
    public ApprovalAction Action { get; private set; }

    /// <summary>Хто виконав дію; <c>null</c> — системний перехід.</summary>
    public int? ByUserId { get; private set; }

    public DateTime At { get; private set; }
    public string? Reason { get; private set; }
    public int? StepOrdinal { get; private set; }

    /// <summary>Запис переходу зі стану <paramref name="fromStatus"/> у поточний стан аркуша.</summary>
    /// <param name="state">Стан ПІСЛЯ дії.</param>
    /// <param name="fromStatus">Стан, знятий ДО дії.</param>
    /// <param name="action">Дія.</param>
    /// <param name="byUserId">Хто виконав; <c>null</c> — система.</param>
    /// <param name="at">Момент дії, UTC.</param>
    /// <param name="reason">Причина, якщо дія її вимагає.</param>
    /// <param name="stepOrdinal">Крок маршруту, якщо є.</param>
    public static ApprovalEvent For(
        ApprovalState state,
        DocumentStatus fromStatus,
        ApprovalAction action,
        int? byUserId,
        DateTime at,
        string? reason = null,
        int? stepOrdinal = null)
    {
        ArgumentNullException.ThrowIfNull(state);

        return new ApprovalEvent(
            state.DocumentId, state.SheetDefId, state.PeriodKey,
            fromStatus, state.Status, action, byUserId, at, reason, stepOrdinal);
    }
}
