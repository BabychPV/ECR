using Ecr.Application.Ports;
using Ecr.Domain.Enums;

namespace Ecr.Calculations;

/// <summary>
/// Generic-модуль: виконує методологію, задану **даними** — формулами,
/// константами і речовинами (ФВ-9.1).
/// </summary>
/// <remarks>
/// Мета — «generic плюс явний список винятків», а не доведення універсальності
/// (ФВ-9.3). Якщо після класифікації 44 методологій рівень 2 потрібен більш ніж
/// для 5 — проблема не в методологіях, а в граматиці рівня 1: дешевше
/// розширити граматику, ніж плодити скрипти.
/// </remarks>
public sealed class GenericCalculationModule(
    IFormulaEngine formulaEngine,
    ConstantResolver constants,
    CalendarContext calendar,
    NumericPolicy numeric) : ICalculationModule
{
    /// <inheritdoc />
    public string Code => "generic";

    /// <inheritdoc />
    public CalculationLevel Level => CalculationLevel.Configuration;

    /// <inheritdoc />
    public bool CanHandle(MethodologyDescriptor methodology)
        => throw new NotImplementedException(
            "TODO: повертати true, якщо methodology.Level == Configuration і всі її формули " +
            "розібралися в діалекті Methodology без діагностик.");

    /// <inheritdoc />
    public Task<CalculationOutput> ExecuteAsync(CalculationInput input, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO — порядок обчислення:\n" +
            "1) побудувати IEvaluationContext: аргументи з input, константи через constants " +
            "   (резолвлені за категорією, речовиною і ДАТОЮ ПЕРІОДУ), календар через calendar;\n" +
            "2) обчислити формули за EvaluationOrder — він уже топологічний із Publish, " +
            "   сортувати граф тут не треба;\n" +
            "3) для КОЖНОЇ речовини методології порахувати виходи (tons, gsec);\n" +
            "4) округлення і арифметику застосовувати через numeric — режим Legacy має " +
            "   відтворювати числа чинної системи побітово (ФВ-9.9);\n" +
            "5) писати трейс лише згідно з TraceLevel версії (Off/ErrorsOnly/Full) — " +
            "   керуємо тим, ЩО пишемо, а не скільки зберігаємо (ЗБР-3);\n" +
            "6) НЕ писати в БД: повернути CalculationOutput, запис робить CalculationOutputWriter.");
}
