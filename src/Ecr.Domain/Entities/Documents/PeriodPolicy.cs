using Ecr.Domain.Abstractions;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Documents;

/// <summary>Offsets переходів періоду. Налаштовуються адміністратором (ФВ-1.6).</summary>
public sealed class PeriodPolicy : Entity<int>
{
    private PeriodPolicy() { }

    public PeriodPolicy(EcrCode code, int openOffsetDays, int graceOffsetDays,
                        int hardCloseOffsetDays, int yearGraceOffsetDays)
    {
        Code = code.Value;
        ApplyOffsets(openOffsetDays, graceOffsetDays, hardCloseOffsetDays, yearGraceOffsetDays);
    }

    public string Code { get; private set; } = null!;

    /// <summary>Коли період відкривається, від його **початку**. Може бути від'ємним.</summary>
    public int OpenOffsetDays { get; private set; }

    /// <summary>Скільки днів після завершення періоду редагування ще дозволене.</summary>
    public int GraceOffsetDays { get; private set; }

    /// <summary>Коли період закривається остаточно.</summary>
    public int HardCloseOffsetDays { get; private set; }

    /// <summary>Скільки днів після завершення **року** дані ще редагуються.</summary>
    public int YearGraceOffsetDays { get; private set; }

    /// <summary>
    /// Змінює offsets політики (T6/#37). Право <c>Project.Manage</c> —
    /// перевіряє обробник.
    /// </summary>
    /// <param name="openOffsetDays">Коли період відкривається від початку.</param>
    /// <param name="graceOffsetDays">Пільговий строк після кінця періоду.</param>
    /// <param name="hardCloseOffsetDays">Коли період закривається остаточно.</param>
    /// <param name="yearGraceOffsetDays">Пільговий строк після кінця року.</param>
    /// <exception cref="DomainException">
    /// <c>ECR-PRD-4225</c> — <paramref name="graceOffsetDays"/> більший за
    /// <paramref name="hardCloseOffsetDays"/>.
    /// </exception>
    /// <remarks>
    /// ⚠ Проєкти, які вже посилаються на цю політику (<c>PeriodPolicyId</c>),
    /// не перераховують свої межі АВТОМАТИЧНО зі зміною тут: перерахунок іде
    /// через ідемпотентний <c>BuildPeriodCalendarHandler</c> (<c>GET
    /// …/periods</c>), який і так перераховує межі наявних періодів при
    /// кожному виклику (ФВ-1.5, коментар у <see cref="Services.PeriodCalendar"/>).
    /// </remarks>
    public void UpdateOffsets(
        int openOffsetDays, int graceOffsetDays, int hardCloseOffsetDays, int yearGraceOffsetDays)
        => ApplyOffsets(openOffsetDays, graceOffsetDays, hardCloseOffsetDays, yearGraceOffsetDays);

    /// <summary>
    /// Перевіряє й записує offsets — спільна логіка конструктора й
    /// <see cref="UpdateOffsets"/>.
    /// </summary>
    /// <remarks>
    /// ⛔ Перевірка дублює <c>CK_PP_Order</c> (база) НАВМИСНО, а не покладається
    /// на неї саму: без неї запис через CRUD впав би SQL-винятком без коду й
    /// без пояснення поля (`500`, а не `422` з текстом), а конструктор
    /// приймав би порядок, якого база все одно не збереже — тобто помилку
    /// побачив би той, хто найменше може її пояснити.
    /// </remarks>
    private void ApplyOffsets(
        int openOffsetDays, int graceOffsetDays, int hardCloseOffsetDays, int yearGraceOffsetDays)
    {
        if (graceOffsetDays > hardCloseOffsetDays)
        {
            throw new DomainException(
                ErrorCodes.PeriodPolicyOrderInvalid,
                $"Пільговий строк ({graceOffsetDays} дн.) не може бути довшим за жорстке "
                + $"закриття ({hardCloseOffsetDays} дн.): період закрився б остаточно раніше, ніж "
                + "скінчився б власний пільговий строк.");
        }

        if (yearGraceOffsetDays < 0)
        {
            throw new DomainException(
                ErrorCodes.PeriodPolicyOrderInvalid,
                $"Річний пільговий строк ({yearGraceOffsetDays} дн.) не може бути від'ємним.");
        }

        OpenOffsetDays = openOffsetDays;
        GraceOffsetDays = graceOffsetDays;
        HardCloseOffsetDays = hardCloseOffsetDays;
        YearGraceOffsetDays = yearGraceOffsetDays;
    }
}
