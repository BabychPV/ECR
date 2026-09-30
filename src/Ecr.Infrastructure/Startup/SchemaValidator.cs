using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Startup;

/// <summary>
/// Середовище несумісне зі збіркою: старт зупинено (ФВ-7.9, <c>ECR-SYS-5031</c>).
/// </summary>
/// <remarks>
/// ⚠ Нащадок <see cref="InvalidOperationException"/>, а не <c>EcrException</c>:
/// цей виняток ніколи не доходить до HTTP — його кидає крок старту ДО того,
/// як застосунок почав приймати запити, тож арму в
/// <c>ExceptionHandlingMiddleware</c> і <c>messageKey</c> він не потребує
/// (каталог рядків на цей момент ще навіть не засіяно). Код несе
/// <see cref="ErrorCode"/> і перший фрагмент <see cref="Exception.Message"/> —
/// саме його адміністратор побачить у журналі старту.
/// </remarks>
public sealed class SchemaIncompatibleException : InvalidOperationException
{
    /// <summary>Створює виняток без тексту.</summary>
    public SchemaIncompatibleException()
    {
    }

    /// <summary>Створює виняток із текстом причини.</summary>
    /// <param name="message">Що несумісне і що виконати.</param>
    public SchemaIncompatibleException(string message)
        : base(message)
    {
    }

    /// <summary>Створює виняток із текстом причини і першопричиною.</summary>
    /// <param name="message">Що несумісне і що виконати.</param>
    /// <param name="innerException">Першопричина.</param>
    public SchemaIncompatibleException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Код зупинки старту — завжди <c>ECR-SYS-5031</c>.</summary>
    public string ErrorCode { get; } = ErrorCodes.StartupSchemaIncompatible;
}

