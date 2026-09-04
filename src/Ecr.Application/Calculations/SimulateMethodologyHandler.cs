// src/Ecr.Application/Calculations/SimulateMethodologyHandler.cs
using Ecr.Application.Ports;

namespace Ecr.Application.Calculations;

/// <summary>
/// Прогін методології **без запису результату** (ФВ-13.5): подивитися, що
/// вийде, до публікації.
/// </summary>
public sealed class SimulateMethodologyHandler(ICalculationModule module)
{
    public Task<SimulationResultDto> HandleAsync(int methodologyVersionId, int periodKey, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) прогін у пам'яті; ⛔ жодного запису в calc.CalculationResult " +
            "   і жодного CalculationRun — інакше симуляція засмітить історію " +
            "   прогонів, за якою відновлюють числа;\n" +
            "2) трейс завжди Full незалежно від TraceLevel версії: сенс симуляції " +
            "   саме в тому, щоб побачити кроки;\n" +
            "3) повернути і результат, і різницю з поточною опублікованою версією.");
}
