// src/Ecr.Application/Ports/ICalculationModule.cs
namespace Ecr.Application.Ports;

/// <summary>
/// Модуль розрахунку емісій. <b>Окрема точка розширення від</b>
/// <see cref="IFormulaEngine"/>: не всі обчислення є формулами (ФВ-9.2).
/// </summary>
public interface ICalculationModule
{
    /// <summary>Код модуля, унікальний у системі.</summary>
    string Code { get; }

    /// <summary>Рівень драбини виразності, який реалізує модуль.</summary>
    Ecr.Domain.Enums.CalculationLevel Level { get; }

    /// <summary>Чи здатний модуль обробити цю методологію.</summary>
    bool CanHandle(MethodologyDescriptor methodology);

    /// <summary>Виконує розрахунок. Не пише в БД — повертає результат.</summary>
    Task<CalculationOutput> ExecuteAsync(CalculationInput input, CancellationToken ct);
}
