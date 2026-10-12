using Ecr.Application.Ports;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>Кеш знімка фактів здоров'я: TTL, одноразове обчислення, старе значення при збої.</summary>
public sealed class HealthSnapshotCacheTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    private static SystemHealthSnapshot Snap(int running)
        => new(new HealthJobCounts(running, 0, 0), new HealthSourceCounts(0, 0, 0), null, null, null);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Свіжий_знімок_не_перераховується_а_прострочений_так()
    {
        var cache = new HealthCountCache();
        var calls = 0;
        Task<SystemHealthSnapshot> Compute(CancellationToken _) => Task.FromResult(Snap(++calls));

        var a = await cache.GetOrComputeSnapshotAsync(Now, Compute, CancellationToken.None);
        var b = await cache.GetOrComputeSnapshotAsync(Now.AddSeconds(10), Compute, CancellationToken.None);
        var c = await cache.GetOrComputeSnapshotAsync(Now.AddSeconds(31), Compute, CancellationToken.None);

        Assert.Equal(1, a.Jobs.Running);
        Assert.Equal(1, b.Jobs.Running);
        Assert.Equal(2, c.Jobs.Running);
        Assert.Equal(2, calls);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Збій_обчислення_віддає_попередній_знімок_а_без_нього_кидає()
    {
        var cache = new HealthCountCache();
        Task<SystemHealthSnapshot> Fail(CancellationToken _) => throw new InvalidOperationException("x");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => cache.GetOrComputeSnapshotAsync(Now, Fail, CancellationToken.None));

        _ = await cache.GetOrComputeSnapshotAsync(Now, _ => Task.FromResult(Snap(7)), CancellationToken.None);
        var stale = await cache.GetOrComputeSnapshotAsync(Now.AddMinutes(4), Fail, CancellationToken.None);

        Assert.Equal(7, stale.Jobs.Running);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Знімок_старший_за_межу_при_збої_не_віддається()
    {
        // Мутація: прибрати перевірку MaxStaleOnFailure - тест червоний (віддається 7-хвилинний знімок).
        var cache = new HealthCountCache();
        Task<SystemHealthSnapshot> Fail(CancellationToken _) => throw new InvalidOperationException("x");
        _ = await cache.GetOrComputeSnapshotAsync(Now, _ => Task.FromResult(Snap(7)), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => cache.GetOrComputeSnapshotAsync(
                Now + HealthCountCache.MaxStaleOnFailure + TimeSpan.FromSeconds(1), Fail, CancellationToken.None));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Скасування_викликача_не_підміняється_старим_знімком()
    {
        var cache = new HealthCountCache();
        _ = await cache.GetOrComputeSnapshotAsync(Now, _ => Task.FromResult(Snap(3)), CancellationToken.None);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cache.GetOrComputeSnapshotAsync(Now.AddMinutes(5), _ => Task.FromResult(Snap(4)), cts.Token));
    }
}
