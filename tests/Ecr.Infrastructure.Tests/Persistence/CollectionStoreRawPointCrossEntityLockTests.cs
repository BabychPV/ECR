// tests/Ecr.Infrastructure.Tests/Persistence/CollectionStoreRawPointCrossEntityLockTests.cs
using System.Data;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Запис сирих точок РІЗНИХ сутностей не йде паралельно в <c>UQ_RawDataPoint</c> (RC19 P3-1: 1205 у
/// <c>CollectionStore.UpsertRawPointsAsync</c> при двох паралельних перших зборах Hours і Fuel).
/// </summary>
/// <remarks>
/// ⛔ Докази. (1) <c>Запис_іншої_сутності_чекає_лок_таблиці_а_не_йде_паралельно</c> детермінований:
/// сторонній утримувач ресурсу <c>Ecr.RawDataPoint</c> блокує запис ІНШОЇ сутності.
/// На коді до виправлення (лок на сутність) запис проходив одразу — тест червоний. (2) Гонка двох сутностей на
/// бар'єрі — страховка від повернення паралельного запису; сама по собі 1205 вона відтворює не завжди (залежить
/// від плану й розкладу сторінок), тому першим доказом є (1).
/// </remarks>
[Collection("SqlServer")]
public sealed class CollectionStoreRawPointCrossEntityLockTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Ресурс <c>sp_getapplock</c> запису сирих точок (таблиця, а не сутність).</summary>
    private const string LockResource = "Ecr.RawDataPoint";

    private const string PathHours = "Stack|Hours";
    private const string PathFuel = "Stack|Fuel";

    [Fact(Timeout = 120_000)]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P3-1")]
    public async Task Запис_іншої_сутності_чекає_лок_таблиці_а_не_йде_паралельно()
    {
        await using var arrange = sql.CreateContext();
        var entityId = await ArrangeEntityAsync(arrange);

        await using var holder = new SqlConnection(sql.ConnectionString);
        await holder.OpenAsync();
        await using var tx = (SqlTransaction)await holder.BeginTransactionAsync();

        try
        {
            await using (var take = holder.CreateCommand())
            {
                take.Transaction = tx;
                take.CommandText =
                    "DECLARE @r int; EXEC @r = sp_getapplock @Resource = @res, @LockMode = 'Exclusive', "
                    + "@LockOwner = 'Transaction', @LockTimeout = 5000; IF @r < 0 THROW 50001, N'holder', 1;";
                take.Parameters.Add(new SqlParameter("@res", SqlDbType.NVarChar, 255) { Value = LockResource });
                await take.ExecuteNonQueryAsync();
            }

            await using var db = sql.CreateContext();
            var store = new CollectionStore(db, new TestClock(Now));
            var runId = await store.StartRunAsync(entityId, Now.AddDays(-1), Now, false, null, CancellationToken.None);

            var write = store.UpsertRawPointsAsync(
                runId, entityId, [new SourceDataPoint(PathFuel, Now.AddHours(-2), 3m, null, null, "Good")], CancellationToken.None);

            // Лок тримає чужа транзакція: запис мусить стояти в черзі, а не пройти повз неї.
            var finished = await Task.WhenAny(write, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.NotSame(write, finished);

            await tx.RollbackAsync();

            Assert.Equal(1, await write.WaitAsync(TimeSpan.FromSeconds(60)));
        }
        finally
        {
            await CleanUpAsync(entityId);
        }
    }

    [Fact(Timeout = 300_000)]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P3-1")]
    public async Task Два_паралельні_записи_різних_сутностей_на_бар_єрі_не_дають_1205()
    {
        const int Iterations = 20;
        const int PointsPerBatch = 120;

        await using var arrange = sql.CreateContext();
        var hours = await ArrangeEntityAsync(arrange);
        var fuel = await ArrangeEntityAsync(arrange);

        var failures = new List<string>();

        try
        {
            for (var i = 0; i < Iterations; i++)
            {
                // Кожна ітерація — нове вікно часу: обидва записи щоразу ВСТАВЛЯЮТЬ, а не оновлюють.
                var start = Now.AddDays(-30).AddHours(i * 3);
                var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var ready = new[]
                {
                    new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                    new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                };

                var a = WriteAsync(hours, PathHours, start, ready[0], go.Task);
                var b = WriteAsync(fuel, PathFuel, start, ready[1], go.Task);
                await Task.WhenAll(ready[0].Task, ready[1].Task);
                go.SetResult();

                foreach (var (error, written) in await Task.WhenAll(a, b))
                {
                    if (error is not null)
                    {
                        var number = (error as SqlException ?? error.InnerException as SqlException)?.Number;
                        failures.Add($"ітерація {i}: {(number is null ? "—" : number.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))} {error.GetType().Name}: {error.Message}");
                    }
                    else if (written != PointsPerBatch)
                    {
                        failures.Add($"ітерація {i}: записано {written}, очікувалось {PointsPerBatch}");
                    }
                }
            }

            Assert.True(failures.Count == 0, string.Join('\n', failures));
        }
        finally
        {
            await CleanUpAsync(hours);
            await CleanUpAsync(fuel);
        }

        async Task<(Exception? Error, int Written)> WriteAsync(
            int entityId, string path, DateTime start, TaskCompletionSource ready, Task go)
        {
            await using var db = sql.CreateContext();
            var store = new CollectionStore(db, new TestClock(Now));
            var runId = await store.StartRunAsync(entityId, Now.AddDays(-40), Now, false, null, CancellationToken.None);
            var points = Enumerable.Range(0, PointsPerBatch)
                .Select(n => new SourceDataPoint(path, start.AddSeconds(n), n, null, null, "Good"))
                .ToList();

            ready.SetResult();
            await go;

            try
            {
                return (null, await store.UpsertRawPointsAsync(runId, entityId, points, CancellationToken.None));
            }
            catch (Exception ex)
            {
                return (ex, 0);
            }
        }
    }

    private static async Task<int> ArrangeEntityAsync(EcrDbContext db)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];

        var dataSource = new DataSource(
            EcrCode.Create($"Src{tag}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "P3-1" }),
            ExternalTransport.PiWebApi,
            "https://example.test",
            "secret");
        db.DataSources.Add(dataSource);
        await db.SaveChangesAsync(CancellationToken.None);

        var entity = new SourceEntity(dataSource.Id, $"Ent{tag}", RegistrySourceKind.External);
        entity.Deactivate();
        db.SourceEntities.Add(entity);
        await db.SaveChangesAsync(CancellationToken.None);

        return entity.Id;
    }

    private async Task CleanUpAsync(int entityId)
    {
        await using var db = sql.CreateContext();
        await db.Database.ExecuteSqlAsync($"DELETE FROM ext.RawDataPoint WHERE SourceEntityId = {entityId}");
    }
}
