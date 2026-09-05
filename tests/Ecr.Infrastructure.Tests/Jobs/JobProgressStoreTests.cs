using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
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
}
