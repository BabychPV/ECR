// tests/Ecr.Infrastructure.Tests/Jobs/CollectionJobAuthAlertDedupTests.cs
using Ecr.Application.Errors;
using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Entities.Integration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Integration;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// J1-01: протермінований пароль з'єднання не перетворюється на шторм негайних листів —
/// один алерт на з'єднання за вікно тиші, а негайна відправка не чіпає чужих подій черги.
/// </summary>
/// <remarks>
/// ⚠ На справжньому SQL Server: дедуплікація тримається на транзакційному
/// <c>sp_getapplock</c> і пошуку в <c>itg.NotificationOutbox</c>. База спільна на колекцію —
/// рядки тесту знаходяться за міткою з'єднання (унікальний <c>DataSourceId</c>) і видаляються
/// наприкінці, щоб чужі флешери їх не забрали.
/// </remarks>
[Collection("SqlServer")]
public sealed class CollectionJobAuthAlertDedupTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2037, 4, 7, 2, 5, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "J1-01")]
    public async Task Сутності_одного_зєднання_дають_один_лист_за_вікно_тиші()
    {
        await using var db = Context();
        var (dataSourceId, entityIds) = await ArrangeAsync(db, entities: 5);
        var tag = CollectionJob.ConnectionTag(dataSourceId);
        var clock = new TestClock(Now);

        try
        {
            // Тик 1: усі сутності з'єднання відмовляють — лист один.
            await FailAllAsync(entityIds, clock);
            Assert.Equal(1, await CountAsync(tag));

            // Через пів вікна той самий тик — у межах вікна тиші (1 год, D-353): нового листа немає.
            clock.Advance(CollectionJob.AuthAlertQuietPeriod / 2);
            await FailAllAsync(entityIds, clock);
            Assert.Equal(1, await CountAsync(tag));

            // Після вікна тиші — нагадування: рівно один новий лист.
            // ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати перевірку `alreadyAlerted` — після першого тику
            // рядків 5, а не 1; прибрати вікно (`CreatedAt >= quietFrom`) — тут лишається 1.
            clock.Advance(CollectionJob.AuthAlertQuietPeriod);
            await FailAllAsync(entityIds, clock);
            Assert.Equal(2, await CountAsync(tag));
        }
        finally
        {
            await using var cleanup = Context();
            await cleanup.NotificationOutbox
                .Where(n => n.EventCode == CollectionFailure.AlertEventCode && n.Body.EndsWith(tag))
                .ExecuteDeleteAsync();
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "J1-01")]
    public async Task Відправка_одного_виду_подій_не_витрачає_спроби_інших()
    {
        var marker = Guid.NewGuid().ToString("N")[..10];
        var mine = $"j1.mine.{marker}";
        var foreign = $"j1.foreign.{marker}";

        await using (var setup = Context())
        {
            setup.NotificationOutbox.Add(new NotificationOutboxItem(mine, "Тема", "Текст", "ops@example.local", Now));
            setup.NotificationOutbox.Add(new NotificationOutboxItem(foreign, "Тема", "Текст", "ops@example.local", Now));
            await setup.SaveChangesAsync();
        }

        try
        {
            // Пошта лежить: кожна спроба відправки кидає.
            var sender = Substitute.For<INotificationSender>();
            sender.IsConfigured.Returns(true);
            sender
                .SendAsync(
                    Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .ThrowsAsync(new InvalidOperationException("smtp down"));

            await using (var db = Context())
            {
                var dispatcher = new OutboxDispatcher(db, new TestClock(Now), sender);
                for (var i = 0; i < 3; i++)
                {
                    await dispatcher.FlushAsync(mine, CancellationToken.None);
                }
            }

            await using var verify = Context();
            var rows = await verify.NotificationOutbox.AsNoTracking()
                .Where(n => n.EventCode == mine || n.EventCode == foreign)
                .ToListAsync();

            // ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати фільтр `onlyEventCode` у `ClaimBatchAsync` — чужа
            // подія отримує 3 спроби замість 0.
            Assert.Equal(3, rows.Single(r => r.EventCode == mine).Attempts);
            var other = rows.Single(r => r.EventCode == foreign);
            Assert.Equal(0, other.Attempts);
            Assert.Equal("Pending", other.State);
        }
        finally
        {
            await using var cleanup = Context();
            await cleanup.NotificationOutbox
                .Where(n => n.EventCode == mine || n.EventCode == foreign)
                .ExecuteDeleteAsync();
        }
    }

    /// <summary>Кожна сутність з'єднання проходить збір, що падає на автентифікації.</summary>
    private async Task FailAllAsync(IReadOnlyList<int> entityIds, TestClock clock)
    {
        foreach (var entityId in entityIds)
        {
            await using var db = Context();
            var runner = Substitute.For<ICollectionRunner>();
            runner
                .RunAsync(
                    Arg.Any<int>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                    Arg.Any<IJobProgress>(), Arg.Any<CancellationToken>())
                .ThrowsAsync(new SourceAuthenticationException(
                    "ECR-INT-0502", CollectionFailure.AuthenticationRefused($"E{entityId}")));

            // ⚠ Відправник не налаштований: подія лишається `Pending`, а тест рахує саме рядки черги.
            var sender = Substitute.For<INotificationSender>();
            sender.IsConfigured.Returns(false);

            var job = new CollectionJob(
                runner,
                db,
                Substitute.For<IBackgroundJobScheduler>(),
                clock,
                new NotificationOutboxStore(db, clock),
                new OutboxDispatcher(db, clock, sender),
                Substitute.For<IRegistrySyncJob>());

            await Assert.ThrowsAsync<SourceAuthenticationException>(() => job.ExecuteAsync(
                new CollectionJobRequest(entityId, null, null), Substitute.For<IJobProgress>(), CancellationToken.None));
        }
    }

    private async Task<int> CountAsync(string tag)
    {
        await using var db = Context();
        return await db.NotificationOutbox.AsNoTracking()
            .CountAsync(n => n.EventCode == CollectionFailure.AlertEventCode && n.Body.EndsWith(tag));
    }

    /// <summary>З'єднання з кількома неактивними сутностями (див. <c>CollectionScheduleWindowTests</c>).</summary>
    private static async Task<(int DataSourceId, IReadOnlyList<int> EntityIds)> ArrangeAsync(EcrDbContext db, int entities)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];

        var dataSource = new DataSource(
            EcrCode.Create($"Src{tag}"), Name("J1-01"), ExternalTransport.PiWebApi,
            "https://example.test", "secret");
        db.DataSources.Add(dataSource);
        await db.SaveChangesAsync(CancellationToken.None);

        var ids = new List<int>();
        for (var i = 0; i < entities; i++)
        {
            var entity = new SourceEntity(dataSource.Id, $"Ent{tag}{i}", RegistrySourceKind.External);
            entity.Deactivate();
            db.SourceEntities.Add(entity);
            await db.SaveChangesAsync(CancellationToken.None);
            ids.Add(entity.Id);
        }

        return (dataSource.Id, ids);
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
