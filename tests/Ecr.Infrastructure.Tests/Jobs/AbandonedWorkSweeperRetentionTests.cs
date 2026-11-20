using System.Globalization;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// PSS-13 (аудит 20.11; рішення людини 20.11 №7): періодичний прохід прибирання видаляє технічні журнали, старші за
/// строк <c>Retention.TechnicalLogDays</c> (<c>sys_ecr.SystemSetting</c>, типово 400 днів, змінюється без перевидання),
/// і не чіпає того, що ще живе: недоставлені сповіщення, останній прогін кожної задачі, слід архівації аудиту молодший
/// за глибину аудиту (не менше 120 міс.), відкриті прогони.
/// </summary>
/// <remarks>
/// ⚠ Власна база: прохід чистить таблиці цілком, а точні лічильники в спільній базі колекції залежали б від сусідів.
/// Мутації: (1) прибрати фільтр <c>State IN ('Sent','Failed')</c> — червоне (Pending зник); (2) прибрати
/// <c>EXISTS … n.Id &gt; r.Id</c> — червоне (єдиний Failed прогін y-job зник); (3) рахувати <c>audit-archive</c> за
/// звичайним строком — червоне (слід переносу аудиту 500-денної давності зник); (4) читати строк константою — червоне
/// (після налаштування 100 днів 200-денні рядки лишаються).
/// </remarks>
[Collection("SqlServer")]
[Trait("Audit", "PSS-13")]
public sealed class AbandonedWorkSweeperRetentionTests
{
    private static readonly DateTime Now = new(2031, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Ретенція_технічних_журналів_за_налаштуванням()
    {
        var database = SqlServerFixture.WithOwnDatabase("_logret");
        await database.InitializeAsync();
        try
        {
            var cs = database.ConnectionString;

            await OutboxAsync(cs, "sent-old", "Sent", Now.AddDays(-500));
            await OutboxAsync(cs, "failed-old", "Failed", Now.AddDays(-500));
            await OutboxAsync(cs, "pending-old", "Pending", Now.AddDays(-500));
            await OutboxAsync(cs, "sent-200", "Sent", Now.AddDays(-200));
            await OutboxAsync(cs, "sent-new", "Sent", Now.AddDays(-10));

            await DeliveryAsync(cs, "d-old", Now.AddDays(-500));
            await DeliveryAsync(cs, "d-200", Now.AddDays(-200));
            await DeliveryAsync(cs, "d-new", Now.AddDays(-10));

            await RunAsync(cs, "x-job", "Succeeded", Now.AddDays(-500), "x-old-1");
            await RunAsync(cs, "x-job", "Succeeded", Now.AddDays(-450), "x-old-2");
            await RunAsync(cs, "x-job", "Succeeded", Now.AddDays(-200), "x-200");
            await RunAsync(cs, "x-job", "Succeeded", Now.AddDays(-1), "x-new");
            await RunAsync(cs, "y-job", "Failed", Now.AddDays(-500), "y-only");
            await RunAsync(cs, AbandonedWorkSweeper.AuditArchiveJobCode, "Succeeded", Now.AddMonths(-130), "arc-ancient");
            await RunAsync(cs, AbandonedWorkSweeper.AuditArchiveJobCode, "Succeeded", Now.AddDays(-500), "arc-old");
            await RunAsync(cs, AbandonedWorkSweeper.AuditArchiveJobCode, "Succeeded", Now.AddDays(-1), "arc-new");
            await RunAsync(cs, "z-job", "Running", Now.AddDays(-500), "z-open", finished: false);

            var outcome = await SweepAsync(database);

            Assert.Equal(["pending-old", "sent-200", "sent-new"], await ListAsync(cs, "SELECT Subject FROM itg.NotificationOutbox ORDER BY Subject"));
            Assert.Equal(["d-200", "d-new"], await ListAsync(cs, "SELECT EventKey FROM itg.NotificationDelivery ORDER BY EventKey"));
            Assert.Equal(
                ["arc-new", "arc-old", "x-200", "x-new", "y-only"],
                await ListAsync(cs, "SELECT DetailsJson FROM itg.MaintenanceRun WHERE JobCode <> N'z-job' ORDER BY DetailsJson"));

            // Покинутий прогін закрито цим самим проходом (FinishedAt = зараз) — і він не зникає.
            Assert.Equal(1L, await ScalarAsync(cs, "SELECT COUNT(*) FROM itg.MaintenanceRun WHERE JobCode = N'z-job' AND FinishedAt IS NOT NULL"));
            Assert.Equal(2 + 1 + 3, outcome.LogsPurged);
            Assert.True(outcome.Any);

            // Налаштування змінюється «на льоту»: 100 днів — 200-денні рядки зникають наступним проходом.
            await ExecAsync(cs, $"""
                INSERT INTO sys_ecr.SystemSetting ([Key], Value, ModifiedAt)
                VALUES (N'{RetentionSettings.TechnicalLogDaysKey}', N'100', SYSUTCDATETIME());
                """);
            outcome = await SweepAsync(database);

            Assert.Equal(["pending-old", "sent-new"], await ListAsync(cs, "SELECT Subject FROM itg.NotificationOutbox ORDER BY Subject"));
            Assert.Equal(["d-new"], await ListAsync(cs, "SELECT EventKey FROM itg.NotificationDelivery ORDER BY EventKey"));
            Assert.Equal(
                ["arc-new", "arc-old", "x-new", "y-only"],
                await ListAsync(cs, "SELECT DetailsJson FROM itg.MaintenanceRun WHERE JobCode <> N'z-job' ORDER BY DetailsJson"));
            Assert.Equal(1L, await ScalarAsync(cs, "SELECT COUNT(*) FROM itg.MaintenanceRun WHERE JobCode = N'z-job'"));
            Assert.Equal(3, outcome.LogsPurged);

            // Без ретенції (старт застосунку) журнали не чіпаються.
            await ExecAsync(cs, $"UPDATE sys_ecr.SystemSetting SET Value = N'30' WHERE [Key] = N'{RetentionSettings.TechnicalLogDaysKey}';");
            await using var db = database.CreateContext();
            var startup = await new AbandonedWorkSweeper(db, new JobProgressStore(db))
                .SweepAsync(AbandonedWorkSweeper.AbandonedJobReason, Now, purge: false, CancellationToken.None);
            Assert.Equal(0, startup.LogsPurged);
            Assert.Equal(2L, await ScalarAsync(cs, "SELECT COUNT(*) FROM itg.NotificationOutbox"));
        }
        finally
        {
            await database.DisposeAsync();
        }
    }

    private static async Task<SweepOutcome> SweepAsync(SqlServerFixture database)
    {
        await using var db = database.CreateContext();
        return await new AbandonedWorkSweeper(db, new JobProgressStore(db))
            .SweepAsync(AbandonedWorkSweeper.AbandonedJobReason, Now, purge: true, CancellationToken.None);
    }

    private static Task OutboxAsync(string cs, string subject, string state, DateTime createdAt) => ExecAsync(cs, string.Create(
        CultureInfo.InvariantCulture,
        $"""
         INSERT INTO itg.NotificationOutbox (EventCode, Subject, Body, CreatedAt, State, Attempts)
         VALUES (N'test', N'{subject}', N'b', '{createdAt:yyyy-MM-ddTHH:mm:ss}', N'{state}', 0);
         """));

    private static Task DeliveryAsync(string cs, string key, DateTime at) => ExecAsync(cs, string.Create(
        CultureInfo.InvariantCulture,
        $"""
         INSERT INTO itg.NotificationDelivery (At, ChannelId, EventKind, EventKey, Status)
         VALUES ('{at:yyyy-MM-ddTHH:mm:ss}', 1, 1, N'{key}', 1);
         """));

    private static Task RunAsync(string cs, string code, string status, DateTime at, string tag, bool finished = true)
        => ExecAsync(cs, string.Create(
            CultureInfo.InvariantCulture,
            $"""
             INSERT INTO itg.MaintenanceRun (JobCode, StartedAt, FinishedAt, Status, DetailsJson)
             VALUES (N'{code}', '{at:yyyy-MM-ddTHH:mm:ss}', {(finished ? $"'{at.AddMinutes(1):yyyy-MM-ddTHH:mm:ss}'" : "NULL")}, N'{status}', N'{tag}');
             """));

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

    private static async Task<List<string>> ListAsync(string cs, string sql)
    {
        await using var c = new SqlConnection(cs);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        var result = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }
}
