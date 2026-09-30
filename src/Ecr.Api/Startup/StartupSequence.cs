using Ecr.Application.Ports;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Startup;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Api.Startup;

/// <summary>
/// Послідовність старту застосунку (B01 §6.3).
/// </summary>
/// <remarks>
/// ⚠ Файла немає ні в дереві `05-skeleton.md` §1, ні в `05h`, але
/// <c>Program.cs</c> викликає <c>app.RunEcrStartupSequenceAsync()</c> —
/// без нього не збирається `Ecr.Api` (Q-015).
///
/// <b>Порядок кроків значущий</b> і не є стилем: прогрів кешу до валідації
/// метаданих закешує невалідні метадані, а seed до міграцій впаде на
/// відсутніх таблицях.
/// </remarks>
public static partial class StartupSequence
{
    /// <summary>Скільки разів чекати на базу, перш ніж здатися.</summary>
    private const int DatabaseRetries = 10;

    /// <summary>Пауза між спробами.</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

    /// <summary>Виконує кроки старту в порядку, заданому B01 §6.3.</summary>
    public static async Task RunEcrStartupSequenceAsync(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger("Ecr.Startup");

        // ⚠ ПЕРШИМ ділом — журнал Quartz на фабрику ЦЬОГО хоста. Quartz
        // тримає постачальника журналу в статичному полі, і без цього рядка
        // другий хост у тому самому процесі (а саме так працює
        // WebApplicationFactory) звертався б до вже закритої фабрики й падав
        // ще до першого запиту.
        Infrastructure.Jobs.QuartzLogging.UseHost(loggerFactory);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EcrDbContext>();

        // 1) Дочекатися БД. Стартувати без неї не можна: застосунок без бази
        //    не «частково працює», він не працює зовсім, і краще, щоб це було
        //    видно як невдалий старт, а не як 500 на кожен запит.
        await WaitForDatabaseAsync(db, logger).ConfigureAwait(false);

        // 2) Можливості СУБД. Читаються один раз: редакція між запитами не
        //    змінюється, а кожна перевірка коштує запиту. ⚠ ДО перевірки схеми:
        //    валідатор вирішує про редакцію саме за цими даними, і з
        //    непрочитаною пробою він бачив би нулі й пропускав будь-який сервер.
        var capabilities = scope.ServiceProvider.GetRequiredService<ISqlCapabilities>();
        await ProbeCapabilitiesAsync(app, db, capabilities, logger).ConfigureAwait(false);

        // 3) Сумісність середовища (ФВ-7.9) — ЄДИНЕ джерело цих перевірок,
        //    `SchemaValidator`. Зупиняє старт (`ECR-SYS-5031`): редакція/версія,
        //    незастосовані міграції в Validate, база новіша за збірку, файлові
        //    групи, функції й схеми партиціонування. Без зупинки: RCSI —
        //    Critical у журналі (і Unhealthy на /health/ready, D-102);
        //    зіставлення, запас партицій — попередження. У проді застосунок DDL-прав не має
        //    (D-66), тому Validate — це саме перевірка, а не тихе «домігруємо».
        var mode = app.Configuration["Schema:StartupMode"] ?? "Validate";
        var validator = new SchemaValidator(
            db, capabilities, scope.ServiceProvider.GetRequiredService<Domain.Abstractions.IClock>());
        await validator.ValidateAsync(mode, CancellationToken.None).ConfigureAwait(false);

        foreach (var finding in validator.Critical)
        {
            LogSchemaCritical(logger, finding);
        }

        foreach (var warning in validator.Warnings)
        {
            LogSchemaWarning(logger, warning);
        }

        LogSchemaValid(logger);

        // 4) Ідемпотентний seed. Без нього немає ні мов, ні прав, ні одиниць —
        //    застосунок формально піднімається і не робить нічого.
        await new SeedRunner(db, logger).RunAsync(CancellationToken.None).ConfigureAwait(false);
        LogSeedDone(logger);

        // 4a) Bootstrap-адміністратор. ⛔ Крок був ОГОЛОШЕНИЙ (обробник є,
        //     зареєстрований, покритий тестами) і НЕ ВИКЛИКАВСЯ (`A7-10`):
        //     у щойно розгорнутій системі не було жодного користувача, і
        //     увійти не міг ніхто. Ані збірка, ані 597 тестів цього не
        //     бачили — обробник перевірявся напряму, а старт його не звав.
        //
        //     ⚠ Йде ПІСЛЯ seed: роль `Administrator` створює саме seed, і
        //     без неї видати її нікому.
        // ⚠ Директива №13 (Q-215): одноразовий файл — ПЕРШЕ джерело; змінна
        // оточення ECR_Bootstrap__Password лишається запасним шляхом для
        // dev/CI. Файл читається й видаляється тут-таки, ДО виклику
        // HandleAsync — не після, щоб виняток усередині HandleAsync не
        // лишив пароль непрочищеним.
        var secretFile = BootstrapSecretFile.ReadAndDelete(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));

