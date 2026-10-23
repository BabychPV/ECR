// tests/Ecr.Infrastructure.Tests/Persistence/D443DocumentParticipantsMigrationTests.cs
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
/// Міграція <c>D443DocumentParticipants</c> (<c>D-443</c>, AN-130): <c>doc.EditAssignment.Level</c> зі значенням
/// <c>2</c> (Write) для наявних передач, CHECK рівня й «місяць/період/ексклюзивність лише для Write»; відкат при
/// учасниках рівнів Read/Submit/Approve зупиняється <c>THROW 50443</c> ДО зміни схеми.
/// </summary>
/// <remarks>
/// Тест будує ВЛАСНУ базу, зупинену на міграції перед цією (як <see cref="D440DropFourEyesMigrationTests"/>).
/// <para>
/// Мутації: прибрати <c>defaultValue: (byte)2</c> з Up — наявна передача отримує 0 і падає CHECK рівня (червоний
/// перший тест); прибрати <c>CK_EditAssignment_LevelSlice</c> — Read із місяцем вставляється (червоний перший тест);
/// прибрати <c>migrationBuilder.Sql(DownPrecheckSql)</c> з Down — відкат мовчки перетворює читача на передачу
/// Write, а не 50443 (червоний другий тест).
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class D443DocumentParticipantsMigrationTests(SqlServerFixture sql)
{
    /// <summary>Остання міграція перед цією.</summary>
    private const string BeforeMigration = "20261023124421_D440DropFourEyes";

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-443")]
    public async Task Up_дає_наявним_передачам_Write_і_CHECK_тримають_рівень_і_зріз()
    {
        var cs = await CreateDatabaseBeforeMigrationAsync("_D443A");
        var legacy = await InsertAssignmentAsync(cs, "S=F1|T=*|M=3|P=*", extraColumns: null);

        await MigrateAsync(cs, null);

        // Наявна передача місяця — Write: поведінка до міграції без змін.
        Assert.Equal(2, await ScalarAsync(cs, $"SELECT CAST([Level] AS int) FROM doc.EditAssignment WHERE Id = {Num(legacy)}"));
        Assert.Equal(1, await CountConstraintAsync(cs, "CK_EditAssignment_Level"));
        Assert.Equal(1, await CountConstraintAsync(cs, "CK_EditAssignment_LevelSlice"));

        // Read на аркуш без місяця й не ексклюзивний — проходить; рівень 5 (Manage) і Read із місяцем — ні.
        await InsertAssignmentAsync(cs, "S=F1|T=*|M=*|P=*|L=Read", "[Level] = 1, IsExclusive = 0");
        var manage = await Record.ExceptionAsync(
            () => InsertAssignmentAsync(cs, "S=F2|T=*|M=*|P=*|L=Manage", "[Level] = 5, IsExclusive = 0"));
        Assert.Contains("CK_EditAssignment_Level", FindSqlException(manage)?.Message ?? manage?.ToString() ?? "-", StringComparison.Ordinal);
        var monthly = await Record.ExceptionAsync(
            () => InsertAssignmentAsync(cs, "S=F3|T=*|M=1|P=*|L=Read", "[Level] = 1, IsExclusive = 0, MonthNumber = 1"));
        Assert.Contains("CK_EditAssignment_LevelSlice", FindSqlException(monthly)?.Message ?? monthly?.ToString() ?? "-", StringComparison.Ordinal);
        var exclusive = await Record.ExceptionAsync(
            () => InsertAssignmentAsync(cs, "S=F4|T=*|M=*|P=*|L=Approve", "[Level] = 4, IsExclusive = 1"));
        Assert.Contains("CK_EditAssignment_LevelSlice", FindSqlException(exclusive)?.Message ?? exclusive?.ToString() ?? "-", StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-443")]
    public async Task Down_з_учасником_Read_зупиняється_THROW_50443_а_без_них_прибирає_колонку()
    {
        var cs = await CreateDatabaseBeforeMigrationAsync("_D443B");
        await MigrateAsync(cs, null);

        var reader = await InsertAssignmentAsync(cs, "S=F1|T=*|M=*|P=*|L=Read", "[Level] = 1, IsExclusive = 0");

        var error = await Record.ExceptionAsync(() => MigrateAsync(cs, BeforeMigration));

        var sqlError = FindSqlException(error);
        Assert.True(
            sqlError is { Number: D443DocumentParticipants.PrecheckErrorNumber },
            $"очікувалась передперевірка {D443DocumentParticipants.PrecheckErrorNumber}, а прийшло: "
            + (sqlError is null ? error?.ToString() ?? "жодної помилки" : $"SQL {sqlError.Number}: {sqlError.Message}"));
        Assert.Contains($"EditAssignment {Num(reader)} (документ", sqlError!.Message, StringComparison.Ordinal);

        // Зупинка ДО зміни схеми: колонка й рядок на місці.
        Assert.Equal(1, await ScalarAsync(
            cs, "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'doc.EditAssignment') AND name = N'Level'"));
        Assert.Equal(1, await ScalarAsync(cs, $"SELECT COUNT(*) FROM doc.EditAssignment WHERE Id = {Num(reader)}"));

        // Учасника знято (рішення адміністратора) — відкат проходить.
        await ExecuteAsync(cs, $"DELETE FROM doc.EditAssignment WHERE Id = {Num(reader)};");
        await MigrateAsync(cs, BeforeMigration);

        Assert.Equal(0, await ScalarAsync(
            cs, "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'doc.EditAssignment') AND name = N'Level'"));
    }

    private static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Передача документа 1 (ключі таблиці вимкнено: батьківських рядків тест не створює).</summary>
    private static async Task<long> InsertAssignmentAsync(string cs, string sliceKey, string? extraColumns)
    {
        var columns = "DocumentId, SliceKey, DelegatedByUserId, HandoffId, CreatedAt, CreatedByUserId, ModifiedAt, ModifiedByUserId";
        var values = $"1, N'{sliceKey}', 7, NEWID(), '2026-10-23', 7, '2026-10-23', 7";
        if (extraColumns is not null)
        {
            var parts = extraColumns.Split(',', StringSplitOptions.TrimEntries);
            columns += ", " + string.Join(", ", parts.Select(p => p.Split('=', 2)[0].Trim()));
            values += ", " + string.Join(", ", parts.Select(p => p.Split('=', 2)[1].Trim()));
        }

        return await ScalarAsync(
            cs,
            $"""
            SET NOCOUNT ON;
            {DisableForeignKeysSql("doc.EditAssignment")}
            INSERT INTO doc.EditAssignment ({columns}) VALUES ({values});
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """);
    }

    /// <summary>
    /// Вимикає ЛИШЕ зовнішні ключі таблиці (батьківських рядків тест не створює). <c>NOCHECK CONSTRAINT ALL</c>
    /// вимкнув би й CHECK — саме те, що тест перевіряє.
    /// </summary>
    private static string DisableForeignKeysSql(string table)
        => $"""
            DECLARE @fk nvarchar(max) = N'';
            SELECT @fk = @fk + N'ALTER TABLE {table} NOCHECK CONSTRAINT ' + QUOTENAME(name) + N';'
            FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID(N'{table}');
            EXEC (@fk);
            """;

    private static Task<int> CountConstraintAsync(string cs, string name)
        => ScalarAsync(cs, $"SELECT COUNT(*) FROM sys.check_constraints WHERE name = N'{name}'");

    /// <summary>Своя база, піднята рівно до <see cref="BeforeMigration"/>.</summary>
    private async Task<string> CreateDatabaseBeforeMigrationAsync(string suffix)
    {
        var builder = new SqlConnectionStringBuilder(sql.ConnectionString);
        var name = builder.InitialCatalog + suffix;

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
        await MigrateAsync(builder.ConnectionString, BeforeMigration);
        return builder.ConnectionString;
    }

    private static async Task MigrateAsync(string connectionString, string? target)
    {
        await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(connectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .Options);
        await db.GetService<IMigrator>().MigrateAsync(target);
    }

    private static async Task ExecuteAsync(string connectionString, string commandText)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.CommandTimeout = 300;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> ScalarAsync(string connectionString, string query)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        command.CommandTimeout = 300;
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
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
}
