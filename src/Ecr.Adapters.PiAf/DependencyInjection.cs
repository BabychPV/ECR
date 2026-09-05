using Ecr.Application.Ports;
using Microsoft.Extensions.DependencyInjection;

namespace Ecr.Adapters.PiAf;

/// <summary>Реєстрація адаптерів PI AF.</summary>
public static class DependencyInjection
{
    /// <summary>Додає обидва транспорти читання.</summary>
    /// <remarks>
    /// ⚠ Обидва транспорти реєструються ЗАВЖДИ, а вибір між ними — це
    /// <c>ext.DataSource.Transport</c>, тобто налаштування, а не гілка коду
    /// (ФВ-11.2). Реєструвати «той, що потрібен», означало б, що зміна
    /// джерела в конфігурації потребує перезбирання.
    ///
    /// ⛔ <c>IExternalDataSink</c> не реєструється ніколи — його не існує
    /// (D-44): система читає із зовнішніх джерел і не пише в них.
    /// </remarks>
    public static IServiceCollection AddPiAfAdapters(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // HttpClient через фабрику: власноруч створений і збережений у полі
        // клієнт тримає з'єднання після зміни DNS, і збір починає ходити на
        // адресу, якої вже немає.
        services.AddHttpClient<PiWebApiDataSource>();

        services.AddScoped<IExternalDataSource, PiWebApiDataSource>();
        services.AddScoped<IExternalDataSource, PiSqlClientDataSource>();

        services.AddScoped<SourceUnitConverter>();
        services.AddScoped<CatchUpPlanner>();
        services.AddScoped<PiAfCatalogReader>();
        services.AddScoped<ICollectionRunner, CollectionRunner>();

        return services;
    }
}