        if (secretFile.DeleteError is not null)
        {
            LogBootstrapSecretNotDeleted(logger, secretFile.DeleteError);
        }

        var bootstrap = scope.ServiceProvider
            .GetRequiredService<Application.Security.EnsureBootstrapAdminHandler>();

        await bootstrap
            .HandleAsync(secretFile.Password ?? app.Configuration["Bootstrap:Password"], CancellationToken.None)
            .ConfigureAwait(false);

        // ⚠ Попередження ВИДИМІ. «Увійти буде нікому» — це стан, про який
        // адміністратор має дізнатися зі старту, а не зі скарги користувача.
        foreach (var warning in bootstrap.Warnings)
        {
            LogBootstrapWarning(logger, warning);
        }

        LogBootstrapOutcome(logger, bootstrap.LastOutcome);

        // 5) ПОКИНУТІ фонові задачі. ⛔ Задача, яку виконував процес, що
        //     впав, лишається `Running`/`Queued` у базі НАЗАВЖДИ — процеса,
        //     який мав позначити її `Failed`, уже немає. Без цього кроку така
        //     задача показує оператору «виконується» місяцями, і ЗБІГ УВАГИ
        //     (директива №09 §6.5, `S-25`) — без сліду про причину.
        //
        // ⛔ Крок валить ЛИШЕ покинуті задачі — ті, чиє биття серця застигло
        //     довше за `IJobProgressStore.StaleAfter`. Раніше предиката не
        //     було зовсім, і цей самий рядок на старті інстанса B позначав
        //     `Failed` перерахунки й імпорти, які в цю мить виконував інстанс
        //     A. Інстанс у розгортанні не один (ціль — 100 одночасних
        //     користувачів), тож це був не крайній випадок, а щоденний
        //     наслідок будь-якого розгортання.
        //
        // ⚠ U4/U11: той самий прохід, що й періодичне прибирання
        //     (`RecurringScheduleService.SweepOnceAsync`), — разом із журналами
        //     прогонів збору й обслуговування, які раніше не прибирав ніхто.
        //     Рядки ПОПЕРЕДНЬОГО процесу цієї ж машини закриваються НЕЗАЛЕЖНО від
        //     свіжості биття — за `itg.JobProgress.InstanceId`; рядки інших машин
        //     — як і раніше, лише за віком биття.
        var progress = scope.ServiceProvider.GetService<IJobProgressStore>();
        if (progress is not null)
        {
            var clock = scope.ServiceProvider.GetRequiredService<Domain.Abstractions.IClock>();
            var swept = await new Infrastructure.Jobs.AbandonedWorkSweeper(db, progress)
                .SweepAsync(
                    "Застосунок перезапущено: задача не завершилася до зупинки процесу.",
                    clock.UtcNow,
                    purge: false,
                    CancellationToken.None,
                    (Infrastructure.Persistence.JobProgressStore.CurrentHostName,
                     Infrastructure.Persistence.JobProgressStore.CurrentRole,
                     Infrastructure.Persistence.JobProgressStore.CurrentInstanceId))
                .ConfigureAwait(false);

            if (swept.Any)
            {
                LogStaleJobsFailed(logger, swept.Jobs, swept.CollectionRuns, swept.MaintenanceRuns);
            }
        }

        // ⚠ Запас партицій окремим кроком тут більше не рахується: його
        //    попередження дає `SchemaValidator` на кроці 3 (одне джерело).

        // 6) Прогрів кешу метаданих — ПІСЛЯ валідації схеми і seed. Прогрітий
        //    до перевірки кеш закешував би структуру, якої ніхто не перевіряв.
        //    Помилка прогріву старт не валить: це оптимізація, і застосунок,
        //    що не піднявся через непрогрітий кеш, гірший за повільний
        //    перший запит.
        var warmed = await scope.ServiceProvider
            .GetRequiredService<MetadataWarmup>()
            .WarmupAsync(CancellationToken.None)
            .ConfigureAwait(false);

        LogWarmupDone(logger, warmed);

