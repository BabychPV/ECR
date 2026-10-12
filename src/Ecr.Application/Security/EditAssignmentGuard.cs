using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Security;

/// <summary>Стан виконавця в передачі (рівень одержувача, DESIGN §3.1).</summary>
public enum AssigneeProgress : byte
{
    /// <summary>Передано: виконавець може писати.</summary>
    Assigned = 0,

    /// <summary>Виконавець позначив «Заповнено»: не пише, доки не повернуть.</summary>
    Filled = 1,

    /// <summary>Повернуто на доопрацювання: виконавець знову пише.</summary>
    Returned = 2,
}

/// <summary>
/// Факти однієї передачі зрізу, потрібні правилу запису. Збирає служба
/// (<c>AccessDecisionService</c>), правило нічого не читає.
/// </summary>
/// <param name="SheetCode">Селектор «аркуш»; <c>null</c> — усі.</param>
/// <param name="TableCode">Селектор «таблиця»; <c>null</c> — усі.</param>
/// <param name="MonthNumber">Селектор «місяць» 1..12; <c>null</c> — не обмежено.</param>
/// <param name="Period">Селектор «період»; <c>null</c> — не обмежено.</param>
/// <param name="IsExclusive">Ексклюзивна (D1): решта за ролями писати не може.</param>
/// <param name="IsValidated">Власник валідував зріз.</param>
/// <param name="DelegatorActive">Делегатор (<c>DelegatedByUserId</c>) активний (R7).</param>
/// <param name="DelegatedByUserId">Хто передав.</param>
/// <param name="ExpiresAt">Строк дії; <c>null</c> — безстроково.</param>
/// <param name="MyProgress">Стан САМЕ цього користувача; <c>null</c> — не виконавець.</param>
public sealed record EditAssignmentFact(
    string? SheetCode,
    string? TableCode,
    byte? MonthNumber,
    PeriodKey? Period,
    bool IsExclusive,
    bool IsValidated,
    bool DelegatorActive,
    int DelegatedByUserId,
    DateTime? ExpiresAt,
    AssigneeProgress? MyProgress);

/// <summary>Факти комірки (зрізу), з якими звіряються селектори передачі.</summary>
/// <param name="SheetCode">Код аркуша; <c>null</c> — невідомий.</param>
/// <param name="TableCode">Код таблиці; <c>null</c> — невідомий.</param>
/// <param name="Period">Період зрізу; <c>null</c> — невідомий.</param>
/// <param name="ColumnIsMonth">Колонка місячна (<c>IsMonthColumn</c>).</param>
/// <param name="ColumnMonthNumber">Номер місяця колонки; <c>null</c> — немає.</param>
/// <param name="IsRowLevel">Рішення про створення рядка (без колонки, D5).</param>
public readonly record struct EditSliceFacts(
    string? SheetCode,
    string? TableCode,
    PeriodKey? Period,
    bool ColumnIsMonth,
    byte? ColumnMonthNumber,
    bool IsRowLevel = false);

/// <summary>Підсумок правила для комірки.</summary>
/// <param name="DelegatedWrite">Піднімати <c>Read → Write</c>.</param>
/// <param name="Lock">
/// <see cref="EditDenyReason.None"/>, <see cref="EditDenyReason.LockedByOwner"/>,
/// <see cref="EditDenyReason.AssignmentFilled"/>, <see cref="EditDenyReason.AssignmentValidated"/>,
/// <see cref="EditDenyReason.AssignmentExpired"/>.
/// </param>
public readonly record struct EditAccessFacts(bool DelegatedWrite, EditDenyReason Lock)
{
    /// <summary>Передача нічого не змінює.</summary>
    public static EditAccessFacts None => default;
}

/// <summary>
/// Чисте правило «делегований запис» (R1, R4–R7 DESIGN-OWNER-EDIT-GRANTS).
/// </summary>
/// <remarks>
/// ⛔ Це ЄДИНЕ місце, де передача зрізу стає дозволом чи забороною. Жодних
/// запитів і годинника (час приходить аргументом) — таблиця рішень
/// перевіряється без бази.
///
/// ⚠ Асиметрія «невідомо»: для ДОЗВОЛУ (підйом) невідомий факт при заповненому
/// селекторі = не збіглося («невідомо → закрито»); для ЗАБОРОНИ — збіглося
/// (консервативно). Селектор-місяць збігається лише з місячною колонкою того
/// самого номера: це відомий факт, не «невідоме».
///
/// ⚠ Чого тут немає: «у межах власних прав» перевіряється при ПЕРЕДАЧІ (EA-C),
/// у записі — лише стан, строк і активність делегатора (профіль чужої людини
/// без AD-груп невідомий, P-02).
/// </remarks>
public static class EditAssignmentGuard
{
    /// <summary>Результат звірки селектора з фактом.</summary>
    private enum Match : byte
    {
        No = 0,
        Unknown = 1,
        Yes = 2,
    }

    /// <summary>
    /// Відсіює передачі, що точно не стосуються зрізу (аркуш/таблиця/період) —
    /// щоб на комірку не ходити по всьому списку документа.
    /// </summary>
    /// <param name="assignments">Передачі документа.</param>
    /// <param name="slice">Факти зрізу (колонка не потрібна).</param>
    /// <returns>Передачі, у яких жоден з трьох селекторів не дав <c>No</c>.</returns>
    public static IReadOnlyList<EditAssignmentFact> Narrow(
        IReadOnlyList<EditAssignmentFact> assignments, EditSliceFacts slice)
    {
        ArgumentNullException.ThrowIfNull(assignments);

        if (assignments.Count == 0)
        {
            return assignments;
        }

        var narrowed = new List<EditAssignmentFact>(assignments.Count);
        foreach (var a in assignments)
        {
            if (Code(a.SheetCode, slice.SheetCode) != Match.No
                && Code(a.TableCode, slice.TableCode) != Match.No
                && PeriodOf(a.Period, slice.Period) != Match.No)
            {
                narrowed.Add(a);
            }
        }

        return narrowed;
    }

