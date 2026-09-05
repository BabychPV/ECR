// src/Ecr.Domain/ValueObjects/PeriodKey.cs

using System.Globalization;
using Ecr.Domain.Enums;

namespace Ecr.Domain.ValueObjects;

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

    /// <summary>
    /// Чи є ключ осмисленим періодом.
    /// </summary>
    /// <remarks>
    /// ⛔ Первинний конструктор нічого не перевіряє — і не має, бо ним EF
    /// матеріалізує збережені значення. Але через нього ж проходить і те, що
    /// прийшло ззовні: незв'язаний параметр запиту дає <c>0</c>, і
    /// <c>PeriodKey(0)</c> виглядає як звичайний період.
    ///
    /// Саме так жила `A7-28`: валідація документа отримувала період 0,
    /// не знаходила в ньому жодного рядка і відповідала «помилок немає» —
    /// зелений результат, який нічого не означає.
    /// </remarks>
    public bool IsValid => Year is >= 1900 and <= 9999 && Sequence is >= 1 and <= 99;

    /// <summary>
    /// Розбирає ключ, що прийшов ЗЗОВНІ.
    /// </summary>
    /// <param name="value">Значення з запиту.</param>
    /// <exception cref="Abstractions.DomainException">Ключ не є періодом.</exception>
    /// <remarks>
    /// ⚠ Окремо від конструктора навмисно: тут інша відповідь на помилку.
    /// Збережене значення довіряють, вхідне — перевіряють.
    /// </remarks>
    public static PeriodKey Parse(int value)
    {
        var key = new PeriodKey(value);

        // ⚠ Саме доменний виняток, а не ArgumentOutOfRangeException: перший
        // конвеєр перетворює на 422 з кодом, другий — на «внутрішню помилку».
        // Невірний період у запиті — помилка того, хто питає, а не системи.
        return key.IsValid
            ? key
            : throw new Abstractions.DomainException(
                "ECR-PRD-0422",
                $"Ключ періоду {value} не є періодом: очікується Рік*100 + Номер, напр. 202601.");
    }

    /// <summary>Створює ключ із року і порядкового номера.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Рік поза 1900..9999 або номер поза 1..99.</exception>
    public static PeriodKey Create(int year, int sequence)
    {
        if (year is < 1900 or > 9999)
        {
                throw new ArgumentOutOfRangeException(nameof(year), year, "Рік має бути в межах 1900..9999.");
        }
        if (sequence is < 1 or > 99)
        {
                throw new ArgumentOutOfRangeException(nameof(sequence), sequence, "Номер періоду має бути в межах 1..99.");
        }
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
