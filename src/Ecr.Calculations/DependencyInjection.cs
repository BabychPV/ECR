using Ecr.Application.Ports;
using Microsoft.Extensions.DependencyInjection;

namespace Ecr.Calculations;

/// <summary>Реєстрація рушія розрахунків.</summary>
public static class DependencyInjection
{
    /// <summary>Додає модулі і оркестратор.</summary>
    public static IServiceCollection AddEcrCalculations(this IServiceCollection services)
        => throw new NotImplementedException(
            "TODO:\n" +
            "AddScoped<ICalculationModule, GenericCalculationModule>();  // рівень 1\n" +
            "AddScoped<CalculationOrchestrator>();\n" +
            "AddScoped<MethodologyResolver>();\n" +
            "AddScoped<ConstantResolver>();\n" +
            "AddSingleton<CalendarContext>();\n" +
            "AddScoped<CalculationInputBuilder>();\n" +
            "AddScoped<CalculationOutputWriter>();\n" +
            "⚠ Модуль рівня 2 (скрипти Roslyn) НЕ реєструвати: він вмикається лише після " +
            "дозволу ІБ (K-1), і його відсутність не має ламати систему — саме тому " +
            "ICalculationModule резолвиться як колекція, а не як одиничний сервіс.");
}
