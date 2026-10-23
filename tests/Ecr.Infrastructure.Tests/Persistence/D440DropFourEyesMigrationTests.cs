// tests/Ecr.Infrastructure.Tests/Persistence/D440DropFourEyesMigrationTests.cs
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
/// Міграція <c>D440DropFourEyes</c> (<c>D-440</c>, AN-127): знімає <c>CK_MV_FourEyes</c> і
/// <c>CK_RecalcApproval_FourEyes</c>; відкат на даних, погоджених власним автором, зупиняється
/// <c>THROW 50440</c> з переліком ДО зміни схеми.
/// </summary>
/// <remarks>
/// Тест будує ВЛАСНУ базу, зупинену на міграції перед цією (як <see cref="D256ExpressionDownTests"/>).
/// <para>
/// Мутації: прибрати <c>DropCheckConstraint</c> з Up — запис «автор = публікатор» падає 547 (червоний перший тест);
/// прибрати <c>migrationBuilder.Sql(DownPrecheckSql)</c> з Down — відкат падає 547 без переліку, а не 50440
/// (червоний другий тест).
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class D440DropFourEyesMigrationTests(SqlServerFixture sql)
{
    /// <summary>Остання міграція перед цією.</summary>
    private const string BeforeMigration = "20261023074525_HFIDX01_HeaderFieldDefLookupIndex";

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-440")]
    public async Task Up_знімає_обидва_CHECK_і_база_приймає_самопогодження_Down_повертає_їх()
    {
        var cs = await CreateDatabaseBeforeMigrationAsync("_D440A");

        Assert.Equal(1, await CountConstraintAsync(cs, "CK_MV_FourEyes"));
        Assert.Equal(1, await CountConstraintAsync(cs, "CK_RecalcApproval_FourEyes"));

        // До міграції база сама відмовляє автору-публікатору (547 — порушення CHECK).
        var before = await Record.ExceptionAsync(() => InsertSelfPublishedVersionAsync(cs, "0.9"));
        Assert.True(FindSqlException(before) is { Number: 547 }, $"очікувалось 547, а прийшло: {before}");

        await MigrateAsync(cs, null);

        Assert.Equal(0, await CountConstraintAsync(cs, "CK_MV_FourEyes"));
        Assert.Equal(0, await CountConstraintAsync(cs, "CK_RecalcApproval_FourEyes"));
        Assert.Equal(1, await CountConstraintAsync(cs, "CK_MV_Published"));
        Assert.Equal(1, await CountConstraintAsync(cs, "CK_RecalcApproval_UsedConfirmed"));

        // ⛔ D-440: автор публікує свою версію, ініціатор підтверджує свій перерахунок — база не заважає.
        var versionId = await InsertSelfPublishedVersionAsync(cs, "1.0");
        var approvalId = await InsertSelfConfirmedApprovalAsync(cs);

        // Розведено (рішення адміністратора) — відкат проходить і повертає обидва CHECK.
        await ExecuteAsync(cs, $"DELETE FROM calc.MethodologyVersion WHERE Id = {Num(versionId)};");
        await ExecuteAsync(cs, $"DELETE FROM calc.RecalculationApproval WHERE Id = {Num(approvalId)};");

        await MigrateAsync(cs, BeforeMigration);

        Assert.Equal(1, await CountConstraintAsync(cs, "CK_MV_FourEyes"));
        Assert.Equal(1, await CountConstraintAsync(cs, "CK_RecalcApproval_FourEyes"));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-440")]
    public async Task Down_на_самопогоджених_записах_зупиняється_THROW_50440_з_переліком_і_не_чіпає_схему()
    {
        var cs = await CreateDatabaseBeforeMigrationAsync("_D440B");
        await MigrateAsync(cs, null);

        var versionId = await InsertSelfPublishedVersionAsync(cs, "2.0");
        var approvalId = await InsertSelfConfirmedApprovalAsync(cs);

        var error = await Record.ExceptionAsync(() => MigrateAsync(cs, BeforeMigration));

        var sqlError = FindSqlException(error);
        Assert.True(
            sqlError is { Number: D440DropFourEyes.PrecheckErrorNumber },
            $"очікувалась передперевірка {D440DropFourEyes.PrecheckErrorNumber}, а прийшло: "
            + (sqlError is null ? error?.ToString() ?? "жодної помилки" : $"SQL {sqlError.Number}: {sqlError.Message}"));

        // Перелік називає обидва записи — інакше адміністратор знає, що зламано, і не знає де.
        var message = sqlError!.Message;
        Assert.Contains($"MethodologyVersion {Num(versionId)} (версія 2.0", message, StringComparison.Ordinal);
        Assert.Contains($"RecalculationApproval {Num(approvalId)} (період 202401", message, StringComparison.Ordinal);

        // Зупинка ДО зміни схеми: міграція лишилась записаною, CHECK не повернуто, дані цілі.
        Assert.Equal(1, await ScalarAsync(
            cs, "SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId LIKE N'%[_]D440DropFourEyes'"));
        Assert.Equal(0, await CountConstraintAsync(cs, "CK_MV_FourEyes"));
        Assert.Equal(0, await CountConstraintAsync(cs, "CK_RecalcApproval_FourEyes"));
        Assert.Equal(1, await ScalarAsync(cs, $"SELECT COUNT(*) FROM calc.MethodologyVersion WHERE Id = {Num(versionId)}"));
    }

    private static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Чернетка версії, де «опублікував» сам автор (користувач 7); ключі таблиці вимкнено.</summary>
    private static Task<int> InsertSelfPublishedVersionAsync(string cs, string version)
        => ScalarAsync(
            cs,
            $"""
            SET NOCOUNT ON;
            {DisableForeignKeysSql("calc.MethodologyVersion")}
            INSERT INTO calc.MethodologyVersion
                (MethodologyId, Version, [Level], Status, CreatedAt, CreatedByUserId, PublishedByUserId,
                 CalendarMode, NumericMode, TraceLevel)
            VALUES (1, N'{version}', 0, 0, SYSUTCDATETIME(), 7, 7, 0, 0, 1);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """);

    /// <summary>Погодження перерахунку, підтверджене самим ініціатором (користувач 7); ключі таблиці вимкнено.</summary>
    private static Task<int> InsertSelfConfirmedApprovalAsync(string cs)
        => ScalarAsync(
            cs,
            $"""
            SET NOCOUNT ON;
            {DisableForeignKeysSql("calc.RecalculationApproval")}
            INSERT INTO calc.RecalculationApproval
                (ProjectId, PeriodKey, PeriodState, PeriodStateChangedAt, Reason, RequestedByUserId, RequestedAt,
                 ExpiresAt, ConfirmedByUserId, ConfirmedAt)
            VALUES (1, 202401, 3, SYSUTCDATETIME(), N'D-440', 7, SYSUTCDATETIME(),
                    DATEADD(hour, 24, SYSUTCDATETIME()), 7, SYSUTCDATETIME());
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """);

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
