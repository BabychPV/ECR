// tests/Ecr.Infrastructure.Tests/Persistence/UnitOfWorkNotificationRuleDuplicateTests.cs
using Ecr.Application.Errors;
using Ecr.Domain.Entities.Notifications;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// A1-05: програш гонки двох заміщень матриці правил сповіщень (<c>PUT /notifications/rules</c> без <c>If-Match</c>)
/// на <c>UQ_NotificationRule_EventChannel</c> — 409 <c>ECR-CELL-0409</c>, а не сирий <c>DbUpdateException</c> (500).
/// </summary>
/// <remarks>
/// ⛔ Обидва запити читають «клітинки немає» й додають правило пари «подія + канал»; другу відбиває лише індекс.
/// Мутація: прибрати арм <c>UQ_NotificationRule_EventChannel</c> з <c>UnitOfWork.TryMapDuplicateKey</c> — тест
/// червоний (сирий виняток бази).
/// </remarks>
[Collection("SqlServer")]
public sealed class UnitOfWorkNotificationRuleDuplicateTests(SqlServerFixture sql)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "A1-05")]
    public async Task Дубль_пари_подія_канал_дає_409_а_не_сирий_виняток_бази()
    {
        var channelId = await ArrangeChannelAsync();

        await using (var winner = Context())
        {
            winner.NotificationRules.Add(
                new NotificationRule(NotificationEventKind.JobFailed, channelId, NotificationSeverity.Error));
            await new UnitOfWork(winner).SaveChangesAsync(CancellationToken.None);
        }

        await using var loser = Context();
        loser.NotificationRules.Add(
            new NotificationRule(NotificationEventKind.JobFailed, channelId, NotificationSeverity.Warning));

        var thrown = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => new UnitOfWork(loser).SaveChangesAsync(CancellationToken.None));

        Assert.Equal("ECR-CELL-0409", thrown.ErrorCode);
        Assert.Equal("err.ECR-CELL-0409.concurrentChange", thrown.Details!["messageKey"]);
    }

    private async Task<int> ArrangeChannelAsync()
    {
        await using var db = Context();
        var channel = new NotificationChannel(
            NotificationChannelKind.TeamsWebhook, $"A105 {_tag}", "{}", new DateTime(2026, 10, 10, 9, 0, 0, DateTimeKind.Utc), null);
        db.NotificationChannels.Add(channel);
        await db.SaveChangesAsync();
        return channel.Id;
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .Options);
}
