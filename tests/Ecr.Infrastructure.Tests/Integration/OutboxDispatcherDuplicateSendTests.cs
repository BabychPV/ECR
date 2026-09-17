// tests/Ecr.Infrastructure.Tests/Integration/OutboxDispatcherDuplicateSendTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Integration;
using Ecr.Infrastructure.Integration;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Integration;

/// <summary>
/// Q-241 — продовження: захоплення партії вже атомарне, але **доставлений**
/// лист усе ще міг піти вдруге.
/// </summary>
/// <remarks>
/// ⛔ Три причини, кожна з яких сама по собі дає дубль уже доставленого листа:
/// <list type="number">
/// <item>відправка йде ПЕРЕД фіксацією: <c>MarkSent</c> лише міняв сутність у
/// пам'яті, а <c>SaveChangesAsync</c> був ОДИН на всю партію (до 200 подій) —
/// процес, зупинений посеред партії, лишав усі вже доставлені листи в стані
/// <c>Sending</c>, і наступний прогін після спливання оренди надсилав їх
/// знову;</item>
/// <item>оренда захоплення (10 хв) КОРОТША за найгіршу тривалість партії
/// (200 подій × 30 с таймауту SMTP ≈ 100 хв) — тобто відправник штатно
/// продовжував слати листи вже ПІСЛЯ того, як його захоплення протухло і
/// будь-хто інший мав право забрати ті самі рядки;</item>
/// <item>запис результату нічим не звірявся з захопленням: протухлий
/// відправник затирав своїм <c>SaveChangesAsync</c> свіже захоплення того,
/// хто вже забрав рядок.</item>
/// </list>
///
/// ⚠ Доставка пошти НЕ буває «рівно один раз»: якщо процес обірвано між
/// «SMTP прийняв» і «позначено Sent», один дубль лишається можливим завжди
/// (див. <see cref="Другий_диспетчер_дублює_лише_той_лист_що_саме_в_дорозі"/>).
/// Тести нижче стверджують рівно те, що досяжне, і НЕ більше.
///
/// ⚠ База спільна на всю колекцію <c>[Collection("SqlServer")]</c>, а партія
/// береться з усієї черги: кожен тест спершу прибирає чужі незавершені рядки,
/// інакше вони зсунули б і порядок партії, і її тривалість.
/// </remarks>
[Collection("SqlServer")]
public sealed class OutboxDispatcherDuplicateSendTests(SqlServerFixture sql)
{
    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    /// <summary>Відправник, яким керує сам тест; рахує лише УСПІШНІ доставки.</summary>
    private sealed class ScriptedSender(TestClock clock, Func<string, Task> onSend) : INotificationSender
    {
        /// <summary>Теми доставлених листів і момент доставки.</summary>
        public List<(string Subject, DateTime At)> Delivered { get; } = [];

        public bool IsConfigured => true;

