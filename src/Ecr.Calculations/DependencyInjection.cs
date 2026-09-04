using Ecr.Application.Ports;
using Microsoft.Extensions.DependencyInjection;

namespace Ecr.Calculations;

/// <summary>Реєстрація рушія розрахунків.</summary>
public static class DependencyInjection
{
    /// <summary>Додає модулі і резолвери.</summary>
    /// <param name="services">Колекція сервісів.</param>
    /// <remarks>
    /// ⚠ Модуль рівня 2 (скрипти Roslyn) не реєструється й не реєструватиметься,
    /// доки не буде дозволу ІБ (K-1). Його відсутність не має ламати систему —
    /// саме тому <c>ICalculationModule</c> резолвиться як КОЛЕКЦІЯ, а не як
    /// одиничний сервіс: оркестратор питає «хто вміє це порахувати», і
    /// відсутність одного відповідача не робить питання беззмістовним.
    /// </remarks>
    public static IServiceCollection AddEcrCalculations(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<ConstantResolver>();
        services.AddScoped<MethodologyResolver>();
        services.AddScoped<CalculationInputBuilder>();
        services.AddScoped<CalculationOutputWriter>();
        services.AddSingleton<CalendarContext>();

        services.AddScoped<ICalculationModule, GenericCalculationModule>();
        services.AddScoped<CalculationOrchestrator>();

        return services;
    }
}
