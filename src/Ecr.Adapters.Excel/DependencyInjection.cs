using Microsoft.Extensions.DependencyInjection;

namespace Ecr.Adapters.Excel;

/// <summary>Реєстрація адаптерів Excel.</summary>
/// <remarks>
/// ⚠ Файл оголошений у дереві `05-skeleton.md` §1 і в заголовку секції `05g`,
/// але блок коду в пакеті є лише для PI AF (Q-010, Q-015). Метод
/// <c>AddExcelAdapters()</c> викликається з <c>Program.cs</c>, тому без нього
/// не збирається `Ecr.Api`.
/// </remarks>
public static class DependencyInjection
{
    /// <summary>Додає експорт, імпорт і їхні допоміжні класи.</summary>
    /// <remarks>
    /// ⚠ Поки не реєструє нічого: експорт та імпорт — Етап 5, і їхні класи
    /// залежать від портів, реалізацій яких ще немає (`Q-051`).
    ///
    /// ⚠ Коли дійде черга: імпорт реєструється ЛИШЕ разом із
    /// <c>ImportDiffBuilder</c> — імпорт без попереднього перегляду diff
    /// заборонений (ФВ-4.3).
    /// </remarks>
    public static IServiceCollection AddExcelAdapters(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services;
    }
}
