using Ecr.Domain.Abstractions;
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
    /// <exception cref="DomainException">
    /// Різні розмірності — <c>ECR-UOM-0422</c>. Це відмова, а не спроба вгадати.
    /// </exception>
    public decimal Convert(decimal value, Unit from, Unit to, UnitConversion? explicitConversion)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        if (from.Id == to.Id)
        {
            return value;
        }

        // ⚠ Явна конверсія має пріоритет над маршрутом через базу — і це не
        // оптимізація. `LegacyPinned` існує саме щоб відтворити число чинної
        // системи, яке порахували за іншим коефіцієнтом; маршрут через базу
        // дав би «правильніше» значення і розійшовся б із поданим звітом.
        if (explicitConversion is not null)
        {
            if (explicitConversion.FromUnitId != from.Id || explicitConversion.ToUnitId != to.Id)
            {
                throw new DomainException(
                    "ECR-UOM-0422",
                    $"Явна конверсія описує {explicitConversion.FromUnitId} → {explicitConversion.ToUnitId}, "
                    + $"а запитано {from.Id} → {to.Id}.");
            }

            return (value * explicitConversion.Factor) + explicitConversion.Offset;
        }

        // ⛔ Різні розмірності — ВІДМОВА, а не спроба вгадати. Саме тут щільність
        // не стає «конверсією»: м³ у кг перевести не можна, бо коефіцієнт
        // залежить від речовини й умов і живе в calc.MethodologyConstant
        // (ФВ-16.3, ФВ-16.5).
        if (from.DimensionId != to.DimensionId)
        {
            throw new DomainException(
                "ECR-UOM-0422",
                $"Конверсія {from.Code} → {to.Code} неможлива: різні розмірності "
                + $"({from.DimensionId} і {to.DimensionId}). Потрібен контекстний коефіцієнт, "
                + "а він належить методології, не довіднику одиниць.");
        }

        // Маршрут через базову одиницю. Зсув потрібен лише температурі, але
        // формула єдина: для решти OffsetToBase дорівнює нулю, і жодного
        // окремого випадку не з'являється.
        var inBase = (value * from.FactorToBase) + from.OffsetToBase;

        if (to.FactorToBase == 0m)
        {
            throw new DomainException(
                "ECR-UOM-0422",
                $"Одиниця {to.Code} має нульовий множник переходу до бази: конверсія неможлива.");
        }

        // Усі обчислення в decimal — float заборонений (D-30): звітні числа
        // звіряються до копійки, і подвійна точність тут дає розбіжність,
        // якої ніхто не може пояснити.
        return (inBase - to.OffsetToBase) / to.FactorToBase;
    }

    /// <summary>Чи можлива конверсія без явного правила.</summary>
    /// <param name="from">Вихідна одиниця.</param>
    /// <param name="to">Цільова одиниця.</param>
    public bool CanConvert(Unit from, Unit to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        return from.DimensionId == to.DimensionId;
    }
}
