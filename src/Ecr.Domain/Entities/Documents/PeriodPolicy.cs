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
                + "скінчився б власний пільговий строк.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-PRD-4225.graceAfterHardClose",
                    ["graceOffsetDays"] = graceOffsetDays.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["hardCloseOffsetDays"] = hardCloseOffsetDays.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        if (yearGraceOffsetDays < 0)
        {
            throw new DomainException(
                ErrorCodes.PeriodPolicyOrderInvalid,
                $"Річний пільговий строк ({yearGraceOffsetDays} дн.) не може бути від'ємним.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-PRD-4225.negativeYearGrace",
                    ["yearGraceOffsetDays"] = yearGraceOffsetDays.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        OpenOffsetDays = openOffsetDays;
        GraceOffsetDays = graceOffsetDays;
        HardCloseOffsetDays = hardCloseOffsetDays;
        YearGraceOffsetDays = yearGraceOffsetDays;
    }

    /// <summary>Межа зсуву в днях в обидва боки: десять років.</summary>
    /// <remarks>
    /// ⛔ Без межі <c>int.MaxValue</c> днів доходив до <c>DateTime.AddDays</c> у перерахунку меж
    /// наявних періодів (<c>Period.RecomputeBoundaries</c>) і падав
    /// <c>ArgumentOutOfRangeException</c> — 500 на <c>PUT /projects/period-policies/{id}</c>.
    /// Десять років — свідомо із запасом: реальні строки — дні й тижні (ФВ-1.6, ФВ-1.8), а межа
    /// лише відсікає числа, з якими календар не може порахувати дату.
    /// </remarks>
    public const int MaxOffsetDays = 3660;

    /// <summary>Перевіряє, що зсуви запиту в межах <see cref="MaxOffsetDays"/>.</summary>
    /// <exception cref="DomainException"><c>ECR-PRD-4225</c> з ключем <c>offsetOutOfRange</c>.</exception>
    /// <remarks>
    /// ⚠ Межа запиту (кличуть обробники створення й зміни політики), а не інваріант сутності:
    /// тести календаря свідомо будують політику з величезним зсувом, щоб поставити строк
    /// періоду далекого року, і для самої сутності таке значення не помилка.
    /// </remarks>
    public static void EnsureOffsetsInRange(
        int openOffsetDays, int graceOffsetDays, int hardCloseOffsetDays, int yearGraceOffsetDays)
    {
        RequireInRange(nameof(openOffsetDays), openOffsetDays);
        RequireInRange(nameof(graceOffsetDays), graceOffsetDays);
        RequireInRange(nameof(hardCloseOffsetDays), hardCloseOffsetDays);
        RequireInRange(nameof(yearGraceOffsetDays), yearGraceOffsetDays);
    }

    private static void RequireInRange(string field, int days)
    {
        if (days is < -MaxOffsetDays or > MaxOffsetDays)
        {
            throw new DomainException(
                ErrorCodes.PeriodPolicyOrderInvalid,
                $"Зсув {field} ({days} дн.) поза межами ±{MaxOffsetDays} дн.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-PRD-4225.offsetOutOfRange",
                    ["field"] = field,
                    ["value"] = days.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["max"] = MaxOffsetDays.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }
    }
}
