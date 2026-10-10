// tests/Ecr.Infrastructure.Tests/Persistence/R11L4SweeperAndApprovalIndexesMigrationTests.cs
using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
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
/// Міграція <c>R11L4SweeperAndApprovalIndexes</c>: <c>IX_CalculationRun_Running</c> (Q1-04, прибиральник покинутих
/// прогонів) і <c>IX_ApprovalEvent_DocumentAt</c> (X6-03, «хто затвердив» у переліку документів).
/// </summary>
/// <remarks>
/// ⚠ Своя база, зупинена на міграції перед цією: так виглядає майданчик до оновлення. Перевіряється: форма індексів
/// (ключ, фільтр, INCLUDE), Down і повторний Up, ідемпотентний <c>migration.sql</c> двічі, наявний індекс (збірка
/// вручну до оновлення) не ламає міграцію, гілка без <c>ONLINE</c> (редакція 4) і - головне - ПЛАН: запити
/// прибиральника й «хто затвердив» користуються новими індексами, а не скан/key lookup.
/// <para>
/// Мутації: прибрати <c>HasFilter</c>/<c>Running</c> у <c>CreateIndexesSql</c> - червоніє форма й план прибиральника
/// (скан); прибрати <c>INCLUDE ([Action], ByUserId)</c> - червоніє план «хто затвердив» (Key Lookup); прибрати
/// <c>INDEXPROPERTY … IS NULL</c> - червоніє тест наявного індексу (1913 «already exists»).
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class R11L4SweeperAndApprovalIndexesMigrationTests(SqlServerFixture sql)
{
    /// <summary>Остання міграція перед цією.</summary>
    private const string BeforeMigration = "20261010055923_R9F4FormulaDependencyFormulaIndex";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "R11-L4")]
    public async Task Up_будує_обидва_індекси_Down_прибирає_і_повторний_Up_проходить()
    {
        await using var database = await OwnDatabaseBeforeMigrationAsync();
        var cs = database.ConnectionString;

        Assert.Equal(0, await IndexCountAsync(cs, "calc", "CalculationRun", "IX_CalculationRun_Running"));
        Assert.Equal(0, await IndexCountAsync(cs, "wf", "ApprovalEvent", "IX_ApprovalEvent_DocumentAt"));

        // Наявні рядки до міграції: індекс будується над даними, а не над порожньою таблицею.
        await ExecuteAsync(cs, "ALTER TABLE calc.CalculationRun NOCHECK CONSTRAINT ALL;");
        await ExecuteAsync(cs, """
            INSERT INTO calc.CalculationRun (ProjectId, PeriodKey, Status, StartedAt, FinishedAt)
            VALUES (1, 202601, N'Running', '2026-01-01', NULL), (1, 202602, N'Superseded', '2026-01-02', '2026-01-02');
            """);

        await MigrateAsync(cs);
        await AssertIndexShapeAsync(cs);

        // Фільтр бачить лише Running: у індексі один рядок із двох.
        Assert.Equal(1L, await ScalarAsync<long>(cs, """
            SELECT SUM(p.rows) FROM sys.partitions AS p
              JOIN sys.indexes AS i ON i.object_id = p.object_id AND i.index_id = p.index_id
             WHERE i.object_id = OBJECT_ID(N'calc.CalculationRun') AND i.name = N'IX_CalculationRun_Running'
            """));

        await MigrateAsync(cs, BeforeMigration);
        Assert.Equal(0, await IndexCountAsync(cs, "calc", "CalculationRun", "IX_CalculationRun_Running"));
        Assert.Equal(0, await IndexCountAsync(cs, "wf", "ApprovalEvent", "IX_ApprovalEvent_DocumentAt"));
        Assert.Equal(1, await IndexCountAsync(cs, "wf", "ApprovalEvent", "IX_ApprovalEvent_Document"));

        await MigrateAsync(cs);
        await AssertIndexShapeAsync(cs);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "R11-L4")]
    public async Task Ідемпотентний_скрипт_двічі_і_наявні_індекси_нічого_не_ламають()
    {
        await using var database = await OwnDatabaseBeforeMigrationAsync();
        var cs = database.ConnectionString;

        // Збірка вручну до оновлення (DBA): індекс прибиральника вже є, під тим самим іменем.
        await ExecuteAsync(cs, "CREATE NONCLUSTERED INDEX IX_CalculationRun_Running ON calc.CalculationRun (StartedAt) WHERE [Status] = 'Running';");

        await RunIdempotentScriptAsync(cs);
        await AssertIndexShapeAsync(cs);

        // Другий прогін - 0 змін: міграція записана, індекси на місці й їх по одному.
        await RunIdempotentScriptAsync(cs);
        await AssertIndexShapeAsync(cs);
        Assert.Equal(1, await ScalarAsync<int>(
            cs, "SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId LIKE N'%[_]R11L4SweeperAndApprovalIndexes'"));

        // Та сама команда, виконана ще раз напряму (як при перезапуску після збою посередині): нічого не змінює.
        await ExecuteAsync(cs, R11L4SweeperAndApprovalIndexes.CreateIndexesSql(R11L4SweeperAndApprovalIndexes.EngineEditionExpression));
        await AssertIndexShapeAsync(cs);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "R11-L4")]
    public async Task Гілка_без_ONLINE_для_Standard_і_Express_будує_ті_самі_індекси()
    {
        await using var database = await OwnDatabaseBeforeMigrationAsync();
        var cs = database.ConnectionString;

        // ⚠ Справжньої Standard/Express у перевірці немає: редакція підставляється літералом, а текст без ONLINE
        // виконується наживо на Developer-інстансі - гарантує, що саме ця гілка синтаксично коректна.
        var offline = R11L4SweeperAndApprovalIndexes.CreateIndexesSql("4");
        var online = R11L4SweeperAndApprovalIndexes.CreateIndexesSql(R11L4SweeperAndApprovalIndexes.EngineEditionExpression);
        Assert.Contains("DECLARE @r11Edition int = 4;", offline, StringComparison.Ordinal);
        Assert.Contains("WITH (ONLINE = ON)", online, StringComparison.Ordinal);

        await ExecuteAsync(cs, offline);
        await AssertIndexShapeAsync(cs);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "R11-L4")]
    public async Task План_прибиральника_і_хто_затвердив_користуються_новими_індексами_без_скану_і_key_lookup()
    {
        await using var database = await OwnDatabaseBeforeMigrationAsync();
        var cs = database.ConnectionString;

        await ExecuteAsync(cs, "ALTER TABLE calc.CalculationRun NOCHECK CONSTRAINT ALL;");
        await ExecuteAsync(cs, "ALTER TABLE wf.ApprovalEvent NOCHECK CONSTRAINT ALL;");

        // 30 000 завершених прогонів і лише 3 «Running»: так виглядає таблиця прибиральника в експлуатації.
        await ExecuteAsync(cs, """
            ;WITH n AS (SELECT TOP (30000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i
                          FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b)
            INSERT INTO calc.CalculationRun (ProjectId, PeriodKey, Status, StartedAt, FinishedAt)
            SELECT 1, 202601, N'Superseded', DATEADD(MINUTE, i, '2026-01-01'), DATEADD(MINUTE, i + 1, '2026-01-01') FROM n;

            INSERT INTO calc.CalculationRun (ProjectId, PeriodKey, Status, StartedAt, FinishedAt)
            VALUES (1, 202602, N'Running', '2026-02-01', NULL),
                   (1, 202603, N'Running', '2026-02-02', NULL),
                   (1, 202604, N'Running', '2026-02-03', NULL);
            """);

        // 30 000 подій на 2 999 документів (≈10 на документ): у кожного є і затвердження, і відхилення.
        await ExecuteAsync(cs, """
            ;WITH n AS (SELECT TOP (30000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i
                          FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b)
            INSERT INTO wf.ApprovalEvent (DocumentId, SheetDefId, PeriodKey, FromStatus, ToStatus, Action, ByUserId, At)
            SELECT i % 2999, 1, 202601 + (i % 3), 1, 2, CAST(1 + (i % 6) AS tinyint), 1 + (i % 7),
                   DATEADD(MINUTE, i, '2026-01-01')
              FROM n;
            """);

        await MigrateAsync(cs);

        // Запит прибиральника: форма `AbandonedWorkSweeper.CloseCalculationRunsAsync` (Status = 'Running', межа за
        // StartedAt, порядок StartedAt, TOP). EF вбудовує `N'Running'` літералом - саме так й виглядає тут.
        var sweeper = await PlanAsync(cs, """
            SELECT TOP (50) c.Id FROM calc.CalculationRun AS c
             WHERE c.Status = N'Running' AND c.StartedAt < @before
             ORDER BY c.StartedAt;
            """, new SqlParameter("@before", SqlDbType.DateTime2) { Value = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc) });

        Assert.Contains("IX_CalculationRun_Running", sweeper, StringComparison.Ordinal);
        Assert.DoesNotContain("Clustered Index Scan", sweeper, StringComparison.Ordinal);

        // «Хто затвердив» (`DocumentStore`): події документа від найновішої, відбір за Action і ByUserId.
        var approver = await PlanAsync(cs, """
            SELECT TOP (1) e.ByUserId FROM wf.ApprovalEvent AS e
             WHERE e.DocumentId = @doc AND e.ByUserId IS NOT NULL AND e.Action IN (2, 3)
             ORDER BY e.At DESC;
            """, new SqlParameter("@doc", SqlDbType.BigInt) { Value = 5L });

        Assert.Contains("IX_ApprovalEvent_DocumentAt", approver, StringComparison.Ordinal);
        Assert.False(
            Regex.IsMatch(approver, "Lookup=\"(1|true)\"", RegexOptions.IgnoreCase),
            "«Хто затвердив» знову добирає Action/ByUserId key lookup-ом - INCLUDE індексу не покриває запит.");
    }

    /// <summary>Форма обох індексів за каталогом: ключ, порядок, INCLUDE, фільтр; індекс рівно один.</summary>
    private static async Task AssertIndexShapeAsync(string cs)
    {
        Assert.Equal(1, await IndexCountAsync(cs, "calc", "CalculationRun", "IX_CalculationRun_Running"));
        Assert.Equal("StartedAt", await IndexColumnsAsync(cs, "calc.CalculationRun", "IX_CalculationRun_Running", includedColumns: false));
        Assert.Equal(string.Empty, await IndexColumnsAsync(cs, "calc.CalculationRun", "IX_CalculationRun_Running", includedColumns: true));
        Assert.Equal("([Status]='Running')", await ScalarAsync<string>(
            cs,
            "SELECT filter_definition FROM sys.indexes WHERE object_id = OBJECT_ID(N'calc.CalculationRun') AND name = N'IX_CalculationRun_Running'"));

        Assert.Equal(1, await IndexCountAsync(cs, "wf", "ApprovalEvent", "IX_ApprovalEvent_DocumentAt"));
        Assert.Equal("DocumentId,At DESC", await IndexColumnsAsync(cs, "wf.ApprovalEvent", "IX_ApprovalEvent_DocumentAt", includedColumns: false));
        Assert.Equal("Action,ByUserId", await IndexColumnsAsync(cs, "wf.ApprovalEvent", "IX_ApprovalEvent_DocumentAt", includedColumns: true));
    }

    private static Task<int> IndexCountAsync(string cs, string schema, string table, string index)
        => ScalarAsync<int>(
            cs,
            $"SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID(N'{schema}.{table}') AND name = N'{index}'");

    /// <summary>Стовпці індексу через кому (ключові - з <c>DESC</c>, якщо спадний; або INCLUDE), за порядком.</summary>
    private static async Task<string> IndexColumnsAsync(string cs, string table, string index, bool includedColumns)
    {
        var result = await ScalarAsync<string?>(
            cs,
            $"""
            SELECT ISNULL(STRING_AGG(c.name + CASE WHEN ic.is_descending_key = 1 THEN N' DESC' ELSE N'' END, N',')
                          WITHIN GROUP (ORDER BY ic.key_ordinal, ic.index_column_id), N'')
              FROM sys.indexes AS i
              JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
              JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
             WHERE i.object_id = OBJECT_ID(N'{table}') AND i.name = N'{index}'
               AND ic.is_included_column = {(includedColumns ? 1 : 0)};
            """);

        return result ?? string.Empty;
    }

    /// <summary>План виконання (<c>SET STATISTICS XML</c>) тексту запиту.</summary>
    private static async Task<string> PlanAsync(string cs, string text, SqlParameter parameter)
    {
        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync();

        await using (var on = connection.CreateCommand())
        {
            on.CommandText = "SET STATISTICS XML ON;";
            await on.ExecuteNonQueryAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = text;
        command.Parameters.Add(parameter);

        await using var reader = await command.ExecuteReaderAsync();
        do
        {
            if (reader.FieldCount == 1
                && reader.GetName(0).Contains("Showplan", StringComparison.OrdinalIgnoreCase)
                && await reader.ReadAsync())
            {
                return reader.GetString(0);
            }
        }
        while (await reader.NextResultAsync());

        throw new InvalidOperationException("SQL Server не повернув план виконання.");
    }

    /// <summary>Своя база (з видаленням у кінці), зупинена на міграції перед цією.</summary>
    private async Task<OwnDatabase> OwnDatabaseBeforeMigrationAsync()
    {
        var builder = new SqlConnectionStringBuilder(sql.ConnectionString);
        var name = builder.InitialCatalog + "_R11L4" + Guid.NewGuid().ToString("N")[..6];

        await ExecuteAsync(
            new SqlConnectionStringBuilder(sql.ConnectionString) { InitialCatalog = "master" }.ConnectionString,
            $"""
            IF DB_ID(N'{name}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{name}];
            END
            CREATE DATABASE [{name}] COLLATE Latin1_General_100_CI_AS_SC;
            """);

        builder.InitialCatalog = name;
        var database = new OwnDatabase(builder.ConnectionString, name, sql.ConnectionString);
        await MigrateAsync(database.ConnectionString, BeforeMigration);

        return database;
    }

    /// <summary>Своя база тесту; <c>DisposeAsync</c> її знищує.</summary>
    private sealed class OwnDatabase(string connectionString, string name, string serverConnectionString) : IAsyncDisposable
    {
        public string ConnectionString { get; } = connectionString;

        public async ValueTask DisposeAsync()
        {
            SqlConnection.ClearAllPools();
            await ExecuteAsync(
                new SqlConnectionStringBuilder(serverConnectionString) { InitialCatalog = "master" }.ConnectionString,
                $"""
                IF DB_ID(N'{name}') IS NOT NULL
                BEGIN
                    ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [{name}];
                END
                """);
        }
    }

    /// <summary>
    /// Той самий <c>migration.sql</c>, що генерують <c>deploy-ecr.ps1</c> і <c>build-installer.ps1</c>
    /// (<c>--idempotent</c>), пакетами по <c>GO</c>; перша ж помилка зупиняє, як <c>sqlcmd -b</c>.
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

        if (result is null or DBNull)
        {
            return default!;
        }

        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return (T)Convert.ChangeType(result, target, CultureInfo.InvariantCulture);
    }
}
