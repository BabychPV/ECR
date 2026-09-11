// tests/Ecr.Infrastructure.Tests/Integration/OutboxDispatcherClaimTests.cs
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
/// Q-241: два одночасні відправники (`NotificationJob` за щогодинним
/// розкладом і `CollectionJob.AlertAuthenticationAsync` негайно за H-20) не
/// мають надіслати той самий лист двічі, а відправник, що впав посеред
/// <c>SendAsync</c>, не має загубити подію назавжди.
/// </summary>
/// <remarks>
/// ⛔ Регресія, яку ловить перший тест: до цього <c>FlushAsync</c> читав
/// партію <c>Pending</c>-рядків у пам'ять ОДНИМ запитом і писав зміни в базу
/// ОДНИМ <c>SaveChangesAsync</c> лише в самому кінці — вікно між читанням і
/// записом лишало той самий рядок видимим як <c>Pending</c> для будь-якого
/// іншого виклику, що стартував у цей момент. Оскільки збір ставиться
/// ОКРЕМО на кожну сутність джерела (свій Quartz <c>JobKey</c> на кожну), а
/// протермінована облікова пара валить автентифікацію одразу на всіх —
/// це ЖОДЕН одиничний рідкісний збіг, а гарантований результат ОДНІЄЇ
/// протермінованої пари: N сутностей джерела фейляться на тому самому тику
/// й ставлять N одночасних <c>FlushAsync</c>.
///
/// ⚠ База — спільна на увесь <c>[Collection("SqlServer")]</c>: маркер у темі
/// листа унікальний на кожен тест, пошук — за вмістом, а не «останній
/// рядок».
/// </remarks>
[Collection("SqlServer")]
public sealed class OutboxDispatcherClaimTests(SqlServerFixture sql)
{
    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "Q-241")]
    public async Task Два_одночасних_флешери_не_надсилають_один_лист_двічі()
    {
        var now = new DateTime(2034, 3, 3, 3, 0, 0, DateTimeKind.Utc);
        var clock = new TestClock(now);
        var marker = $"CLAIM-{Guid.NewGuid():N}";

        await using (var setup = CreateContext())
        {
            setup.NotificationOutbox.Add(new NotificationOutboxItem(
                "test.claim", $"Тема {marker}", "Текст листа", "ops@example.local", now));
            await setup.SaveChangesAsync();
        }

        var sendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sendCount = 0;

        // ⚠ Один спільний substitute на обидва флешери — рахує ВСІ виклики
        // SendAsync незалежно від того, хто з них його зробив. Саме кількість
        // викликів (а не те, ХТО викликав) доводить чи не доводить дубль.
        var sender = Substitute.For<INotificationSender>();
        sender.IsConfigured.Returns(true);
        sender
            .SendAsync(
                Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                Interlocked.Increment(ref sendCount);

                // ⛔ Перший виклик зупиняється ТУТ, усередині "відправки" —
                // рядок у базі вже позначено Sending (захоплення — окремий,
                // раніший крок), але SaveChangesAsync з MarkSent ще НЕ
                // виконано. Це і є вікно, у якому стара реалізація губила
                // атомарність.
                sendStarted.TrySetResult();
                await releaseSend.Task;
            });

        await using var dbA = CreateContext();
        await using var dbB = CreateContext();
        var flusherA = new OutboxDispatcher(dbA, clock, sender);
        var flusherB = new OutboxDispatcher(dbB, clock, sender);

        // ⛔ Свідомо БЕЗ await: флешер A мусить зависнути ВСЕРЕДИНІ SendAsync,
        // а не завершитися до того, як B спробує забрати ту саму подію —
        // інакше тест перевіряв би послідовний виклик, а не одночасний.
        var flushA = flusherA.FlushAsync(CancellationToken.None);

        await sendStarted.Task;

        // Флешер B стартує, поки A ще "відправляє" перший (і єдиний) рядок.
        // На старому коді B прочитав би той самий Pending-рядок і теж
        // покликав би SendAsync (і теж заблокувався б на тому самому
        // `releaseSend`) — на новому предикат `State == "Pending"` у самому
        // UPDATE не дає йому забрати те, що вже захопив A.
        //
        // ⛔ B свідомо теж БЕЗ await тут: якщо стара реалізація змусить і
        // B зависнути на тому самому `releaseSend`, очікування B ПЕРЕД
        // звільненням шлюзу було б справжнім дедлоком (шлюз відкриває
        // рядок нижче, а не цей виклик). Форма тесту не має залежати від
        // того, чи саме так поводиться код, який вона перевіряє.
        var flushB = flusherB.FlushAsync(CancellationToken.None);

        // Обидва виклики, які встигли дійти до SendAsync (один — на
        // виправленому коді, до двох — на старому), зараз чекають на цей
        // самий шлюз. Відкриваємо його БЕЗУМОВНО — і лише ПОТІМ забираємо
        // результати обох.
        releaseSend.TrySetResult();

        var (sentA, _) = await flushA;
        var (sentB, _) = await flushB;

        Assert.Equal(1, sendCount);
        Assert.Equal(1, sentA + sentB);

        await using var verify = CreateContext();
        var stored = await verify.NotificationOutbox
            .AsNoTracking()
            .Where(n => n.Body == "Текст листа" && n.Subject.Contains(marker))
            .OrderByDescending(n => n.Id)
            .FirstOrDefaultAsync();

        Assert.NotNull(stored);
        Assert.Equal("Sent", stored!.State);
        Assert.Null(stored.ClaimToken);
        Assert.Null(stored.ClaimedAt);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "Q-241")]
    public async Task Зависле_захоплення_повертається_в_чергу_і_надсилається()
    {
        var claimedAt = new DateTime(2034, 4, 4, 4, 0, 0, DateTimeKind.Utc);
        var marker = $"STALE-{Guid.NewGuid():N}";

        await using (var setup = CreateContext())
        {
            var item = new NotificationOutboxItem(
                "test.stale", $"Тема {marker}", "Текст", "ops@example.local", claimedAt);
            setup.NotificationOutbox.Add(item);
            await setup.SaveChangesAsync();

            // ⚠ Симулюємо захоплення напряму в базі — те саме, що лишив би
            // процес, убитий посеред SendAsync: рядок у стані Sending,
            // ClaimedAt у минулому, і ЖОДНОГО подальшого запису.
            await setup.NotificationOutbox
                .Where(n => n.Id == item.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(n => n.State, "Sending")
                    .SetProperty(n => n.ClaimedAt, claimedAt)
                    .SetProperty(n => n.ClaimToken, (Guid?)Guid.NewGuid()));
        }

        var now = claimedAt + OutboxDispatcher.ClaimTimeout + TimeSpan.FromSeconds(1);
        var clock = new TestClock(now);

        var sender = Substitute.For<INotificationSender>();
        sender.IsConfigured.Returns(true);

        await using var db = CreateContext();
        var dispatcher = new OutboxDispatcher(db, clock, sender);

        var (sent, _) = await dispatcher.FlushAsync(CancellationToken.None);

        Assert.Equal(1, sent);

        await sender.Received(1).SendAsync(
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Is<string>(s => s.Contains(marker, StringComparison.Ordinal)),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "Q-241")]
    public async Task Свіже_захоплення_НЕ_повертається_в_чергу()
    {
        var claimedAt = new DateTime(2034, 5, 5, 5, 0, 0, DateTimeKind.Utc);
        var marker = $"FRESH-{Guid.NewGuid():N}";

        await using (var setup = CreateContext())
        {
            var item = new NotificationOutboxItem(
                "test.fresh", $"Тема {marker}", "Текст", "ops@example.local", claimedAt);
            setup.NotificationOutbox.Add(item);
            await setup.SaveChangesAsync();

            await setup.NotificationOutbox
                .Where(n => n.Id == item.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(n => n.State, "Sending")
                    .SetProperty(n => n.ClaimedAt, claimedAt)
                    .SetProperty(n => n.ClaimToken, (Guid?)Guid.NewGuid()));
        }

        // ⚠ Ще ВСЕРЕДИНІ вікна захоплення (на секунду коротше за межу) —
        // інший флешер, що взяв цей рядок хвилину тому, МІГ би ще
        // відправляти. Повертати його в чергу тут означало б справжній
        // дубль, а не рятувати від зависання.
        var now = claimedAt + OutboxDispatcher.ClaimTimeout - TimeSpan.FromSeconds(1);
        var clock = new TestClock(now);

        var sender = Substitute.For<INotificationSender>();
        sender.IsConfigured.Returns(true);

        await using var db = CreateContext();
        var dispatcher = new OutboxDispatcher(db, clock, sender);

        var (sent, _) = await dispatcher.FlushAsync(CancellationToken.None);

        Assert.Equal(0, sent);

        await sender.DidNotReceive().SendAsync(
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
