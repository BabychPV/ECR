using Ecr.Application.Ports;
using Microsoft.Extensions.DependencyInjection;

namespace Ecr.Adapters.Sql;

/// <summary>Реєстрація адаптера довільного SQL-джерела.</summary>
public static class DependencyInjection
{
    /// <summary>Додає транспорт <c>Sql</c> (ФВ-11.8).</summary>
    /// <param name="services">Колекція служб.</param>
    /// <returns>Та сама колекція.</returns>
    /// <remarks>
    /// ⚠ Реєструється ЗАВЖДИ, як і обидва транспорти PI: вибір між ними — це
    /// <c>ext.DataSource.Transport</c>, тобто налаштування, а не гілка коду
    /// (ФВ-11.2). <c>CollectionRunner</c> бере з контейнера всі реалізації
    /// <see cref="IExternalDataSource"/> і шукає ту, чий <c>Transport</c>
    /// збігається; отже поява SQL-джерела в проді не потребує ні
    /// перезбирання, ні правки збирача.
    ///
    /// ⛔ <c>IExternalDataSink</c> не реєструється — його не існує (D-44).
    /// FLERT, як і PI AF, читається і ніколи не пишеться.
    /// </remarks>
    public static IServiceCollection AddSqlAdapters(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IExternalDataSource, SqlDataSource>();

        return services;
    }
}
