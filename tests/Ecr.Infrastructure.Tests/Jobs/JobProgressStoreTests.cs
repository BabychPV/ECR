using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Життєвий цикл стану фонової задачі в <c>itg.JobProgress</c>.
/// </summary>
/// <remarks>
/// ⚠ Стан живе в БАЗІ, а не в пам'яті планувальника. Інстансів застосунку
/// кілька, і клієнт, що опитує прогрес, потрапляє не обов'язково на той, який
/// задачу виконує: стан у пам'яті відповів би «немає такої».
/// </remarks>
[Collection("SqlServer")]
public sealed class JobProgressStoreTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 5, 1, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-14.8")]
    public async Task Задача_видима_одразу_після_постановки_а_не_лише_після_старту()
    {
        // ⚠ Саме цей розрив ловить тест: клієнт отримує 202 з jobId і одразу
        // питає стан. Без запису при постановці він отримав би 404 на задачу,
        // яку щойно прийняли, і вирішив би, що вона загубилася.
        await using var db = sql.CreateContext();
        var store = new JobProgressStore(db);
        var jobId = $"queued-{Guid.NewGuid():N}";

        await store.QueueAsync(jobId, "excel-export", Now, CancellationToken.None);

        var status = await store.FindAsync(jobId, CancellationToken.None);

        Assert.NotNull(status);
        Assert.Equal("Queued", status.State);
        Assert.Equal(0, status.Percent);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-12.7")]
    public async Task Постановка_старт_прогрес_і_завершення_дають_один_запис_а_не_чотири()
    {
        await using var db = sql.CreateContext();
        var store = new JobProgressStore(db);
        var jobId = $"cycle-{Guid.NewGuid():N}";

        await store.QueueAsync(jobId, "collection", Now, CancellationToken.None);
        await store.StartAsync(jobId, "collection", Now.AddSeconds(1), CancellationToken.None);
        await store.ReportAsync(jobId, 40, "Читання", Now.AddSeconds(2), CancellationToken.None);
        await store.FinishAsync(jobId, "Succeeded", null, Now.AddSeconds(3), CancellationToken.None);

        var status = await store.FindAsync(jobId, CancellationToken.None);

        Assert.NotNull(status);
        Assert.Equal("Succeeded", status.State);
        Assert.Equal(100, status.Percent);
        Assert.Null(status.Error);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-12.4")]
    public async Task Провал_зберігає_текст_помилки_і_НЕ_виставляє_сто_відсотків()
    {
        // Задача, яка впала на сорока відсотках і показує сто, читається як
        // успішна — і її результату шукають там, де його немає.
        await using var db = sql.CreateContext();
        var store = new JobProgressStore(db);
        var jobId = $"failed-{Guid.NewGuid():N}";

        await store.StartAsync(jobId, "report-snapshot", Now, CancellationToken.None);
        await store.ReportAsync(jobId, 40, "Побудова", Now.AddSeconds(1), CancellationToken.None);
        await store.FinishAsync(jobId, "Failed", "Джерело недоступне", Now.AddSeconds(2), CancellationToken.None);

        var status = await store.FindAsync(jobId, CancellationToken.None);

        Assert.NotNull(status);
        Assert.Equal("Failed", status.State);
        Assert.Equal(40, status.Percent);
        Assert.Equal("Джерело недоступне", status.Error);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Стан_невідомої_задачі_це_відсутність_а_не_порожній_запис()
    {
        await using var db = sql.CreateContext();
        var store = new JobProgressStore(db);

        Assert.Null(await store.FindAsync($"missing-{Guid.NewGuid():N}", CancellationToken.None));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-12.7")]
    public async Task Прибирання_на_старті_валить_покинуті_задачі_і_НЕ_чіпає_живі_чужого_інстанса()
    {
        // ⛔ Саме цей дефект ловить тест. `FailStaleAsync` не мала предиката
        // застарілості й валила КОЖНУ задачу в стані `Running`/`Queued` — а
        // інстанс у розгортанні не один (ціль — 100 одночасних користувачів).
        // Перезапуск інстанса B позначав `Failed` перерахунки, експорти й
        // імпорти, які в цю саму мить виконував інстанс A: користувач бачив
        // провал задачі, яка насправді успішно доробила до кінця.
        //
        // Розрізняє їх биття серця: живу задачу веде процес, що її виконує, і
        // він оновлює `HeartbeatAt`; задачу процесу, який упав, не оновлює
        // ніхто, і її биття застигає.
        await using var db = sql.CreateContext();
        var store = new JobProgressStore(db);

        var alive = $"alive-{Guid.NewGuid():N}";
        var abandoned = $"abandoned-{Guid.NewGuid():N}";

        // Задача «чужого» інстанса, що ЗАРАЗ виконується: биття свіже.
        await store.StartAsync(alive, "recalculation", Now, CancellationToken.None);

        // Задача процесу, який упав годину тому: биття застигло тоді ж.
        await store.StartAsync(abandoned, "excel-import", Now.AddHours(-1), CancellationToken.None);

        var failed = await store.FailStaleAsync(
            "Застосунок перезапущено.", Now.AddMinutes(1), CancellationToken.None);

        var aliveStatus = await store.FindAsync(alive, CancellationToken.None);
        var abandonedStatus = await store.FindAsync(abandoned, CancellationToken.None);

        Assert.NotNull(aliveStatus);
        Assert.NotNull(abandonedStatus);

        // Жива задача переживає перезапуск СУСІДНЬОГО інстанса.
        Assert.Equal("Running", aliveStatus.State);
        Assert.Null(aliveStatus.Error);

        // Покинута — таки валиться: заради цього метод і існує.
        Assert.Equal("Failed", abandonedStatus.State);
        Assert.Equal("Застосунок перезапущено.", abandonedStatus.Error);

        // ⚠ Лічильник теж мусить бути чесним: він іде в лог старту, і
        // завищене число означало б розслідування задач, яких не валили.
        Assert.True(failed >= 1, $"Очікували щонайменше одну покинуту задачу, отримали {failed}.");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-12.7")]
    public async Task Задача_що_стартувала_ПІД_ЧАС_прибирання_не_валиться()
    {
        // ⚠ Нова межа, яку легко проґавити, лагодячи попередню: задача, яку
        // інстанс A щойно поставив у чергу й запустив, поки інстанс B уже
        // рахує, кого валити. Її биття молодше за поріг — і предикат мусить
        // це бачити, інакше виправлення просто звужує вікно замість закрити.
        await using var db = sql.CreateContext();
        var store = new JobProgressStore(db);

        var justStarted = $"justborn-{Guid.NewGuid():N}";
        var sweepAt = Now.AddMinutes(1);

        await store.QueueAsync(justStarted, "excel-export", sweepAt, CancellationToken.None);
        await store.StartAsync(justStarted, "excel-export", sweepAt, CancellationToken.None);

        await store.FailStaleAsync("Застосунок перезапущено.", sweepAt, CancellationToken.None);

        var status = await store.FindAsync(justStarted, CancellationToken.None);

        Assert.NotNull(status);
        Assert.Equal("Running", status.State);
        Assert.Null(status.Error);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-12.7")]
    public async Task Биття_серця_рятує_довгу_мовчазну_задачу_від_прибирання()
    {
        // ⛔ Без цього биття виправлення нагородило б новим дефектом замість
        // старого: довгий імпорт, який годину не повідомляє відсотків, мав би
        // застигле `HeartbeatAt` і його зачистили б як покинутий. Биття веде
        // процес-власник (`QuartzJobAdapter`), і воно НЕ прогрес: `UpdatedAt`
        // і `Percent` воно не чіпає, інакше стрічка «останні задачі»
        // перетасовувалася б від самого факту, що задача ще жива.
        await using var db = sql.CreateContext();
        var store = new JobProgressStore(db);

        var slow = $"slow-{Guid.NewGuid():N}";

        await store.StartAsync(slow, "excel-import", Now.AddHours(-1), CancellationToken.None);
        await store.ReportAsync(slow, 30, "Читання", Now.AddHours(-1), CancellationToken.None);

        // Власник живий і б'є — рівно перед прибиранням.
        await store.HeartbeatAsync(slow, Now.AddMinutes(1), CancellationToken.None);

        await store.FailStaleAsync(
            "Застосунок перезапущено.", Now.AddMinutes(1), CancellationToken.None);

        var status = await store.FindAsync(slow, CancellationToken.None);

        Assert.NotNull(status);
        Assert.Equal("Running", status.State);

        // Биття — не прогрес: відсоток лишився тим, що повідомила сама задача.
        Assert.Equal(30, status.Percent);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-12.7")]
    public async Task Биття_НЕ_воскрешає_вже_завершену_задачу()
    {
        // ⚠ Насос биття зупиняється у `finally`, тобто вже після того, як
        // гілка встигла записати завершення. Удар, що розминувся з `Finish`
        // на мілісекунди, не сміє повернути задачі ознаку життя: інакше
        // завершена задача виглядала б живою для будь-якої майбутньої
        // перевірки за биттям.
        await using var db = sql.CreateContext();
        var store = new JobProgressStore(db);

        var done = $"done-{Guid.NewGuid():N}";

        await store.StartAsync(done, "collection", Now.AddHours(-1), CancellationToken.None);
        await store.FinishAsync(done, "Succeeded", null, Now.AddHours(-1), CancellationToken.None);

        await store.HeartbeatAsync(done, Now.AddMinutes(1), CancellationToken.None);

        var heartbeat = await db.JobProgresses
            .AsNoTracking()
            .Where(p => p.JobId == done)
            .Select(p => p.HeartbeatAt)
            .SingleAsync(CancellationToken.None);

        Assert.Equal(Now.AddHours(-1), heartbeat);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "BE-08")]
    public async Task Спроба_і_кореляція_зберігаються_і_читаються_обома_шляхами()
    {
        await using var db = sql.CreateContext();
        var store = new JobProgressStore(db);
        var jobId = $"be08-{Guid.NewGuid():N}";
        var code = $"be08-{Guid.NewGuid():N}"[..20];

        await store.QueueAsync(jobId, code, Now, CancellationToken.None, correlationId: "req-be08");

        // До старту спроби немає — «0» чи «1» тут були б вигадкою.
        var queued = await store.FindAsync(jobId, CancellationToken.None);
        Assert.Null(queued!.Attempt);
        Assert.Equal("req-be08", queued.CorrelationId);

        await store.StartAsync(jobId, code, Now.AddSeconds(1), CancellationToken.None, attempt: 2);

        // Старт без кореляції не стирає ту, що прийшла з постановки.
        var status = await store.FindAsync(jobId, CancellationToken.None);
        Assert.Equal(2, status!.Attempt);
        Assert.Equal("req-be08", status.CorrelationId);

        var listed = Assert.Single(await store.ListRecentAsync(
            new Ecr.Application.Ports.JobListFilter(JobCode: code), 5, CancellationToken.None));
        Assert.Equal(2, listed.Attempt);
        Assert.Equal("req-be08", listed.CorrelationId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "BE-08")]
    public async Task Стан_несе_стелю_спроб_а_перелік_автора_і_повідомлення()
    {
        await using var db = sql.CreateContext();
        var store = new JobProgressStore(db);
        var code = $"be08m-{Guid.NewGuid():N}"[..20];

        var name = $"be08_{Guid.NewGuid():N}"[..20];
        var user = new Ecr.Domain.Entities.Security.User(name, $"Author {name}", Ecr.Domain.Enums.AuthProvider.Local);
        user.SetPassword("hash"); // CK_User_Provider: локальному користувачу потрібен хеш.
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var authored = $"be08a-{Guid.NewGuid():N}";
        var system = $"be08s-{Guid.NewGuid():N}";
        await store.QueueAsync(authored, code, Now, CancellationToken.None, createdByUserId: user.Id);
        await store.QueueAsync(system, code, Now.AddSeconds(1), CancellationToken.None);
        await store.ReportAsync(authored, 10, "phase-1", Now.AddSeconds(2), CancellationToken.None);

        // Перша спроба + три ретраї QuartzJobAdapter — число літералом, не з константи.
        Assert.Equal(4, (await store.FindAsync(authored, CancellationToken.None))!.MaxAttempts);

        var listed = (await store.ListRecentAsync(
                new Ecr.Application.Ports.JobListFilter(JobCode: code), 5, CancellationToken.None))
            .ToDictionary(j => j.JobId, StringComparer.Ordinal);

        Assert.Equal($"Author {name}", listed[authored].CreatedByDisplayName);
        Assert.Equal("phase-1", listed[authored].Message);
        Assert.Null(listed[system].CreatedByDisplayName);
        Assert.Null(listed[system].Message);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "BE-08")]
    public async Task Постановка_документ_і_код_провалу_переживають_старт_і_перезапуск()
    {
        await using var db = sql.CreateContext();
        var store = new JobProgressStore(db);
        var code = $"be08c-{Guid.NewGuid():N}"[..20];
        var doc = $"be08d-{Guid.NewGuid():N}";
        var bare = $"be08b-{Guid.NewGuid():N}";

        await store.QueueAsync(doc, code, Now, CancellationToken.None, documentId: 4242);
        await store.StartAsync(doc, code, Now.AddMinutes(1), CancellationToken.None);
        await store.FinishAsync(doc, "Failed", "boom", Now.AddMinutes(2), CancellationToken.None, "ECR-SYS-0500");

        // Задача за розкладом: рядок з'являється на старті, постановки не було.
        await store.StartAsync(bare, code, Now.AddMinutes(3), CancellationToken.None);

        var failed = await store.FindAsync(doc, CancellationToken.None);
        Assert.Equal(Now, failed!.CreatedAt); // не момент старту (Now + 1 хв)
        Assert.Equal(4242L, failed.DocumentId);
        Assert.Equal("ECR-SYS-0500", failed.ErrorCode);

        var listed = (await store.ListRecentAsync(
                new Ecr.Application.Ports.JobListFilter(JobCode: code), 5, CancellationToken.None))
            .ToDictionary(j => j.JobId, StringComparer.Ordinal);
        Assert.Equal(Now, listed[doc].CreatedAt);
        Assert.Equal("ECR-SYS-0500", listed[doc].ErrorCode);
        Assert.Equal(4242L, listed[doc].DocumentId);
        Assert.Null(listed[bare].CreatedAt);
        Assert.Null(listed[bare].ErrorCode);
        Assert.Null(listed[bare].DocumentId);

        // Ручний перезапуск: код провалу знято, постановка й документ — ті самі.
        await store.RestartAsync(doc, Now.AddMinutes(5), CancellationToken.None);
        var restarted = await store.FindAsync(doc, CancellationToken.None);
        Assert.Null(restarted!.ErrorCode);
        Assert.Equal(Now, restarted.CreatedAt);
        Assert.Equal(4242L, restarted.DocumentId);
    }
}
