// src/Ecr.Application/Security/EditDecision.cs

using Ecr.Domain.Enums;

namespace Ecr.Application.Security;

/// <summary>
/// Рішення про доступ. Повертає <b>причину</b>, а не <c>bool</c>: користувач має
/// розуміти, чому комірка сіра, інакше він піде до адміністратора, а той —
/// до розробника.
/// </summary>
/// <param name="IsAllowed">Чи дозволена дія.</param>
/// <param name="Reason">Причина відмови; <see cref="EditDenyReason.None"/> при дозволі.</param>
/// <param name="Detail">Уточнення для UI (напр. дата закриття періоду). Не для логіки.</param>
/// <param name="RequiresConfirmation">
/// Дозволено, але лише після ЯВНОГО підтвердження оператора
/// (<c>ФВ-2.16</c>, <c>AllowWithConfirmation</c>, <c>#43</c>). Завжди
/// <c>false</c> при відмові: підтверджувати нема чого, коли дія й так
/// заборонена.
/// </param>
/// <param name="OutOfWindowMark">
/// Дозволено поза вікном доступу за політикою <c>Warn</c> («дозволити з
/// позначкою», <c>ФВ-2.16</c>, <c>D-239</c>): правка проходить без
/// підтвердження, але в журналі стає <c>IsOutOfWindow = 1</c>.
/// </param>
public readonly record struct EditDecision(
    bool IsAllowed, EditDenyReason Reason, string? Detail = null, bool RequiresConfirmation = false,
    bool OutOfWindowMark = false)
{
    /// <summary>
    /// Дозвіл дала САМЕ передача зрізу (EA-B): без неї рівень був би
    /// <c>Read</c> і рішення — <c>InsufficientGrantLevel</c>. Потрібен запису,
    /// щоб повторити перевірку передачі під блокуванням аркуша: відкликання
    /// чи «Заповнено» між рішенням і комітом не мають пропустити правку.
    /// </summary>
    public bool ViaAssignment { get; init; }

    /// <summary>
    /// У документа цього рішення є передачі зрізу (EA-B): запис мусить повторно перевірити їх
    /// під блокуванням аркуша, навіть якщо САМЕ це рішення передачею не піднято (заборона
    /// «Заповнено»/ексклюзивність могла з'явитися між рішенням і комітом). Документ без передач —
    /// <c>false</c>: повторного читання немає.
    /// </summary>
    public bool AssignmentsPresent { get; init; }

    /// <summary>
    /// D-442: дозвіл на ПОДАНИЙ аркуш дала САМЕ роль погоджувача поточного кроку маршруту
    /// (<see cref="CellAccessContext.IsCurrentApprover"/>): без неї рішення було б
    /// <see cref="EditDenyReason.DocumentSubmitted"/>. Запис повторює перевірку погоджувача під
    /// блокуванням аркуша (крок міг змінитися між рішенням і комітом) і пише в журнал
    /// походження «правка погоджувача».
    /// </summary>
    public bool ByApprover { get; init; }

    /// <summary>
    /// P2-A (рішення людини 26.10): ключ періоду місячної колонки (<c>MonthsInColumns</c>), стан якого діяв у цьому
    /// рішенні ЗАМІСТЬ стану періоду екземпляра таблиці; <c>null</c> — рішення про період екземпляра. Запис повторює
    /// перевірку стану аркуша саме цього періоду під його блокуванням (<c>sheetGate</c>) і ставить <c>IsLateEdit</c>
    /// за його станом.
    /// </summary>
    public int? ColumnPeriod { get; init; }

    /// <summary>
    /// Q-17 (<c>Collection:MonthColumnRouting</c>): ключі періодів календаря року, агрегат яких керував цим рішенням
    /// (введений не-місячний стовпець чи склад рядків таблиці «місяці в колонках», <see cref="AnnualPeriodRule"/>);
    /// <c>null</c> — рішення не річне. Запис бере блокування ВСІХ цих періодів за зростанням ключа й повторює
    /// агрегат стану аркуша під ними.
    /// </summary>
    public IReadOnlyList<int>? AnnualPeriods { get; init; }

    /// <summary>
    /// AN-142 (рішення людини, амендує D-457): комірка — звичайний ТЕКСТ якоря року (<c>String</c>, не бізнес-ключ, не
    /// обчислювана). Така комірка пишеться і в <c>Submitted</c>-аркуші (опис, примітка): вона не змінює поданих чисел, тож
    /// перерахунок не ставиться. <c>Approved</c> лишається замкненим. Запис повторює цю поблажку під блокуванням.
    /// </summary>
    public bool AnnualTextOnly { get; init; }

    /// <summary>AN-142: поблажка справді спрацювала (аркуш був Submitted) - лише тоді правка не ставить перерахунок.</summary>
    public bool AnnualTextRelaxed { get; init; }

    public static EditDecision Allow() => new(true, EditDenyReason.None);

    /// <summary>Дозволено погоджувачеві поточного кроку правити поданий аркуш (D-442).</summary>
    public static EditDecision AllowByApprover() => new(true, EditDenyReason.None) { ByApprover = true };

    /// <summary>Дозволено завдяки передачі зрізу (підйом <c>Read → Write</c>).</summary>
    public static EditDecision AllowViaAssignment() => new(true, EditDenyReason.None) { ViaAssignment = true };
    public static EditDecision Deny(EditDenyReason reason, string? detail = null) => new(false, reason, detail);

    /// <summary>
    /// Дозволено за умови підтвердження (<c>ФВ-2.16</c>, <c>#43</c>).
    /// </summary>
    /// <remarks>
    /// ⚠ Не <see cref="Deny"/>: комірка лишається редаговною і без цього
    /// підтвердження — заборона тут перетворила б <c>AllowWithConfirmation</c>
    /// на <c>ReadOnly</c>, і дві з трьох поведінок <c>ФВ-2.16</c> знову
    /// злилися б в одну.
    /// </remarks>
    public static EditDecision AllowWithConfirmation(string? detail)
        => new(true, EditDenyReason.None, detail, RequiresConfirmation: true);

    /// <summary>Дозволено з позначкою в журналі (<c>ФВ-2.16</c>, <c>Warn</c>, <c>D-239</c>).</summary>
    public static EditDecision AllowMarked(string? detail)
        => new(true, EditDenyReason.None, detail, OutOfWindowMark: true);
}
