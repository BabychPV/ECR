using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
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

        // L1-01: збережений новий штамп скидає його кеш (`SecurityStampCacheInvalidator`).
        services.AddSingleton<SecurityStampCacheInvalidator>();
        services.AddDbContext<EcrDbContext>((sp, options) =>
            options.AddInterceptors(sp.GetRequiredService<SecurityStampCacheInvalidator>()).UseSqlServer(connectionString, sql =>
            {
                sql.MigrationsHistoryTable("__EFMigrationsHistory", "dbo");
                // ⛔ Саме `Database:`, а не `Sql:` (`S-11`). Префікс у файлі
                // перейменували, а обидва читачі лишили на старому — і
                // налаштування мовчки перестали діяти. Тут дефолт випадково
                // дорівнював значенню у файлі, тож дефект не проявлявся б до
                // першої зміни таймаута адміністратором.
                sql.CommandTimeout(ReadInt(configuration, "Database:CommandTimeoutSeconds", 60));

                // Повтори на транзієнтних збоях. ⚠ Транзакцію з таким
                // налаштуванням треба виконувати цілком усередині
                // ExecutionStrategy — інакше повтор розірве її посередині.
                sql.EnableRetryOnFailure(
                    maxRetryCount: 3,
                    maxRetryDelay: TimeSpan.FromSeconds(5),
                    errorNumbersToAdd: null);
            }));

        // F-13. Читач архіву, від якого залежать і NormalizedCellStore, і
        // RowStore (фолбек «гаряча схема порожня → перевір arc.*»); Scoped,
        // бо тримає EcrDbContext, а той теж Scoped.
        services.AddScoped<ArchiveAwareCellReader>();
        services.AddScoped<ICellStore, NormalizedCellStore>();
        services.AddScoped<IRowStore, RowStore>();
        services.AddScoped<IDocumentHeaderStore, DocumentHeaderStore>();
        services.AddScoped<ITableFillStore, TableFillStore>();
        services.AddScoped<IDocumentListSummaryStore, DocumentListSummaryStore>();
        services.AddScoped<ICampaignSummaryStore, CampaignSummaryStore>();
        services.AddSingleton(new Application.Reporting.CampaignProgressPolicy(Math.Max(0,
            ReadInt(configuration, "Campaign:AtRiskDays", Application.Reporting.CampaignProgressPolicy.DefaultAtRiskDays))));

        // Каталог джерела читають перед екраном: межа коротка, 1–60 с (ФВ-13.13).
        services.AddSingleton(new Application.Integration.SourceCatalogPolicy(TimeSpan.FromSeconds(Math.Clamp(
            ReadInt(configuration, "Integration:CatalogTimeoutSeconds", Application.Integration.SourceCatalogPolicy.DefaultTimeoutSeconds),
            1, 60))));
        services.AddScoped<ITemplateVersionStore, TemplateVersionStore>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IAuditWriter, AuditWriter>();
        services.AddScoped<IWorkflowStore, WorkflowStore>();
        services.AddScoped<ISheetEditGate, SheetEditGate>();
        services.AddSingleton(new SheetEditGatePolicy(TimeSpan.FromSeconds(Math.Clamp(
            ReadInt(configuration, "Database:SheetLockTimeoutSeconds", SheetEditGatePolicy.DefaultLockTimeoutSeconds),
            1, 300))));
        services.AddScoped<IDocumentVersionStore, DocumentVersionStore>();
        services.AddScoped<IDocumentVersionMigrationStore, DocumentVersionMigrationStore>();
        services.AddScoped<IPeriodStore, PeriodStore>();
        services.AddScoped<IAuditReader, AuditReader>();
        services.AddScoped<IConsistencyIssueReader, ConsistencyIssueReader>();
        services.AddScoped<IDocumentStore, DocumentStore>();
        services.AddScoped<IDocumentDeletionStore, DocumentDeletionStore>();
        services.AddScoped<IDocumentKeyStore, DocumentKeyStore>();
        services.AddScoped<IColumnDefSearchStore, ColumnDefSearchStore>();
        services.AddScoped<ISearchStore, SearchStore>();
        services.AddScoped<IValidationResultStore, ValidationResultStore>();
        services.AddScoped<IProjectStore, ProjectStore>();
        services.AddScoped<IRegistryStore, RegistryStore>();
        services.AddScoped<IRegistryUseStore, RegistryUseStore>(); // RT-23b
        services.AddScoped<IRegistryImpactStore, RegistryImpactStore>(); // RT-25
        services.AddScoped<IRegistryDraftStore, RegistryDraftStore>();
        services.AddScoped<IRegistryExternalKeyStore, RegistryExternalKeyStore>(); // FEATURE-REGISTRY-SYNC S2
        services.AddScoped<IUnitCatalog, UnitCatalog>();
        services.AddScoped<IUnitStore, UnitStore>();
        services.AddScoped<IWhereUsedStore, WhereUsedStore>();
        services.AddScoped<IUserPreferenceStore, UserPreferenceStore>();
        services.AddScoped<IRecalculationApprovalStore, RecalculationApprovalStore>();
        services.AddScoped<IMethodologyStore, MethodologyStore>();
        services.AddScoped<IRuleCoverageReader, RuleCoverageReader>();
        services.AddScoped<IColumnPathMapper, ColumnPathMapper>();
        services.AddScoped<IMethodologyDraftStore, MethodologyDraftStore>();
        services.AddScoped<IMethodologyVersionDeletionStore, MethodologyVersionDeletionStore>();
        services.AddScoped<IConstantStore, ConstantStore>();
        services.AddScoped<ICalculationResultStore, CalculationResultStore>();
        services.AddScoped<ICalculationBindingStore, CalculationBindingStore>();
        services.AddScoped<IOrphanScanner, OrphanScanner>();
        services.AddScoped(typeof(IRepository<,>), typeof(Repository<,>));

        // BulkCellLoader працює власним з'єднанням (SqlBulkCopy), тому рядок
        // підключення передається йому напряму, а не через DbContext.
        //
        // ⛔ `Database:BulkBatchSize`, не `Sql:BulkBatchSize` (`S-11`). Тут
        // розбіжність коштувала найдорожче: файл оголошує 50 000, читач із
        // чужим префіксом брав СВІЙ дефолт 5 000, і масове завантаження йшло
        // вдесятеро дрібнішими пакетами — без жодної ознаки ззовні. Дефолт
        // читача тепер дорівнює файлу (50 000): SqlBulkCopy стрімить рядки з
        // IDataReader, тож розмір пакета — межа транзакції, а не буфер у пам'яті.
        services.AddScoped(_ => new BulkCellLoader(
            connectionString, ReadInt(configuration, "Database:BulkBatchSize", 50_000)));

        // ⚠ Кеш метаданих — Scoped, а не Singleton, попри те що сам
        // IMemoryCache спільний: MetadataCache тримає EcrDbContext, а той
        // Scoped. Спільним лишається саме сховище кешу, тож ключ v{id}:r{rev}
        // працює між запитами так само (D-16).
        //
        // ⛔ `SizeLimit` НЕ задається, і це вибір, а не забудькуватість
        // (`RD-05`). До цього кроку тут був НАПІВСТАН: ліміту немає, а записи
        // несли `Size = …` разом із коментарями, що пояснювали неіснуючу
        // стелю. Вибрано другий бік розвилки — `Size` прибрано з усіх записів
        // `Caching/**`, бо ввімкнути ліміт звідси неможливо БЕЗПЕЧНО:
        // `MemoryCache` із `SizeLimit` кидає на КОЖНОМУ `Set` без `Size`, а
        // таких записувачів у це саме сховище двоє поза межами цього кроку —
        // `Security/SecurityStampValidator.cs` (перевірка штампа на кожен
        // запит, тобто шлях входу) і `Localization/UiStringCatalogStore.cs`.
        // Ліміт, увімкнений тут, поклав би вхід у систему — стеля пам'яті
        // ціною падіння автентифікації не є покращенням.
        //
        // ⚠ Пам'ять натомість тримає СТРОК: кожен запис у `Caching/**` має
        // абсолютну стелю життя (`Cache:*SlidingMinutes`, 15 хв довідники,
        // 5 с ревізія), тож безмежного зростання немає й без `SizeLimit`.
        // Увімкнення ліміту з `Size` в УСІХ записувачів процесу — окремий
        // крок, і він має починатися з тих двох файлів.
        services.AddMemoryCache();

        // ⚠ Singleton, і це несуча деталь: `MetadataCache` нижче — Scoped, і
        // словник «що зараз будується» мусить пережити окремий запит, інакше
        // зливати нічого (`RD-05`).
        services.AddSingleton<Caching.SingleFlight<TemplateVersionSnapshot>>();

        // ⚠ Строки кешів читаються ОДИН раз і передаються явною фабрикою, а не
        // через необов'язковий параметр конструктора: контейнер
        // Microsoft.Extensions.DependencyInjection значень за замовчуванням не
        // застосовує — він або резолвить параметр, або кидає. Дефолт у
        // сигнатурі лишається для прямих `new` (тести).
        services.AddSingleton(_ => CacheLifetimes.FromConfiguration(configuration));

        services.AddScoped<IMetadataCache>(sp => new MetadataCache(
            sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(),
            sp.GetRequiredService<EcrDbContext>(),
            sp.GetRequiredService<CacheLifetimes>(),
            sp.GetRequiredService<Caching.SingleFlight<TemplateVersionSnapshot>>()));

        // ⛔ `ITemplateStructure`/`CachedTemplateStructure` тут БІЛЬШЕ НЕМАЄ
        // (`Q-192`, `AR-06`, `unreachable-mechanisms.md` §4a). Порт існував
        // лише заради синхронного читання кешу в `ExtractDependencies`; після
        // `H-3` знімок приходить туди параметром, і викликати `.Get` стало
        // нікому — жодного виклику в `src/` не було. Разом із портом пішов
        // запис `rev:{id}` на кожен `GetAsync`, заведений під нього.

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
        services.AddSingleton(sp => new Caching.AccessProfileCache(
            sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(),
            sp.GetRequiredService<CacheLifetimes>()));
        services.AddSingleton<IAccessProfileInvalidator>(sp => sp.GetRequiredService<Caching.AccessProfileCache>());
        services.AddSingleton<IRegistryEntryCache, Caching.RegistryEntryCache>();
        // ⛔ V-06: назовні — обгортка, що підставляє профіль суб'єкта під час
        // симуляції. `SimulationService` отримує САМУ службу: обгортці він
        // потрібен, і через обгортку утворилося б коло залежностей.
        services.AddScoped<AccessDecisionService>();
        services.AddScoped<Application.Security.IAccessDecisionService>(sp => new SimulationAwareAccessDecisionService(
            sp.GetRequiredService<AccessDecisionService>(),
            sp.GetRequiredService<ISimulationService>(),
            sp.GetRequiredService<Application.Common.ICurrentUser>()));
        services.AddSingleton<Application.Security.IPasswordHasher, PasswordHasher>();
        services.AddScoped<SecurityStampValidator>();
        services.AddScoped<SelfStampRotation>();
        services.AddScoped<IUserStore>(sp => new UserStore(
            sp.GetRequiredService<EcrDbContext>(), sp.GetRequiredService<SelfStampRotation>()));
        services.AddScoped<Application.Ports.IEffectiveAccessStore, EffectiveAccessStore>();
        services.AddSingleton<Application.Ports.IPrincipalNameResolver, WindowsPrincipalNameResolver>();
        services.AddScoped<Application.Ports.IResourceNameResolver, ResourceNameResolver>();
        services.AddScoped<ISimulationService>(sp => new SimulationService(
            sp.GetRequiredService<EcrDbContext>(), sp.GetRequiredService<AccessDecisionService>()));

        // Каталог рядків інтерфейсу — Scoped через EcrDbContext; сам зріз
        // лежить у спільному IMemoryCache під ключем із версією (ФВ-14.9c).
        services.AddScoped<IUiStringCatalog, Localization.UiStringCatalogStore>();
        services.AddScoped<Application.Ports.INotificationOutbox, Integration.NotificationOutboxStore>();
        services.AddScoped<Application.Ports.ICellPatcher, Integration.IntegrationCellPatcher>();
        services.AddScoped<Application.Ports.ICoverageJournal, Integration.CoverageJournal>();
        services.AddScoped<Application.Ports.IMaterializeCollectedDataJob, Jobs.MaterializeCollectedDataJob>();

        // HSE301:A4 — автоперерахунок після запису без людини (V-5 → D-174).
        // Без цього рядка `MaterializeCollectedDataJob` отримав би `null` у
        // необов'язковому параметрі й мовчки не ставив би перерахунку.
        services.AddScoped<Application.Ports.ICalculationTrigger, Application.Calculations.CalculationTrigger>();
        // HSE301:A4 — кінець

        // ⛔ P0: технічний автор задач інтеграції (`svc-integration`). Сам
        // `JobActorScope` реєструє `Program.cs` разом з обгорткою
        // `ICurrentUser` — обидва мусять бути ОДНИМ екземпляром на scope.
        services.AddScoped<Jobs.IntegrationActor>();

        // Синк довідника (FEATURE-REGISTRY-SYNC S5): його ставить `CollectionJob`
        // для сутності з `RegistryDefId` — окремого коду в черзі немає.
        services.AddScoped<Jobs.IRegistrySyncJob, Jobs.RegistrySyncJob>();

        // ⚠ Планувальник тепер справжній. Quartz піднімається як hosted
        // service, а порт лишається тим самим: заміна на Hangfire, якщо ІБ
        // погодить LGPL, коштує день (D-09).
        services.AddQuartz(quartz =>
        {
            // Сховище в пам'яті: постійні розклади відновлюються при старті
            // (`RecurringScheduleService`), а разові задачі з черги перезапуску
            // процесу НЕ переживають. Це чинний стан, а не рішення `D-66` (те
            // про DDL і SQL Agent — `DIRECTIVE-14.md`, S-01); черга в базі —
            // рішення `D14-01`, ще не зроблене.
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
        services.AddScoped(sp => new Jobs.QuartzJobScheduler(
            sp.GetService<ISchedulerFactory>(),
            sp.GetService<IJobProgressStore>(),
            sp.GetService<IClock>(),
            sp.GetService<ICorrelationIdAccessor>(),
            sp.GetService<Microsoft.Extensions.Logging.ILogger<Jobs.QuartzJobScheduler>>(),
            // N-5: коренева фабрика - відкладена перепостановка бере власний scope.
            sp.GetService<IServiceScopeFactory>()));

        // MI-02 (F1c): черга в базі. Порти реєструються завжди (fencing читає оренду
        // й у режимі Quartz — там вона null); виконавець і адаптер — лише за
        // `Jobs:Queue:Mode = Database`. Дефолт — Quartz до зеленого F1d.
        services.AddScoped<IJobQueue, Jobs.DbJobQueue>();
        services.AddScoped<Jobs.JobLeaseContext>();
        services.AddScoped<IJobLeaseContext>(sp => sp.GetRequiredService<Jobs.JobLeaseContext>());
        services.AddSingleton<Jobs.JobQueueSignal>();
        if (Jobs.DbBackgroundJobScheduler.ReadMode(configuration) == Jobs.JobQueueMode.Database)
        {
            services.AddScoped<IBackgroundJobScheduler, Jobs.DbBackgroundJobScheduler>();
            services.AddSingleton(new Jobs.JobWorkerOptions
            {
                Lanes = Jobs.JobLaneMap.ApiLanes(Jobs.JobLaneMap.ReadExecutor(configuration)),
            });
            services.AddHostedService<Jobs.JobWorker>();

            // B5.10: gauge ecr.jobs.queue_depth; кеш раз на N секунд, лише в Api
            // (ChildComposition прибирає цю службу з воркера пулу).
            services.AddSingleton(Jobs.JobQueueDepthOptions.Read(configuration));
            services.AddSingleton<Jobs.IJobQueueDepthSource, Jobs.DbJobQueueDepthSource>();
            services.AddHostedService<Jobs.JobQueueDepthSampler>();
        }
        else
        {
            services.AddScoped<IBackgroundJobScheduler>(sp => sp.GetRequiredService<Jobs.QuartzJobScheduler>());

            // ⛔ L2-04: дренаж залишків черги після перемикання Database → Quartz
            // (runbook §10.1, deploy-ecr.ps1 -DisableWorker). Нові задачі йдуть у Quartz,
            // а рядки з Lane, що на мить перемикання стояли Queued (інкрементні формули,
            // перерахунки, імпорт) чи Running (дочірні вбито), інакше ніхто не виконав і не
            // закрив би: FailStale/SummarizeStale свідомо фільтрують Lane IS NULL, а Expire
            // кличе лише JobWorker. Рідке опитування — черга тут порожня майже завжди.
            services.AddSingleton(new Jobs.JobWorkerOptions
            {
                Lanes = JobLanes.All,
                PollInterval = Jobs.JobWorkerOptions.QuartzModeDrainPollInterval,
            });
            services.AddHostedService<Jobs.JobWorker>();
        }

        // ⚠ Задача реєструється як МАРКЕР IRecalculationJob, бо саме ним її
        // називає use-case. Без цього рядка `EnqueueAsync<IRecalculationJob>`
        // приймав би завдання, і не виконувалося б нічого.
        // ПРД-13: вимір бюджету перерахунку — і в Api, і в Ecr.Worker (обидва беруть це складання).
        services.AddSingleton(Jobs.RecalculationBudgetOptions.Read(configuration));
        services.AddSingleton<Jobs.RecalculationBudgetMonitor>();
        services.AddScoped<IRecalculationJob, Jobs.RecalculationJob>();
        services.AddScoped<IFormulaRecalculationJob, Jobs.FormulaRecalculationJob>();

        // ⚠ Кожна задача реєструється ПО ТИПУ: QuartzJobAdapter резолвить її
        // за повним іменем із JobDataMap. Незареєстрована задача приймалася б
        // у чергу і не виконувалася б — черга без виконавця ззовні виглядає
        // як «дуже довго рахує».
        services.AddScoped<Jobs.OrphanScanJob>();
        services.AddScoped<IOrphanScanJob, Jobs.OrphanScanJob>();
        services.AddScoped<Jobs.PeriodStateJob>();

        // ⚠ Матеріалізація PI з місця переходу періоду в Open/Grace — її кличуть
        // `PeriodStateJob` і `ActivateProjectHandler` після коміту.
        services.AddScoped<IMaterializationScheduler, Jobs.MaterializationScheduler>();
        services.AddScoped<Jobs.ArchiveJob>();
        services.AddScoped<Jobs.ConsistencyCheckJob>();
        services.AddScoped<Jobs.PartitionCheckJob>();
        services.AddScoped<Jobs.ReportRetentionJob>();
        services.AddScoped<Jobs.ReportSnapshotFormatJob>();
        services.AddScoped<Jobs.NotificationJob>();

        // ⚠ Розсилка черги — окремий компонент, бо відправників двоє: зведення
        // за розкладом і негайний алерт про відмову джерела в автентифікації
        // (`H-20`). Дві копії логіки «кому і як пішов лист» розійшлися б у
        // тому, що найважче помітити, — у тому, кому лист НЕ пішов.
        services.AddScoped<Integration.OutboxDispatcher>();

        // ⛔ Відправник за замовчуванням НЕ доставляє і не вдає, що доставив:
        // транспорт — рішення замовника (`P-13`). Реєстрація потрібна, щоб
        // задача створювалася; вона бачить `IsConfigured = false` і лишає
        // події в черзі.
        // ⚠ Відправник обирається ЗА КОНФІГУРАЦІЄЮ, а не прапорцем збірки:
        // контур без пошти і контур із поштою — це те саме розгортання з
        // різними змінними (`D-124`). Без `ECR_Smtp__Host` лишається
        // «не налаштовано», і черга накопичує, замість тихо губити події.
        // ✎ D-263: відправник реєструється ЗАВЖДИ — налаштування SMTP тепер можуть прийти з БД
        // (адмін-налаштування), а не лише з `Smtp:*`. «Не налаштовано» він повідомляє сам
        // (`IsConfigured`), і події лишаються в черзі.
        services.AddSingleton<Integration.SmtpNotificationSender>();
        services.AddSingleton<INotificationSender>(sp => sp.GetRequiredService<Integration.SmtpNotificationSender>());
        services.AddSingleton<ISmtpSettingsCache>(sp => sp.GetRequiredService<Integration.SmtpNotificationSender>());
        services.AddSingleton<Notifications.SmtpPasswordProtector>();
        services.AddSingleton<ISmtpPasswordProtector>(sp => sp.GetRequiredService<Notifications.SmtpPasswordProtector>());
        services.AddScoped<ISmtpSettingsStore, Notifications.SmtpSettingsStore>();

        // BE-33. ⛔ Перелік хостів вебхука — лише з конфігурації процесу:
        // порожній перелік означає «жоден вебхук не приймається».
        services.AddScoped<INotificationStore, Notifications.NotificationStore>();
        services.AddSingleton<INotificationSecretProtector, Notifications.DataProtectionNotificationSecretProtector>();
        services.AddSingleton(new Application.Notifications.WebhookUrlPolicy(
            (configuration["Notifications:WebhookAllowedHostSuffixes"] ?? string.Empty).Split(';', ',')));

        // BE-34. Диспетчер каналів і відправники транспортів.
        // ⚠ Відправники реєструються як КОЛЕКЦІЯ (`IEnumerable<INotificationChannelSender>`):
        // диспетчер обирає за транспортом каналу, а незареєстрований транспорт
        // дає рядок `Failed` у журналі доставок, не тишу.
        services.AddScoped<INotificationDispatchStore, Notifications.NotificationDispatchStore>();
        services.AddScoped<Notifications.NotificationDispatcher>();
        services.AddScoped<INotificationChannelSender, Notifications.TeamsWebhookSender>();

        // ⚠ SMTP-канал іде поверх транспорту ПРОЦЕСУ, вибраного вище: адресатів
        // дає канал, сервер і облікові дані — конфігурація. Без цього рядка
        // кожне правило на SMTP-канал лишало б рядок `Failed` «відправника не
        // зареєстровано» — чесний, але марний.
        services.AddScoped<INotificationChannelSender, Notifications.SmtpChannelSender>();

        // ⛔ Іменований клієнт, а не `new HttpClient`: власноруч створений тримає
        // з'єднання після зміни DNS. Таймаут виставляє САМ відправник
        // (`TeamsWebhookSender.Timeout`) — тут лише реєстрація фабрики, щоб
        // забута тут лямбда не могла мовчки повернути типові 100 секунд.
        services.AddHttpClient(Notifications.TeamsWebhookSender.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(Notifications.TeamsWebhookSender.CreateHandler); // S9: без редиректів

        // ⚠ Задачі, які use-case називає МАРКЕРОМ, реєструються ще й за ним:
        // `EnqueueAsync<IReportSnapshotJob>` кладе в JobDataMap повне імʼя
        // саме маркера, і без цієї реєстрації адаптер Quartz не знайшов би
        // виконавця — черга приймала б завдання і не робила нічого.
        services.AddScoped<IReportSnapshotJob, Jobs.ReportSnapshotJob>();
        services.AddScoped<ICollectionJob, Jobs.CollectionJob>();

        // HSE301 A5b: синк подій джерела в рядки таблиць. Ставить його `CollectionJob` (сутність із
        // активним `SourceEventMap`) — за маркером, як решту задач у черзі.
        services.AddScoped<ISourceEventSyncJob, Jobs.SourceEventSyncJob>();
        services.AddScoped<IRegistryImpactRecalculationJob, Jobs.RegistryImpactRecalculationJob>(); // RT-25: батьківська задача перерахунку зачеплених

        // HSE301:A1 — підтягування значень PI за вікном рядка (§4.4). Задачу ставить хук запису комірок
        // (`IRowWindowTrigger` у `PatchCellsHandler`) і щогодинний `RowWindowRefetchJob` (конкретний клас — як
        // `PeriodStateJob`, ставиться розкладом у `RecurringScheduleService`). Знімок колонок вікна — одиночка:
        // хук питає його на кожен запис комірок.
        services.AddScoped<IRowWindowFetchJob, Jobs.RowWindowFetchJob>();
        services.AddScoped<Jobs.RowWindowRefetchJob>();
        services.AddSingleton<Application.Ports.IRowWindowColumnIndex, Jobs.RowWindowColumnIndex>();
        services.AddScoped<Application.Ports.IRowWindowTrigger, Application.Integration.RowWindowTrigger>();
        // HSE301:A1 — кінець

        // ⚠ Та сама задача, що вже зареєстрована по типу вище: нічний розклад
        // ставить її конкретним класом, а `POST /consistency/run` — маркером
        // (`BE-30`). Без цього рядка ручний прогін приймався б у чергу й не
        // виконувався б — черга без виконавця ззовні виглядає як «дуже довго».
        services.AddScoped<IConsistencyCheckJob, Jobs.ConsistencyCheckJob>();

        services.AddScoped<IExcelExportJob, Jobs.ExcelExportJob>();
        services.AddScoped<IExcelImportJob, Jobs.ExcelImportJob>();

        // Сховища Етапу 5.
        services.AddScoped<IJobProgressStore, JobProgressStore>();
        services.AddSingleton<HealthCountCache>(); // кеш sources.failed на 60 с (на хост)
        services.AddScoped<ISystemHealthStore, SystemHealthStore>();
        services.AddScoped<ICollectionStore, CollectionStore>();
        services.AddScoped<ISourceEventMapStore, SourceEventMapStore>();
        services.AddScoped<IRowWindowMapStore, RowWindowMapStore>(); // HSE301 A1
        services.AddScoped<ICollectionScheduleStore, CollectionScheduleStore>();
        services.AddScoped<IDataSourceStore, DataSourceStore>();
        services.AddScoped<ICollectionRunReader, CollectionRunReader>();
        services.AddScoped<Ecr.Application.Sources.IMappingPreviewStore, MappingPreviewStore>();
        services.AddScoped<IStyleCatalog, StyleCatalog>();
        services.AddScoped<IConditionalFormatStore, ConditionalFormatStore>();
        services.AddScoped<IReportDefinitionStore, ReportDefinitionStore>();
        services.AddScoped<IReportSnapshotBuilder, Reporting.ReportSnapshotBuilder>();
        services.AddSingleton<IReportViewStatus, Reporting.ReportViewStatus>();
        services.AddScoped<IReportViewGenerator, Reporting.ReportViewGenerator>();
        services.AddSingleton<ISecretProvider, ConfigurationSecretProvider>();
        services.AddSingleton<Ecr.Application.Ports.IEndpointNetwork, Integration.EndpointNetwork>();

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

        // ── FEATURE-REGISTRY-TABLES: append-only блоки треків (RT-01) ──
        // Крок дописує реєстрації ЛИШЕ під свій маркер; власники —
        // docs/build/FEATURE-REGISTRY-TABLES.md §9.0 і §9.1.
        // RT: keys
        services.AddScoped<IRegistryKeyStore, RegistryKeyStore>(); // RT-10a

        // RT: data
        services.AddScoped<IRegistryRowsQuery, RegistryRowsQuery>(); // RT-13

        // RT: expressions
        services.AddScoped<IRegistrySnapshotLoader, RegistrySnapshotLoader>(); // RT-22

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
