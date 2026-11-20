using System.Globalization;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// PSS-09 (аудит 20.11; рішення людини 20.11 №6 — межі для поточного року): <c>arc.usp_EnsurePartitions</c> тримає межі
/// всього поточного року, наступного і січня Y+2, а межу, SPLIT якої переніс би понад <c>@MaxRowsToMove</c> рядків
/// (лише рядки >= межі — рев'ю H1), пропускає: прогін — <c>Degraded</c> зі списком <c>refused</c>.
/// </summary>
/// <remarks>
/// ⚠ Власні бази: у спільній базі колекції крайня права партиція містить чужі рядки тестів (PeriodKey 2099xx).
/// Мутації: (1) повернути початок діапазону на «наступний місяць» — перший тест червоний (немає 203501…203505);
/// (2) прибрати перевірку рядків, що переїдуть, — другий тест червоний (межі 203001…203003 додано, Succeeded);
/// (3) рахувати всі рядки партиції, а не >= межі (як до рев'ю H1) — другий і третій червоні.
/// </remarks>
[Collection("SqlServer")]
[Trait("Audit", "PSS-09")]
public sealed class PartitionEnsureYearTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Межі_поточного_року_наступного_і_січня_Y2_додаються_повтор_нічого_не_змінює()
    {
        var database = SqlServerFixture.WithOwnDatabase("_pey1");
        await database.InitializeAsync();
        try
        {
            var cs = database.ConnectionString;

            // Свіжа база: від 202601 до січня Y+2 (Y — рік розгортання), без «історичних» років.
            Assert.Equal(202601L, await ScalarAsync(cs, BoundaryAggregate("MIN")));
            Assert.Equal((DateTime.UtcNow.Year + 2) * 100L + 1, await ScalarAsync(cs, BoundaryAggregate("MAX")));
            Assert.Equal(12L, await ScalarAsync(cs, BoundaryCount((DateTime.UtcNow.Year * 100) + 1, (DateTime.UtcNow.Year * 100) + 12)));

            await ExecAsync(cs, "EXEC arc.usp_EnsurePartitions @MonthsAhead = 6, @Today = '2035-05-15', @RetryDelaySeconds = 0;");

            // Січень…травень 2035 (минулі місяці поточного року) теж є, а не лише «наперед».
            Assert.Equal(25L, await ScalarAsync(cs, BoundaryCount(203501, 203701)));
            Assert.Equal(0L, await ScalarAsync(cs, BoundaryCount(203702, 209912)));
            Assert.Equal("Succeeded|25", await LastRunAsync(cs));

            await ExecAsync(cs, "EXEC arc.usp_EnsurePartitions @MonthsAhead = 6, @Today = '2035-05-15', @RetryDelaySeconds = 0;");
            Assert.Equal("Succeeded|0", await LastRunAsync(cs));

            // Запас понад січень Y+2 — як і раніше, @MonthsAhead від поточного місяця.
            await ExecAsync(cs, "EXEC arc.usp_EnsurePartitions @MonthsAhead = 30, @Today = '2035-05-15', @RetryDelaySeconds = 0;");
            Assert.Equal(1L, await ScalarAsync(cs, BoundaryCount(203711, 203711)));
            Assert.Equal(0L, await ScalarAsync(cs, BoundaryCount(203712, 209912)));
        }
        finally
        {
            await database.DisposeAsync();
        }
    }

    /// <summary>
    /// ✎ рев'ю H1: SPLIT переносить лише рядки >= нової межі. Рядки 203003 у крайній правій партиції НЕ блокують межі
    /// 203004…203201 (нижче за них — нічого не переїжджає); з @MaxRowsToMove = 0 пропускаються лише 203001…203003, бо
    /// для них рядки 203003 переїхали б. Межі йдуть від найвищої.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Рядки_нижче_межі_не_блокують_SPLIT_пропускаються_лише_межі_що_переносили_б_рядки()
    {
        var database = SqlServerFixture.WithOwnDatabase("_pey2");
        await database.InitializeAsync();
        try
        {
            var cs = database.ConnectionString;

            // Рядки за останньою межею (крайня права партиція).
            var doc = await new TestDocumentBuilder(cs).BuildAsync(periodKey: 203003, rowCount: 2);
            var rowsBefore = await ScalarAsync(cs, $"SELECT COUNT(*) FROM doc.TableRow WHERE PeriodKey = {doc.PeriodKey.Value}");

            await ExecAsync(cs, "EXEC arc.usp_EnsurePartitions @MonthsAhead = 6, @Today = '2030-02-10', @RetryDelaySeconds = 0, @MaxRowsToMove = 0;");

            Assert.Equal(22L, await ScalarAsync(cs, BoundaryCount(203004, 203201)));
            Assert.Equal(0L, await ScalarAsync(cs, BoundaryCount(203001, 203003)));
            Assert.Equal(rowsBefore, await ScalarAsync(cs, $"SELECT COUNT(*) FROM doc.TableRow WHERE PeriodKey = {doc.PeriodKey.Value}"));

            var details = await LastDetailsAsync(cs);
            Assert.StartsWith("Degraded|", details, StringComparison.Ordinal);
            Assert.Contains("\"added\":22,", details, StringComparison.Ordinal);
            Assert.Contains("\"movedRows\":0,", details, StringComparison.Ordinal);
            Assert.Contains("\"refused\":[203001,203002,203003]", details, StringComparison.Ordinal);

            // Типова стеля (5000 рядків): решта меж додається з переносом цих кількох рядків; дані на місці.
            await ExecAsync(cs, "EXEC arc.usp_EnsurePartitions @MonthsAhead = 6, @Today = '2030-02-10', @RetryDelaySeconds = 0;");
            Assert.Equal(3L, await ScalarAsync(cs, BoundaryCount(203001, 203003)));
            Assert.Equal(rowsBefore, await ScalarAsync(cs, $"SELECT COUNT(*) FROM doc.TableRow WHERE PeriodKey = {doc.PeriodKey.Value}"));
            Assert.Equal(1L, await ScalarAsync(cs, $"""
                SELECT COUNT(DISTINCT $PARTITION.pf_ByPeriodKey(PeriodKey)) FROM doc.TableRow WHERE PeriodKey = {doc.PeriodKey.Value}
                """));
            details = await LastDetailsAsync(cs);
            Assert.StartsWith("Succeeded|", details, StringComparison.Ordinal);
            Assert.DoesNotContain("\"movedRows\":0,", details, StringComparison.Ordinal);
        }
        finally
        {
            await database.DisposeAsync();
        }
    }

    /// <summary>
    /// ✎ рев'ю H1: дані НИЖЧЕ всіх нових меж (документ 203003, «сьогодні» 2031) — SPLIT дозволено навіть із
    /// @MaxRowsToMove = 0: нічого не переїжджає, прогін Succeeded. Мутація: рахувати всі рядки партиції (як було) —
    /// червоне (усі межі refused).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Дані_нижче_межі_SPLIT_дозволено_Succeeded()
    {
        var database = SqlServerFixture.WithOwnDatabase("_pey3");
        await database.InitializeAsync();
        try
        {
            var cs = database.ConnectionString;
            await new TestDocumentBuilder(cs).BuildAsync(periodKey: 203003, rowCount: 2);

            await ExecAsync(cs, "EXEC arc.usp_EnsurePartitions @MonthsAhead = 6, @Today = '2031-03-10', @RetryDelaySeconds = 0, @MaxRowsToMove = 0;");

            Assert.Equal(25L, await ScalarAsync(cs, BoundaryCount(203101, 203301)));
            var details = await LastDetailsAsync(cs);
            Assert.StartsWith("Succeeded|", details, StringComparison.Ordinal);
            Assert.Contains("\"movedRows\":0", details, StringComparison.Ordinal);
            Assert.DoesNotContain("refused", details, StringComparison.Ordinal);
        }
        finally
        {
            await database.DisposeAsync();
        }
    }

    private static Task<string> LastDetailsAsync(string cs) => ScalarTextAsync(cs, """
        SELECT TOP (1) Status + N'|' + DetailsJson FROM itg.MaintenanceRun
        WHERE JobCode = N'EnsurePartitions' ORDER BY Id DESC
        """);

    private static string BoundaryAggregate(string fn) => $"""
        SELECT {fn}(CAST(rv.value AS int)) FROM sys.partition_range_values rv
        JOIN sys.partition_functions pf ON pf.function_id = rv.function_id WHERE pf.name = N'pf_ByPeriodKey'
        """;

    private static string BoundaryCount(int from, int to) => string.Create(CultureInfo.InvariantCulture, $"""
        SELECT COUNT(*) FROM sys.partition_range_values rv
        JOIN sys.partition_functions pf ON pf.function_id = rv.function_id
        WHERE pf.name = N'pf_ByPeriodKey' AND CAST(rv.value AS int) BETWEEN {from} AND {to}
        """);

    private static async Task<string> LastRunAsync(string cs)
        => await ScalarTextAsync(cs, """
            SELECT TOP (1) Status + N'|' + JSON_VALUE(DetailsJson, '$.added') FROM itg.MaintenanceRun
            WHERE JobCode = N'EnsurePartitions' ORDER BY Id DESC
            """);

    private static async Task ExecAsync(string cs, string sql)
    {
        await using var c = new SqlConnection(cs);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandTimeout = 120;
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(string cs, string sql)
    {
        await using var c = new SqlConnection(cs);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<string> ScalarTextAsync(string cs, string sql)
    {
        await using var c = new SqlConnection(cs);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return (string)(await cmd.ExecuteScalarAsync())!;
    }
}
