using Microsoft.Extensions.DependencyInjection;

namespace Ecr.Calculations;

/// <summary>Реєстрація рушія розрахунків.</summary>
public static class DependencyInjection
{
    /// <summary>Додає модулі і оркестратор.</summary>
    /// <remarks>
    /// ⚠ Поки не реєструє нічого — і це не пропуск. Оркестратор, резолвери
    /// констант і методологій залежать від <c>IMethodologyStore</c>,
    /// <c>IConstantStore</c> та <c>ICalculationResultStore</c>, чиї
    /// реалізації належать Етапу 4. У Development контейнер перевіряється при
    /// побудові, тому реєстрація «на майбутнє» означала б, що застосунок не
    /// стартує взагалі (`Q-051`).
    ///
    /// ⚠ Модуль рівня 2 (скрипти Roslyn) не реєструватиметься й потім, доки
    /// не буде дозволу ІБ (K-1): його відсутність не має ламати систему —
    /// саме тому <c>ICalculationModule</c> резолвиться як колекція, а не як
    /// одиничний сервіс.
    /// </remarks>
    public static IServiceCollection AddEcrCalculations(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services;
    }
}
