// src/Ecr.Application/Calculations/Dto/SimulationResultDto.cs
namespace Ecr.Application.Calculations.Dto;

/// <summary>
/// Результат прогону методології **без запису** (`ФВ-13.5`): що вийде, якщо
/// опублікувати.
/// </summary>
/// <param name="Outputs">Код виходу → значення й одиниця.</param>
/// <param name="DiffWithPublished">
/// Різниця з чинною опублікованою версією. Порожня — версія нічого не змінює;
/// саме це і треба бачити перед публікацією (`ФВ-9.6`).
/// </param>
/// <param name="Trace">Покроковий журнал; у симуляції завжди повний.</param>
public sealed record SimulationResultDto(
    IReadOnlyDictionary<string, decimal> Outputs,
    IReadOnlyDictionary<string, decimal> DiffWithPublished,
    IReadOnlyList<string> Trace);
