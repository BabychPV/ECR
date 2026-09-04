// src/Ecr.Domain/ValueObjects/PeriodKey.cs
namespace Ecr.Domain.ValueObjects;

using System.Globalization;
using Ecr.Domain.Enums;

/// <summary>
/// Ключ партиціонування: <c>Year * 100 + Sequence</c> (R-A6).
/// Для місячних періодів збігається з <c>YYYYMM</c>, для решти — ні,
/// тому виводити місяць арифметикою заборонено.
/// </summary>
public readonly record struct PeriodKey(int Value)
{
    /// <summary>Рік періоду.</summary>
    public int Year => Value / 100;

    /// <summary>Порядковий номер періоду в році (1-based).</summary>
    public int Sequence => Value % 100;

    /// <summary>Створює ключ із року і порядкового номера.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Рік поза 1900..9999 або номер поза 1..99.</exception>
    public static PeriodKey Create(int year, int sequence)
    {
        if (year is < 1900 or > 9999)
            throw new ArgumentOutOfRangeException(nameof(year), year, "Рік має бути в межах 1900..9999.");
        if (sequence is < 1 or > 99)
            throw new ArgumentOutOfRangeException(nameof(sequence), sequence, "Номер періоду має бути в межах 1..99.");
        return new PeriodKey(year * 100 + sequence);
    }

    /// <summary>Перший і останній ключ року — межі діапазону партицій для архівації.</summary>
    public static (PeriodKey From, PeriodKey To) YearRange(int year, PeriodKind kind) => kind switch
    {
        PeriodKind.Monthly => (Create(year, 1), Create(year, 12)),
        PeriodKind.Quarterly => (Create(year, 1), Create(year, 4)),
        PeriodKind.Yearly => (Create(year, 1), Create(year, 1)),
        // Custom: верхню межу знає лише календар проєкту — повертаємо максимум,
        // виклик зобов'язаний звузити його фактичною кількістю періодів.
        _ => (Create(year, 1), Create(year, 99))
    };

    /// <remarks>
    /// ⚠ <see cref="CultureInfo.InvariantCulture"/> обов'язково: ключ їде в
    /// SQL, у ключі кешу <c>v{id}:r{rev}</c> і в URL. Локаль сервера не має
    /// права на нього впливати (`docs/tz/08-nfr.md` §90, `Q-040`).
    /// </remarks>
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}
