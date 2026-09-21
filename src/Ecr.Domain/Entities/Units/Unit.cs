using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Units;

/// <summary>Одиниця вимірювання.</summary>
/// <remarks>
/// Похідні одиниці складаються **посиланнями** на чисельник і знаменник, а не
/// розбираються з рядка: повної алгебри розмірностей навмисно немає — вона не
/// потрібна для звітності й коштує дорого (ФВ-16.2).
/// </remarks>
public sealed class Unit
{
    private Unit() { }

    /// <summary>Заводить одиницю.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="factorToBase"/> ≤ 0.
    /// </exception>
    /// <remarks>
    /// ⛔ <paramref name="factorToBase"/> мусить бути додатним — це інваріант
    /// домену, не перевірка форми. Нуль згортає конверсію `(value × factor) +
    /// offset` до КОНСТАНТИ: кожне вхідне значення дає те саме число, тихо, без
    /// помилки. Від'ємний множник перевертає знак величини. Нічого з цього не
    /// «майже правильно» — це неправильні числа у звітності про викиди.
    /// </remarks>
    public Unit(EcrCode code, LocalizedText symbol, LocalizedText name, byte dimensionId,
                bool isBase, decimal factorToBase, decimal offsetToBase)
    {
        if (factorToBase <= 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(factorToBase),
                factorToBase,
                "Множник переходу до базової одиниці мусить бути додатним: нуль згортає "
                + "конверсію до константи, від'ємний — перевертає знак величини.");
        }

        Code = code.Value;
        SymbolL10n = symbol;
        NameL10n = name;
        DimensionId = dimensionId;
        IsBase = isBase;
        FactorToBase = factorToBase;
        OffsetToBase = offsetToBase;
        IsActive = true;
    }

    /// <summary>Змінює позначення, назву і коефіцієнти переходу.</summary>
    /// <remarks>
    /// Код, розмірність і ознака базової не змінюються: за кодом на одиницю
    /// посилаються формули, а зміна розмірності — це вже інша одиниця.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Множник ≤ 0 або базова одиниця отримує множник ≠ 1 чи зсув ≠ 0.
    /// </exception>
    public void Update(LocalizedText symbol, LocalizedText name, decimal factorToBase, decimal offsetToBase)
    {
        if (factorToBase <= 0m || (IsBase && (factorToBase != 1m || offsetToBase != 0m)))
        {
            throw new ArgumentOutOfRangeException(
                nameof(factorToBase),
                factorToBase,
                "Множник мусить бути додатним, а базова одиниця — лишатися з множником 1 і зсувом 0.");
        }

        SymbolL10n = symbol;
        NameL10n = name;
        FactorToBase = factorToBase;
        OffsetToBase = offsetToBase;
    }

    public int Id { get; private set; }
    public string Code { get; private set; } = null!;
    public LocalizedText SymbolL10n { get; private set; } = null!;
    public LocalizedText NameL10n { get; private set; } = null!;
    public byte DimensionId { get; private set; }
    public bool IsBase { get; private set; }

    /// <summary>Множник переходу до базової одиниці розмірності.</summary>
    public decimal FactorToBase { get; private set; }

    /// <summary>Зсув; потрібен лише для температури (<c>°C → K</c>).</summary>
    public decimal OffsetToBase { get; private set; }

    public int? NumeratorUnitId { get; private set; }
    public int? DenominatorUnitId { get; private set; }
    public string? DisplayFormat { get; private set; }
    public bool IsActive { get; private set; }
}
