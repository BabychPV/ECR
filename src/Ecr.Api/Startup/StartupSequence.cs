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

        // 2–3) Схема. У проді застосунок DDL-прав не має (D-66), тому
        //      Validate — це саме перевірка, а не тихе «домігруємо».
        var mode = app.Configuration["Schema:StartupMode"] ?? "Validate";
        await ApplySchemaModeAsync(db, mode, logger).ConfigureAwait(false);

        // 4) Ідемпотентний seed. Без нього немає ні мов, ні прав, ні одиниць —
        //    застосунок формально піднімається і не робить нічого.
        await new SeedRunner(db).RunAsync(CancellationToken.None).ConfigureAwait(false);
        LogSeedDone(logger);

        // 5) Можливості СУБД. Читаються один раз: редакція між запитами
        //    не змінюється, а кожна перевірка коштує запиту.
        var capabilities = scope.ServiceProvider.GetRequiredService<ISqlCapabilities>();
        if (capabilities is SqlCapabilitiesProbe probe)
        {
            var connectionString = db.Database.GetConnectionString()
                ?? throw new InvalidOperationException("У контексту немає рядка підключення.");
            await probe.ProbeAsync(connectionString, Domain.Enums.SqlEditionMode.Auto, CancellationToken.None)
                .ConfigureAwait(false);

            LogSqlMode(logger, probe.EditionName, probe.EffectiveMode, probe.IsReadCommittedSnapshotOn);

            foreach (var limitation in probe.Limitations())
            {
                LogLimitation(logger, limitation);
            }
        }

        // 6) Запас партицій. Дізнатися про це треба на старті, а не вночі
        //    під час архівації, коли межі вже не вистачає.
        var ahead = await PartitionsAheadAsync(
            db, scope.ServiceProvider.GetRequiredService<Domain.Abstractions.IClock>()).ConfigureAwait(false);
        if (ahead < 2)
        {
            LogFewPartitions(logger, ahead);
        }

        // 7) Прогрів кешу метаданих — ПІСЛЯ валідації схеми і seed. Прогрітий
        //    до перевірки кеш закешував би структуру, якої ніхто не перевіряв.
        //    Помилка прогріву старт не валить: це оптимізація, і застосунок,
        //    що не піднявся через непрогрітий кеш, гірший за повільний
        //    перший запит.
        var warmed = await scope.ServiceProvider
            .GetRequiredService<MetadataWarmup>()
            .WarmupAsync(CancellationToken.None)
            .ConfigureAwait(false);

        LogWarmupDone(logger, warmed);

        // 8) Постійні розклади ставить окремий hosted service після того, як
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

    private static async Task ApplySchemaModeAsync(EcrDbContext db, string mode, ILogger logger)
    {
        var pending = (await db.Database.GetPendingMigrationsAsync().ConfigureAwait(false)).ToList();

        if (string.Equals(mode, "Migrate", StringComparison.OrdinalIgnoreCase))
        {
            if (pending.Count > 0)
            {
                LogApplyingMigrations(logger, pending.Count);
                await db.Database.MigrateAsync().ConfigureAwait(false);
            }

            return;
        }

        // Validate. Працювати на невідповідній схемі гірше, ніж не працювати:
        // запити мовчки повертатимуть не те, і виявиться це в звіті.
        if (pending.Count > 0)
        {
            throw new InvalidOperationException(
                $"Схема БД застаріла: не застосовано міграцій — {pending.Count} " +
                $"({string.Join(", ", pending)}). У режимі Validate застосунок не стартує.");
        }

        LogSchemaValid(logger);
    }

    private static async Task<int> PartitionsAheadAsync(EcrDbContext db, Domain.Abstractions.IClock clock)
    {
        var now = clock.UtcNow;
        var currentKey = (now.Year * 100) + now.Month;

        var result = await db.Database
            .SqlQueryRaw<int>(
                """
                SELECT COUNT(*) AS Value
                FROM sys.partition_range_values rv
                JOIN sys.partition_functions pf ON pf.function_id = rv.function_id
                WHERE pf.name = 'pf_ByPeriodKey' AND CAST(rv.value AS int) > {0}
                """,
                currentKey)
            .ToListAsync().ConfigureAwait(false);

        return result.Count > 0 ? result[0] : 0;
    }

    // ⚠ Логування через згенеровані делегати, а не через LogInformation(...):
    // на старті це не про швидкість, а про правило (CA1848), яке в проєкті
    // діє як помилка збірки. Заодно шаблони повідомлень стають типізованими
    // і не розповзаються по коду в різних формулюваннях.

    [LoggerMessage(Level = LogLevel.Information, Message = "Старт: база доступна (спроба {Attempt}).")]
    private static partial void LogDatabaseReady(ILogger logger, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Старт: база недоступна, спроба {Attempt} з {Total}.")]
    private static partial void LogDatabaseWaiting(ILogger logger, int attempt, int total);

    [LoggerMessage(Level = LogLevel.Information, Message = "Старт: застосовую {Count} міграцій.")]
    private static partial void LogApplyingMigrations(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Старт: схема відповідає моделі.")]
    private static partial void LogSchemaValid(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Старт: seed виконано.")]
    private static partial void LogSeedDone(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Старт: прогріто версій шаблонів: {Count}.")]
    private static partial void LogWarmupDone(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Старт: SQL {Edition}, режим {Mode}, RCSI {Rcsi}.")]
    private static partial void LogSqlMode(
        ILogger logger, string edition, Domain.Enums.SqlEditionMode mode, bool rcsi);

    [LoggerMessage(Level = LogLevel.Information, Message = "Обмеження режиму: {Limitation}")]
    private static partial void LogLimitation(ILogger logger, string limitation);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Старт: попереду лише {Ahead} партицій. Виконайте 04-partition-maintenance.sql.")]
    private static partial void LogFewPartitions(ILogger logger, int ahead);
}
