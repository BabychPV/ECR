using Ecr.Application.Ports;
using Microsoft.Extensions.DependencyInjection;

namespace Ecr.Calculations;

/// <summary>Реєстрація рушія розрахунків.</summary>
public static class DependencyInjection
{
    /// <summary>Додає модулі і резолвери.</summary>
    /// <param name="services">Колекція сервісів.</param>
    /// <param name="limits">
    /// Ліміти прогону з секції <c>Calculations</c> (ФВ-9.8); <c>null</c> — типові.
    /// Зв'язує їх з конфігурацією викликач: ця збірка <c>IConfiguration</c> не знає.
    /// </param>
    /// <remarks>
    /// ⚠ Модуль рівня 2 (скрипти Roslyn) не реєструється й не реєструватиметься,
    /// доки не буде дозволу ІБ (K-1). Його відсутність не має ламати систему —
    /// саме тому <c>ICalculationModule</c> резолвиться як КОЛЕКЦІЯ, а не як
    /// одиничний сервіс: оркестратор питає «хто вміє це порахувати», і
    /// відсутність одного відповідача не робить питання беззмістовним.
    /// </remarks>
    public static IServiceCollection AddEcrCalculations(
        this IServiceCollection services, CalculationLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // ⚠ Без EnsureValid тут: недійсне значення зупиняє старт через
        // `EcrConfigurationValidation` з ім'ям ключа й змінною оточення, а
        // оркестратор перевіряє ліміти сам, коли його створюють.
        services.AddSingleton(limits ?? new CalculationLimits());

        services.AddScoped<ConstantResolver>();
        services.AddScoped<MethodologyResolver>();
        services.AddScoped<CalculationInputBuilder>();
        services.AddScoped<CalculationOutputWriter>();
        services.AddSingleton<CalendarContext>();

        services.AddScoped<ICalculationModule, GenericCalculationModule>();
        services.AddScoped<CalculationOrchestrator>();
        services.AddScoped<ICalculationRunner>(p => p.GetRequiredService<CalculationOrchestrator>());

        return services;
    }
}
