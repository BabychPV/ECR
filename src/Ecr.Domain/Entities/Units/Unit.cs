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

    public Unit(EcrCode code, LocalizedText symbol, LocalizedText name, byte dimensionId,
                bool isBase, decimal factorToBase, decimal offsetToBase)
    {
        Code = code.Value;
        SymbolL10n = symbol;
        NameL10n = name;
        DimensionId = dimensionId;
        IsBase = isBase;
        FactorToBase = factorToBase;
        OffsetToBase = offsetToBase;
        IsActive = true;
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