        public async Task SendAsync(
            IReadOnlyList<string> recipients, string subject, string body, CancellationToken ct)
        {
            // ⚠ Сценарій виконується ДО запису в Delivered: якщо він кидає
            // виняток, доставки не було — рівно як обірваний SMTP-виклик.
            await onSend(subject).ConfigureAwait(false);

            Delivered.Add((subject, clock.UtcNow));
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "Q-241")]
    public async Task Обірваний_посеред_партії_флешер_не_надсилає_доставлене_вдруге()
    {
        var start = new DateTime(2035, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var clock = new TestClock(start);
        var (first, second, third) = await SeedThreeAsync(start).ConfigureAwait(true);

        var crashed = false;
        var sender = new ScriptedSender(clock, subject =>
        {
            // Кожна доставка «повільна», але в межах оренди.
            clock.Advance(TimeSpan.FromMinutes(1));

            // ⛔ Застосунок зупиняють посеред партії: два перші листи вже
            // доставлені, третій — ні. FlushAsync проштовхує
            // OperationCanceledException нагору, тому єдиний SaveChangesAsync
            // у кінці партії не виконувався ніколи.
            if (!crashed && subject.Contains(third, StringComparison.Ordinal))
            {
                crashed = true;
                throw new OperationCanceledException("Зупинка застосунку посеред партії.");
            }

            return Task.CompletedTask;
        });

        await using (var db = CreateContext())
        {
            var dispatcher = new OutboxDispatcher(db, clock, sender);

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => dispatcher.FlushAsync(CancellationToken.None)).ConfigureAwait(true);
        }

        // Процес підняли пізніше — оренда захоплення встигла спливти.
        clock.Set(start + OutboxDispatcher.ClaimTimeout + TimeSpan.FromMinutes(10));

        await using (var db = CreateContext())
        {
            await new OutboxDispatcher(db, clock, sender)
                .FlushAsync(CancellationToken.None)
                .ConfigureAwait(true);
        }

        // ⛔ Саме тут падала стара реалізація: перші два листи вже були в
        // поштових скриньках, але в базі лишалися Sending — і другий прогін
        // надсилав їх удруге.
        Assert.Equal(1, DeliveredCount(sender, first));
        Assert.Equal(1, DeliveredCount(sender, second));

        // Третій не був доставлений жодного разу до обриву — рівно один раз
        // після відновлення.
        Assert.Equal(1, DeliveredCount(sender, third));

        await AssertStateAsync(first, "Sent").ConfigureAwait(true);
        await AssertStateAsync(second, "Sent").ConfigureAwait(true);
        await AssertStateAsync(third, "Sent").ConfigureAwait(true);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "Q-241")]
    public async Task Жоден_лист_не_йде_під_протухлим_захопленням()
    {
        var start = new DateTime(2035, 2, 2, 0, 0, 0, DateTimeKind.Utc);
        var clock = new TestClock(start);
        var (first, second, third) = await SeedThreeAsync(start).ConfigureAwait(true);

        // Чотири хвилини на лист — «повільний адресат». Три таких листи це
        // 12 хвилин, тобто ДОВШЕ за десятихвилинну оренду захоплення.
        var sender = new ScriptedSender(clock, _ =>
        {
            clock.Advance(TimeSpan.FromMinutes(4));

            return Task.CompletedTask;
        });

        await using (var db = CreateContext())
        {
            await new OutboxDispatcher(db, clock, sender)
                .FlushAsync(CancellationToken.None)
                .ConfigureAwait(true);
        }

        // ⛔ Інваріант, а не число: доставка, що завершилась ПІСЛЯ спливання
        // власного захоплення, — це лист, надісланий рядком, який у цей момент
        // мав право забрати будь-який інший відправник. Стара реалізація
        // доставляла третій лист на 12-й хвилині оренди завдовжки 10 хвилин.
        var lease = start + OutboxDispatcher.ClaimTimeout;
        Assert.All(sender.Delivered, d => Assert.True(
            d.At <= lease,
            $"Лист '{d.Subject}' доставлено о {d.At:O} — після спливання захоплення о {lease:O}."));

        Assert.Equal(1, DeliveredCount(sender, first));
        Assert.Equal(1, DeliveredCount(sender, second));

        // Те, на що бюджету партії не вистачило, повертається в чергу
        // НЕГАЙНО — а не висить у Sending до спливання оренди.
        Assert.Equal(0, DeliveredCount(sender, third));
        await AssertStateAsync(third, "Pending").ConfigureAwait(true);
        await AssertUnclaimedAsync(third).ConfigureAwait(true);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "Q-241")]
    public async Task Другий_диспетчер_дублює_лише_той_лист_що_саме_в_дорозі()
    {
        var start = new DateTime(2035, 3, 3, 0, 0, 0, DateTimeKind.Utc);
        var clock = new TestClock(start);
        var (first, second, third) = await SeedThreeAsync(start).ConfigureAwait(true);

        await using var dbA = CreateContext();
        await using var dbB = CreateContext();

        var intruded = false;
        ScriptedSender? sender = null;
        sender = new ScriptedSender(clock, async subject =>
        {
            if (intruded || !subject.Contains(first, StringComparison.Ordinal))
            {
                return;
            }

            // ⛔ Перший лист «висить» у SMTP довше за оренду — і рівно в цей
            // момент стартує другий диспетчер. Виклик вкладений і послідовний
            // навмисно: гонка тут не потрібна, потрібен точний порядок подій.
            intruded = true;
            clock.Advance(OutboxDispatcher.ClaimTimeout + TimeSpan.FromMinutes(1));

            await new OutboxDispatcher(dbB, clock, sender!)
                .FlushAsync(CancellationToken.None)
                .ConfigureAwait(false);
        });

        await new OutboxDispatcher(dbA, clock, sender)
            .FlushAsync(CancellationToken.None)
            .ConfigureAwait(true);

        // ⚠ ЦЕ — межа можливого, а не недогляд. Перший лист SMTP уже прийняв,
        // коли його захоплення протухло і другий диспетчер забрав рядок:
        // скасувати вже прийнятий лист не може ніхто, тож дубль саме тут
        // невідворотний. Черга сповіщень — at-least-once, і тест це фіксує
        // явно, а не ховає.
        Assert.Equal(2, DeliveredCount(sender, first));

        // ⛔ А ось це вже виправна частина: другий і третій листи НЕ були в
        // дорозі. Стара реалізація надсилала їх двічі — спершу другий
        // диспетчер (рядки протухли й повернулися в чергу), потім перший, що
        // продовжував партію, не помічаючи втрати захоплення.
        Assert.Equal(1, DeliveredCount(sender, second));
        Assert.Equal(1, DeliveredCount(sender, third));
    }

    private static int DeliveredCount(ScriptedSender sender, string marker)
        => sender.Delivered.Count(d => d.Subject.Contains(marker, StringComparison.Ordinal));

    /// <summary>Кладе три події з РІЗНИМ <c>CreatedAt</c> — порядок партії має бути визначеним.</summary>
    private async Task<(string First, string Second, string Third)> SeedThreeAsync(DateTime start)
    {
        var markers = (
            First: $"D1-{Guid.NewGuid():N}",
            Second: $"D2-{Guid.NewGuid():N}",
            Third: $"D3-{Guid.NewGuid():N}");

        await using var db = CreateContext();

        // ⚠ Чужі незавершені рядки з попередніх тестів колекції потрапили б у
        // ту саму партію.
        await db.NotificationOutbox
            .Where(n => n.State == "Pending" || n.State == "Sending")
            .ExecuteDeleteAsync()
            .ConfigureAwait(false);

        db.NotificationOutbox.AddRange(
            new NotificationOutboxItem("test.dup", $"Тема {markers.First}", "Текст", "ops@example.local", start),
            new NotificationOutboxItem(
                "test.dup", $"Тема {markers.Second}", "Текст", "ops@example.local", start.AddSeconds(1)),
            new NotificationOutboxItem(
                "test.dup", $"Тема {markers.Third}", "Текст", "ops@example.local", start.AddSeconds(2)));

        await db.SaveChangesAsync().ConfigureAwait(false);

        return markers;
    }

    private async Task AssertStateAsync(string marker, string expected)
    {
        await using var db = CreateContext();

        var state = await db.NotificationOutbox
            .AsNoTracking()
            .Where(n => n.Subject.Contains(marker))
            .Select(n => n.State)
            .SingleAsync()
            .ConfigureAwait(false);

        Assert.Equal(expected, state);
    }

    private async Task AssertUnclaimedAsync(string marker)
    {
        await using var db = CreateContext();

        var row = await db.NotificationOutbox
            .AsNoTracking()
            .Where(n => n.Subject.Contains(marker))
            .Select(n => new { n.ClaimedAt, n.ClaimToken })
            .SingleAsync()
            .ConfigureAwait(false);

        Assert.Null(row.ClaimedAt);
        Assert.Null(row.ClaimToken);
    }
}