/// <summary>
/// Перевірки при старті (ФВ-7.9). Мета — **впасти зрозуміло**, а не працювати
/// на несумісному середовищі й з'ясувати це на першому записі.
/// </summary>
/// <remarks>
/// Класифікація: помилка (зупинка старту, <see cref="SchemaIncompatibleException"/>)
/// — непідтримувана редакція/версія, рівень сумісності бази нижче 130,
/// незастосовані міграції в Validate, база
/// новіша за збірку, немає файлових груп, функцій чи схем партиціонування.
/// Попередження (<see cref="Warnings"/>) — вимкнений RCSI, чутливе до регістру
/// зіставлення, запас партицій менше двох: це виправляє DBA, не застосунок.
/// Викликається з <c>StartupSequence</c> — єдине джерело цих перевірок.
/// </remarks>
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

    /// <summary>
    /// Найнижчий рівень сумісності бази (130 = SQL Server 2016).
    /// </summary>
    /// <remarks>
    /// ⛔ <c>OPENJSON</c> (пошук, збір, зведення кампаній, читання архіву,
    /// методологія — <c>Persistence/*Store.cs</c>) існує лише за
    /// <c>COMPATIBILITY_LEVEL ≥ 130</c> — навіть на сервері 2019: база,
    /// відновлена зі старого бекапу чи створена з <c>model</c> на 120, лишає
    /// рівень старим. Без цієї перевірки застосунок стартує, а падає на першому
    /// записі з «Invalid object name 'OPENJSON'». Перевірка тут, а не в
    /// <c>01-filegroups.sql</c>: рівень може змінити DBA ПІСЛЯ розгортання, а
    /// старт — єдине місце, яке бачить базу щоразу.
    /// </remarks>
    private const int MinimumCompatibilityLevel = 130;

    /// <summary>Файлові групи, без яких фізична модель не існує.</summary>
    private static readonly string[] RequiredFilegroups =
        ["DATA_HOT", "DATA_ARCHIVE", "AUDIT", "INDEXES"];

    /// <summary>Функції партиціонування, на яких стоять схеми нижче.</summary>
    private static readonly string[] RequiredPartitionFunctions =
        ["pf_ByPeriodKey", "pf_AuditByMonth"];

    /// <summary>Схеми партиціонування, без яких не працює архівація.</summary>
    private static readonly string[] RequiredPartitionSchemes =
        ["ps_ByPeriodKey", "ps_AuditByMonth"];

    /// <summary>Попередження, які не зупиняють старт, але мають бути видні.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    private readonly List<string> _warnings = [];

    /// <summary>Виконує послідовність перевірок.</summary>
    /// <param name="startupMode"><c>Validate</c> у прод, <c>Migrate</c> у dev/test.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="InvalidOperationException">
    /// Середовище непридатне; повідомлення пояснює, що саме і як виправити.
    /// </exception>
    public async Task ValidateAsync(string startupMode, CancellationToken ct)
    {
        _warnings.Clear();

        await ValidateEditionAsync().ConfigureAwait(false);

        // ⚠ ДО міграцій: у режимі Migrate вони самі використовують OPENJSON і
        // на рівні 120 падали б посеред DDL із текстом, що не каже, як виправити.
        await ValidateCompatibilityLevelAsync(ct).ConfigureAwait(false);
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
            throw Incompatible(
                $"SQL Server {capabilities.ProductMajorVersion} ({capabilities.EditionName}) " +
                "у режимі Standard не підтримує партиціонування, columnstore і компресію — " +
                "модель архівації на ньому не працює. Потрібен SQL Server 2016 SP1 або новіший " +
                "(АРХ-7).");
        }

        return Task.CompletedTask;
    }

    /// <summary>Рівень сумісності бази: нижче 130 немає <c>OPENJSON</c>.</summary>
    private async Task ValidateCompatibilityLevelAsync(CancellationToken ct)
    {
        var levels = await db.Database
            .SqlQueryRaw<int>(
                "SELECT CAST(compatibility_level AS int) AS Value FROM sys.databases WHERE name = DB_NAME()")
            .ToListAsync(ct).ConfigureAwait(false);

        if (levels.Count > 0 && levels[0] < MinimumCompatibilityLevel)
        {
            throw Incompatible(
                $"Рівень сумісності бази {levels[0]} нижчий за {MinimumCompatibilityLevel}: " +
                "без нього немає OPENJSON, і пошук, збір та читання даних не працюватимуть. Виконайте (DBA): " +
                $"ALTER DATABASE CURRENT SET COMPATIBILITY_LEVEL = {MinimumCompatibilityLevel}; " +
                "або вище, до рівня версії сервера.");
        }
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
            throw Incompatible(
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
            // ⚠ Початок речення «Схема БД застаріла» — той самий, що давав
            // старий крок `StartupSequence.ApplySchemaModeAsync`: на нього
            // спирається тест реального старту (`StartupSchemaCheckTests`).
            throw Incompatible(
                $"Схема БД застаріла: не застосовано міграцій — {pending.Count} ({string.Join(", ", pending)}). " +
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
            throw Incompatible(
                $"Немає файлових груп: {string.Join(", ", missingGroups)}. " +
                "Виконайте src/Ecr.Infrastructure/Persistence/Sql/01-filegroups.sql.");
        }

        var functions = await db.Database
            .SqlQueryRaw<string>("SELECT name AS Value FROM sys.partition_functions")
            .ToListAsync(ct).ConfigureAwait(false);

        var missingFunctions = RequiredPartitionFunctions
            .Where(pf => !functions.Contains(pf, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (missingFunctions.Count > 0)
        {
            throw Incompatible(
                $"Немає функцій партиціонування: {string.Join(", ", missingFunctions)}. " +
                "Виконайте src/Ecr.Infrastructure/Persistence/Sql/02-partitions.sql.");
        }

        var schemes = await db.Database
            .SqlQueryRaw<string>("SELECT name AS Value FROM sys.partition_schemes")
            .ToListAsync(ct).ConfigureAwait(false);

        var missingSchemes = RequiredPartitionSchemes
            .Where(ps => !schemes.Contains(ps, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (missingSchemes.Count > 0)
        {
            throw Incompatible(
                $"Немає схем партиціонування: {string.Join(", ", missingSchemes)}. " +
                "Виконайте src/Ecr.Infrastructure/Persistence/Sql/02-partitions.sql.");
        }
    }

    /// <summary>Зупинка старту: код попереду тексту, щоб його було видно в журналі.</summary>
    private static SchemaIncompatibleException Incompatible(string text)
        => new($"{ErrorCodes.StartupSchemaIncompatible}: {text}");

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
