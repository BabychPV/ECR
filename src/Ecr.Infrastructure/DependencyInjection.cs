using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.Infrastructure.Startup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Quartz;

namespace Ecr.Infrastructure;

/// <summary>Реєстрація інфраструктури в контейнері.</summary>
public static class DependencyInjection
{
    /// <summary>Ім'я рядка підключення. Значення задається змінною <c>ECR_ConnectionStrings__Ecr</c>.</summary>
    private const string ConnectionName = "Ecr";

    /// <summary>Додає EF Core, сховища, кеш і безпеку.</summary>
    /// <remarks>
    /// ⚠ Тут реєструється лише те, чиї реалізації **існують**. Порти етапу 5
    /// (<c>ICollectionStore</c>,
    /// <c>IJobProgress</c>)
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
        services.AddScoped<IWorkflowStore, WorkflowStore>();
        services.AddScoped<IPeriodStore, PeriodStore>();
        services.AddScoped<IAuditReader, AuditReader>();
        services.AddScoped<IDocumentStore, DocumentStore>();
        services.AddScoped<IValidationResultStore, ValidationResultStore>();
        services.AddScoped<IProjectStore, ProjectStore>();
        services.AddScoped<IRegistryStore, RegistryStore>();
        services.AddScoped<IUnitCatalog, UnitCatalog>();
        services.AddScoped<IMethodologyStore, MethodologyStore>();
        services.AddScoped<IConstantStore, ConstantStore>();
        services.AddScoped<ICalculationResultStore, CalculationResultStore>();
        services.AddScoped<IOrphanScanner, OrphanScanner>();
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

        // ⛔ Реєстрація без споживача. Тут стояло «потрібен рушію виразів, бо
        // ExtractDependencies у контракті синхронний» — і це вже неправда:
        // після `H-3` порт приймає знімок ПАРАМЕТРОМ, тож у кеш не лізе ніхто.
        //
        // ⚠ Лишена свідомо і на один крок: прибирати порт означає правити
        // `02-contracts.md`, знімати синхронний аксесор у `MetadataCache` і
        // проходити двома сторожами портів. Записано в
        // `unreachable-mechanisms.md` як ⛔, щоб не загубитися.
        services.AddSingleton<ITemplateStructure, Caching.CachedTemplateStructure>();

        // Рушій виразів. Парсер, обчислювач і сортувальник без стану —
        // Singleton; сам рушій теж, бо знімок бере з кешу, а не тримає.
        services.AddSingleton<Ecr.Expressions.Parsing.Parser>();
        services.AddSingleton<Ecr.Expressions.Evaluation.Evaluator>();
        services.AddSingleton<Ecr.Expressions.Graph.TopologicalSorter>();
        services.AddSingleton<Ecr.Expressions.Functions.FunctionRegistry>();
        services.AddSingleton<IFormulaEngine, Expressions.FormulaEngine>();

        // Безпека. AccessProfileCache — Singleton поверх IMemoryCache: профіль
        // будується раз на (користувач × SecurityStamp), і зміна штампа сама
        // дає новий ключ, тому інвалідація не потрібна (ФВ-6.7).
        services.AddSingleton<Caching.AccessProfileCache>();
        services.AddSingleton<IRegistryEntryCache, Caching.RegistryEntryCache>();
        services.AddScoped<Application.Security.IAccessDecisionService, AccessDecisionService>();
        services.AddSingleton<Application.Security.IPasswordHasher, PasswordHasher>();
        services.AddScoped<SecurityStampValidator>();
        services.AddScoped<IUserStore, UserStore>();
        services.AddScoped<ISimulationService, SimulationService>();

        // Каталог рядків інтерфейсу — Scoped через EcrDbContext; сам зріз
        // лежить у спільному IMemoryCache під ключем із версією (ФВ-14.9c).
        services.AddScoped<IUiStringCatalog, Localization.UiStringCatalogStore>();
        services.AddScoped<Application.Ports.INotificationOutbox, Integration.NotificationOutboxStore>();
        services.AddScoped<Application.Ports.ICellPatcher, Integration.IntegrationCellPatcher>();
        services.AddScoped<Application.Ports.ICoverageJournal, Integration.CoverageJournal>();
        services.AddScoped<Application.Ports.IMaterializeCollectedDataJob, Jobs.MaterializeCollectedDataJob>();

        // ⚠ Планувальник тепер справжній. Quartz піднімається як hosted
        // service, а порт лишається тим самим: заміна на Hangfire, якщо ІБ
        // погодить LGPL, коштує день (D-09).
        services.AddQuartz(quartz =>
        {
            // Сховище в пам'яті: розклад описаний у коді й відновлюється при
            // старті. Персистентне сховище Quartz мало б власну схему в нашій
            // базі, а застосунок DDL-прав не має (D-66).
            quartz.UseSimpleTypeLoader();
            quartz.UseInMemoryStore();
        });

        services.AddQuartzHostedService(options =>
        {
            // ⚠ Дочекатися завершення задач при зупинці. Убитий посеред
            // пакета перерахунок лишив би половину результатів записаними, і
            // жоден статус про це не сказав би.
            options.WaitForJobsToComplete = true;
        });

