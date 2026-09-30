// tests/Ecr.Infrastructure.Tests/Persistence/U1UnitForeignKeysMigrationTests.cs
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
/// Міграція <c>U1UnitForeignKeys</c> (HSE301 U1, аудит C6 п.1 і п.3): висяча одиниця
/// зупиняє оновлення ЗРОЗУМІЛО і ДО зміни схеми; після виправлення ключі й індекс
/// стають на місце, і міграція відкочується.
/// </summary>
/// <remarks>
/// ⚠ Свіжої бази передперевірка не стосується: у порожніх таблицях висіти нічому. Тест
/// тому будує ВЛАСНУ базу, зупинену на міграції перед U1, — так виглядає майданчик,
/// розгорнутий до 2026-09-29, де видалення одиниці могло лишити колонку з її номером.
/// Механізм і форма тесту — ті самі, що в <see cref="D148ScalePrecheckTests"/>.
///
/// ⚠ Гілку індексу без <c>ONLINE</c> (Standard, Express) наживо тут не прогнати: на
/// машині розробки лише Developer (<c>EngineEdition = 3</c>). Тому команда індексу
/// збирається на сервері з підставленою редакцією (<see cref="U1UnitForeignKeys.IndexStatementSql"/>),
/// і гілка «4» виконується по-справжньому на цьому ж Developer — синтаксис і місце
/// перевіряються, відмова 1712 справжньої Express — ні.
/// </remarks>
[Collection("SqlServer")]
public sealed class U1UnitForeignKeysMigrationTests(SqlServerFixture sql)
{
    /// <summary>Остання міграція перед U1.</summary>
    private const string BeforeU1 = "20260928200307_Analiz1JobsCoverageIndexes";

    /// <summary>Одиниці з такими номерами немає в жодній базі.</summary>
    private const int MissingA = 999_998;

