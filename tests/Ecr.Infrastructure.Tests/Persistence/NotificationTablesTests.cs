// tests/Ecr.Infrastructure.Tests/Persistence/NotificationTablesTests.cs
using Ecr.Domain.Entities.Notifications;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// <c>BE-32</c>: три таблиці сповіщень на справжній базі, розгорнутій міграцією.
/// </summary>
/// <remarks>
/// ⚠ Саме на базі: унікальності й зовнішнього ключа фікстура в пам'яті не знає,
/// а на них стоятиме матриця правил (<c>BE-33</c>).
/// </remarks>
[Collection("SqlServer")]
public sealed class NotificationTablesTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc);

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Канал_правило_і_доставка_зберігаються_й_читаються_а_секрет_лежить_блобом()
    {
        byte[] secret = [1, 2, 3, 250];
        var channelId = await AddChannelAsync($"Ops {_tag}", NotificationChannelKind.TeamsWebhook, secret);

        await using (var db = Context())
        {
            db.NotificationRules.Add(new NotificationRule(
                NotificationEventKind.CollectionFailed, channelId, NotificationSeverity.Warning));
            db.NotificationDeliveries.Add(new NotificationDelivery(
                Now, channelId, NotificationEventKind.CollectionFailed, $"collection:{_tag}",
                NotificationDeliveryStatus.Failed, new string('x', 1000)));
            await db.SaveChangesAsync();
        }

        await using (var db = Context())
        {
            var channel = await db.NotificationChannels.AsNoTracking().SingleAsync(c => c.Id == channelId);
            Assert.Equal(NotificationChannelKind.TeamsWebhook, channel.Kind);
            Assert.True(channel.IsEnabled);
            Assert.True(channel.HasSecret);
            Assert.Equal(secret, channel.SecretProtected);
            Assert.Equal(8, channel.RowVersion.Length);

            var rule = await db.NotificationRules.AsNoTracking().SingleAsync(r => r.ChannelId == channelId);
            Assert.Equal(NotificationSeverity.Warning, rule.MinSeverity);

            // Довга відмова обрізається до ширини стовпця — числом, а не константою.
            var delivery = await db.NotificationDeliveries.AsNoTracking().SingleAsync(d => d.ChannelId == channelId);
            Assert.Equal(NotificationDeliveryStatus.Failed, delivery.Status);
            Assert.Equal(400, delivery.Error!.Length);
        }

        // Числа enum-ів — контракт зі сховищем (`02a-db-schema.md`).
        Assert.Equal(2, await ScalarAsync($"SELECT CAST(Kind AS int) FROM sys_ecr.NotificationChannel WHERE Id = {channelId}"));
        Assert.Equal(4, await ScalarAsync($"SELECT CAST(EventKind AS int) FROM sys_ecr.NotificationRule WHERE ChannelId = {channelId}"));
        Assert.Equal(2, await ScalarAsync($"SELECT CAST(Status AS int) FROM itg.NotificationDelivery WHERE ChannelId = {channelId}"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Дві_назви_каналу_не_збігаються()
    {
        await AddChannelAsync($"Dup {_tag}", NotificationChannelKind.Smtp);

        var error = await Assert.ThrowsAsync<DbUpdateException>(
            () => AddChannelAsync($"Dup {_tag}", NotificationChannelKind.TeamsWebhook));

        Assert.Contains("UQ_NotificationChannel_Name", error.InnerException!.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Пара_подія_канал_у_правилах_одна()
    {
        var channelId = await AddChannelAsync($"Rule {_tag}", NotificationChannelKind.Smtp);

        await using var db = Context();
        db.NotificationRules.Add(new NotificationRule(NotificationEventKind.JobFailed, channelId, NotificationSeverity.Error));

        // Інша подія того самого каналу — окрема клітинка матриці.
        db.NotificationRules.Add(new NotificationRule(NotificationEventKind.ExportFailed, channelId, NotificationSeverity.Error));
        await db.SaveChangesAsync();

        db.NotificationRules.Add(new NotificationRule(NotificationEventKind.JobFailed, channelId, NotificationSeverity.Info));
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        Assert.Contains("UQ_NotificationRule_EventChannel", error.InnerException!.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Правило_не_посилається_на_неіснуючий_канал_і_тримає_свій_канал_від_видалення()
    {
        await using (var db = Context())
        {
            db.NotificationRules.Add(new NotificationRule(
                NotificationEventKind.JobFailed, channelId: int.MaxValue, NotificationSeverity.Error));
            var orphan = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Contains("FK_NotificationRule_Channel", orphan.InnerException!.Message, StringComparison.Ordinal);
        }

        var channelId = await AddChannelAsync($"Keep {_tag}", NotificationChannelKind.Smtp);
        await using (var db = Context())
        {
            db.NotificationRules.Add(new NotificationRule(
                NotificationEventKind.JobFailed, channelId, NotificationSeverity.Error));
            await db.SaveChangesAsync();
        }

        var held = await Assert.ThrowsAsync<SqlException>(
            () => ExecuteAsync($"DELETE FROM sys_ecr.NotificationChannel WHERE Id = {channelId}"));
        Assert.Contains("FK_NotificationRule_Channel", held.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Журнал_доставок_переживає_видалення_каналу()
    {
        var channelId = await AddChannelAsync($"Gone {_tag}", NotificationChannelKind.Smtp);
        await using (var db = Context())
        {
            db.NotificationDeliveries.Add(new NotificationDelivery(
                Now, channelId, NotificationEventKind.JobFailed, $"job:{_tag}", NotificationDeliveryStatus.Sent));
            await db.SaveChangesAsync();
        }

        await ExecuteAsync($"DELETE FROM sys_ecr.NotificationChannel WHERE Id = {channelId}");

        Assert.Equal(1, await ScalarAsync($"SELECT COUNT(*) FROM itg.NotificationDelivery WHERE ChannelId = {channelId}"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task База_відхиляє_невідомий_транспорт_і_невідомий_статус()
    {
        var kind = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync($"""
            INSERT sys_ecr.NotificationChannel (Kind, Name, SettingsJson, ModifiedAt)
            VALUES (9, N'Bad {_tag}', N'{"{}"}', SYSUTCDATETIME());
            """));
        Assert.Contains("CK_NotificationChannel_Kind", kind.Message, StringComparison.Ordinal);

        var status = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync($"""
            INSERT itg.NotificationDelivery (At, ChannelId, EventKind, EventKey, Status)
            VALUES (SYSUTCDATETIME(), 1, 1, N'bad:{_tag}', 0);
            """));
        Assert.Contains("CK_NotificationDelivery_Status", status.Message, StringComparison.Ordinal);
    }

    private async Task<int> AddChannelAsync(string name, NotificationChannelKind kind, byte[]? secret = null)
    {
        await using var db = Context();
        var channel = new NotificationChannel(kind, name, """{"recipients":[]}""", Now, byUserId: null);
        channel.ReplaceSecret(secret, Now, byUserId: null);
        db.NotificationChannels.Add(channel);
        await db.SaveChangesAsync();
        return channel.Id;
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private async Task<int> ScalarAsync(string query)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(query, connection);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private async Task ExecuteAsync(string query)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(query, connection);
        await command.ExecuteNonQueryAsync();
    }
}
