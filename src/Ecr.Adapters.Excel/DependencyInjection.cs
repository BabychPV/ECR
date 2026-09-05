using Ecr.Application.Ports;
using Microsoft.Extensions.DependencyInjection;

namespace Ecr.Adapters.Excel;

/// <summary>Реєстрація адаптерів Excel.</summary>
public static class DependencyInjection
{
    /// <summary>Додає експорт, імпорт і їхні допоміжні класи.</summary>
    /// <remarks>
    /// ⚠ Імпорт реєструється ЛИШЕ разом із <see cref="ImportDiffBuilder"/>:
    /// імпорт без попереднього перегляду diff заборонений (ФВ-4.3), і
    /// відсутність будівника diff зробила б цю заборону непрацездатною —
    /// контейнер просто не створив би імпортера, а помилку побачили б на
    /// живому запиті.
    ///
    /// ⚠ Scoped, а не Singleton: обидва працюють із <c>DbContext</c> через
    /// порти, а той scoped. Singleton тримав би один контекст на всі
    /// одночасні експорти.
    /// </remarks>
    public static IServiceCollection AddExcelAdapters(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Без стану — тому Singleton: стиль і трансляція формули є чистими
        // функціями від своїх аргументів.
        services.AddSingleton<StyleMapper>();
        services.AddSingleton<FormulaTranslator>();

        services.AddScoped<ImportDiffBuilder>();
        services.AddScoped<IExcelExporter, ExcelExporter>();
        services.AddScoped<IExcelImporter, ExcelImporter>();

        return services;
    }
}