        // ⚠ Місток Quartz → IBackgroundJob. Transient, бо Quartz створює
        // екземпляр на кожен запуск; свій scope задача відкриває сама.
        services.AddTransient<Jobs.QuartzJobAdapter>();

        // ⚠ Scoped, а не Singleton: прогрес живе в itg.JobProgress, тобто в
        // DbContext, а той scoped. Singleton тримав би один контекст на всі
        // одночасні постановки в чергу.
        services.AddScoped<IBackgroundJobScheduler>(sp => new Jobs.QuartzJobScheduler(
            sp.GetService<ISchedulerFactory>(),
            sp.GetService<IJobProgressStore>(),
            sp.GetService<IClock>()));

        // ⚠ Задача реєструється як МАРКЕР IRecalculationJob, бо саме ним її
        // називає use-case. Без цього рядка `EnqueueAsync<IRecalculationJob>`
        // приймав би завдання, і не виконувалося б нічого.
        services.AddScoped<IRecalculationJob, Jobs.RecalculationJob>();
        services.AddScoped<IFormulaRecalculationJob, Jobs.FormulaRecalculationJob>();

        // ⚠ Кожна задача реєструється ПО ТИПУ: QuartzJobAdapter резолвить її
        // за повним іменем із JobDataMap. Незареєстрована задача приймалася б
        // у чергу і не виконувалася б — черга без виконавця ззовні виглядає
        // як «дуже довго рахує».
        services.AddScoped<Jobs.OrphanScanJob>();
        services.AddScoped<Jobs.PeriodStateJob>();
        services.AddScoped<Jobs.ArchiveJob>();
        services.AddScoped<Jobs.ConsistencyCheckJob>();
        services.AddScoped<Jobs.PartitionCheckJob>();
        services.AddScoped<Jobs.NotificationJob>();

        // ⛔ Відправник за замовчуванням НЕ доставляє і не вдає, що доставив:
        // транспорт — рішення замовника (`P-13`). Реєстрація потрібна, щоб
        // задача створювалася; вона бачить `IsConfigured = false` і лишає
        // події в черзі.
        // ⚠ Відправник обирається ЗА КОНФІГУРАЦІЄЮ, а не прапорцем збірки:
        // контур без пошти і контур із поштою — це те саме розгортання з
        // різними змінними (`D-124`). Без `ECR_Smtp__Host` лишається
        // «не налаштовано», і черга накопичує, замість тихо губити події.
        var smtpHost = configuration["Smtp:Host"];

        if (string.IsNullOrWhiteSpace(smtpHost))
        {
            services.AddSingleton<INotificationSender, Jobs.UnconfiguredNotificationSender>();
        }
        else
        {
            services.AddSingleton<INotificationSender, Integration.SmtpNotificationSender>();
        }

        // ⚠ Задачі, які use-case називає МАРКЕРОМ, реєструються ще й за ним:
        // `EnqueueAsync<IReportSnapshotJob>` кладе в JobDataMap повне імʼя
        // саме маркера, і без цієї реєстрації адаптер Quartz не знайшов би
        // виконавця — черга приймала б завдання і не робила нічого.
        services.AddScoped<IReportSnapshotJob, Jobs.ReportSnapshotJob>();
        services.AddScoped<ICollectionJob, Jobs.CollectionJob>();
        services.AddScoped<IExcelExportJob, Jobs.ExcelExportJob>();

        // Сховища Етапу 5.
        services.AddScoped<IJobProgressStore, JobProgressStore>();
        services.AddScoped<ICollectionStore, CollectionStore>();
        services.AddScoped<IStyleCatalog, StyleCatalog>();
        services.AddScoped<IReportDefinitionStore, ReportDefinitionStore>();
        services.AddScoped<IReportSnapshotBuilder, Reporting.ReportSnapshotBuilder>();
        services.AddSingleton<ISecretProvider, ConfigurationSecretProvider>();

        // ⚠ Diff імпорту живе в РОЗПОДІЛЕНОМУ кеші: перегляд і застосування —
        // два запити, і другий може потрапити на інший інстанс.
        services.AddDistributedSqlServerCache(cache =>
        {
            cache.ConnectionString = connectionString;
            cache.SchemaName = configuration["Cache:SchemaName"] ?? "dbo";
            cache.TableName = configuration["Cache:TableName"] ?? "Cache";
        });

        services.AddScoped<IImportPreviewStore, ImportPreviewStore>();
        services.AddScoped<IExportStore, ExportStore>();

        // Прогрів кешу метаданих на старті (B01 §6.3, крок 7).
        services.AddScoped<MetadataWarmup>();

        // ⚠ Доменні сервіси — Singleton: вони не мають стану і не тримають
        // з'єднань. Без цих двох рядків не створювалися б ані PeriodStateJob,
        // ані збирач PI AF — а побачити це можна було б лише на живому старті.
        services.AddSingleton<Domain.Services.UnitConverter>();
        services.AddSingleton<Domain.Services.PeriodStateCalculator>();

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
