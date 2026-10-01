using System.Globalization;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// <c>arc.usp_EnsureAuditPartitions</c>: межі <c>pf_AuditByMonth</c> продовжуються вперед
/// (раніше закінчувались 2027-06-01 і ніхто їх не довантажував) без переміщення даних і
/// без дублів; тригери незмінності <c>aud.*</c> лишаються чинними.
/// </summary>
/// <remarks>
/// ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати <c>SPLIT RANGE</c> з процедури — тест `Межі_продовжуються…`
/// червоніє. «Сьогодні» передається параметром, тож перевірка не залежить від годинника
/// машини; межі лише додаються (спільна БД колекції від цього не страждає).
/// </remarks>
[Collection("SqlServer")]
public sealed class AuditPartitionExtensionTests(SqlServerFixture sql)
{
    private const int MonthsAhead = 12;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Межі_продовжуються_на_12_місяців_рядки_на_місцях_повтор_ідемпотентний_тригери_діють()
    {
        // «Сьогодні» далеко за зашитими межами (2027-06-01) — гарантує, що є що додавати.
        var today = new DateTime(2029, 3, 15, 0, 0, 0, DateTimeKind.Utc);
        var tag = "ext-" + Guid.NewGuid().ToString("N")[..8];

        // Рядки аудиту в різних місяцях ДО виклику (крайні партиції зашитих меж і середина).
        var ids = new List<long>();
        foreach (var at in new[] { "2026-02-10", "2027-05-20", "2027-06-15" })
        {
            ids.Add(await ScalarAsync(
                $"INSERT INTO aud.SecurityEvent (ChangedAt, EventType, ChangedByUserId) OUTPUT INSERTED.Id VALUES ('{at}', N'{tag}', 42)"));
        }

        var before = await PartitionOfAsync(ids);

        var added = await EnsureAsync(today);
        Assert.True(added >= 0);

        // Межі: усі перші числа місяців від 2029-04-01 до 2030-03-01 існують рівно по одній.
        for (var i = 1; i <= MonthsAhead; i++)
        {
            var d = new DateTime(2029, 3, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(i);
            Assert.Equal(1, await ScalarAsync(string.Create(CultureInfo.InvariantCulture, $"""
                SELECT COUNT(*) FROM sys.partition_range_values rv
                JOIN sys.partition_functions pf ON pf.function_id = rv.function_id
                WHERE pf.name = N'pf_AuditByMonth' AND CAST(rv.value AS datetime2(3)) = '{d:yyyy-MM-dd}'
                """)));
        }

        // Рядки на місцях: ті самі партиції, та сама кількість, жодних втрат.
        Assert.Equal(before, await PartitionOfAsync(ids));
        Assert.Equal(3, await ScalarAsync($"SELECT COUNT(*) FROM aud.SecurityEvent WHERE EventType = N'{tag}'"));

        // Повтор: нічого не додає, кількість меж не змінюється.
        var boundariesBefore = await ScalarAsync(
            "SELECT COUNT(*) FROM sys.partition_range_values rv JOIN sys.partition_functions pf ON pf.function_id = rv.function_id WHERE pf.name = N'pf_AuditByMonth'");
        Assert.Equal(0, await EnsureAsync(today));
        Assert.Equal(boundariesBefore, await ScalarAsync(
            "SELECT COUNT(*) FROM sys.partition_range_values rv JOIN sys.partition_functions pf ON pf.function_id = rv.function_id WHERE pf.name = N'pf_AuditByMonth'"));

        // Тригери незмінності діють після SPLIT.
        var error = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(
            $"UPDATE aud.SecurityEvent SET EventType = N'x' WHERE Id = {ids[0]}"));
        Assert.Equal(50060, error.Number);
        Assert.Equal(9, await ScalarAsync("""
            SELECT COUNT(*) FROM sys.triggers
            WHERE name IN (N'TR_ColumnDef_Immutable', N'TR_RowDef_Immutable', N'TR_FormulaDef_Immutable',
                           N'TR_ConditionalFormatRule_Immutable', N'TR_CellChange_Immutable',
                           N'TR_StructureChange_Immutable', N'TR_SecurityEvent_Immutable',
                           N'TR_PublicationEvent_Immutable', N'TR_SimulationSession_Immutable')
              AND is_disabled = 0
            """));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Нічна_задача_сама_продовжує_межі_аудиту()
    {
        await using var db = new Ecr.Infrastructure.Persistence.EcrDbContext(
            new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<Ecr.Infrastructure.Persistence.EcrDbContext>()
                .UseSqlServer(sql.ConnectionString).Options);
        var job = new Ecr.Infrastructure.Jobs.PartitionCheckJob(
            db, NSubstitute.Substitute.For<Ecr.Application.Ports.ISqlCapabilities>(),
            new TestClock(new DateTime(2031, 1, 15, 0, 0, 0, DateTimeKind.Utc)));

        await job.ExecuteAsync(null, NSubstitute.Substitute.For<Ecr.Application.Ports.IJobProgress>(), CancellationToken.None);

        // Прогін із годинником 2031 - найпізніший за StartedAt: прибрати, інакше сусідні тести,
        // що читають "останній" прогін, побачать його.
        await ExecuteAsync("DELETE FROM itg.MaintenanceRun WHERE JobCode = N'partition-check' AND StartedAt >= '2031-01-01'");

        Assert.Equal(1, await ScalarAsync("""
            SELECT COUNT(*) FROM sys.partition_range_values rv
            JOIN sys.partition_functions pf ON pf.function_id = rv.function_id
            WHERE pf.name = N'pf_AuditByMonth' AND CAST(rv.value AS datetime2(3)) = '2032-01-01'
            """));
    }

    private async Task<int> EnsureAsync(DateTime today)
    {
        await using var conn = new SqlConnection(sql.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "EXEC arc.usp_EnsureAuditPartitions @MonthsAhead = @m, @Today = @t, @Added = @a OUTPUT;";
        cmd.Parameters.AddWithValue("@m", MonthsAhead);
        cmd.Parameters.Add("@t", System.Data.SqlDbType.Date).Value = today;
        var output = cmd.Parameters.Add("@a", System.Data.SqlDbType.Int);
        output.Direction = System.Data.ParameterDirection.Output;
        await cmd.ExecuteNonQueryAsync();
        return (int)output.Value;
    }

    private async Task<string> PartitionOfAsync(IEnumerable<long> ids) =>
        string.Join(",", await Task.WhenAll(ids.Select(async id => $"{id}:" + await ScalarAsync(
            $"SELECT $PARTITION.pf_AuditByMonth(ChangedAt) FROM aud.SecurityEvent WHERE Id = {id}"))));

    private async Task<long> ScalarAsync(string commandText)
    {
        await using var conn = new SqlConnection(sql.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = commandText;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private async Task ExecuteAsync(string commandText)
    {
        await using var conn = new SqlConnection(sql.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = commandText;
        await cmd.ExecuteNonQueryAsync();
    }
}
