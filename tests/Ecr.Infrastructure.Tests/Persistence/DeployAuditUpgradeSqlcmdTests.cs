// tests/Ecr.Infrastructure.Tests/Persistence/DeployAuditUpgradeSqlcmdTests.cs
using System.Diagnostics;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Апгрейд наявної бази ЧЕРЕЗ справжній <c>sqlcmd -b -I -i</c> (як
/// <c>tools/setup-dev-db.ps1</c> і <c>deploy-ecr.ps1</c>): на базі «до ФВ-2.16»
/// (без <c>aud.CellChange.IsOutOfWindow</c>) <c>11-audit-tables.sql</c> додає колонку.
/// </summary>
/// <remarks>
/// ⛔ Предмет: NUL-байт у коментарі перед <c>ALTER TABLE … ADD</c> змушував sqlcmd
/// завершитись з кодом 0, тихо не виконавши ALTER. <see cref="DeployScriptsRerunTests"/>
/// йде через SqlClient і цього не бачить. На CI sqlcmd ставиться в job <c>server</c>
/// (ci.yml, крок «sqlcmd»); відсутній sqlcmd — червоний тест, не пропуск.
/// </remarks>
[Collection("SqlServer")]
public sealed class DeployAuditUpgradeSqlcmdTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Sqlcmd_додає_IsOutOfWindow_на_базі_без_колонки_з_DEFAULT_0()
    {
        var database = SqlServerFixture.WithOwnDatabase("_auditup");
        await database.InitializeAsync();
        try
        {
            var cs = database.ConnectionString;

            // Модель бази «до ФВ-2.16»: ні колонки, ні індексу значка на ній
            // (`IX_CellChange_OutOfWindow` тримає колонку — без DROP INDEX
            // SQL Server колонку не віддасть).
            await ExecAsync(cs, """
                IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CellChange_OutOfWindow'
                           AND object_id = OBJECT_ID(N'aud.CellChange'))
                    DROP INDEX IX_CellChange_OutOfWindow ON aud.CellChange;
                DECLARE @df sysname = (SELECT dc.name FROM sys.default_constraints dc
                    JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id
                    WHERE dc.parent_object_id = OBJECT_ID(N'aud.CellChange') AND c.name = N'IsOutOfWindow');
                IF @df IS NULL THROW 50001, N'DEFAULT для IsOutOfWindow не знайдено у вихідній схемі', 1;
                DECLARE @sql nvarchar(max) = N'ALTER TABLE aud.CellChange DROP CONSTRAINT ' + QUOTENAME(@df)
                    + N'; ALTER TABLE aud.CellChange DROP COLUMN IsOutOfWindow;';
                EXEC sys.sp_executesql @sql;
                """);
            Assert.Null(await ScalarAsync(cs, "SELECT COL_LENGTH(N'aud.CellChange', N'IsOutOfWindow')"));

            var script = Path.Combine(RepoRoot(), "src", "Ecr.Infrastructure", "Persistence", "Sql", "11-audit-tables.sql");
            RunSqlcmd(cs, script);

            Assert.NotNull(await ScalarAsync(cs, "SELECT COL_LENGTH(N'aud.CellChange', N'IsOutOfWindow')"));
            Assert.Equal(
                "((0))",
                (string?)await ScalarAsync(cs, """
                    SELECT dc.definition FROM sys.default_constraints dc
                    JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id
                    WHERE dc.parent_object_id = OBJECT_ID(N'aud.CellChange') AND c.name = N'IsOutOfWindow'
                    """));

            // Оновлена база отримує й індекс значка в зрізі (той самий скрипт).
            Assert.NotNull(await ScalarAsync(cs, """
                SELECT filter_definition FROM sys.indexes
                 WHERE object_id = OBJECT_ID(N'aud.CellChange') AND name = N'IX_CellChange_OutOfWindow'
                """));
        }
        finally
        {
            await database.DisposeAsync();
        }
    }

    private static void RunSqlcmd(string connectionString, string file)
    {
        var b = new SqlConnectionStringBuilder(connectionString);
        var psi = new ProcessStartInfo("sqlcmd")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in new[] { "-S", b.DataSource, "-d", b.InitialCatalog, "-C", "-b", "-I" })
        {
            psi.ArgumentList.Add(a);
        }

        if (b.IntegratedSecurity)
        {
            psi.ArgumentList.Add("-E");
        }
        else
        {
            psi.ArgumentList.Add("-U");
            psi.ArgumentList.Add(b.UserID);
            psi.Environment["SQLCMDPASSWORD"] = b.Password;
        }

        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(file);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("sqlcmd не запустився");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(300_000), "sqlcmd не завершився за 300 с");
        Assert.True(process.ExitCode == 0, $"sqlcmd повернув {process.ExitCode}: {stdout.Result}{stderr.Result}");
    }

    private static async Task ExecAsync(string cs, string sql)
    {
        await using var c = new SqlConnection(cs);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(string cs, string sql)
    {
        await using var c = new SqlConnection(cs);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        var v = await cmd.ExecuteScalarAsync();
        return v is DBNull ? null : v;
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Ecr.sln")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("Ecr.sln не знайдено вгору від каталогу збірки.");
    }
}
