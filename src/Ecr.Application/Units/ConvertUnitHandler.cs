// src/Ecr.Application/Units/ConvertUnitHandler.cs
using Ecr.Application.Errors;
using Ecr.Application.Ports;

namespace Ecr.Application.Units;

/// <summary>
/// Явна конверсія одиниць. **Неявних конверсій не буває** (ФВ-16.4, D-74):
/// рушій перетворює величину лише за викликом <c>CONVERT</c> у виразі або за
/// правилом мапінгу інтеграції.
/// </summary>
public sealed class ConvertUnitHandler(IUnitCatalog catalog)
{
    /// <summary>Конвертує значення між одиницями.</summary>
    /// <param name="value">Значення у вихідній одиниці.</param>
    /// <param name="fromUnit">Код вихідної одиниці.</param>
    /// <param name="toUnit">Код цільової одиниці.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Одиниці немає в довіднику.</exception>
    /// <exception cref="BusinessRuleException">Різні розмірності — <c>ECR-UOM-0422</c>.</exception>
    public async Task<decimal> HandleAsync(
        decimal value, string fromUnit, string toUnit, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromUnit);
        ArgumentException.ThrowIfNullOrWhiteSpace(toUnit);

        // Однакові одиниці — значення без арифметики. Множення на 1.0 у
        // decimal дає зайвий хвіст: 123.456 стає 123.4560, і звірка рядка
        // з рядком перестає сходитися.
        if (string.Equals(fromUnit, toUnit, StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        var catalogue = await catalog.GetAsync(ct).ConfigureAwait(false);

        var from = Resolve(catalogue, fromUnit);
        var to = Resolve(catalogue, toUnit);

        // ⛔ Різні розмірності — ВІДМОВА, а не пошук шляху «через базу». Маса в
        // об'єм не переводиться без щільності, а щільність залежить від
        // речовини й умов: це константа методології, не конверсія (ФВ-16.3,
        // ФВ-16.5). Дозволити тут означало б, що те саме число перетворюється
        // по-різному залежно від того, хто заповнив довідник.
        if (from.DimensionId != to.DimensionId)
        {
            throw new BusinessRuleException(
                "ECR-UOM-0422",
                $"Конверсія {fromUnit} → {toUnit} неможлива: різні розмірності "
                + $"({from.DimensionId} і {to.DimensionId}). Потрібен контекстний коефіцієнт, "
                + "а він належить методології, не довіднику одиниць.",
                new Dictionary<string, object?> { ["from"] = fromUnit, ["to"] = toUnit });
        }

        if (to.FactorToBase == 0m)
        {
            throw new BusinessRuleException(
                "ECR-UOM-0422",
                $"Одиниця {toUnit} має нульовий множник переходу до бази: конверсія неможлива.");
        }

        // Маршрут через базову одиницю. Зсув потрібен лише температурі, але
        // формула єдина: для решти OffsetToBase дорівнює нулю, і жодного
        // окремого випадку не з'являється. Уся арифметика в decimal (D-30).
        var inBase = (value * from.FactorToBase) + from.OffsetToBase;
        return (inBase - to.OffsetToBase) / to.FactorToBase;
    }

    private static UnitRef Resolve(UnitCatalogSnapshot catalogue, string code)
        => catalogue.Units.TryGetValue(code, out var unit)
            ? unit
            : throw new NotFoundException("ECR-UOM-0404", $"Одиниці «{code}» немає в довіднику.");
}

/// <summary>Перелік одиниць із розмірностями (ФВ-16.2).</summary>
/// <remarks>
/// Розмірність віддається разом із одиницею навмисно: без неї клієнт не може
/// перевірити нічого — ні того, що конверсія можлива, ні того, що величини
/// сумісні.
/// </remarks>
public sealed class ListUnitsHandler(IUnitCatalog catalog)
{
    /// <summary>Читає довідник одиниць.</summary>
    /// <param name="ct">Токен скасування.</param>
    public async Task<IReadOnlyList<UnitRef>> HandleAsync(CancellationToken ct)
    {
        var catalogue = await catalog.GetAsync(ct).ConfigureAwait(false);
        return catalogue.Units.Values.OrderBy(u => u.Code, StringComparer.Ordinal).ToList();
    }
}
