// tests/Ecr.Infrastructure.Tests/Integration/OutboxDispatcherRetryBackoffTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Integration;
using Ecr.Infrastructure.Integration;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Integration;

/// <summary>
/// D-9: невдала спроба не має повторюватися кожним наступним «відправити зараз» —
/// п'ять подань аркушів поспіль інакше витрачають усі п'ять спроб за хвилини.
/// </summary>
/// <remarks>
/// ⚠ База спільна на весь <c>[Collection("SqlServer")]</c>: кожен тест працює зі своїм унікальним
/// кодом події (<c>onlyEventCode</c>), щоб чужі <c>Pending</c>-рядки не рахувались у спробах.
/// </remarks>
[Collection("SqlServer")]
public sealed class OutboxDispatcherRetryBackoffTests(SqlServerFixture sql)
{
    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static INotificationSender FailingSender()
    {
        var sender = Substitute.For<INotificationSender>();
        sender.IsConfigured.Returns(true);
        sender
            .SendAsync(
                Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("SMTP недоступний"));
        return sender;
    }

    private async Task<string> SeedAsync(DateTime now)
    {
        var eventCode = $"test.backoff.{Guid.NewGuid():N}";
        await using var setup = CreateContext();
        setup.NotificationOutbox.Add(new NotificationOutboxItem(
            eventCode, "Тема backoff", "Текст листа", "ops@example.local", now));
        await setup.SaveChangesAsync();
        return eventCode;
    }

    private async Task<NotificationOutboxItem> LoadAsync(string eventCode)
    {
        await using var verify = CreateContext();
        return await verify.NotificationOutbox.AsNoTracking().SingleAsync(n => n.EventCode == eventCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-9")]
    public async Task Failed_attempt_is_not_retried_by_next_flush_within_delay()
    {
        var now = new DateTime(2034, 5, 5, 5, 0, 0, DateTimeKind.Utc);
        var clock = new TestClock(now);
        var eventCode = await SeedAsync(now);
        var sender = FailingSender();

        await using (var db1 = CreateContext())
        {
            await new OutboxDispatcher(db1, clock, sender).FlushAsync(eventCode, CancellationToken.None);
        }

        var afterFirst = await LoadAsync(eventCode);
        Assert.Equal(1, afterFirst.Attempts);
        Assert.Equal("Pending", afterFirst.State);
        Assert.Equal(now + OutboxDispatcher.RetryDelay, afterFirst.NextAttemptAt);

        clock.Advance(TimeSpan.FromMinutes(1));
        await using (var db2 = CreateContext())
        {
            await new OutboxDispatcher(db2, clock, sender).FlushAsync(eventCode, CancellationToken.None);
        }

        var afterSecond = await LoadAsync(eventCode);
        Assert.Equal(1, afterSecond.Attempts);
        await sender.Received(1).SendAsync(
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-9")]
    public async Task Failed_attempt_is_retried_after_delay_has_passed()
    {
        var now = new DateTime(2034, 5, 6, 5, 0, 0, DateTimeKind.Utc);
        var clock = new TestClock(now);
        var eventCode = await SeedAsync(now);
        var sender = FailingSender();

        await using (var db1 = CreateContext())
        {
            await new OutboxDispatcher(db1, clock, sender).FlushAsync(eventCode, CancellationToken.None);
        }

        clock.Advance(TimeSpan.FromMinutes(51));
        await using (var db2 = CreateContext())
        {
            await new OutboxDispatcher(db2, clock, sender).FlushAsync(eventCode, CancellationToken.None);
        }

        var stored = await LoadAsync(eventCode);
        Assert.Equal(2, stored.Attempts);
        Assert.Equal("Pending", stored.State);
        await sender.Received(2).SendAsync(
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
