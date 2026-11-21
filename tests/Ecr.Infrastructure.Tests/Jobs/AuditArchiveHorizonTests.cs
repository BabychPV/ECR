using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// PSS-27 (аудит 20.11; рішення людини 20.11 №7): публічна <c>arc.usp_ArchiveAudit</c> не переносить з <c>aud.*</c>
/// аудит, молодший за 120 місяців, хоч би що передав викликач, і не приймає «сьогодні» з майбутнього; нічна задача бере
/// глибину з налаштування <c>Retention.AuditMonths</c> (<c>sys_ecr.SystemSetting</c>) з тією ж підлогою.
/// </summary>
/// <remarks>
/// ⚠ Власна база: архівація зачіпає ВСІ старі партиції, а спільна БД колекції містить чужі рядки аудиту.
/// Механіку SWITCH на коротких порогах доводить <see cref="AuditArchiveSwitchTests"/> (внутрішня процедура з явною межею).
/// Мутації: (1) прибрати підлогу 120 у процедурі — перший тест червоний (лютий…вересень 2026 переїздять в архів);
/// (2) прибрати перевірку @Today — другий червоний (50018 не кидається, усе переїздить); (3) задача передає
/// <c>RetentionSettings.Resolve</c> без підлоги — третій червоний (<c>auditArchiveMonths</c> = 6).
/// </remarks>
[Collection("SqlServer")]
[Trait("Audit", "PSS-27")]
public sealed class AuditArchiveHorizonTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Поріг_менший_за_120_місяців_піднімається_до_120_і_нічого_не_переносить()
    {
        var database = SqlServerFixture.WithOwnDatabase("_audhz1");
        await database.InitializeAsync();
        try
        {
            var cs = database.ConnectionString;
            await ExecAsync(cs, CellChangeInsert("2026-02-10", "old-feb"));
            await ExecAsync(cs, CellChangeInsert("2026-05-20", "old-may"));

            foreach (int? months in new int?[] { 1, 0, -5, null, 119 })
            {
                var (partitions, rows) = await ArchiveAsync(cs, months, DateTime.UtcNow.Date);
                Assert.Equal((0, 0L), (partitions, rows));
            }

            Assert.Equal(2L, await ScalarAsync(cs, "SELECT COUNT(*) FROM aud.CellChange WHERE NewValue IN (N'old-feb', N'old-may')"));
            Assert.Equal(0L, await ScalarAsync(cs, "SELECT COUNT(*) FROM arc.AuditCellChange"));
            Assert.Equal(0L, await ScalarAsync(cs, "SELECT COUNT(*) FROM itg.MaintenanceRun WHERE JobCode = N'audit-archive'"));
        }
        finally
        {
            await database.DisposeAsync();
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Сьогодні_з_майбутнього_відхиляється_і_нічого_не_переносить()
    {
        var database = SqlServerFixture.WithOwnDatabase("_audhz2");
        await database.InitializeAsync();
        try
        {
            var cs = database.ConnectionString;
            await ExecAsync(cs, CellChangeInsert("2026-02-10", "old-feb"));

            // Пробний набір з аудиту (1, '2099-01-01') — раніше переносив увесь аудит.
            var error = await Assert.ThrowsAsync<SqlException>(
                () => ArchiveAsync(cs, 1, new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
            Assert.Equal(50018, error.Number);

            // Завтра — ще в межах розбіжності годинників (день запасу): дозволено, і з підлогою 120 нічого не переносить.
            Assert.Equal((0, 0L), await ArchiveAsync(cs, 1, DateTime.UtcNow.Date.AddDays(1)));

            Assert.Equal(1L, await ScalarAsync(cs, "SELECT COUNT(*) FROM aud.CellChange WHERE NewValue = N'old-feb'"));
            Assert.Equal(0L, await ScalarAsync(cs, "SELECT COUNT(*) FROM arc.AuditCellChange"));
        }
        finally
        {
            await database.DisposeAsync();
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Нічна_задача_бере_глибину_аудиту_з_налаштування_з_підлогою_120()
    {
        var database = SqlServerFixture.WithOwnDatabase("_audhz3");
        await database.InitializeAsync();
        try
        {
            var cs = database.ConnectionString;

            // Рядка немає — типове значення (120).
            Assert.Equal("120", await RunJobAsync(cs));

            // Менше за підлогу — 120 (опечатка не переносить свіжий аудит).
            await SetAsync(cs, RetentionSettings.AuditMonthsKey, "6");
            Assert.Equal("120", await RunJobAsync(cs));

            // Більше — як задано; змінюється без перезапуску (наступний прохід читає знову).
            await SetAsync(cs, RetentionSettings.AuditMonthsKey, "150");
            Assert.Equal("150", await RunJobAsync(cs));

            // Не число — типове.
            await SetAsync(cs, RetentionSettings.AuditMonthsKey, "abc");
            Assert.Equal("120", await RunJobAsync(cs));
        }
        finally
        {
            await database.DisposeAsync();
        }
    }

    private static async Task<string> RunJobAsync(string cs)
    {
        await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(cs).Options);
        await new PartitionCheckJob(db, Substitute.For<ISqlCapabilities>(), new TestClock(DateTime.UtcNow))
            .ExecuteAsync(null, Substitute.For<IJobProgress>(), CancellationToken.None);

        await using var c = new SqlConnection(cs);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT TOP (1) JSON_VALUE(DetailsJson, '$.auditArchiveMonths') FROM itg.MaintenanceRun "
                          + $"WHERE JobCode = N'{PartitionCheckJob.Code}' ORDER BY Id DESC";
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    private static Task SetAsync(string cs, string key, string value) => ExecAsync(cs, string.Create(
        CultureInfo.InvariantCulture,
        $"""
         DELETE FROM sys_ecr.SystemSetting WHERE [Key] = N'{key}';
         INSERT INTO sys_ecr.SystemSetting ([Key], Value, ModifiedAt) VALUES (N'{key}', N'{value}', SYSUTCDATETIME());
         """));

    private static string CellChangeInsert(string at, string newValue) => string.Create(
        CultureInfo.InvariantCulture,
        $"""
         INSERT INTO aud.CellChange (ChangedAt, PeriodKey, DocumentId, TableRowId, RowKey, ColumnDefId, OldValue, NewValue, ChangedByUserId, Origin)
         VALUES ('{at}', 202601, 1, 1, N'k', 1, N'o', N'{newValue}', 1, N'UserEdit')
         """);

    private static async Task<(int Partitions, long Rows)> ArchiveAsync(string cs, int? olderThanMonths, DateTime today)
    {
        await using var c = new SqlConnection(cs);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "EXEC arc.usp_ArchiveAudit @OlderThanMonths = @m, @Today = @t, @PartitionsSwitched = @p OUTPUT, @RowsSwitched = @r OUTPUT;";
        cmd.Parameters.Add("@m", System.Data.SqlDbType.Int).Value = (object?)olderThanMonths ?? DBNull.Value;
        cmd.Parameters.Add("@t", System.Data.SqlDbType.Date).Value = today;
        var p = cmd.Parameters.Add("@p", System.Data.SqlDbType.Int);
        p.Direction = System.Data.ParameterDirection.Output;
        var r = cmd.Parameters.Add("@r", System.Data.SqlDbType.BigInt);
        r.Direction = System.Data.ParameterDirection.Output;
        await cmd.ExecuteNonQueryAsync();
        return ((int)p.Value, (long)r.Value);
    }

    private static async Task ExecAsync(string cs, string sql)
    {
        await using var c = new SqlConnection(cs);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
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
}

/// <summary>
/// Розбір строків зберігання (без бази). ✎ рев'ю M2: нижче мінімуму чи не число — ТИПОВЕ значення з причиною (а не
/// мінімум: «0», «-1», «40» замість «400» інакше стерли б журнали за 30 днів); вище максимуму — максимум з причиною.
/// Мутація: повернути притискання до мінімуму — рядки «1», «0», «-10», «40» червоні.
/// </summary>
public sealed class RetentionSettingsClampTests
{
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [InlineData(null, 400, false)]
    [InlineData("", 400, false)]
    [InlineData("abc", 400, true)]
    [InlineData("365", 365, false)]
    [InlineData(" 365 ", 365, false)]
    [InlineData("30", 30, false)]
    [InlineData("1", 400, true)]
    [InlineData("0", 400, true)]
    [InlineData("-10", 400, true)]
    [InlineData("40", 40, false)]
    [InlineData("29", 400, true)]
    [InlineData("99999999", 36_500, true)]
    public void Технічні_журнали(string? raw, int expected, bool rejected)
    {
        var value = RetentionSettings.Resolve(
            raw, RetentionSettings.DefaultTechnicalLogDays, RetentionSettings.MinTechnicalLogDays,
            RetentionSettings.MaxTechnicalLogDays, out var problem);
        Assert.Equal(expected, value);
        Assert.Equal(rejected, problem is not null);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [InlineData(null, 120, false)]
    [InlineData("24", 120, true)]
    [InlineData("119", 120, true)]
    [InlineData("0", 120, true)]
    [InlineData("121", 121, false)]
    [InlineData("5000", 1_200, true)]
    public void Глибина_аудиту_не_менша_за_120(string? raw, int expected, bool rejected)
    {
        var value = RetentionSettings.Resolve(
            raw, RetentionSettings.DefaultAuditMonths, RetentionSettings.MinAuditMonths, RetentionSettings.MaxAuditMonths,
            out var problem);
        Assert.Equal(expected, value);
        Assert.Equal(rejected, problem is not null);
    }
}