    /// <summary>Рішення правила для комірки.</summary>
    /// <param name="userId">Хто пише.</param>
    /// <param name="documentOwnerUserId">Автор документа.</param>
    /// <param name="isIntegrationWriter">Системний шлях (D3): передача його не блокує.</param>
    /// <param name="assignments">Передачі документа (можна вже звужені <see cref="Narrow"/>).</param>
    /// <param name="slice">Факти комірки.</param>
    /// <param name="utcNow">Поточний час (строк дії).</param>
    /// <returns>Підйом і/або заборона.</returns>
    public static EditAccessFacts Evaluate(
        int userId,
        int documentOwnerUserId,
        bool isIntegrationWriter,
        IReadOnlyList<EditAssignmentFact> assignments,
        EditSliceFacts slice,
        DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(assignments);

        // R3/D3: власник і системні шляхи не блокуються й не піднімаються —
        // власник пише за своїми ролями.
        if (assignments.Count == 0 || isIntegrationWriter || userId == documentOwnerUserId)
        {
            return EditAccessFacts.None;
        }

        var lift = false;
        var locked = EditDenyReason.None;

        foreach (var a in assignments)
        {
            // R7: передача діє, лише поки делегатор активний.
            if (!a.DelegatorActive)
            {
                continue;
            }

            var expired = a.ExpiresAt is { } until && until <= utcNow;

            // Той, хто передав, ексклюзивністю власної передачі не блокується.
            if (a.DelegatedByUserId == userId)
            {
                continue;
            }

            var match = Combine(
                Code(a.SheetCode, slice.SheetCode),
                Code(a.TableCode, slice.TableCode),
                PeriodOf(a.Period, slice.Period),
                MonthOf(a.MonthNumber, slice));

            if (match == Match.No)
            {
                continue;
            }

            if (expired)
            {
                // Строк минув: виконавець бачить причину («строк минув»), а не
                // «немає права»; ексклюзивність чужого прострочена передача
                // не тримає.
                if (a.MyProgress is not null)
                {
                    locked = Stronger(locked, EditDenyReason.AssignmentExpired);
                }

                continue;
            }

            if (a.MyProgress is { } progress)
            {
                // Виконавець. Стан замикає й за Unknown (консервативно).
                if (a.IsValidated)
                {
                    locked = Stronger(locked, EditDenyReason.AssignmentValidated);
                }
                else if (progress == AssigneeProgress.Filled)
                {
                    locked = Stronger(locked, EditDenyReason.AssignmentFilled);
                }
                else if (match == Match.Yes && !slice.IsRowLevel)
                {
                    // Підйом — лише за точного збігу і не для створення рядка (D5).
                    lift = true;
                }
            }
            else if (a.IsExclusive)
            {
                // Не виконавець, ексклюзивна передача: за Unknown теж (консервативно).
                locked = Stronger(locked, EditDenyReason.LockedByOwner);
            }
        }

        // R6: будь-яка заборона виграє, підйом — лише за її відсутності.
        return locked != EditDenyReason.None
            ? new EditAccessFacts(false, locked)
            : new EditAccessFacts(lift, EditDenyReason.None);
    }

    private static EditDenyReason Stronger(EditDenyReason current, EditDenyReason candidate)
        => Rank(candidate) > Rank(current) ? candidate : current;

    private static int Rank(EditDenyReason reason) => reason switch
    {
        EditDenyReason.AssignmentValidated => 4,
        EditDenyReason.AssignmentFilled => 3,
        EditDenyReason.AssignmentExpired => 2,
        EditDenyReason.LockedByOwner => 1,
        _ => 0,
    };

    private static Match Combine(Match a, Match b, Match c, Match d)
    {
        if (a == Match.No || b == Match.No || c == Match.No || d == Match.No)
        {
            return Match.No;
        }

        return a == Match.Unknown || b == Match.Unknown || c == Match.Unknown || d == Match.Unknown
            ? Match.Unknown
            : Match.Yes;
    }

    private static Match Code(string? selector, string? fact)
    {
        if (selector is null)
        {
            return Match.Yes;
        }

        if (fact is null)
        {
            return Match.Unknown;
        }

        return string.Equals(selector, fact, StringComparison.OrdinalIgnoreCase) ? Match.Yes : Match.No;
    }

    private static Match PeriodOf(PeriodKey? selector, PeriodKey? fact)
    {
        if (selector is null)
        {
            return Match.Yes;
        }

        if (fact is null)
        {
            return Match.Unknown;
        }

        return selector.Value == fact.Value ? Match.Yes : Match.No;
    }

    private static Match MonthOf(byte? selector, EditSliceFacts slice)
    {
        if (selector is null)
        {
            return Match.Yes;
        }

        // Рівень рядка: місяця колонки немає, селектор-місяць не застосовується (§3.4).
        if (slice.IsRowLevel)
        {
            return Match.No;
        }

        return slice.ColumnIsMonth && slice.ColumnMonthNumber == selector ? Match.Yes : Match.No;
    }
}
