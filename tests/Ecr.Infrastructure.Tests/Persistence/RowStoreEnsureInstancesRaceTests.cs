// tests/Ecr.Infrastructure.Tests/Persistence/RowStoreEnsureInstancesRaceTests.cs
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Два ПАРАЛЕЛЬНІ перші заведення екземплярів таблиць одного свіжого документа (RC15 вердикт Land, P3-1:
/// два перші збори в щойно створений документ).
/// </summary>
/// <remarks>
/// Синхронізація детермінована, без sleep: обидва контексти зупиняються перед збереженням, поки обидва
/// не прочитали «екземплярів немає» і не підготували вставку, - тоді вставки гарантовано змагаються.
/// ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати <c>catch</c> на <c>UQ_TableInstance</c> у <c>RowStore.EnsureTableInstancesAsync</c> -
/// програш гонки дає 2627.
/// </remarks>
[Collection("SqlServer")]
public sealed class RowStoreEnsureInstancesRaceTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 2, 20, 6, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "RC15-Land-P2")]
    public async Task Два_паралельні_заведення_екземплярів_свіжого_документа_дають_по_одному_екземпляру_і_обидва_бачать_їх()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);
        var period = chain.PeriodKey;

        await using (var prep = builder.CreateContext())
        {
            prep.DocumentSheets.Add(new DocumentSheet(chain.DocumentId, chain.SheetDefId));
            await prep.SaveChangesAsync();
            await prep.CellValues
                .Where(c => c.PeriodKeyValue == period.Value
                            && prep.TableRows.Any(r => r.Id == c.TableRowId && r.PeriodKeyValue == period.Value
                                                       && r.TableInstanceId == chain.TableInstanceId))
                .ExecuteDeleteAsync();
            await prep.TableRows
                .Where(r => r.PeriodKeyValue == period.Value && r.TableInstanceId == chain.TableInstanceId)
                .ExecuteDeleteAsync();
            await prep.TableInstances
                .Where(i => i.DocumentId == chain.DocumentId && i.PeriodKeyValue == period.Value)
                .ExecuteDeleteAsync();
        }

        var gate = new AsyncGate(2);

        async Task<int> EnsureAsync()
        {
            await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
                .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
                .AddInterceptors(new GateBeforeInstanceSave(gate))
                .Options);
            var store = new RowStore(db, new BulkCellLoader(sql.ConnectionString, 1000), new TestClock(Now));
            return await store.EnsureTableInstancesAsync(chain.DocumentId, period, CancellationToken.None);
        }

        var results = await Task.WhenAll(Task.Run(EnsureAsync), Task.Run(EnsureAsync));

        await using var check = builder.CreateContext();
        var instances = await check.TableInstances.AsNoTracking()
            .Where(i => i.DocumentId == chain.DocumentId && i.PeriodKeyValue == period.Value)
            .Select(i => i.TableDefId)
            .ToListAsync();

        Assert.NotEmpty(instances);
        Assert.Equal(instances.Count, instances.Distinct().Count());
        // Переможець створив усе, програвший - нічого.
        Assert.Equal(instances.Count, results.Sum());
    }

    /// <summary>Асинхронний бар'єр на N учасників.</summary>
    private sealed class AsyncGate(int parties)
    {
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public Task ArriveAsync()
        {
            if (Interlocked.Increment(ref _arrived) >= parties)
            {
                _open.TrySetResult();
            }

            return _open.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    /// <summary>Затримує збереження, що додає екземпляри таблиць, доки не дійшли всі учасники.</summary>
    private sealed class GateBeforeInstanceSave(AsyncGate gate) : SaveChangesInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<TableInstance>().Any(e => e.State == EntityState.Added))
            {
                await gate.ArriveAsync().ConfigureAwait(false);
            }

            return result;
        }
    }
}