    private const int MissingB = 999_999;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-U1")]
    public async Task Висяча_одиниця_зупиняє_U1_до_зміни_схеми_з_переліком_а_виправлена_база_проходить_і_відкочується()
    {
        var connectionString = await CreateDatabaseBeforeU1Async();

        // ── Фаза «відмова»: дві колонки й одне поле з одиницями, яких немає ─────────
        var columnA = await InsertAsync(connectionString, "cfg.ColumnDef", ("Code", "N'U1A'"), ("UnitId", Int(MissingA)));
        var columnB = await InsertAsync(connectionString, "cfg.ColumnDef", ("Code", "N'U1B'"), ("UnitId", Int(MissingB)));
        var field = await InsertAsync(connectionString, "cfg.RegistryFieldDef", ("Code", "N'U1C'"), ("UnitId", Int(MissingB)));

        // Два шляхи застосування, обидва мають зупинитись однаково:
        // ідемпотентний скрипт — ним розгортають прод (deploy-ecr.ps1, пакет
        // інсталятора); MigrateAsync — режим StartupMode=Migrate.
        // Мутація: прибрати migrationBuilder.Sql(PrecheckSql) з Up — обидва шляхи
        // приносять 547 (FK_ColumnDef_Unit), а не 50301: червоне.
        var viaScript = await Record.ExceptionAsync(() => RunIdempotentScriptAsync(connectionString));
        var viaMigrate = await Record.ExceptionAsync(() => MigrateAsync(connectionString));

        foreach (var (path, error) in new[] { ("migration.sql", viaScript), ("MigrateAsync", viaMigrate) })
        {
            var sqlError = FindSqlException(error);
            Assert.True(
                sqlError is { Number: U1UnitForeignKeys.PrecheckErrorNumber },
                $"{path}: очікувалась передперевірка {U1UnitForeignKeys.PrecheckErrorNumber}, а прийшло: " +
                $"{(sqlError is null ? error?.ToString() ?? "жодної помилки" : $"SQL {sqlError.Number}: {sqlError.Message}")}");

            // Повідомлення називає таблицю, кількість, відсутні одиниці й рядки —
            // інакше оператор знає, що зламано, і не знає де.
            Assert.Contains(
                $"cfg.ColumnDef.UnitId: рядків 2; одиниць немає: {MissingA}, {MissingB}; перші рядки: "
                + $"Id {columnA} U1A (TableDefId 0, UnitId {MissingA}), Id {columnB} U1B (TableDefId 0, UnitId {MissingB})",
                sqlError!.Message,
                StringComparison.Ordinal);
            Assert.Contains(
                $"cfg.RegistryFieldDef.UnitId: рядків 1; одиниць немає: {MissingB}; перші рядки: "
                + $"Id {field} U1C (RegistryDefId 0, UnitId {MissingB})",
                sqlError.Message,
                StringComparison.Ordinal);
            Assert.Contains("operations-runbook.md", sqlError.Message, StringComparison.Ordinal);
        }

        // ⛔ Зупинка ДО зміни схеми: міграція не записана, ключів і індексу немає,
        // дані не обнулено.
        Assert.Equal(0, await ScalarAsync<int>(
            connectionString,
            "SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId LIKE N'%[_]U1UnitForeignKeys'"));
        Assert.Equal(0, await SchemaObjectsAsync(connectionString));
        Assert.Equal(MissingB, await ScalarAsync<int>(
            connectionString, $"SELECT UnitId FROM cfg.RegistryFieldDef WHERE Id = {field}"));

        // ── Фаза «виправлено»: рішення оператора (runbook §8.2) — тут зняти одиницю ──
        await ExecuteAsync(connectionString, "UPDATE cfg.ColumnDef SET UnitId = NULL WHERE UnitId IS NOT NULL;");
        await ExecuteAsync(connectionString, "UPDATE cfg.RegistryFieldDef SET UnitId = NULL WHERE UnitId IS NOT NULL;");

        await MigrateAsync(connectionString);

        Assert.Equal(3, await SchemaObjectsAsync(connectionString));

        // Ключ довірений (WITH CHECK) і справді тримає: нова висяча одиниця — 547.
        Assert.Equal(0, await ScalarAsync<int>(
            connectionString,
            "SELECT COUNT(*) FROM sys.foreign_keys WHERE name IN (N'FK_ColumnDef_Unit', N'FK_RegField_Unit') AND (is_not_trusted = 1 OR is_disabled = 1)"));
        // ⚠ InsertAsync вимикає ВСІ обмеження таблиці — і новий ключ теж, тож тут він
        // вмикається назад із перевіркою; решта (TableDefId = 0) лишається вимкненою.
        var rejected = await Record.ExceptionAsync(
            () => InsertAsync(
                connectionString, "cfg.ColumnDef", keepChecked: "FK_ColumnDef_Unit",
                ("Code", "N'U1D'"), ("UnitId", Int(MissingA))));
        var fkError = FindSqlException(rejected);
        Assert.True(fkError is { Number: 547 }, $"очікувалось 547, а прийшло: {rejected}");
        Assert.Contains("FK_ColumnDef_Unit", fkError!.Message, StringComparison.Ordinal);

        // ── Фаза «вниз»: Down прибирає рівно своє, і вгору знову проходить ─────────
        await MigrateAsync(connectionString, BeforeU1);
        Assert.Equal(0, await SchemaObjectsAsync(connectionString));

        await MigrateAsync(connectionString);
        Assert.Equal(3, await SchemaObjectsAsync(connectionString));
    }

