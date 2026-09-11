// tests/Ecr.Infrastructure.Tests/Jobs/MaintenanceRunFailureDigestTests.cs
using Ecr.Application.Ports;
using Ecr.Infrastructure.Integration;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Q-240: задача обслуговування, що впала, мусить ЗАКРИТИ свій прогін — інакше
/// її провал не бачить ніхто.
/// </summary>
/// <remarks>
/// ⛔ Регресія, яку ловлять ці тести. Задачі відкривають прогін у
/// <c>itg.MaintenanceRun</c> (<c>Status = "Running"</c>, <c>FinishedAt = NULL</c>)
/// і закривали його ЛИШЕ на успішному шляху: жодного <c>try/catch</c>. Виняток
/// — і рядок лишався <c>Running</c> назавжди. Саме по собі це ще не тиша;
/// тишею це робить читач: зведення <see cref="NotificationJob"/> бере збої
/// запитом <c>FinishedAt &gt;= since &amp;&amp; Status != "Succeeded"</c>, а
/// <c>NULL &gt;= since</c> у SQL — не істина. Тобто провалена нічна перевірка
/// не давала ні рядка у зведенні, ні події в <c>itg.NotificationOutbox</c>, ні
/// листа. Єдиним слідом лишався <c>itg.JobProgress</c>, який зведення читає
/// тільки для матеріалізації (<c>Q-235</c>).
///
/// ⚠ Прогін ведеться РЕАЛЬНОЮ задачею (<see cref="ConsistencyCheckJob"/>) проти
/// реального SQL Server, а падіння вноситься в її залежність
/// (<see cref="IOrphanScanner"/>), а не в саму задачу: перевіряється те, що
/// станеться в проді, а не власноруч підготовлений рядок таблиці.
///
/// ⚠ Час прогонів — 2033 рік, ПІЗНІШЕ за сусідні тести цієї ж бази
/// (<see cref="NotificationJobMaterializationDigestTests"/> працює в 2030–2031),
/// а написані рядки <c>itg.MaintenanceRun</c> прибираються у <c>finally</c>.
/// Причина: <c>NotificationJob.SinceAsync</c> бере «відколи» з ОСТАННЬОГО за
/// <c>StartedAt</c> прогону з кодом <c>notification</c>, база в
/// <c>[Collection("SqlServer")]</c> спільна, а порядок виконання класів не
/// гарантований — прогін 2033 року, лишений по собі, зсунув би вікно сусіднього
/// тесту за межі його власних даних.
/// </remarks>
[Collection("SqlServer")]
public sealed class MaintenanceRunFailureDigestTests(SqlServerFixture sql)
{
    /// <summary>Межа прибирання: усе, що цей клас написав, лежить пізніше.</summary>
    private static readonly DateTime CleanupFrom = new(2033, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "Q-240")]
    public async Task Падіння_задачі_закриває_прогін_станом_Failed()
    {
        var now = new DateTime(2033, 5, 5, 5, 0, 0, DateTimeKind.Utc);
        var reason = $"Довідник недоступний {Guid.NewGuid():N}";

        try
        {
            await using var db = CreateContext();

            // ⚠ Падає саме залежність задачі, а не задача: так виглядає
            // реальний збій (недоступна база, дедлок, обрив з'єднання).
            var scanner = Substitute.For<IOrphanScanner>();
            scanner.ScanAllAsync(Arg.Any<CancellationToken>())
                .Returns<Task<int>>(_ => throw new InvalidOperationException(reason));

            var job = new ConsistencyCheckJob(
                db, scanner, new TestClock(now), Substitute.For<IConsistencyMetrics>());

            // ⛔ Виняток мусить ПІТИ НАГОРУ: без нього QuartzJobAdapter вважав
            // би прогін успішним і не поставив би ретрай.
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => job.ExecuteAsync(null, Substitute.For<IJobProgress>(), CancellationToken.None));

            await using var read = CreateContext();

            var run = await read.MaintenanceRuns
                .AsNoTracking()
                .Where(r => r.JobCode == ConsistencyCheckJob.Code && r.StartedAt >= CleanupFrom)
                .OrderByDescending(r => r.Id)
                .FirstOrDefaultAsync();

            Assert.NotNull(run);

            // ⛔ Ось що ламалося: стан лишався "Running", а FinishedAt — NULL.
            Assert.Equal("Failed", run!.Status);
            Assert.NotNull(run.FinishedAt);
            Assert.Equal(now, run.FinishedAt);

            // Причина зберігається — без неї рядок каже «щось упало» і не
            // каже, куди йти.
            Assert.Contains(reason, run.DetailsJson!, StringComparison.Ordinal);
        }
        finally
        {
            await CleanupAsync();
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "Q-240")]
    public async Task Провалена_задача_обслуговування_потрапляє_у_зведення_і_чергу_сповіщень()
    {
        var now = new DateTime(2033, 6, 6, 6, 0, 0, DateTimeKind.Utc);
        var clock = new TestClock(now);

        // ⚠ Мітка пошуку — ASCII навмисно. Саме зведення серіалізується
        // типовим кодувальником (`NotificationJob.Options`), тож кирилиця в
        // `DetailsJson` зведення виходить послідовностями `\u04XX`, і пошук
        // `LIKE` за нею не знайшов би нічого — ця перевірка про ЗМІСТ
        // зведення, а не про його кодування (те перевіряє тест вище).
        var marker = $"ORPHAN-SCAN-{Guid.NewGuid():N}";
        var reason = $"Перерахунок IsOrphaned обірвано: {marker}";

        try
        {
            await using (var db = CreateContext())
            {
                var scanner = Substitute.For<IOrphanScanner>();
                scanner.ScanAllAsync(Arg.Any<CancellationToken>())
                    .Returns<Task<int>>(_ => throw new InvalidOperationException(reason));

                var failing = new ConsistencyCheckJob(
                    db, scanner, clock, Substitute.For<IConsistencyMetrics>());

                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => failing.ExecuteAsync(null, Substitute.For<IJobProgress>(), CancellationToken.None));
            }

            await using var digestDb = CreateContext();

            // ⚠ Транспорт не налаштований (`P-13`): подія лишається Pending, а
            // не зникає. Перевіряється саме постановка в чергу — доставку
            // вирішує замовник.
            var sender = Substitute.For<INotificationSender>();
            sender.IsConfigured.Returns(false);

            var notification = new NotificationJob(
                digestDb, clock, new OutboxDispatcher(digestDb, clock, sender));

            await notification.ExecuteAsync(
                null, Substitute.For<IJobProgress>(), CancellationToken.None);

            // Пошук за ВМІСТОМ, а не «останній за часом»: база спільна на весь
            // прогін, і сусідні тести пишуть у ті самі таблиці.
            var digest = await digestDb.MaintenanceRuns
                .AsNoTracking()
                .Where(r => r.JobCode == NotificationJob.Code
                            && r.DetailsJson != null && r.DetailsJson.Contains(marker))
                .OrderByDescending(r => r.Id)
                .FirstOrDefaultAsync();

            // ⛔ Ось чого не було: провал нічної перевірки не потрапляв у
            // зведення ЖОДНОГО разу — рядок `Running`/`FinishedAt = NULL`
            // не проходить фільтр `FinishedAt >= since`.
            Assert.NotNull(digest);
            Assert.Equal("Degraded", digest!.Status);
            Assert.Contains(ConsistencyCheckJob.Code, digest.DetailsJson!, StringComparison.Ordinal);

            var queued = await digestDb.NotificationOutbox
                .AsNoTracking()
                .Where(n => n.EventCode == "maintenance.failures" && n.Body.Contains(marker))
                .OrderByDescending(n => n.Id)
                .FirstOrDefaultAsync();

            // Зведення без події в черзі — це запис у базі, який ніхто не
            // читає: людину інформує саме черга.
            Assert.NotNull(queued);
            Assert.Equal("Pending", queued!.State);
            Assert.Contains(ConsistencyCheckJob.Code, queued.Body, StringComparison.Ordinal);
        }
        finally
        {
            await CleanupAsync();
        }
    }

    /// <summary>
    /// Прибирає прогони, написані цим класом.
    /// </summary>
    /// <remarks>
    /// ⚠ Саме <c>itg.MaintenanceRun</c>, і саме за часом: вікно зведення
    /// (<c>NotificationJob.SinceAsync</c>) будується з останнього прогону з
    /// кодом <c>notification</c>, тож прогін 2033 року, лишений у спільній
    /// базі, зсунув би вікно сусіднього тесту за межі його власних даних.
    /// Черга сповіщень нікому не заважає: сусіди шукають у ній за вмістом.
    /// </remarks>
    private async Task CleanupAsync()
    {
        await using var db = CreateContext();

        await db.MaintenanceRuns
            .Where(r => r.StartedAt >= CleanupFrom)
            .ExecuteDeleteAsync(CancellationToken.None);
    }
}
