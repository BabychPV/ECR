// tests/Ecr.Infrastructure.Tests/Persistence/RK03PeriodStartMarginTests.cs
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
/// <c>RK03RegistryTemporalHistory</c> на базі з РЯДКАМИ в довідниках не падає
/// на <c>ADD PERIOD FOR SYSTEM_TIME</c> через «початок періоду в майбутньому»
/// (Msg 13542), навіть коли час, який SQL Server ставить наявним рядкам,
/// випереджає момент перевірки періоду.
/// </summary>
/// <remarks>
/// ⚠ Звідки «майбутнє»: <c>SYSUTCDATETIME()</c> — <c>datetime2(7)</c>, колонка —
/// <c>datetime2(3)</c>, і перетворення округлює ВГОРУ до 0,5 мс; перевірка ж
/// порівнює з годинником у мить, коли дійшла до рядків. У CI (Linux-контейнер,
/// b12c5860) ALTER встигав за ці півмілісекунди й падав зрідка; на Windows —
/// ніколи з 2000 прогонів. Недетерміновану гонитву тест замінює детермінованою
/// моделлю: у SQL, який генерує САМА міграція, годинник у <c>DEFAULT</c>
/// зсунуто на <see cref="ClockAheadMs"/> мс уперед — більше за будь-яку
/// реальну похибку і більше за час, за який однорядковий ALTER доходить до
/// перевірки, але менше за запас міграції (1 с).
///
/// ⚠ Контроль моделі — у тому ж тесті: колишнє тіло (голий
/// <c>SYSUTCDATETIME()</c>) за тієї самої умови мусить впасти саме з 13542.
/// Без контролю «проходить» нічого б не доводило: могло б пройти лише тому,
/// що модель не створює майбутнього.
///
/// Мутаційний доказ: повернути в міграції <c>DEFAULT SYSUTCDATETIME()</c> —
/// тест червоний в обох режимах скрипта (13542 на першій же таблиці).
/// </remarks>
[Collection("SqlServer")]
public sealed class RK03PeriodStartMarginTests(SqlServerFixture sql)
{
    private const string RK02 = "20260927222003_RK02RegistryComposition";
    private const string RK03 = "20260927232407_RK03RegistryTemporalHistory";

    /// <summary>«ADD PERIOD … open records with start of period set to a value in the future».</summary>
    private const int FuturePeriodStart = 13542;

    private const int ClockAheadMs = 500;

    private static readonly string ClockAhead =
        $"DATEADD(millisecond, {ClockAheadMs.ToString(CultureInfo.InvariantCulture)}, SYSUTCDATETIME())";

