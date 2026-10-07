using Ecr.Domain.Entities.External;
using Ecr.Domain.Entities.Integration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// <c>sources.failed</c> (UI-39): семантика «останній запуск сутності — провал» після переписи
/// <c>NOT EXISTS</c> → <c>ROW_NUMBER</c>, і кеш результату на 60 с.
/// </summary>
[Collection("SqlServer")]
public sealed class SystemHealthFailedSourcesTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Лічиться_лише_активне_джерело_з_провалом_як_останнім_запуском_і_один_раз()
    {
        var created = new List<int>();
        try
        {
            await using var db = sql.CreateContext();
            var before = await FailedAsync(db, new HealthCountCache(), Now);

            // A: активне; дві сутності, обидві провалені → одне джерело, рахується ОДИН раз.
            var a = await SourceAsync(db, created, active: true);
            await RunsAsync(db, a.E1, ("Failed", -3));
            await RunsAsync(db, a.E2, ("Failed", -2));

            // B: активне; провал СТАРІШИЙ за успіх → не рахується.
            var b = await SourceAsync(db, created, active: true);
            await RunsAsync(db, b.E1, ("Failed", -5), ("Succeeded", -1));

            // C: НЕактивне; останній запуск — провал → не рахується.
            var c = await SourceAsync(db, created, active: false);
            await RunsAsync(db, c.E1, ("Failed", -1));

            var after = await FailedAsync(db, new HealthCountCache(), Now);

            Assert.Equal(1, after - before);
        }
        finally
        {
            await CleanAsync(created);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Провал_старший_за_успіх_іншої_сутності_того_ж_джерела_усе_одно_рахується()
    {
        var created = new List<int>();
        try
        {
            await using var db = sql.CreateContext();
            var before = await FailedAsync(db, new HealthCountCache(), Now);

            // Сутність 1 одужала, сутність 2 лежить — джерело «з помилкою».
            var s = await SourceAsync(db, created, active: true);
            await RunsAsync(db, s.E1, ("Failed", -5), ("Succeeded", -1));
            await RunsAsync(db, s.E2, ("Succeeded", -5), ("Failed", -2));

            Assert.Equal(1, await FailedAsync(db, new HealthCountCache(), Now) - before);
        }
        finally
        {
            await CleanAsync(created);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Кеш_тримає_число_до_60_с_і_оновлює_після()
    {
        var created = new List<int>();
        try
        {
            await using var db = sql.CreateContext();
            var cache = new HealthCountCache();

            var first = await FailedAsync(db, cache, Now);

            var s = await SourceAsync(db, created, active: true);
            await RunsAsync(db, s.E1, ("Failed", -1));

            // У межах 60 с — старе число, запиту до БД немає.
            Assert.Equal(first, await FailedAsync(db, cache, Now.AddSeconds(59)));

            // Після 60 с — нове.
            Assert.Equal(first + 1, await FailedAsync(db, cache, Now.AddSeconds(61)));
        }
        finally
        {
            await CleanAsync(created);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Кеш_рахує_обчислення_і_не_запам_ятовує_виняток()
    {
        var cache = new HealthCountCache();
        var calls = 0;

        Task<int> Compute(CancellationToken _)
        {
            calls++;
            return Task.FromResult(7);
        }

        Assert.Equal(7, await cache.GetOrComputeAsync(Now, Compute, CancellationToken.None));
        Assert.Equal(7, await cache.GetOrComputeAsync(Now.AddSeconds(59), Compute, CancellationToken.None));
        Assert.Equal(1, calls);

        await cache.GetOrComputeAsync(Now.AddSeconds(60), Compute, CancellationToken.None);
        Assert.Equal(2, calls);

        var failing = new HealthCountCache();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => failing.GetOrComputeAsync(Now, _ => throw new InvalidOperationException("boom"), CancellationToken.None));
        Assert.Equal(5, await failing.GetOrComputeAsync(Now, _ => Task.FromResult(5), CancellationToken.None));
    }

    private static async Task<int> FailedAsync(EcrDbContext db, HealthCountCache cache, DateTime now)
    {
        var store = new SystemHealthStore(db, NullLogger<SystemHealthStore>.Instance, null, cache);
        var snapshot = await store.ReadAsync(now, CancellationToken.None);

        return snapshot.Sources.Failed;
    }

    private static async Task<(int Id, int E1, int E2)> SourceAsync(EcrDbContext db, List<int> created, bool active)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var source = new DataSource(
            EcrCode.Create($"Fs{tag}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Failed sources" }),
            ExternalTransport.PiWebApi,
            "https://example.test",
            "secret");
        if (!active)
        {
            source.Deactivate();
        }

        db.DataSources.Add(source);
        await db.SaveChangesAsync();
        created.Add(source.Id);

        var e1 = new SourceEntity(source.Id, $"Fs{tag}a", RegistrySourceKind.External);
        var e2 = new SourceEntity(source.Id, $"Fs{tag}b", RegistrySourceKind.External);
        db.SourceEntities.AddRange(e1, e2);
        await db.SaveChangesAsync();

        return (source.Id, e1.Id, e2.Id);
    }

    private static async Task RunsAsync(EcrDbContext db, int entityId, params (string Status, int HoursAgo)[] runs)
    {
        foreach (var (status, hoursAgo) in runs)
        {
            // Запуски датовані відносно реального «зараз», бо вставка йде в живу таблицю.
            var started = DateTime.UtcNow.AddHours(hoursAgo);
            var run = new CollectionRun(entityId, started.AddHours(-1), started, false, null, started);
            run.Complete(status, 0, started.AddMinutes(1), status == "Failed" ? "boom" : null);
            db.CollectionRuns.Add(run);
        }

        await db.SaveChangesAsync();
    }

    private async Task CleanAsync(List<int> dataSourceIds)
    {
        await using var db = sql.CreateContext();
        foreach (var id in dataSourceIds)
        {
            var entities = db.SourceEntities.Where(e => e.DataSourceId == id).Select(e => e.Id);
            await db.CollectionRuns.Where(r => entities.Contains(r.SourceEntityId)).ExecuteDeleteAsync();
            await db.SourceEntities.Where(e => e.DataSourceId == id).ExecuteDeleteAsync();
            await db.DataSources.Where(s => s.Id == id).ExecuteDeleteAsync();
        }
    }
}
