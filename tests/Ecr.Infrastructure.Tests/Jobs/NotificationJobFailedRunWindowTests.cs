// tests/Ecr.Infrastructure.Tests/Jobs/NotificationJobFailedRunWindowTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Entities.Integration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Integration;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Notifications;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// J1-02: прогін зведення, що впав, не забирає з собою своє вікно — наступний прогін бере
/// межу від останнього ЗАВЕРШЕНОГО не-<c>Failed</c> прогону і бачить збої вікна невдалого.
/// </summary>
/// <remarks>
/// ⚠ База спільна на <c>[Collection("SqlServer")]</c>, а вікно будується з прогонів
/// <c>notification</c>. Час — 2038 рік, пізніше за всіх сусідів (до 2037), і в <c>finally</c>
/// прибирається все, що клас написав: лишений прогін зсунув би вікно сусідам.
/// </remarks>
[Collection("SqlServer")]
public sealed class NotificationJobFailedRunWindowTests(SqlServerFixture sql)
{
    private static readonly DateTime T0 = new(2038, 2, 1, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime CleanupFrom = new(2038, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "J1-02")]
    public async Task Збій_прогону_зведення_не_губить_його_вікно()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];

        // ⚠ ASCII-код: кирилиця в тексті зведення могла б піти escape-послідовностями.
        var entityCode = $"J102-{tag}";
        int entityId;

        await using (var setup = CreateContext())
        {
            var source = new DataSource(
                EcrCode.Create($"J102S{tag}"), Name("J1-02"), ExternalTransport.PiWebApi,
                "https://example.test", "secret");
            source.Deactivate();
            setup.DataSources.Add(source);
            await setup.SaveChangesAsync(CancellationToken.None);

            var entity = new SourceEntity(source.Id, entityCode, RegistrySourceKind.External);
            entity.Deactivate();
            setup.SourceEntities.Add(entity);
            await setup.SaveChangesAsync(CancellationToken.None);
            entityId = entity.Id;

            // T0: успішне зведення.
            var ok = new MaintenanceRun(NotificationJob.Code, T0);
            ok.Complete("Succeeded", null, T0.AddSeconds(5));

            // T0+10 хв: збій збору — він мусить потрапити в лист.
            var collection = new CollectionRun(entityId, T0, T0.AddMinutes(10), false, null, T0.AddMinutes(10));
            collection.Complete("Failed", 0, T0.AddMinutes(10), $"boom-{tag}");

            // T0+60 хв: прогін зведення впав до коміту листа (MaintenanceRunFailure.RecordAsync).
            var failed = new MaintenanceRun(NotificationJob.Code, T0.AddMinutes(60));
            failed.Complete(MaintenanceRunFailure.FailedStatus, null, T0.AddMinutes(60).AddSeconds(30));

            setup.MaintenanceRuns.AddRange(ok, failed);
            setup.CollectionRuns.Add(collection);
            await setup.SaveChangesAsync(CancellationToken.None);
        }

        try
        {
            // Ретрай / наступний тик.
            var clock = new TestClock(T0.AddMinutes(61));
            await using (var db = CreateContext())
            {
                var sender = Substitute.For<INotificationSender>();
                sender.IsConfigured.Returns(false);
                var channels = Substitute.For<INotificationDispatchStore>();
                channels.GetPlanAsync(Arg.Any<CancellationToken>()).Returns(new NotificationDispatchPlan("none", [], []));

                var job = new NotificationJob(
                    db, clock, new OutboxDispatcher(db, clock, sender),
                    new NotificationDispatcher(channels, [], clock),
                    Substitute.For<IUiStringCatalog>());

                await job.ExecuteAsync(null, Substitute.For<IJobProgress>(), CancellationToken.None);
            }

            // ⛔ МУТАЦІЙНИЙ ДОКАЗ: повернути в `SinceAsync` вибір будь-якого прогону (без фільтра
            // статусу) — межа стає T0+60 хв, збій T0+10 хв випадає, рядка в черзі немає.
            await using var verify = CreateContext();
            var letters = await verify.NotificationOutbox.AsNoTracking()
                .Where(n => n.EventCode == "maintenance.failures" && n.Body.Contains(entityCode))
                .CountAsync();
            Assert.Equal(1, letters);
        }
        finally
        {
            await using var cleanup = CreateContext();
            await cleanup.MaintenanceRuns.Where(r => r.StartedAt >= CleanupFrom).ExecuteDeleteAsync();
            await cleanup.CollectionRuns.Where(r => r.SourceEntityId == entityId).ExecuteDeleteAsync();
            await cleanup.NotificationOutbox.Where(n => n.Body.Contains(entityCode)).ExecuteDeleteAsync();
        }
    }

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
