using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Startup;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Startup;

/// <summary>
/// Перевірки при старті. Мета — **впасти зрозуміло**, а не працювати на
/// несумісному середовищі й з'ясувати це на першому записі (ФВ-7.9).
/// </summary>
[Collection("SqlServer")]
public sealed class SchemaValidatorTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Незастосована_міграція_у_режимі_Validate_зупиняє_старт()
    {
        // Прибираємо запис про міграцію — база стає «старішою» за збірку.
        await using var db = CreateContext();
        var applied = await db.Database.GetAppliedMigrationsAsync();
        var victim = applied.Last();

        await ExecuteAsync($"DELETE FROM dbo.__EFMigrationsHistory WHERE MigrationId = N'{victim}'");
        try
        {
            var validator = new SchemaValidator(db, Capabilities(), Clock);

            var error = await Assert.ThrowsAsync<SchemaIncompatibleException>(
                () => validator.ValidateAsync("Validate", CancellationToken.None));

            // Повідомлення має називати, ЩО саме не застосовано: «схема не
            // збігається» без переліку не дає адміністратору нічого.
            Assert.Contains(victim, error.Message, StringComparison.Ordinal);
            Assert.Contains("Validate", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            await ExecuteAsync(
                "INSERT INTO dbo.__EFMigrationsHistory (MigrationId, ProductVersion) " +
                $"VALUES (N'{victim}', N'10.0.11')");
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Міграція_у_БД_якої_немає_у_збірці_зупиняє_старт()
    {
        // ⚠ Це відкат версії застосунку на новішу базу. Фатально в БУДЬ-ЯКОМУ
        // режимі, зокрема Migrate: старший код міг змінити схему так, як
        // молодший не розуміє, і «спробувати попрацювати» означає псувати дані.
        const string Ghost = "29991231235959_FromTheFuture";
        await ExecuteAsync(
            "INSERT INTO dbo.__EFMigrationsHistory (MigrationId, ProductVersion) " +
            $"VALUES (N'{Ghost}', N'99.0.0')");
        try
        {
            await using var db = CreateContext();
            var validator = new SchemaValidator(db, Capabilities(), Clock);

            var error = await Assert.ThrowsAsync<SchemaIncompatibleException>(
                () => validator.ValidateAsync("Migrate", CancellationToken.None));

            Assert.Contains(Ghost, error.Message, StringComparison.Ordinal);
        }
        finally
        {
            await ExecuteAsync($"DELETE FROM dbo.__EFMigrationsHistory WHERE MigrationId = N'{Ghost}'");
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Відсутність_схеми_партиціонування_зупиняє_старт_із_інструкцією()
    {
        // Саме той сценарій, що буває в житті: розгортання зробили міграціями
        // і забули скрипти. Тому режим Migrate — валідатор спершу накотить
        // таблиці (вони лягають на PRIMARY), а потім упреться у відсутні
        // файлові групи і схеми партиціонування.
        await using var db = CreateContext(await CreateBareDatabaseAsync());
        var validator = new SchemaValidator(db, Capabilities(), Clock);

        var error = await Assert.ThrowsAsync<SchemaIncompatibleException>(
            () => validator.ValidateAsync("Migrate", CancellationToken.None));

        // ⚠ Повідомлення мусить називати СКРИПТ. Інакше адміністратор знає, що
        // зламано, і не знає, що виконати.
        Assert.Contains(".sql", error.Message, StringComparison.Ordinal);

        // Код зупинки старту — і у властивості, і першим у тексті журналу.
        Assert.Equal(Ecr.Domain.Errors.ErrorCodes.StartupSchemaIncompatible, error.ErrorCode);
        Assert.StartsWith("ECR-SYS-5031: ", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Вимкнений_RCSI_дає_критичний_стан_здоровя_але_не_зупиняє_старт()
    {
        await using var db = CreateContext();
        var validator = new SchemaValidator(db, Capabilities(rcsi: false), Clock);

        // Не виняток: вмикання RCSI — операція DBA і потребує вікна
        // обслуговування. Зупиняти старт через те, чого застосунок не має права
        // виправити, означало б зробити його незапускним без DBA.
        await validator.ValidateAsync("Validate", CancellationToken.None);

        Assert.Contains(validator.Warnings, w => w.Contains("RCSI", StringComparison.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Standard_старший_за_2016_SP1_приймається()
    {
        await using var db = CreateContext();
        var validator = new SchemaValidator(db, Capabilities(mode: SqlEditionMode.Standard, major: 15), Clock);

        // ⚠ Твердження явне: тест без жодного `Assert` не каже, ЩО перевіряє,
        // і мовчки перестане щось означати, якщо метод почне повертати
        // результат замість кидати.
        var exception = await Record.ExceptionAsync(
            () => validator.ValidateAsync("Validate", CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Standard_до_2016_SP1_зупиняє_старт_бо_модель_архівації_не_працює()
    {
        await using var db = CreateContext();
        var validator = new SchemaValidator(db, Capabilities(mode: SqlEditionMode.Standard, major: 12), Clock);

        var error = await Assert.ThrowsAsync<SchemaIncompatibleException>(
            () => validator.ValidateAsync("Validate", CancellationToken.None));

        // ⛔ До 2016 SP1 партиціонування, columnstore і компресія — лише
        // Enterprise. Це не «повільніше», а «неможливо».
        Assert.Contains("партиціонування", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Developer_Edition_розпізнається_як_Enterprise()
    {
        var probe = new SqlCapabilitiesProbe();
        await probe.ProbeAsync(sql.ConnectionString, SqlEditionMode.Auto, CancellationToken.None);

        // ⚠ Developer і Evaluation повідомляють EngineEdition = 3 і зовні
        // невідрізнювані від Enterprise. Тест фіксує саме це правило, а не
        // редакцію конкретного сервера: на Express він теж має бути істинним,
        // бо перевіряє відображення 3 → Enterprise.
        var engineEdition = await ScalarAsync<int>("SELECT CAST(SERVERPROPERTY('EngineEdition') AS int)");
        var expected = engineEdition == 3 ? SqlEditionMode.Enterprise : SqlEditionMode.Standard;

        Assert.Equal(expected, probe.EffectiveMode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Явний_режим_Standard_перекриває_автовизначення()
    {
        var probe = new SqlCapabilitiesProbe();
        await probe.ProbeAsync(sql.ConnectionString, SqlEditionMode.Standard, CancellationToken.None);

        // Саме заради цього режим фіксують у проді явно: Developer виглядає як
        // Enterprise, і автовизначення обрало б стратегії, яких ліцензія не дає.
        Assert.Equal(SqlEditionMode.Standard, probe.EffectiveMode);
        Assert.False(probe.SupportsOnlineIndexRebuild);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-101")]
    public async Task Рівень_сумісності_нижче_130_зупиняє_старт_до_міграцій()
    {
        // ⛔ OPENJSON існує лише за COMPATIBILITY_LEVEL ≥ 130 — навіть на
        // сервері 2019+. База зі старого бекапу чи з `model` на 120 інакше
        // стартувала б і падала на першому пошуку чи зборі.
        var connectionString = await CreateBareDatabaseAsync("_Compat120", compatibilityLevel: 120);
        await using var db = CreateContext(connectionString);
        var validator = new SchemaValidator(db, Capabilities(), Clock);

        // Migrate, а не Validate: без перевірки саме тут валідатор пішов би
        // накочувати міграції на непридатну базу.
        var error = await Assert.ThrowsAsync<SchemaIncompatibleException>(
            () => validator.ValidateAsync("Migrate", CancellationToken.None));

        Assert.Contains("COMPATIBILITY_LEVEL = 130", error.Message, StringComparison.Ordinal);
        Assert.Contains("120", error.Message, StringComparison.Ordinal);
        Assert.StartsWith("ECR-SYS-5031: ", error.Message, StringComparison.Ordinal);

        // Зупинка ДО міграцій: історії міграцій на базі не з'явилось.
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sys.tables WHERE name = N'__EFMigrationsHistory'";
        Assert.Equal(0, (int)(await command.ExecuteScalarAsync())!);
    }

    /// <summary>Порожня база: є, але без файлових груп і схем партиціонування.</summary>
    /// <param name="suffix">Суфікс імені: різні тести — різні бази.</param>
    /// <param name="compatibilityLevel">Рівень сумісності; <see langword="null"/> — типовий інстансу.</param>
    private async Task<string> CreateBareDatabaseAsync(string suffix = "_Bare", int? compatibilityLevel = null)
    {
        var builder = new SqlConnectionStringBuilder(sql.ConnectionString);
        var bare = builder.InitialCatalog + suffix;

        await using var connection = new SqlConnection(
            new SqlConnectionStringBuilder(sql.ConnectionString) { InitialCatalog = "master" }.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            IF DB_ID(N'{bare}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{bare}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{bare}];
            END
            CREATE DATABASE [{bare}];
            """;
        if (compatibilityLevel is { } level)
        {
            command.CommandText += $"\nALTER DATABASE [{bare}] SET COMPATIBILITY_LEVEL = {level};";
        }

        await command.ExecuteNonQueryAsync();

        builder.InitialCatalog = bare;
        return builder.ConnectionString;
    }

    private EcrDbContext CreateContext(string? connectionString = null)
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(connectionString ?? sql.ConnectionString)
            .Options);

    /// <summary>Керований годинник: запас партицій рахується від «зараз».</summary>
    private static readonly TestClock Clock = new(new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc));

    private static FakeCapabilities Capabilities(
        SqlEditionMode mode = SqlEditionMode.Standard, int major = 15, bool rcsi = true)
        => new FakeCapabilities(mode, major, rcsi);

    private async Task ExecuteAsync(string text)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string query)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        return (T)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>Можливості СУБД без звернення до сервера.</summary>
    /// <remarks>
    /// Підміняються саме вони, а не база: підняти SQL Server 2014 Standard
    /// заради одного тесту неможливо, а перевіряється тут рішення валідатора,
    /// а не вміння сервера.
    /// </remarks>
    private sealed record FakeCapabilities(SqlEditionMode Mode, int Major, bool Rcsi) : ISqlCapabilities
    {
        public SqlEditionMode EffectiveMode => Mode;

        public string EditionName => Mode.ToString();

        public int ProductMajorVersion => Major;

        public bool IsReadCommittedSnapshotOn => Rcsi;

        public bool SupportsOnlineIndexRebuild => Mode == SqlEditionMode.Enterprise;

        public bool SupportsResourceGovernor => Mode == SqlEditionMode.Enterprise;

        public int ArchiveBatchSize => Mode == SqlEditionMode.Enterprise ? 2_000_000 : 500_000;
    }
}