    private static readonly string[] NamedDefaults =
        ["DF_RegEntry_PE", "DF_RegEntry_PS", "DF_RegValue_PE", "DF_RegValue_PS"];

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "RT-04")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RK03_на_рядках_проходить_коли_час_DEFAULT_випереджає_перевірку_періоду(bool idempotent)
    {
        var connectionString = await CreateDatabaseAtRK02Async(idempotent ? "_RK03mi" : "_RK03m");
        await InsertNeutralRowAsync(connectionString, "dic.RegistryEntry");
        await InsertNeutralRowAsync(connectionString, "dic.RegistryValue");

        // ── Контроль моделі: колишнє тіло за тієї ж умови падає як у CI ────────
        var control = await Record.ExceptionAsync(() => ExecuteAsync(connectionString, $"""
            ALTER TABLE [dic].[RegistryValue] ADD
                [ChangedByUserId] int NULL,
                [PeriodStart] datetime2(3) GENERATED ALWAYS AS ROW START HIDDEN NOT NULL
                    CONSTRAINT [DF_RegValue_PS] DEFAULT {ClockAhead},
                [PeriodEnd]   datetime2(3) GENERATED ALWAYS AS ROW END   HIDDEN NOT NULL
                    CONSTRAINT [DF_RegValue_PE] DEFAULT CONVERT(datetime2(3), '9999-12-31 23:59:59.999'),
                PERIOD FOR SYSTEM_TIME ([PeriodStart], [PeriodEnd]);
            """));
        var controlError = Assert.IsType<SqlException>(control);
        Assert.Equal(FuturePeriodStart, controlError.Number);
        Assert.Contains("start of period set to a value in the future", controlError.Message, StringComparison.Ordinal);

        // Невдалий ALTER атомарний: таблиця лишилась у стані RK02.
        Assert.Equal(0, await ScalarAsync<int>(
            connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dic.RegistryValue') AND name = N'PeriodStart'"));

        // ── Сама міграція, той самий зсув годинника ────────────────────────────
        string script;
        await using (var db = CreateContext(connectionString))
        {
            script = db.GetService<IMigrator>().GenerateScript(
                fromMigration: RK02,
                toMigration: RK03,
                options: idempotent ? MigrationsSqlGenerationOptions.Idempotent : MigrationsSqlGenerationOptions.Default);
        }

        // Рівно два DEFAULT від годинника — по одному на таблицю. Інша кількість
        // означає, що тіло міграції змінилось і модель більше не зсуває те, що треба.
        const string Clock = "SYSUTCDATETIME()";
        Assert.Equal(2, CountOccurrences(script, Clock));
        var modeled = script.Replace(Clock, ClockAhead, StringComparison.Ordinal);

        var before = await ScalarAsync<DateTime>(connectionString, "SELECT CAST(SYSUTCDATETIME() AS datetime2(3))");
        await RunBatchesAsync(connectionString, modeled);
        var after = await ScalarAsync<DateTime>(connectionString, "SELECT CAST(SYSUTCDATETIME() AS datetime2(3))");

        Assert.Equal(1, await ScalarAsync<int>(
            connectionString,
            $"SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId = N'{RK03}'"));

        // RT-04 (§3.5): наявні рядки отримують момент міграції, а не 0001-01-01 —
        // інакше «станом на» будь-яку дату до міграції повертав би нинішні значення.
        // Нижня межа — запас міграції (1 с) і зсув моделі; верхня — кінець міграції.
        foreach (var table in new[] { "dic.RegistryEntry", "dic.RegistryValue" })
        {
            var start = await ScalarAsync<DateTime>(connectionString, $"SELECT MIN(PeriodStart) FROM {table}");
            Assert.InRange(start, before.AddSeconds(-2), after);
            Assert.Equal(1, await ScalarAsync<int>(connectionString, $"SELECT COUNT(*) FROM {table}"));
        }

        // Іменовані обмеження лишились іменованими — їх знімає Down за іменем.
        var defaults = await ScalarAsync<string>(
            connectionString,
            """
            SELECT STRING_AGG(name, N',') WITHIN GROUP (ORDER BY name)
            FROM sys.default_constraints
            WHERE parent_object_id IN (OBJECT_ID(N'dic.RegistryEntry'), OBJECT_ID(N'dic.RegistryValue'))
              AND name LIKE N'DF[_]Reg%[_]P[SE]'
            """);
        Assert.Equal(string.Join(',', NamedDefaults), defaults);
    }

    /// <summary>Своя база, зупинена на RK02 — стан майданчика з даними в довідниках до RK03.</summary>
    private async Task<string> CreateDatabaseAtRK02Async(string suffix)
    {
        var builder = new SqlConnectionStringBuilder(sql.ConnectionString);
        var name = builder.InitialCatalog + suffix;

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

        await using var db = CreateContext(builder.ConnectionString);
        await db.GetService<IMigrator>().MigrateAsync(RK02);

        return builder.ConnectionString;
    }

    /// <summary>
    /// Рядок із нейтральними значеннями в обов'язкових стовпцях; обмеження
    /// таблиці вимкнено.
    /// </summary>
    /// <remarks>
    /// ⚠ Перевіряється поведінка ALTER на НЕПОРОЖНІЙ таблиці, а не зв'язність
    /// даних — будувати граф довідника заради одного рядка зайве.
    /// Дати — будь-які: стовпці періоду з'являються лише в RK03.
    /// </remarks>
    private static Task InsertNeutralRowAsync(string connectionString, string table)
        => ExecuteAsync(
            connectionString,
            $"""
            DECLARE @obj int = OBJECT_ID(N'{table}');
            DECLARE @cols nvarchar(max), @vals nvarchar(max);

            SELECT
                @cols = STRING_AGG(CAST(QUOTENAME(c.name) AS nvarchar(max)), N', ') WITHIN GROUP (ORDER BY c.column_id),
                @vals = STRING_AGG(CAST(
                    CASE
                        WHEN ty.name IN (N'int', N'bigint', N'smallint', N'tinyint', N'bit', N'decimal', N'numeric', N'float', N'real', N'money')
                            THEN N'0'
                        WHEN ty.name IN (N'nvarchar', N'varchar', N'nchar', N'char') THEN N'N''x'''
                        WHEN ty.name IN (N'datetime2', N'datetime', N'date', N'datetimeoffset', N'smalldatetime')
                            THEN N'''2020-01-01'''
                        WHEN ty.name = N'time' THEN N'''00:00'''
                        WHEN ty.name = N'uniqueidentifier' THEN N'NEWID()'
                        WHEN ty.name IN (N'varbinary', N'binary') THEN N'0x00'
                        ELSE N'NULL'
                    END AS nvarchar(max)), N', ') WITHIN GROUP (ORDER BY c.column_id)
            FROM sys.columns AS c
            JOIN sys.types AS ty ON ty.user_type_id = c.user_type_id
            WHERE c.object_id = @obj
              AND c.is_identity = 0
              AND c.is_computed = 0
              AND ty.name NOT IN (N'timestamp', N'rowversion')
              AND c.is_nullable = 0 AND c.default_object_id = 0;

            EXEC (N'ALTER TABLE {table} NOCHECK CONSTRAINT ALL');

            DECLARE @insert nvarchar(max) = N'INSERT INTO {table} (' + @cols + N') VALUES (' + @vals + N');';
            EXEC (@insert);
            """);

    /// <summary>Скрипт пакетами по <c>GO</c>; перша ж помилка зупиняє, як <c>sqlcmd -b</c>.</summary>
    private static async Task RunBatchesAsync(string connectionString, string script)
    {
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

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
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
        var result = await command.ExecuteScalarAsync();
        return (T)Convert.ChangeType(result!, typeof(T), CultureInfo.InvariantCulture);
    }
}
