// src/Ecr.Application/Units/ConvertUnitHandler.cs
using Ecr.Application.Ports;

namespace Ecr.Application.Units;

/// <summary>
/// Явна конверсія одиниць. **Неявних конверсій не буває** (ФВ-16.4, D-74):
/// рушій перетворює величину лише за викликом <c>CONVERT</c> у виразі або за
/// правилом мапінгу інтеграції.
/// </summary>
public sealed class ConvertUnitHandler(Ecr.Domain.Services.UnitConverter converter)
{
    public Task<decimal> HandleAsync(decimal value, string fromUnit, string toUnit, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) різні розмірності → ECR-UOM-0422 (ФВ-16.3). НЕ шукати шлях " +
            "   «через базу»: маса в об'єм не переводиться без щільності, а " +
            "   щільність — контекстний коефіцієнт, не конверсія (ФВ-16.5);\n" +
            "2) однакові одиниці → повернути значення без арифметики: множення " +
            "   на 1.0 у decimal дає зайве заокруглення;\n" +
            "3) маршрут from → base → to через FactorToBase;\n" +
            "4) арифметика лише в decimal, ніколи в double (ФВ-9.11).");
}
