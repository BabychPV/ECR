// tests/Ecr.Infrastructure.Tests/Persistence/AN37MethodologyEffectiveUniqueMigrationTests.cs
using System.Globalization;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Міграція <c>AN37MethodologyEffectiveUnique</c> (аудит 1G, L7-08): фільтрований унікальний
/// <c>UQ_MV_Effective (MethodologyId, EffectiveFrom) WHERE Status = 1</c> - дві ОПУБЛІКОВАНІ
/// версії однієї методології від однієї дати (ФВ-13.3) база відхиляє сама; база, де такі вже є,
/// зупиняється ЗРОЗУМІЛО і ДО зміни схеми.
/// </summary>
/// <remarks>
/// ⚠ Свіжої бази передперевірка не стосується: у порожній таблиці дублям нема де взятись. Тест
/// будує ВЛАСНУ базу, зупинену на міграції перед цією, - так виглядає майданчик, розгорнутий до
/// індексу, де гонка двох публікацій уже лишила дві опубліковані версії на одну дату. Механізм і
/// форма - ті самі, що в <see cref="AN34SourceEntityRegistryUniqueMigrationTests"/>.
/// <para>
/// Мутації (2026-10-05, власний worktree): прибрати індекс з <c>Up</c> - червоний перший тест
/// («індекс тримає»); прибрати <c>Sql(PrecheckSql)</c> - обидва шляхи дають 1505 замість 50708;
/// зняти <c>WHERE Status = 1</c> - червоний тест «Deprecated і чернетки на ту саму дату
/// дозволені».
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class AN37MethodologyEffectiveUniqueMigrationTests(SqlServerFixture sql)
{
    /// <summary>Остання міграція перед цією.</summary>
    private const string BeforeMigration = "20261005090900_AN34CollectionCoverageDedupIndex";

    /// <summary>Номер помилки передперевірки (L7-08).</summary>
    private const int PrecheckErrorNumber = 50708;

    private const string IndexName = "UQ_MV_Effective";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "AN-37-L7-08")]
    public async Task Дві_опубліковані_версії_на_одну_дату_зупиняють_міграцію_з_переліком_а_розведена_база_проходить_тримає_і_відкочується()
    {
        var connectionString = await CreateDatabaseBeforeMigrationAsync();

        // ── Фаза «відмова»: методологія 1 має ДВІ опубліковані версії від 2026-01-01 (гонка
        // двох публікацій), плюс застаріла (Deprecated) від тієї ж дати - вона не дубль;
        // методологія 2 має опубліковану від тієї ж дати - це інша методологія, не дубль.
        var first = await InsertAsync(connectionString, methodologyId: 1, version: "1.0", status: 1, effectiveFrom: "2026-01-01");
        var second = await InsertAsync(connectionString, methodologyId: 1, version: "1.1", status: 1, effectiveFrom: "2026-01-01");
        await InsertAsync(connectionString, methodologyId: 1, version: "0.9", status: 2, effectiveFrom: "2026-01-01");
        await InsertAsync(connectionString, methodologyId: 2, version: "1.0", status: 1, effectiveFrom: "2026-01-01");

        var viaScript = await Record.ExceptionAsync(() => RunIdempotentScriptAsync(connectionString));
        var viaMigrate = await Record.ExceptionAsync(() => MigrateAsync(connectionString));

        foreach (var (path, error) in new[] { ("migration.sql", viaScript), ("MigrateAsync", viaMigrate) })
        {
            var sqlError = FindSqlException(error);
            Assert.True(
                sqlError is { Number: PrecheckErrorNumber },
                $"{path}: очікувалась передперевірка {PrecheckErrorNumber}, а прийшло: " +
                $"{(sqlError is null ? error?.ToString() ?? "жодної помилки" : $"SQL {sqlError.Number}: {sqlError.Message}")}");

            // Перелік називає методологію, дату й версії (id і номер) - інакше оператор знає, що
            // зламано, і не знає де. Одна група: методологія 2 і Deprecated - не дубль.
            Assert.Contains(
                $"Груп (методологія, дата): 1; перші: (методологія 1, чинна від 2026-01-01, версії: {first} 1.0, {second} 1.1)",
                sqlError!.Message,
                StringComparison.Ordinal);
            Assert.DoesNotContain("методологія 2", sqlError.Message, StringComparison.Ordinal);
            Assert.Contains("operations-runbook.md", sqlError.Message, StringComparison.Ordinal);
        }

        // ⛔ Зупинка ДО зміни схеми: міграція не записана, індексу немає, дані не змінено.
        Assert.Equal(0, await ScalarAsync<int>(
            connectionString,
            "SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId LIKE N'%[_]AN37MethodologyEffectiveUnique'"));
        Assert.Equal(0, await IndexCountAsync(connectionString));
        Assert.Equal(1, await ScalarAsync<int>(
            connectionString, $"SELECT CAST(Status AS int) FROM calc.MethodologyVersion WHERE Id = {second}"));

        // ── Фаза «розведено»: рішення оператора (runbook §8.6) - зайву версію вивести з обігу ──
        await ExecuteAsync(connectionString, $"UPDATE calc.MethodologyVersion SET Status = 2 WHERE Id = {second};");

        await MigrateAsync(connectionString);
        Assert.Equal(1, await IndexCountAsync(connectionString));

        // Індекс справді тримає: друга опублікована версія методології 1 від тієї ж дати - 2601/2627.
        var rejected = await Record.ExceptionAsync(
            () => InsertAsync(connectionString, methodologyId: 1, version: "1.2", status: 1, effectiveFrom: "2026-01-01"));
        var uniqueError = FindSqlException(rejected);
        Assert.True(uniqueError is { Number: 2601 or 2627 }, $"очікувалось 2601/2627, а прийшло: {rejected}");
        Assert.Contains(IndexName, uniqueError!.Message, StringComparison.Ordinal);

        // Фільтр Status = 1: чернетки (дати ще немає) і Deprecated від тієї ж дати індекс не обмежує;
        // інша дата тієї ж методології й та сама дата іншої методології - теж можна.
        await InsertAsync(connectionString, methodologyId: 1, version: "2.0", status: 0, effectiveFrom: null);
        await InsertAsync(connectionString, methodologyId: 1, version: "2.1", status: 0, effectiveFrom: null);
        await InsertAsync(connectionString, methodologyId: 1, version: "0.8", status: 2, effectiveFrom: "2026-01-01");
        await InsertAsync(connectionString, methodologyId: 1, version: "1.3", status: 1, effectiveFrom: "2026-02-01");
        await InsertAsync(connectionString, methodologyId: 3, version: "1.0", status: 1, effectiveFrom: "2026-01-01");

        // ── Фаза «вниз»: Down прибирає рівно свій індекс, і вгору знову проходить ──────────
        await MigrateAsync(connectionString, BeforeMigration);
        Assert.Equal(0, await IndexCountAsync(connectionString));

        // Без індексу ті самі дві опубліковані на одну дату знову вставляються - сторож саме він.
        await InsertAsync(connectionString, methodologyId: 4, version: "1.0", status: 1, effectiveFrom: "2026-03-01");
        await InsertAsync(connectionString, methodologyId: 4, version: "1.1", status: 1, effectiveFrom: "2026-03-01");
        await ExecuteAsync(connectionString, "DELETE FROM calc.MethodologyVersion WHERE MethodologyId = 4;");

        await MigrateAsync(connectionString);
        Assert.Equal(1, await IndexCountAsync(connectionString));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "AN-37-L7-08")]
    public async Task База_без_дублів_проходить_ідемпотентний_скрипт_двічі_і_вдруге_нічого_не_змінює()
    {
        var connectionString = await CreateDatabaseBeforeMigrationAsync();
        await InsertAsync(connectionString, methodologyId: 1, version: "1.0", status: 1, effectiveFrom: "2026-01-01");
        await InsertAsync(connectionString, methodologyId: 1, version: "1.1", status: 1, effectiveFrom: "2026-02-01");

        await RunIdempotentScriptAsync(connectionString);
        Assert.Equal(1, await IndexCountAsync(connectionString));

        // Другий прогін - 0 змін: міграція вже записана, ні передперевірка, ні індекс не повторюються.
        await RunIdempotentScriptAsync(connectionString);
        Assert.Equal(1, await IndexCountAsync(connectionString));
        Assert.Equal(1, await ScalarAsync<int>(
            connectionString,
            "SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId LIKE N'%[_]AN37MethodologyEffectiveUnique'"));
    }

    /// <summary>Скільки є індексу <c>UQ_MV_Effective</c> (фільтрований унікальний, ключ - методологія й дата).</summary>
    private static Task<int> IndexCountAsync(string connectionString)
        => ScalarAsync<int>(
            connectionString,
            $"""
            SELECT COUNT(*) FROM sys.indexes
            WHERE object_id = OBJECT_ID(N'calc.MethodologyVersion') AND name = N'{IndexName}'
              AND is_unique = 1 AND has_filter = 1 AND filter_definition = N'([Status]=(1))'
              AND (SELECT STRING_AGG(c.name, N',') WITHIN GROUP (ORDER BY ic.key_ordinal)
                   FROM sys.index_columns AS ic
                   JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                   WHERE ic.object_id = sys.indexes.object_id AND ic.index_id = sys.indexes.index_id) = N'MethodologyId,EffectiveFrom'
            """);

    /// <summary>
    /// Рядок <c>calc.MethodologyVersion</c>. Обмеження вимкнено: методологій у цій базі немає, а
    /// перевіряється саме унікальність пари; чотири очі й повнота опублікованої - вже в значеннях.
    /// </summary>
    /// <returns>Ідентифікатор вставленого рядка.</returns>
    private static async Task<int> InsertAsync(
        string connectionString, int methodologyId, string version, byte status, string? effectiveFrom)
    {
        await ExecuteAsync(connectionString, "ALTER TABLE calc.MethodologyVersion NOCHECK CONSTRAINT FK_MV_Methodology;");

        var published = status == 1;
        var from = effectiveFrom is null ? "NULL" : $"'{effectiveFrom}'";
        return await ScalarAsync<int>(
            connectionString,
            string.Create(
                CultureInfo.InvariantCulture,
                $"""
                SET NOCOUNT ON;
                INSERT INTO calc.MethodologyVersion
                    (MethodologyId, Version, Level, EffectiveFrom, Status, CreatedAt, CreatedByUserId,
                     PublishedByUserId, PublishedAt, ChangeReason)
                VALUES
                    ({methodologyId}, N'{version}', 0, {from}, {status}, '2026-01-01', 1,
                     {(published ? "2" : "NULL")}, {(published ? "'2026-01-01'" : "NULL")}, {(published ? "N'test'" : "NULL")});
                SELECT CAST(SCOPE_IDENTITY() AS int);
                """));
    }

    /// <summary>Своя база, зупинена на міграції перед цією.</summary>
    private async Task<string> CreateDatabaseBeforeMigrationAsync()
    {
        var builder = new SqlConnectionStringBuilder(sql.ConnectionString);
        var name = builder.InitialCatalog + "_L708" + Guid.NewGuid().ToString("N")[..6];

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

    private static SqlException? FindSqlException(Exception? error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is SqlException found)
            {
                return found;
            }
        }

        return null;
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
