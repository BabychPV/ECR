using Ecr.Domain.Entities.Units;

namespace Ecr.Domain.Services;

/// <summary>
/// Конверсія одиниць. **Неявних конверсій не буває** (D-74): цей сервіс
/// викликається лише там, де у виразі написано <c>CONVERT</c> або задано
/// мапінг <c>SourceUnit → TargetUnit</c>.
/// </summary>
public sealed class UnitConverter
{
    /// <summary>
    /// Виконує конверсію за маршрутом: тотожність → явна конверсія →
    /// через базову одиницю → помилка.
    /// </summary>
    /// <param name="value">Значення у вихідній одиниці.</param>
    /// <param name="from">Вихідна одиниця.</param>
    /// <param name="to">Цільова одиниця.</param>
    /// <param name="explicitConversion">Явна конверсія, якщо вона є в <c>uom.Conversion</c>.</param>
    /// <returns>Значення в цільовій одиниці.</returns>
    /// <exception cref="Abstractions.DomainException">
    /// Різні розмірності — <c>ECR-UOM-0422</c>. Це відмова, а не спроба вгадати.
    /// </exception>
    public decimal Convert(decimal value, Unit from, Unit to, UnitConversion? explicitConversion)
        => throw new NotImplementedException(
            "TODO: 1) from.Id == to.Id → value; " +
            "2) explicitConversion != null → value * Factor + Offset; " +
            "3) from.DimensionId == to.DimensionId → base = value * from.FactorToBase + from.OffsetToBase, " +
            "   result = (base - to.OffsetToBase) / to.FactorToBase; " +
            "4) інакше DomainException('ECR-UOM-0422'). " +
            "Усі обчислення в decimal — float заборонений (D-30).");

    /// <summary>Чи можлива конверсія без явного правила.</summary>
    public bool CanConvert(Unit from, Unit to) => from.DimensionId == to.DimensionId;
}