    /// <remarks>
    /// ⚠ Не «перевірка тексту заради тексту»: для «4» команда виконується наживо на
    /// партиційованій спільній базі — і вона мусить лягти туди ж, де лежить кластерний
    /// індекс таблиці. Для «3/5/8» ONLINE наживо перевіряє сама міграція (цей інстанс —
    /// Developer). Мутація: зробити умову <c>@u1Edition &gt;= 2</c> — червоне на «2» і «4».
    /// </remarks>
    [Theory]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(8, true)]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-U1")]
    public async Task Індекс_CalculationResult_UnitId_ONLINE_лише_на_Enterprise_і_Azure(int engineEdition, bool online)
    {
        var edition = engineEdition.ToString(CultureInfo.InvariantCulture);
        var statement = await ScalarAsync<string>(
            sql.ConnectionString,
            U1UnitForeignKeys.IndexStatementSql(edition) + "SELECT @u1Create;");

        Assert.StartsWith(
            "CREATE NONCLUSTERED INDEX IX_CalculationResult_UnitId ON calc.CalculationResult (UnitId)",
            statement,
            StringComparison.Ordinal);
        Assert.Equal(online, statement.Contains("ONLINE = ON", StringComparison.Ordinal));

        if (online)
        {
            return;
        }

        // Гілка без ONLINE — наживо: зняти індекс і поставити тією самою командою.
        const string Drop = """
            IF INDEXPROPERTY(OBJECT_ID(N'calc.CalculationResult'), N'IX_CalculationResult_UnitId', 'IndexID') IS NOT NULL
                DROP INDEX IX_CalculationResult_UnitId ON calc.CalculationResult;
            """;
        var create = U1UnitForeignKeys.IndexStatementSql(edition) + "EXEC sys.sp_executesql @u1Create;";

        try
        {
            await ExecuteAsync(sql.ConnectionString, Drop);
            await ExecuteAsync(sql.ConnectionString, create);

            // Той самий простір даних, що в кластерного індексу: на партиційованій
            // базі — схема партиціонування, на решті — файлова група.
            Assert.Equal(1, await ScalarAsync<int>(
                sql.ConnectionString,
                """
                SELECT COUNT(*)
                FROM sys.indexes AS ix
                JOIN sys.indexes AS cl ON cl.object_id = ix.object_id AND cl.index_id IN (0, 1)
                WHERE ix.object_id = OBJECT_ID(N'calc.CalculationResult')
                  AND ix.name = N'IX_CalculationResult_UnitId'
                  AND ix.data_space_id = cl.data_space_id
                """));
        }
        finally
        {
            // Спільна база лишається з індексом, хоч би що сталося вище.
            await ExecuteAsync(
                sql.ConnectionString,
                U1UnitForeignKeys.IndexStatementSql(U1UnitForeignKeys.EngineEditionExpression)
                + "IF INDEXPROPERTY(OBJECT_ID(N'calc.CalculationResult'), N'IX_CalculationResult_UnitId', 'IndexID') IS NULL "
                + "EXEC sys.sp_executesql @u1Create;");
        }
    }

    /// <summary>Скільки з трьох об'єктів U1 є в базі: два ключі й індекс.</summary>
    private static Task<int> SchemaObjectsAsync(string connectionString)
        => ScalarAsync<int>(
            connectionString,
            """
            SELECT
                (SELECT COUNT(*) FROM sys.foreign_keys WHERE name IN (N'FK_ColumnDef_Unit', N'FK_RegField_Unit'))
              + (SELECT COUNT(*) FROM sys.indexes
                 WHERE object_id = OBJECT_ID(N'calc.CalculationResult') AND name = N'IX_CalculationResult_UnitId')
            """);

    /// <summary>Своя база, зупинена на міграції перед U1.</summary>
    /// <remarks>
    /// Без файлових груп і схем партиціонування — як у <see cref="D148ScalePrecheckTests"/>:
    /// міграції лягають на <c>PRIMARY</c>, і для передперевірки розміщення байдуже.
    /// </remarks>
    private async Task<string> CreateDatabaseBeforeU1Async()
    {
        var builder = new SqlConnectionStringBuilder(sql.ConnectionString);
        var name = builder.InitialCatalog + "_U1";

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
        await MigrateAsync(builder.ConnectionString, BeforeU1);

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

    /// <summary>
    /// Рядок, у якому задано лише потрібні стовпці; решта обов'язкових — нейтральні
    /// значення за типом, обмеження таблиці вимкнено (як у <see cref="D148ScalePrecheckTests"/>).
    /// </summary>
    /// <returns>Ідентифікатор вставленого рядка.</returns>
    /// <remarks>
    /// ⚠ Перевіряється посилання на одиницю, а не зв'язність таблиці з шаблоном чи
    /// довідником: <c>NOCHECK CONSTRAINT ALL</c> діє лише на наявні обмеження цієї
    /// тестової бази — ключ, який поставить U1, народиться ввімкненим.
    /// </remarks>
    private static Task<int> InsertAsync(
        string connectionString, string table, params (string Column, string Literal)[] values)
        => InsertAsync(connectionString, table, keepChecked: null, values);

    /// <param name="connectionString">База.</param>
    /// <param name="table">Таблиця.</param>
    /// <param name="keepChecked">Обмеження, яке після вимкнення всіх вмикається назад <c>WITH CHECK</c>.</param>
    /// <param name="values">Стовпці й літерали.</param>
    private static async Task<int> InsertAsync(
        string connectionString, string table, string? keepChecked, params (string Column, string Literal)[] values)
    {
        var recheck = keepChecked is null
            ? string.Empty
            : $"EXEC (N'ALTER TABLE {table} WITH CHECK CHECK CONSTRAINT {keepChecked}');";

        var given = string.Join(
            " UNION ALL ",
            values.Select(v => $"SELECT N'{v.Column}', N'{v.Literal.Replace("'", "''", StringComparison.Ordinal)}'"));

        return await ScalarAsync<int>(
            connectionString,
            $"""
            SET NOCOUNT ON;
            DECLARE @obj int = OBJECT_ID(N'{table}');
            DECLARE @given TABLE (ColumnName sysname, Literal nvarchar(200));
            INSERT @given (ColumnName, Literal) {given};

            DECLARE @cols nvarchar(max), @vals nvarchar(max);
            SELECT
                @cols = STRING_AGG(CAST(QUOTENAME(c.name) AS nvarchar(max)), N', ') WITHIN GROUP (ORDER BY c.column_id),
                @vals = STRING_AGG(CAST(COALESCE(g.Literal,
                    CASE
                        WHEN ty.name IN (N'int', N'bigint', N'smallint', N'tinyint', N'bit', N'decimal', N'numeric', N'float', N'real', N'money')
                            THEN N'0'
                        WHEN ty.name IN (N'nvarchar', N'varchar', N'nchar', N'char') THEN N'N''x'''
                        WHEN ty.name IN (N'datetime2', N'datetime', N'date', N'datetimeoffset', N'smalldatetime')
                            THEN N'''2026-01-01'''
                        WHEN ty.name = N'time' THEN N'''00:00'''
                        WHEN ty.name = N'uniqueidentifier' THEN N'NEWID()'
                        WHEN ty.name IN (N'varbinary', N'binary') THEN N'0x00'
                        ELSE N'NULL'
                    END) AS nvarchar(max)), N', ') WITHIN GROUP (ORDER BY c.column_id)
            FROM sys.columns AS c
            JOIN sys.types AS ty ON ty.user_type_id = c.user_type_id
            LEFT JOIN @given AS g ON g.ColumnName = c.name
            WHERE c.object_id = @obj
              AND c.is_identity = 0
              AND c.is_computed = 0
              AND c.generated_always_type = 0
              AND ty.name NOT IN (N'timestamp', N'rowversion')
              AND (g.ColumnName IS NOT NULL OR (c.is_nullable = 0 AND c.default_object_id = 0));

            EXEC (N'ALTER TABLE {table} NOCHECK CONSTRAINT ALL');
            {recheck}

            DECLARE @insert nvarchar(max) =
                N'INSERT INTO {table} (' + @cols + N') VALUES (' + @vals + N'); SELECT CAST(SCOPE_IDENTITY() AS int);';
            EXEC sys.sp_executesql @insert;
            """);
    }

    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

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
