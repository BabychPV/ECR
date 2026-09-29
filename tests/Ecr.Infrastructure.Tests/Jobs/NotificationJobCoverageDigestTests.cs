// tests/Ecr.Infrastructure.Tests/Jobs/NotificationJobCoverageDigestTests.cs
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Entities.Integration;
using Ecr.Domain.Entities.Notifications;
using Ecr.Domain.Enums;
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
/// ІНТ-3.3, D-118: події журналу покриття (<c>itg.CollectionCoverage</c> зі
/// статусом) мусять потрапити у зведення <see cref="NotificationJob"/>.
/// </summary>
/// <remarks>
/// ⛔ Регресія, яку ловлять тести: матеріалізація, що ПРОПУСТИЛА інтервал
/// (закритий період, стеля точок), завершується успішно — у
/// <c>itg.JobProgress</c> вона не <c>Failed</c>, і зведення її не бачило. Точок
/// у комірках немає, а лист мовчить — тиша замість ознаки здоров'я.
///
/// ⚠ База спільна на <c>[Collection("SqlServer")]</c>, а вікно зведення
/// (<c>NotificationJob.SinceAsync</c>) береться з ОСТАННЬОГО прогону з кодом
/// <c>notification</c>. Тому кожен тест сам кладе «попередній» прогін на
/// <c>now - 1 год</c> — вікно стає точно відомим — і працює в 2035 році,
/// пізніше за сусідів (2030–2033), а в <c>finally</c> прибирає свої
/// <c>itg.MaintenanceRun</c> і рядки покриття: лишений прогін 2035 року зсунув
/// би вікно сусіднього тесту за межі його даних.
/// </remarks>
[Collection("SqlServer")]
public sealed class NotificationJobCoverageDigestTests(SqlServerFixture sql)
{
    /// <summary>Межа прибирання: усе, що клас написав у журнал обслуговування, пізніше.</summary>
    private static readonly DateTime CleanupFrom = new(2035, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "INT-3.3")]
    public async Task Пропуски_покриття_потрапляють_у_зведення_згрупованими_без_конфліктів_і_старих_подій()
    {
        var now = new DateTime(2035, 3, 3, 3, 0, 0, DateTimeKind.Utc);
        var world = await ArrangeAsync(now);

        try
        {
            await using (var setup = CreateContext())
            {
                // 5 однакових пропусків за стелею — мусять стати ОДНИМ рядком.
                for (var i = 0; i < 5; i++)
                {
                    setup.CollectionCoverages.Add(CollectionCoverage.Skipped(
                        world.EntityId, 203501, CollectionCoverage.SkippedPointCeiling,
                        "TOTAL: 120000 points, ceiling 100000", now.AddMinutes(-50 + i)));
                }

                setup.CollectionCoverages.Add(CollectionCoverage.Skipped(
                    world.EntityId, 203412, CollectionCoverage.SkippedPeriodClosed,
                    "Period 203412 is closed", now.AddMinutes(-30)));

                // Очікувана поведінка «людина має рацію» — лише в UI, не в листі.
                for (var i = 0; i < 3; i++)
                {
                    setup.CollectionCoverages.Add(CollectionCoverage.Skipped(
                        world.EntityId, 203502, CollectionCoverage.ConflictKeptManual,
                        "Manual value kept", now.AddMinutes(-20 + i)));
                }

                // До `since` (= now - 1 год): уже були в попередньому зведенні.
                for (var i = 0; i < 2; i++)
                {
                    setup.CollectionCoverages.Add(CollectionCoverage.Skipped(
                        world.EntityId, 203500, CollectionCoverage.SkippedPointCeiling,
                        "Old ceiling", now.AddHours(-2).AddMinutes(i)));
                }

                await setup.SaveChangesAsync(CancellationToken.None);
            }

            var (deliveries, messages) = await RunJobAsync(now, NotificationSeverity.Info);

            await using var db = CreateContext();
            var digest = await db.MaintenanceRuns
                .AsNoTracking()
                .Where(r => r.JobCode == NotificationJob.Code
                            && r.DetailsJson != null && r.DetailsJson.Contains(world.EntityCode))
                .OrderByDescending(r => r.Id)
                .FirstOrDefaultAsync();

            Assert.NotNull(digest);
            Assert.Equal("Degraded", digest!.Status);

            var mine = ItemsOf(digest.DetailsJson!, world.EntityCode);

            // ⛔ Рівно два рядки: стеля (5 → 1) і закритий період. Ні конфліктів,
            // ні подій до `since`.
            Assert.Equal(2, mine.Count);
            Assert.All(mine, i => Assert.Equal(NotificationJob.CoverageKind, i.Kind));
            Assert.DoesNotContain(mine, i => i.Status == CollectionCoverage.ConflictKeptManual);
            Assert.DoesNotContain(mine, i => i.Details.Contains("203500", StringComparison.Ordinal));

            var ceiling = Assert.Single(mine, i => i.Status == CollectionCoverage.SkippedPointCeiling);
            Assert.Equal("період 203501: 5 подій; TOTAL: 120000 points, ceiling 100000", ceiling.Details);

            var closed = Assert.Single(mine, i => i.Status == CollectionCoverage.SkippedPeriodClosed);
            Assert.Equal("період 203412: 1 подій; Period 203412 is closed", closed.Details);

            // ⚠ Подія матриці — збій ЗБОРУ (адресат — відповідальний за джерело),
            // а не збій задачі.
            var delivery = Assert.Single(deliveries);
            Assert.Equal(NotificationEventKind.CollectionFailed, delivery.EventKind);
            Assert.Contains(world.EntityCode, delivery.EventKey, StringComparison.Ordinal);

            var message = Assert.Single(messages);
            Assert.Contains($"[coverage] {world.EntityCode}: SkippedPointCeiling", message.Body, StringComparison.Ordinal);
            Assert.DoesNotContain(CollectionCoverage.ConflictKeptManual, message.Body, StringComparison.Ordinal);

            var queued = await db.NotificationOutbox
                .AsNoTracking()
                .Where(n => n.EventCode == "maintenance.failures" && n.Body.Contains(world.EntityCode))
                .OrderByDescending(n => n.Id)
                .FirstOrDefaultAsync();

            Assert.NotNull(queued);
            Assert.Contains("період 203501: 5 подій", queued!.Body, StringComparison.Ordinal);
        }
        finally
        {
            await CleanupAsync(world.EntityId);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "INT-3.3")]
    public async Task Закритий_період_іде_попередженням_і_не_проходить_правило_від_помилки()
    {
        var now = new DateTime(2035, 4, 4, 4, 0, 0, DateTimeKind.Utc);
        var world = await ArrangeAsync(now);

        try
        {
            await using (var setup = CreateContext())
            {
                setup.CollectionCoverages.Add(CollectionCoverage.Skipped(
                    world.EntityId, 203503, CollectionCoverage.SkippedPeriodClosed,
                    "Period 203503 is closed", now.AddMinutes(-10)));
                await setup.SaveChangesAsync(CancellationToken.None);
            }

            // Правило «від помилки»: попередження його не проходить.
            var (deliveries, messages) = await RunJobAsync(now, NotificationSeverity.Error);

            Assert.Empty(deliveries);
            Assert.Empty(messages);
        }
        finally
        {
            await CleanupAsync(world.EntityId);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "INT-3.3")]
    public async Task Стеля_точок_іде_помилкою_і_проходить_правило_від_помилки()
    {
        var now = new DateTime(2035, 5, 5, 5, 0, 0, DateTimeKind.Utc);
        var world = await ArrangeAsync(now);

        try
        {
            await using (var setup = CreateContext())
            {
                setup.CollectionCoverages.Add(CollectionCoverage.Skipped(
                    world.EntityId, 203504, CollectionCoverage.SkippedPointCeiling,
                    "TOTAL: 200000 points, ceiling 100000", now.AddMinutes(-10)));
                await setup.SaveChangesAsync(CancellationToken.None);
            }

            var (deliveries, _) = await RunJobAsync(now, NotificationSeverity.Error);

            var delivery = Assert.Single(deliveries);
            Assert.Equal(NotificationEventKind.CollectionFailed, delivery.EventKind);
        }
        finally
        {
            await CleanupAsync(world.EntityId);
        }
    }

    /// <summary>
    /// Незаписане через конфлікт запису і через підтвердження — У зведенні
    /// (на відміну від <c>ConflictKeptManual</c>), попередженням.
    /// </summary>
    /// <remarks>
    /// ⛔ Доти обидва випадки журналювалися як <c>ConflictKeptManual</c>, а той
    /// свідомо поза зведенням — незаписані дані мовчали в листі.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-118")]
    public async Task Конфлікт_запису_і_підтвердження_йдуть_у_зведення_попередженням()
    {
        var now = new DateTime(2035, 6, 6, 6, 0, 0, DateTimeKind.Utc);
        var world = await ArrangeAsync(now);

        try
        {
            await using (var setup = CreateContext())
            {
                setup.CollectionCoverages.Add(CollectionCoverage.Skipped(
                    world.EntityId, 203505, CollectionCoverage.SkippedWriteConflict,
                    "R1:C1 write conflict", now.AddMinutes(-10)));
                setup.CollectionCoverages.Add(CollectionCoverage.Skipped(
                    world.EntityId, 203505, CollectionCoverage.SkippedNeedsConfirmation,
                    "R1:C2 needs confirmation", now.AddMinutes(-9)));
                await setup.SaveChangesAsync(CancellationToken.None);
            }

            // Серйозність (Warning) — `Серйозність_події_покриття_залежить_від_статусу`;
            // тут — що рядки взагалі доходять до листа.
            var (deliveries, messages) = await RunJobAsync(now, NotificationSeverity.Warning);

            var delivery = Assert.Single(deliveries);
            Assert.Equal(NotificationEventKind.CollectionFailed, delivery.EventKind);

            var message = Assert.Single(messages);
            Assert.Contains($"[coverage] {world.EntityCode}: {CollectionCoverage.SkippedWriteConflict}", message.Body, StringComparison.Ordinal);
            Assert.Contains($"[coverage] {world.EntityCode}: {CollectionCoverage.SkippedNeedsConfirmation}", message.Body, StringComparison.Ordinal);
        }
        finally
        {
            await CleanupAsync(world.EntityId);
        }
    }

    /// <summary>
    /// U12: причина прогону збору, записана конвертом, іде в лист ТЕКСТОМ мови
    /// листа; стара причина (сирий текст до U12) — як є.
    /// </summary>
    /// <remarks>
    /// ⛔ Регресія: без резолву в листі стояв би сирий JSON із ключем
    /// каталогу замість причини. Мутація «Details = r.ErrorMessage» (без
    /// резолву) робить цей тест червоним.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "U12")]
    public async Task Причина_прогону_збору_конвертом_іде_в_лист_текстом_а_стара_як_є()
    {
        var now = new DateTime(2035, 7, 7, 7, 0, 0, DateTimeKind.Utc);
        var world = await ArrangeAsync(now);

        try
        {
            await using (var setup = CreateContext())
            {
                var reason = JobProgressMessageCodec.Encode(new JobProgressMessageEnvelope(
                    "jobs.collectionRunReason",
                    new Dictionary<string, string> { ["code"] = "ECR-INT-0503" },
                    new JobProgressMessageEnvelope(
                        "jobs.collectionTimeout", new Dictionary<string, string> { ["minutes"] = "15" })));

                var fresh = new CollectionRun(world.EntityId, now.AddHours(-3), now.AddHours(-2), false, null, now.AddMinutes(-40));
                fresh.Complete("Degraded", 3, now.AddMinutes(-30), reason);

                var legacy = new CollectionRun(world.EntityId, now.AddHours(-2), now.AddHours(-1), false, null, now.AddMinutes(-25));
                legacy.Complete("Degraded", 0, now.AddMinutes(-20), "ECR-INT-0503: legacy raw reason");

                setup.CollectionRuns.AddRange(fresh, legacy);
                await setup.SaveChangesAsync(CancellationToken.None);
            }

            var catalog = Substitute.For<IUiStringCatalog>();
            catalog.GetScopedAsync(NotificationJob.DigestLanguage, UiStringScope.Private, Arg.Any<CancellationToken>())
                .Returns(new UiStringCatalog(
                    NotificationJob.DigestLanguage,
                    1,
                    new Dictionary<string, string>
                    {
                        ["jobs.collectionRunReason"] = "{code}: {message}",
                        ["jobs.collectionTimeout"] = "time limit of {minutes} min exceeded",
                    }));

            var (_, messages) = await RunJobAsync(now, NotificationSeverity.Info, catalog);

            var message = Assert.Single(messages);
            Assert.Contains(
                $"[collection] {world.EntityCode}: Degraded. ECR-INT-0503: time limit of 15 min exceeded",
                message.Body,
                StringComparison.Ordinal);
            Assert.Contains(
                $"[collection] {world.EntityCode}: Degraded. ECR-INT-0503: legacy raw reason",
                message.Body,
                StringComparison.Ordinal);
            Assert.DoesNotContain("jobs.collection", message.Body, StringComparison.Ordinal);

            await using var db = CreateContext();
            var queued = await db.NotificationOutbox
                .AsNoTracking()
                .Where(n => n.EventCode == "maintenance.failures" && n.Body.Contains(world.EntityCode))
                .OrderByDescending(n => n.Id)
                .FirstOrDefaultAsync();

            Assert.NotNull(queued);
            Assert.Contains("ECR-INT-0503: time limit of 15 min exceeded", queued!.Body, StringComparison.Ordinal);
            Assert.DoesNotContain("jobs.collection", queued.Body, StringComparison.Ordinal);
        }
        finally
        {
            await CleanupAsync(world.EntityId);
        }
    }

    [Theory]
    [InlineData(CollectionCoverage.SkippedPeriodClosed, NotificationSeverity.Warning)]
    [InlineData(CollectionCoverage.SkippedPointCeiling, NotificationSeverity.Error)]
    // Значення не записано, але причина відома й не є дефектом: повтор
    // наступним прогоном / потрібне підтвердження людини — попередження.
    [InlineData(CollectionCoverage.SkippedWriteConflict, NotificationSeverity.Warning)]
    [InlineData(CollectionCoverage.SkippedNeedsConfirmation, NotificationSeverity.Warning)]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "INT-3.3")]
    public void Серйозність_події_покриття_залежить_від_статусу(string status, NotificationSeverity expected)
    {
        Assert.Equal(expected, NotificationJob.SeverityOf(NotificationJob.CoverageKind, status));

        // Збій збору з тим самим текстом статусу лишається помилкою.
        Assert.Equal(NotificationSeverity.Error, NotificationJob.SeverityOf(NotificationJob.CollectionKind, status));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "INT-3.3")]
    public void Подія_покриття_мапиться_на_збій_збору()
        => Assert.Equal(
            NotificationEventKind.CollectionFailed,
            NotificationJob.EventKindOf(NotificationJob.CoverageKind));

    /// <summary>
    /// Джерело (вимкнене — щоб не жовтити <c>/health/ready</c> сусідам) і
    /// «попередній» прогін зведення на <c>now - 1 год</c>.
    /// </summary>
    private async Task<World> ArrangeAsync(DateTime now)
    {
        await using var db = CreateContext();

        var code = $"NC{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        var source = new DataSource(
            Ecr.Domain.ValueObjects.EcrCode.Create(code),
            new Ecr.Domain.ValueObjects.LocalizedText(new Dictionary<string, string> { ["en"] = "PI AF" }),
            ExternalTransport.PiWebApi, "https://pi.corp.example/piwebapi", $"DataSource.{code}");
        source.Deactivate();
        db.DataSources.Add(source);
        await db.SaveChangesAsync(CancellationToken.None);

        // ⚠ ASCII-код навмисно: кирилиця в `DetailsJson` зведення виходить
        // послідовностями `\u04XX`, і `LIKE` за нею не знайшов би нічого.
        var entityCode = $"COV-{code}";
        var entity = new SourceEntity(source.Id, entityCode, RegistrySourceKind.External);
        entity.Deactivate();
        db.SourceEntities.Add(entity);

        db.MaintenanceRuns.Add(new MaintenanceRun(NotificationJob.Code, now.AddHours(-1)));
        await db.SaveChangesAsync(CancellationToken.None);

        return new World(entity.Id, entityCode);
    }

    /// <summary>
    /// Запускає зведення з одним каналом і правилами на обидві події матриці.
    /// </summary>
    /// <param name="now">Момент прогону.</param>
    /// <param name="minSeverity">Межа правила для <see cref="NotificationEventKind.CollectionFailed"/>.</param>
    /// <param name="catalog">Каталог рядків для резолву причин збору (U12); <c>null</c> — порожня підміна.</param>
    /// <remarks>
    /// ⚠ Правило на <see cref="NotificationEventKind.JobFailed"/> теж є (від
    /// <c>Info</c>): якби подію покриття змапили не туди, доставка все одно
    /// відбулася б — і тест побачив би ХИБНИЙ вид, а не тишу.
    /// </remarks>
    private async Task<(List<NotificationDelivery> Deliveries, List<NotificationMessage> Messages)> RunJobAsync(
        DateTime now, NotificationSeverity minSeverity, IUiStringCatalog? catalog = null)
    {
        var clock = new TestClock(now);
        var channel = new NotificationChannel(NotificationChannelKind.Smtp, "coverage-test", "{}", now, null);
        var plan = new NotificationDispatchPlan(
            "coverage-test",
            [channel],
            [
                new NotificationRule(NotificationEventKind.CollectionFailed, channel.Id, minSeverity),
                new NotificationRule(NotificationEventKind.JobFailed, channel.Id, NotificationSeverity.Info),
            ]);

        var deliveries = new List<NotificationDelivery>();
        var store = Substitute.For<INotificationDispatchStore>();
        store.GetPlanAsync(Arg.Any<CancellationToken>()).Returns(plan);
        store.WasSentSinceAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(false);
        store.AppendDeliveryAsync(Arg.Do<NotificationDelivery>(deliveries.Add), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var messages = new List<NotificationMessage>();
        var channelSender = Substitute.For<INotificationChannelSender>();
        channelSender.Kind.Returns(NotificationChannelKind.Smtp);
        channelSender.SendAsync(Arg.Any<NotificationChannel>(), Arg.Do<NotificationMessage>(messages.Add), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await using var db = CreateContext();
        var sender = Substitute.For<INotificationSender>();
        sender.IsConfigured.Returns(false);

        var job = new NotificationJob(
            db, clock, new OutboxDispatcher(db, clock, sender),
            new NotificationDispatcher(store, [channelSender], clock),
            catalog ?? Substitute.For<IUiStringCatalog>());

        await job.ExecuteAsync(null, Substitute.For<IJobProgress>(), CancellationToken.None);

        return (deliveries, messages);
    }

    /// <summary>Рядки зведення, що стосуються саме цієї сутності.</summary>
    private static List<Item> ItemsOf(string detailsJson, string subject)
    {
        using var doc = JsonDocument.Parse(detailsJson);

        return [.. doc.RootElement.GetProperty("items").EnumerateArray()
            .Where(i => i.GetProperty("subject").GetString() == subject)
            .Select(i => new Item(
                i.GetProperty("kind").GetString()!,
                i.GetProperty("status").GetString()!,
                i.GetProperty("details").GetString() ?? string.Empty))];
    }

    private async Task CleanupAsync(int entityId)
    {
        await using var db = CreateContext();

        await db.CollectionCoverages
            .Where(c => c.SourceEntityId == entityId)
            .ExecuteDeleteAsync(CancellationToken.None);

        await db.CollectionRuns
            .Where(r => r.SourceEntityId == entityId)
            .ExecuteDeleteAsync(CancellationToken.None);

        await db.MaintenanceRuns
            .Where(r => r.StartedAt >= CleanupFrom)
            .ExecuteDeleteAsync(CancellationToken.None);
    }

    private sealed record World(int EntityId, string EntityCode);

    private sealed record Item(string Kind, string Status, string Details);
}