        // 7) Постійні розклади ставить окремий hosted service після того, як
        //    застосунок піднявся: планувальник Quartz стає придатним лише
        //    після ApplicationStarted (RecurringScheduleService).
    }

    private static async Task WaitForDatabaseAsync(EcrDbContext db, ILogger logger)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await db.Database.OpenConnectionAsync().ConfigureAwait(false);
                await db.Database.CloseConnectionAsync().ConfigureAwait(false);
                LogDatabaseReady(logger, attempt);
                return;
            }
            catch (SqlException) when (attempt < DatabaseRetries)
            {
                LogDatabaseWaiting(logger, attempt, DatabaseRetries);
                await Task.Delay(RetryDelay).ConfigureAwait(false);
            }
        }
    }

    private static async Task ProbeCapabilitiesAsync(
        WebApplication app, EcrDbContext db, ISqlCapabilities capabilities, ILogger logger)
    {
        // ⚠ Проба — лише для справжнього SqlCapabilitiesProbe. Підмінені в
        // тестах можливості вже «знають» відповідь, і питати сервер нема чого.
        if (capabilities is not SqlCapabilitiesProbe probe)
        {
            return;
        }

        var connectionString = db.Database.GetConnectionString()
            ?? throw new InvalidOperationException("У контексту немає рядка підключення.");

        // ⛔ Q-223: раніше тут БУВ хардкод `SqlEditionMode.Auto` — ключ
        // `Database:EditionMode` існував у appsettings.json (з іншим
        // значенням у Development!) і НІКОЛИ не читався. Developer/
        // Evaluation повідомляють EngineEdition = 3 й зовні невідрізнювані
        // від Enterprise (`SqlCapabilitiesProbe`), тож у проді режим
        // мусить бути заданий явно, а не вгаданий автовизначенням.
        var configuredMode = Enum.TryParse<Domain.Enums.SqlEditionMode>(
            app.Configuration["Database:EditionMode"], ignoreCase: true, out var parsed)
            ? parsed
            : Domain.Enums.SqlEditionMode.Auto;

        await probe.ProbeAsync(connectionString, configuredMode, CancellationToken.None)
            .ConfigureAwait(false);

        LogSqlMode(logger, probe.EditionName, probe.EffectiveMode, probe.IsReadCommittedSnapshotOn);

        foreach (var limitation in probe.Limitations())
        {
            LogLimitation(logger, limitation);
        }
    }

    // ⚠ Логування через згенеровані делегати, а не через LogInformation(...):
    // на старті це не про швидкість, а про правило (CA1848), яке в проєкті
    // діє як помилка збірки. Заодно шаблони повідомлень стають типізованими
    // і не розповзаються по коду в різних формулюваннях.

    [LoggerMessage(Level = LogLevel.Information, Message = "Старт: база доступна (спроба {Attempt}).")]
    private static partial void LogDatabaseReady(ILogger logger, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Старт: база недоступна, спроба {Attempt} з {Total}.")]
    private static partial void LogDatabaseWaiting(ILogger logger, int attempt, int total);

    [LoggerMessage(Level = LogLevel.Information, Message = "Старт: схема відповідає моделі.")]
    private static partial void LogSchemaValid(ILogger logger);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Старт (критично): {Finding}")]
    private static partial void LogSchemaCritical(ILogger logger, string finding);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Старт (попередження): {Warning}")]
    private static partial void LogSchemaWarning(ILogger logger, string warning);

    [LoggerMessage(Level = LogLevel.Information, Message = "Старт: seed виконано.")]
    private static partial void LogSeedDone(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Старт: застарілих задач позначено Failed — {Count}; прогонів збору закрито — {CollectionRuns}; "
            + "прогонів обслуговування закрито — {MaintenanceRuns}.")]
    private static partial void LogStaleJobsFailed(ILogger logger, int count, int collectionRuns, int maintenanceRuns);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Старт: {Warning}")]
    private static partial void LogBootstrapWarning(ILogger logger, string warning);

    [LoggerMessage(Level = LogLevel.Information, Message = "Старт: bootstrap-адміністратор — {Outcome}.")]
    private static partial void LogBootstrapOutcome(
        ILogger logger, Application.Security.BootstrapAdmin.Outcome outcome);

    [LoggerMessage(Level = LogLevel.Information, Message = "Старт: прогріто версій шаблонів: {Count}.")]
    private static partial void LogWarmupDone(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Старт: SQL {Edition}, режим {Mode}, RCSI {Rcsi}.")]
    private static partial void LogSqlMode(
        ILogger logger, string edition, Domain.Enums.SqlEditionMode mode, bool rcsi);

    [LoggerMessage(Level = LogLevel.Information, Message = "Обмеження режиму: {Limitation}")]
    private static partial void LogLimitation(ILogger logger, string limitation);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Старт: не вдалося видалити одноразовий файл bootstrap-пароля: {Error}. " +
                   "Пароль у ньому вже використано — прибери файл вручну.")]
    private static partial void LogBootstrapSecretNotDeleted(ILogger logger, string error);
}
