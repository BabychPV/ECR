using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.Infrastructure.Startup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ecr.Infrastructure;

/// <summary>Реєстрація інфраструктури в контейнері.</summary>
public static class DependencyInjection
{
    /// <summary>Ім'я рядка підключення. Значення задається змінною <c>ECR_ConnectionStrings__Ecr</c>.</summary>
    private const string ConnectionName = "Ecr";

    /// <summary>Додає EF Core, сховища, кеш і безпеку.</summary>
    /// <remarks>
    /// ⚠ Тут реєструється лише те, чиї реалізації **існують**. Порти етапів
    /// 2–5 (<c>ICollectionStore</c>, <c>IMethodologyStore</c>,
    /// <c>IConstantStore</c>, <c>ICalculationResultStore</c>,
    /// <c>IOrphanScanner</c>, <c>ISimulationService</c>,
    /// <c>IUiStringCatalog</c>, <c>IRecalculationJob</c>, <c>IJobProgress</c>)
    /// не реєструються, бо реалізацій ще немає. Зареєструвати їх «на майбутнє»
    /// не можна: у Development контейнер перевіряється при побудові, і
    /// застосунок просто не стартував би.
    /// </remarks>
    public static IServiceCollection AddEcrInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString = configuration.GetConnectionString(ConnectionName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // ⚠ Саме IsNullOrWhiteSpace, а не перевірка на null: у
            // appsettings.json ключ присутній із ПОРОЖНІМ значенням (Q-029) —
            // щоб було видно, що він існує, і щоб секрет не потрапив у файл.
            // Перевірка лише на null пропустила б порожній рядок далі, і
            // падало б аж у SqlConnection із «ConnectionString property has
            // not been initialized» — без натяку, де саме шукати.
            throw new InvalidOperationException(
                "Рядок підключення 'Ecr' не заданий. Він береться зі змінної оточення " +
                "ECR_ConnectionStrings__Ecr і НЕ зберігається в appsettings.json (D-11).");
        }

        services.AddDbContext<EcrDbContext>(options =>
            options.UseSqlServer(connectionString, sql =>
            {
                sql.MigrationsHistoryTable("__EFMigrationsHistory", "dbo");
                sql.CommandTimeout(ReadInt(configuration, "Sql:CommandTimeoutSeconds", 60));

                // Повтори на транзієнтних збоях. ⚠ Транзакцію з таким
                // налаштуванням треба виконувати цілком усередині
                // ExecutionStrategy — інакше повтор розірве її посередині.
                sql.EnableRetryOnFailure(
                    maxRetryCount: 3,
                    maxRetryDelay: TimeSpan.FromSeconds(5),
                    errorNumbersToAdd: null);
            }));

        services.AddScoped<ICellStore, NormalizedCellStore>();
        services.AddScoped<IRowStore, RowStore>();
        services.AddScoped<ITemplateVersionStore, TemplateVersionStore>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IAuditWriter, AuditWriter>();
        services.AddScoped(typeof(IRepository<,>), typeof(Repository<,>));

        // BulkCellLoader працює власним з'єднанням (SqlBulkCopy), тому рядок
        // підключення передається йому напряму, а не через DbContext.
        services.AddScoped(_ => new BulkCellLoader(
            connectionString, ReadInt(configuration, "Sql:BulkBatchSize", 5_000)));

        // ⚠ Кеш метаданих — Scoped, а не Singleton, попри те що сам
        // IMemoryCache спільний: MetadataCache тримає EcrDbContext, а той
        // Scoped. Спільним лишається саме сховище кешу, тож ключ v{id}:r{rev}
        // працює між запитами так само (D-16).
        services.AddMemoryCache();
        services.AddScoped<IMetadataCache, MetadataCache>();

        // Безпека. AccessProfileCache — Singleton поверх IMemoryCache: профіль
        // будується раз на (користувач × SecurityStamp), і зміна штампа сама
        // дає новий ключ, тому інвалідація не потрібна (ФВ-6.7).
        services.AddSingleton<Caching.AccessProfileCache>();
        services.AddScoped<Application.Security.IAccessDecisionService, AccessDecisionService>();
        services.AddSingleton<Application.Security.IPasswordHasher, PasswordHasher>();
        services.AddScoped<SecurityStampValidator>();

        services.AddSingleton<ISqlCapabilities>(_ => new SqlCapabilitiesProbe());
        services.AddSingleton<IClock, SystemClock>();

        // ⚠ IExternalDataSink НЕ реєструється: його не існує (D-44).
        return services;
    }

    /// <summary>
    /// Ціле число з конфігурації або значення за замовчуванням.
    /// </summary>
    /// <remarks>
    /// Власний зчитувач замість <c>GetValue&lt;T&gt;</c>: той живе в пакеті
    /// <c>Configuration.Binder</c>, а `Ecr.Infrastructure` посилається лише на
    /// <c>Configuration.Abstractions</c>. Тягнути пакет заради двох чисел —
    /// гірше, ніж три рядки розбору.
    /// </remarks>
    private static int ReadInt(IConfiguration configuration, string key, int fallback)
        => int.TryParse(configuration[key], System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
}
