// tests/Ecr.Infrastructure.Tests/Persistence/TvpGrantsSurviveRecreateTests.cs
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// L10-07: <c>15-cell-tvp.sql</c> перестворює табличні типи, застарілі за формою
/// (<c>DROP TYPE</c> + <c>CREATE TYPE</c>), і не губить виданих на них прав.
/// </summary>
/// <remarks>
/// ⛔ Предмет. <c>DROP TYPE</c> знімає всі об'єктні права на тип, а runbook просить
/// DBA видати службі <c>GRANT EXECUTE ON TYPE::…</c>. Без відновлення прав перше
/// оновлення бази зі старою формою типу лишало службу без <c>EXECUTE</c>, і кожен
/// запис комірок та аудиту падав «EXECUTE permission was denied». Решта тестів
/// ганяє скрипти під <c>sa</c>/власником, тож цього не бачила жодна.
///
/// ⚠ Власна мінімальна база (схеми <c>doc</c>/<c>aud</c> і старі типи), а не
/// фікстура колекції: тест перестворює типи, і сусіди по спільній базі їх би
/// побачили напівзмінленими. Скрипт іде як <c>sqlcmd</c>: один сеанс, пакети по
/// <c>GO</c> (<see cref="SqlBatches"/>) — тимчасова таблиця прав живе між пакетами.
///
/// Мутаційний доказ (прогнано): повернути <c>15-cell-tvp.sql</c> до версії без
/// блоків «L10-07» → <c>HAS_PERMS_BY_NAME</c> = 0 для обох типів, тест червоний.
/// </remarks>
[Collection("SqlServer")]
public sealed class TvpGrantsSurviveRecreateTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Права_EXECUTE_на_табличні_типи_переживають_перестворення_типів()
    {
        var master = new SqlConnectionStringBuilder(sql.ConnectionString) { InitialCatalog = "master", Pooling = false };
        var database = "EcrTest_tvpgrant_" + Guid.NewGuid().ToString("N")[..8];
        var scriptPath = Path.Combine(RepoRoot(), "src", "Ecr.Infrastructure", "Persistence", "Sql", "15-cell-tvp.sql");
        var script = await File.ReadAllTextAsync(scriptPath);

        await using (var admin = new SqlConnection(master.ConnectionString))
        {
            await admin.OpenAsync();
            await ExecuteAsync(admin, $"CREATE DATABASE [{database}];");
        }

        try
        {
            var db = new SqlConnectionStringBuilder(master.ConnectionString) { InitialCatalog = database };
            await using var connection = new SqlConnection(db.ConnectionString);
            await connection.OpenAsync();

            // Стара форма обох типів: scale 10 замість (34,16) і без IsOutOfWindow, тобто
            // скрипт МУСИТЬ їх перестворити. Служба — користувач без входу з EXECUTE на типи.
            await ExecuteAsync(connection, """
                EXEC(N'CREATE SCHEMA doc'); EXEC(N'CREATE SCHEMA aud');
                CREATE TYPE doc.CellValueTvp AS TABLE (PeriodKey int NOT NULL, ValueNumeric decimal(28,10) NULL);
                CREATE TYPE aud.CellChangeTvp AS TABLE (ChangedAt datetime2(3) NOT NULL);
                CREATE USER tvp_service WITHOUT LOGIN;
                GRANT EXECUTE ON TYPE::doc.CellValueTvp TO tvp_service;
                GRANT EXECUTE ON TYPE::aud.CellChangeTvp TO tvp_service;
                """);

            Assert.Equal(1, await ScalarAsync(connection, PermissionProbe("doc.CellValueTvp")));
            Assert.Equal(1, await ScalarAsync(connection, PermissionProbe("aud.CellChangeTvp")));

            foreach (var batch in SqlBatches.Split(script))
            {
                await ExecuteAsync(connection, batch);
            }

            // Контроль засновку: типи справді перестворено (інакше тест нічого не довів би).
            Assert.Equal(1, await ScalarAsync(connection,
                "SELECT COUNT(*) FROM sys.columns WHERE object_id = (SELECT type_table_object_id FROM sys.table_types WHERE user_type_id = TYPE_ID(N'aud.CellChangeTvp')) AND name = N'IsOutOfWindow'"));
            Assert.Equal(1, await ScalarAsync(connection,
                "SELECT COUNT(*) FROM sys.columns WHERE object_id = (SELECT type_table_object_id FROM sys.table_types WHERE user_type_id = TYPE_ID(N'doc.CellValueTvp')) AND name = N'ValueNumeric' AND scale = 16"));

            Assert.Equal(1, await ScalarAsync(connection, PermissionProbe("doc.CellValueTvp")));
            Assert.Equal(1, await ScalarAsync(connection, PermissionProbe("aud.CellChangeTvp")));

            // Повторне виконання (другий-третій запуск deploy) лишає права на місці.
            foreach (var batch in SqlBatches.Split(script))
            {
                await ExecuteAsync(connection, batch);
            }

            Assert.Equal(1, await ScalarAsync(connection, PermissionProbe("doc.CellValueTvp")));
            Assert.Equal(1, await ScalarAsync(connection, PermissionProbe("aud.CellChangeTvp")));
        }
        finally
        {
            await using var admin = new SqlConnection(master.ConnectionString);
            await admin.OpenAsync();
            await ExecuteAsync(admin, $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];");
        }
    }

    /// <summary>EXECUTE службового користувача на тип — 1/0, з його імені, не з прав власника.</summary>
    private static string PermissionProbe(string type)
        => $"EXECUTE AS USER = 'tvp_service'; SELECT CAST(HAS_PERMS_BY_NAME(N'{type}', N'TYPE', N'EXECUTE') AS int); REVERT;";

    private static async Task ExecuteAsync(SqlConnection connection, string text)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        command.CommandTimeout = 120;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> ScalarAsync(SqlConnection connection, string text)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
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
