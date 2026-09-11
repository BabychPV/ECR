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
        //
        // ⚠ Таймаут — 30 с, а не типові 100 с (Q-250). GetAsync повторює
        // відповідь 5xx/таймаут до трьох разів із паузами 2 с і 4 с
        // (PiWebApiDataSource.MaxAttempts/RetryDelay): при дефолтних 100 с
        // одна лише «напівживу» відповідь джерела (TCP приймає, тіла не
        // віддає) розтягувала одну спробу збору до ~306 с (3×100 с + 2 с +
        // 4 с), тримаючи воркер Quartz (пул за замовчуванням ~10) увесь цей
        // час — а `CollectionRunner.RunAsync` іде по інтервалах і атрибутах
        // ПОСЛІДОВНО, тож повільне джерело множило цю затримку на кожен
        // крок наздоганяння. Немає задокументованого SLA відповіді PI Web
        // API в цьому репозиторії — 30 с узято як практичний поріг «досить
        // повільно, щоб не чекати», не з довідника постачальника.
        services.AddHttpClient<PiWebApiDataSource>()
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30));

        services.AddScoped<IExternalDataSource, PiWebApiDataSource>();
        services.AddScoped<IExternalDataSource, PiSqlClientDataSource>();

        services.AddScoped<SourceUnitConverter>();
        services.AddScoped<CatchUpPlanner>();
        services.AddScoped<PiAfCatalogReader>();
        services.AddScoped<ICollectionRunner, CollectionRunner>();

        return services;
    }
}
