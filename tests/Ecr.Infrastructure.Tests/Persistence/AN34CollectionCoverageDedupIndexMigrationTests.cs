// tests/Ecr.Infrastructure.Tests/Persistence/AN34CollectionCoverageDedupIndexMigrationTests.cs
using System.Globalization;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Persistence.Migrations;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Міграція <c>AN34CollectionCoverageDedupIndex</c> (аудит 1D, L4-11): на базі з уже накопиченим
/// журналом покриття індекс будується, дані не змінюються, Down знімає рівно його, ідемпотентний
/// скрипт другим прогоном нічого не робить; гілка без <c>ONLINE</c> (Standard/Express) будує
/// той самий індекс.
/// </summary>
/// <remarks>
/// ⚠ Передперевірки немає: індекс не унікальний і даних не змінює. Тест будує ВЛАСНУ базу,
/// зупинену на міграції перед цією, - як база замовника перед оновленням.
/// <para>
/// ⛔ Мутації (2026-10-05, власний worktree): прибрати <c>EXEC sys.sp_executesql</c> з
/// <see cref="AN34CollectionCoverageDedupIndex.CreateIndexSql"/> - індексу немає, червоний;
/// прибрати <c>INDEXPROPERTY … IS NULL</c> - другий прогін ідемпотентного скрипта на базі з
/// наявним індексом падає 1913; завжди <c>ONLINE = ON</c> - гілка редакції 4 червона.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class AN34CollectionCoverageDedupIndexMigrationTests(SqlServerFixture sql)
{
    /// <summary>Остання міграція перед цією.</summary>
    private const string BeforeMigration = "20261005043829_AN34SourceEntityRegistryUnique";

    private const string RowsSql = """
        SELECT COUNT(*) FROM itg.CollectionCoverage
        """;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "AN-34-L4-11")]
    public async Task Індекс_будується_на_базі_з_журналом_дані_не_змінюються_Down_знімає_рівно_його_і_Up_повертає()
    {
        var connectionString = await CreateDatabaseBeforeMigrationAsync();
        await SeedJournalAsync(connectionString);

        var rowsBefore = await ScalarAsync<int>(connectionString, RowsSql);
        var checksumBefore = await ChecksumAsync(connectionString);
        Assert.Equal(0, await IndexCountAsync(connectionString));

        await MigrateAsync(connectionString);

        Assert.Equal(1, await IndexCountAsync(connectionString));
        Assert.Equal(rowsBefore, await ScalarAsync<int>(connectionString, RowsSql));
        Assert.Equal(checksumBefore, await ChecksumAsync(connectionString));

        // Форма індексу - рівно та, під яку написано запит дедупу: ключ (SourceEntityId, Id),
        // INCLUDE (Status, PeriodKey, Details), фільтр дослівно.
        Assert.Equal("SourceEntityId,Id", await KeyColumnsAsync(connectionString, included: false));
        Assert.Equal("Details,PeriodKey,Status", await KeyColumnsAsync(connectionString, included: true));
        Assert.Equal(
            "([Status] IS NOT NULL AND [PeriodKey] IS NULL)",
            await ScalarAsync<string>(
                connectionString,
                "SELECT filter_definition FROM sys.indexes WHERE object_id = OBJECT_ID(N'itg.CollectionCoverage') "
                + $"AND name = N'{AN34CollectionCoverageDedupIndex.IndexName}'"));

        // Сусідні індекси таблиці не зачеплені.
        Assert.Equal(1, await ScalarAsync<int>(
            connectionString,
            "SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID(N'itg.CollectionCoverage') "
            + "AND name = N'IX_CollectionCoverage_SourceEntity_CoveredTo'"));

        // Down знімає рівно свій індекс і не чіпає даних; Up знову будує.
        await MigrateAsync(connectionString, BeforeMigration);
        Assert.Equal(0, await IndexCountAsync(connectionString));
        Assert.Equal(rowsBefore, await ScalarAsync<int>(connectionString, RowsSql));

        await MigrateAsync(connectionString);
        Assert.Equal(1, await IndexCountAsync(connectionString));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "AN-34-L4-11")]
    public async Task Ідемпотентний_скрипт_двічі_індекс_один_запис_в_історії_один_а_наявний_індекс_не_ламає_повтор()
    {
        var connectionString = await CreateDatabaseBeforeMigrationAsync();
        await SeedJournalAsync(connectionString);

        await RunIdempotentScriptAsync(connectionString);
        Assert.Equal(1, await IndexCountAsync(connectionString));

        // Другий прогін - 0 змін: міграція записана в історії, індекс не повторюється.
        await RunIdempotentScriptAsync(connectionString);
        Assert.Equal(1, await IndexCountAsync(connectionString));
        Assert.Equal(1, await ScalarAsync<int>(
            connectionString,
            "SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId LIKE N'%[_]AN34CollectionCoverageDedupIndex'"));

        // Збірка вручну до оновлення: міграція не записана, а індекс уже є - Up нічого не ламає.
        await MigrateAsync(connectionString, BeforeMigration);
        await ExecuteAsync(
            connectionString,
            $"CREATE NONCLUSTERED INDEX {AN34CollectionCoverageDedupIndex.IndexName} ON itg.CollectionCoverage (SourceEntityId, Id) "
            + "INCLUDE (Status, PeriodKey, Details) WHERE [Status] IS NOT NULL AND [PeriodKey] IS NULL;");
        await RunIdempotentScriptAsync(connectionString);
        Assert.Equal(1, await IndexCountAsync(connectionString));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "AN-34-L4-11")]
    public async Task Гілка_без_ONLINE_для_Standard_і_Express_будує_той_самий_індекс_а_Enterprise_будує_ONLINE()
    {
        // Текст команди: ONLINE лише на редакціях 3, 5, 8.
        foreach (var (edition, expectOnline) in new[] { ("3", true), ("5", true), ("8", true), ("2", false), ("4", false) })
        {
            var captured = await CapturedCommandAsync(sql.ConnectionString, edition);
            Assert.Equal(expectOnline, captured.Contains("ONLINE = ON", StringComparison.Ordinal));
        }

        // Наживо на Developer із підставленою редакцією 4 (Express): індекс будується без ONLINE.
        var connectionString = await CreateDatabaseBeforeMigrationAsync();
        await SeedJournalAsync(connectionString);
        await ExecuteAsync(connectionString, AN34CollectionCoverageDedupIndex.CreateIndexSql("4"));
        Assert.Equal(1, await IndexCountAsync(connectionString));
    }

    /// <summary>Команда, яку міграція виконала б для редакції: <c>EXEC</c> замінено на <c>SELECT</c> тексту.</summary>
    private static Task<string> CapturedCommandAsync(string connectionString, string edition)
    {
        var script = AN34CollectionCoverageDedupIndex.CreateIndexSql(edition)
            .Replace("EXEC sys.sp_executesql @an34Create;", "SELECT @an34Create;", StringComparison.Ordinal)
            // Індекс у спільній базі вже може бути: перевірка наявності не має заступати вивід.
            .Replace("IF INDEXPROPERTY(", "IF 1 = 1 OR INDEXPROPERTY(", StringComparison.Ordinal);
        return ScalarAsync<string>(connectionString, script);
    }

    /// <summary>Скільки є індексу <c>IX_CollectionCoverage_RegistryEvents</c> (фільтрований, не унікальний).</summary>
    private static Task<int> IndexCountAsync(string connectionString)
        => ScalarAsync<int>(
            connectionString,
            $"""
            SELECT COUNT(*) FROM sys.indexes
            WHERE object_id = OBJECT_ID(N'itg.CollectionCoverage') AND name = N'{AN34CollectionCoverageDedupIndex.IndexName}'
              AND is_unique = 0 AND has_filter = 1
            """);

    /// <summary>Ключові (<paramref name="included"/> = false) або включені стовпці індексу, через кому.</summary>
    private static Task<string> KeyColumnsAsync(string connectionString, bool included)
        => ScalarAsync<string>(
            connectionString,
            $"""
            SELECT STUFF((
                SELECT N',' + c.name
                FROM sys.index_columns AS ic
                JOIN sys.indexes AS i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE i.object_id = OBJECT_ID(N'itg.CollectionCoverage') AND i.name = N'{AN34CollectionCoverageDedupIndex.IndexName}'
                  AND ic.is_included_column = {(included ? 1 : 0)}
                ORDER BY {(included ? "c.name" : "ic.key_ordinal")}
                FOR XML PATH(N'')), 1, 1, N'')
            """);

    /// <summary>Контрольна сума вмісту журналу: міграція не змінює жодного рядка.</summary>
    private static Task<long> ChecksumAsync(string connectionString)
        => ScalarAsync<long>(
            connectionString,
            "SELECT SUM(CAST(BINARY_CHECKSUM(Id, SourceEntityId, PeriodKey, Status, Details) AS bigint)) FROM itg.CollectionCoverage");

    /// <summary>
    /// Журнал, як у бойовій базі: покриття (<c>Status NULL</c>), події матеріалізації (з періодом)
    /// і події синку (без періоду). Обмеження вимкнено: сутностей і прогонів у цій базі немає.
    /// </summary>
    private static async Task SeedJournalAsync(string connectionString)
    {
        await ExecuteAsync(
            connectionString,
            """
            ALTER TABLE itg.CollectionCoverage NOCHECK CONSTRAINT ALL;
            ;WITH n AS (SELECT TOP (3000) ROW_NUMBER() OVER (ORDER BY (SELECT 1)) AS i FROM sys.all_columns)
            INSERT itg.CollectionCoverage (SourceEntityId, CollectionRunId, PeriodKey, CoveredFrom, CoveredTo, Status, Details)
            SELECT 1 + (i % 3), NULL,
                   CASE WHEN i % 3 = 1 THEN 202601 + (i % 12) ELSE NULL END,
                   DATEADD(HOUR, i, '2026-01-01'), DATEADD(HOUR, i + 1, '2026-01-01'),
                   CASE i % 3 WHEN 0 THEN NULL WHEN 1 THEN N'SkippedPeriodClosed' ELSE N'RegistryPendingUpdate' END,
                   CASE i % 3 WHEN 0 THEN NULL WHEN 1 THEN N'period closed' ELSE N'event; key=s' + CONVERT(nvarchar(20), i) + N':1' END
            FROM n;
            """);
    }

    /// <summary>Своя база, зупинена на міграції перед цією.</summary>
    private async Task<string> CreateDatabaseBeforeMigrationAsync()
    {
        var builder = new SqlConnectionStringBuilder(sql.ConnectionString);
        var name = builder.InitialCatalog + "_L411" + Guid.NewGuid().ToString("N")[..6];

        await using (var connection = new SqlConnection(
            new SqlConnectionStringBuilder(sql.ConnectionString) { InitialCatalog = "master" }.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                IF DB_ID(N'{name}') IS NOT NULL
                BEGIN
                    ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [{name}];
                END
                CREATE DATABASE [{name}] COLLATE Latin1_General_100_CI_AS_SC;
                """;
            await command.ExecuteNonQueryAsync();
        }

        builder.InitialCatalog = name;
        await MigrateAsync(builder.ConnectionString, BeforeMigration);

        return builder.ConnectionString;
    }

    /// <summary>
    /// Той самий <c>migration.sql</c>, що генерують <c>deploy-ecr.ps1</c> і
    /// <c>build-installer.ps1</c> (<c>--idempotent</c>), пакетами по <c>GO</c>;
    /// перша ж помилка зупиняє, як <c>sqlcmd -b</c>.
    /// </summary>
    private static async Task RunIdempotentScriptAsync(string connectionString)
    {
        string script;
        await using (var db = CreateContext(connectionString))
        {
            script = db.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        foreach (var batch in SqlBatches.Split(script))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = batch;
            command.CommandTimeout = 300;
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task MigrateAsync(string connectionString, string? target = null)
    {
        await using var db = CreateContext(connectionString);
        await db.GetService<IMigrator>().MigrateAsync(target);
    }

    private static EcrDbContext CreateContext(string connectionString)
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(connectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .Options);

    private static async Task ExecuteAsync(string connectionString, string text)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        command.CommandTimeout = 300;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string query)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        command.CommandTimeout = 300;
        var result = await command.ExecuteScalarAsync();
        return (T)Convert.ChangeType(result!, typeof(T), CultureInfo.InvariantCulture);
    }
}
