using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Startup;

/// <summary>
/// Перевірки при старті (ФВ-7.9). Мета — **впасти зрозуміло**, а не працювати
/// на несумісному середовищі й з'ясувати це на першому записі.
/// </summary>
public sealed class SchemaValidator(
    EcrDbContext db, ISqlCapabilities capabilities, Domain.Abstractions.IClock clock)
{
    /// <summary>
    /// Найнижча підтримувана мажорна версія SQL Server.
    /// </summary>
    /// <remarks>
    /// ⛔ 13 — це 2016. До 2016 SP1 партиціонування, columnstore і компресія
    /// доступні лише в Enterprise, тобто на Standard **модель архівації не
    /// працює в принципі** (АРХ-7). Це не «повільніше», а «неможливо», тому
    /// старт зупиняється, а не деградує.
    /// </remarks>
    private const int MinimumMajorVersion = 13;

    /// <summary>Файлові групи, без яких фізична модель не існує.</summary>
    private static readonly string[] RequiredFilegroups =
        ["DATA_HOT", "DATA_ARCHIVE", "AUDIT", "INDEXES"];

    /// <summary>Схеми партиціонування, без яких не працює архівація.</summary>
    private static readonly string[] RequiredPartitionSchemes =
        ["ps_ByPeriodKey", "ps_AuditByMonth"];

    /// <summary>Попередження, які не зупиняють старт, але мають бути видні.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    private readonly List<string> _warnings = [];

    /// <summary>Виконує послідовність перевірок.</summary>
    /// <param name="startupMode"><c>Validate</c> у прод, <c>Migrate</c> у dev/test.</param>
    /// <exception cref="InvalidOperationException">
    /// Середовище непридатне; повідомлення пояснює, що саме і як виправити.
    /// </exception>
    public async Task ValidateAsync(string startupMode, CancellationToken ct)
    {
        _warnings.Clear();

        await ValidateEditionAsync().ConfigureAwait(false);
        await ValidateMigrationsAsync(startupMode, ct).ConfigureAwait(false);
        await ValidatePhysicalModelAsync(ct).ConfigureAwait(false);
        await ValidateRuntimeOptionsAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Редакція і версія сервера.</summary>
    private Task ValidateEditionAsync()
    {
        if (capabilities.EffectiveMode == SqlEditionMode.Standard
            && capabilities.ProductMajorVersion is > 0 and < MinimumMajorVersion)
        {
            throw new InvalidOperationException(
                $"SQL Server {capabilities.ProductMajorVersion} ({capabilities.EditionName}) " +
                "у режимі Standard не підтримує партиціонування, columnstore і компресію — " +
                "модель архівації на ньому не працює. Потрібен SQL Server 2016 SP1 або новіший " +
                "(АРХ-7).");
        }

        return Task.CompletedTask;
    }

    /// <summary>Стан міграцій.</summary>
    private async Task ValidateMigrationsAsync(string startupMode, CancellationToken ct)
    {
        var applied = (await db.Database.GetAppliedMigrationsAsync(ct).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
        var known = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);

        // ⚠ Міграція, яка є в БД і якої немає у збірці, означає відкат версії
        // застосунку на вже мігровану базу. Це фатально в будь-якому режимі:
        // старший код міг змінити схему так, як молодший не розуміє, і
        // «спробувати попрацювати» тут означає псувати дані.
        var unknown = applied.Except(known).ToList();
        if (unknown.Count > 0)
        {
            throw new InvalidOperationException(
                $"У базі є міграції, яких немає у збірці: {string.Join(", ", unknown)}. " +
                "Схоже на відкат версії застосунку на новішу базу. Старт зупинено.");
        }

        var pending = known.Except(applied).ToList();
        if (pending.Count == 0)
        {
            return;
        }

        if (!string.Equals(startupMode, "Migrate", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Не застосовано міграцій: {pending.Count} ({string.Join(", ", pending)}). " +
                "У режимі Validate застосунок не стартує: працювати на невідповідній схемі " +
                "гірше, ніж не працювати (D-66).");
        }

        // ⚠ sp_getapplock: два інстанси, які стартують одночасно, інакше
        // мігрують паралельно. EF цього не координує, і результат — гонка на
        // DDL, яку неможливо відтворити в тестах.
        await using var connection = new SqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using (var acquire = connection.CreateCommand())
        {
            acquire.CommandText =
                "EXEC sp_getapplock @Resource = N'Ecr.Migrate', @LockMode = 'Exclusive', " +
                "@LockOwner = 'Session', @LockTimeout = 120000;";
            await acquire.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        try
        {
            await db.Database.MigrateAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            await using var release = connection.CreateCommand();
            release.CommandText =
                "EXEC sp_releaseapplock @Resource = N'Ecr.Migrate', @LockOwner = 'Session';";
            await release.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Файлові групи і схеми партиціонування.</summary>
    private async Task ValidatePhysicalModelAsync(CancellationToken ct)
    {
        var filegroups = await db.Database
            .SqlQueryRaw<string>("SELECT name AS Value FROM sys.filegroups")
            .ToListAsync(ct).ConfigureAwait(false);

        var missingGroups = RequiredFilegroups
            .Where(fg => !filegroups.Contains(fg, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (missingGroups.Count > 0)
        {
            throw new InvalidOperationException(
                $"Немає файлових груп: {string.Join(", ", missingGroups)}. " +
                "Виконайте src/Ecr.Infrastructure/Persistence/Sql/01-filegroups.sql.");
        }

        var schemes = await db.Database
            .SqlQueryRaw<string>("SELECT name AS Value FROM sys.partition_schemes")
            .ToListAsync(ct).ConfigureAwait(false);

        var missingSchemes = RequiredPartitionSchemes
            .Where(ps => !schemes.Contains(ps, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (missingSchemes.Count > 0)
        {
            throw new InvalidOperationException(
                $"Немає схем партиціонування: {string.Join(", ", missingSchemes)}. " +
                "Виконайте src/Ecr.Infrastructure/Persistence/Sql/02-partitions.sql.");
        }
    }

    /// <summary>RCSI і запас партицій — це попередження, а не зупинка.</summary>
    /// <remarks>
    /// ⚠ Вмикання RCSI — операція DBA і потребує вікна (<c>ALTER DATABASE …
    /// WITH ROLLBACK IMMEDIATE</c> обриває чужі сеанси). Зупиняти старт через
    /// те, чого застосунок не має права виправити, означало б зробити його
    /// незапускним без DBA. Тому — критичний стан у health і запис у журнал.
    /// </remarks>
    private async Task ValidateRuntimeOptionsAsync(CancellationToken ct)
    {
        if (!capabilities.IsReadCommittedSnapshotOn)
        {
            _warnings.Add(
                "RCSI вимкнено: у пік останнього дня періоду читання блокуватимуть запис (D-29). " +
                "Виконайте 06-rcsi.sql у вікні обслуговування.");
        }

        // ⚠ Зіставлення бази перевіряється саме тут, а не в `01-filegroups.sql`:
        // скрипт бачить лише той інстанс, де його запустили, а помилка виявиться
        // на іншому (`Q-061`). Чутливе до регістру зіставлення тихо змінює
        // ПОВЕДІНКУ УНІКАЛЬНОСТІ бізнес-кодів: `UQ_Template_Code` перестає
        // вважати `ABC` і `abc` одним кодом, і той самий seed на двох інстансах
        // дає різний результат. Це попередження, а не зупинка, з тієї ж
        // причини, що й RCSI: змінити зіставлення бази застосунок не може.
        var collation = await db.Database
            .SqlQueryRaw<string>(
                "SELECT CAST(DATABASEPROPERTYEX(DB_NAME(), 'Collation') AS nvarchar(200)) AS Value")
            .ToListAsync(ct).ConfigureAwait(false);

        if (collation.Count > 0 && collation[0] is { } name
            && name.Contains("_CS", StringComparison.OrdinalIgnoreCase))
        {
            _warnings.Add(
                $"Зіставлення бази {name} чутливе до регістру. " +
                "Унікальність бізнес-кодів (UQ_Template_Code, UQ_Unit_Code, UQ_Role) " +
                "працюватиме інакше, ніж на еталонному Latin1_General_100_CI_AS_SC (02a §1).");
        }

        var now = clock.UtcNow;
        var currentKey = (now.Year * 100) + now.Month;

        var ahead = await db.Database
            .SqlQueryRaw<int>(
                """
                SELECT COUNT(*) AS Value
                FROM sys.partition_range_values rv
                JOIN sys.partition_functions pf ON pf.function_id = rv.function_id
                WHERE pf.name = 'pf_ByPeriodKey' AND CAST(rv.value AS int) > {0}
                """,
                currentKey)
            .ToListAsync(ct).ConfigureAwait(false);

        if (ahead.Count > 0 && ahead[0] < 2)
        {
            _warnings.Add(
                $"Запас партицій {ahead[0]}: менше за два періоди попереду. " +
                "Виконайте 04-partition-maintenance.sql.");
        }
    }
}
