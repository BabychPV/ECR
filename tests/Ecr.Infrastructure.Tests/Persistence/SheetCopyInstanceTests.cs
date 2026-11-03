// tests/Ecr.Infrastructure.Tests/Persistence/SheetCopyInstanceTests.cs
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// RS (копії аркушів, рішення людини 03.11): копія існує в УСІХ періодах — її екземпляр заводиться при першому
/// відкритті періоду, зокрема минулого; він порожній (лише Fixed-рядки шаблону, без комірок) і в стані <c>Draft</c>
/// (рядка <c>wf.ApprovalState</c> немає). У заархівованому році копій не заводять (Q3).
/// </summary>
/// <remarks>
/// ⛔ Мутаційний доказ (локально, не комітиться): у <c>RowStore.FindMissingTableDefsAsync</c> відкинути цілі копій
/// (<c>CopyId</c> завжди <c>null</c>, тобто «копія — лише в періоді, де її додали») — червоніють
/// <see cref="Копія_отримує_порожній_екземпляр_із_Fixed_рядками_і_стан_Draft"/> і
/// <see cref="Копія_заводиться_в_минулому_періоді_при_першому_відкритті"/>.
/// </remarks>
[Collection("SqlServer")]
public sealed class SheetCopyInstanceTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 3, 20, 6, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "RS-1103")]
    public async Task Копія_отримує_порожній_екземпляр_із_Fixed_рядками_і_стан_Draft()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);
        var copyId = await AddCopyAsync(builder, chain);

        await using var db = builder.CreateContext();
        var store = Store(db);

        // Оригінал за період уже є (будівник) — заводиться лише екземпляр копії.
        Assert.Equal(1, await store.EnsureTableInstancesAsync(chain.DocumentId, chain.PeriodKey, CancellationToken.None));
        Assert.Equal(0, await store.EnsureTableInstancesAsync(chain.DocumentId, chain.PeriodKey, CancellationToken.None));

        var instances = await store.GetTableInstancesAsync(chain.DocumentId, chain.PeriodKey, CancellationToken.None);
        Assert.Equal(2, instances.Count);
        Assert.Equal((chain.TableInstanceId, (long?)null), (instances[0].TableInstanceId, instances[0].SheetCopyId));
        var copy = instances[1];
        Assert.Equal((chain.TableDefId, (long?)copyId), (copy.TableDefId, copy.SheetCopyId));

        await AssertEmptyCopyAsync(db, chain, copy.TableInstanceId, chain.PeriodKey);
        Assert.False(await db.ApprovalStates.AnyAsync(a => a.DocumentId == chain.DocumentId && a.SheetCopyId == copyId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "RS-1103")]
    public async Task Копія_заводиться_в_минулому_періоді_при_першому_відкритті()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(periodKey: 202603, ct: CancellationToken.None);
        var copyId = await AddCopyAsync(builder, chain);
        var past = new PeriodKey(202601);
        await builder.AddPeriodAsync(chain, past);

        await using var db = builder.CreateContext();
        var store = Store(db);

        Assert.Equal(2, await store.EnsureTableInstancesAsync(chain.DocumentId, past, CancellationToken.None));

        var instances = await store.GetTableInstancesAsync(chain.DocumentId, past, CancellationToken.None);
        Assert.Equal([null, copyId], instances.Select(i => i.SheetCopyId));
        Assert.All(instances, i => Assert.Equal(chain.TableDefId, i.TableDefId));
        await AssertEmptyCopyAsync(db, chain, instances[1].TableInstanceId, past);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "RS-1103")]
    public async Task У_заархівованому_періоді_копію_не_заводять()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);
        await AddCopyAsync(builder, chain);
        var archived = new PeriodKey(202602);
        await builder.AddPeriodAsync(chain, archived);

        await using var db = builder.CreateContext();

        // Слід архівації (F-13): екземпляр оригіналу лежить лише в arc.TableInstance.
        var loader = new BulkCellLoader(sql.ConnectionString, 1000);
        var archivedId = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO arc.TableInstance (PeriodKey, Id, DocumentId, TableDefId, CreatedAt, ModifiedAt)
            VALUES ({archived.Value}, {archivedId}, {chain.DocumentId}, {chain.TableDefId}, {Now}, {Now});
            """);

        try
        {
            var store = new RowStore(db, loader, new TestClock(Now), new ArchiveAwareCellReader(db));

            Assert.Equal(0, await store.EnsureTableInstancesAsync(chain.DocumentId, archived, CancellationToken.None));
            Assert.False(await db.TableInstances.AnyAsync(
                t => t.DocumentId == chain.DocumentId && t.PeriodKeyValue == archived.Value));
        }
        finally
        {
            // ⚠ База спільна: архівний слід періоду заважав би ArchiveJobTests («діапазон уже заархівовано»).
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM arc.TableInstance WHERE PeriodKey = {archived.Value} AND Id = {archivedId};");
        }
    }

    /// <summary>
    /// Два одночасні перші відкриття документа з копією: рівно один екземпляр на (таблицю, копію) —
    /// <c>UQ_TableInstance</c> із <c>SheetCopyId</c> без фільтра (NULL рівні) тримає і оригінал, і копію.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "RS-1103")]
    public async Task Два_паралельні_перші_відкриття_дають_по_одному_екземпляру_на_таблицю_і_копію()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);
        var copyId = await AddCopyAsync(builder, chain);
        var period = new PeriodKey(202602);
        await builder.AddPeriodAsync(chain, period);

        var gate = new AsyncGate(2);

        async Task<int> EnsureAsync()
        {
            await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
                .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
                .AddInterceptors(new GateBeforeInstanceSave(gate))
                .Options);
            return await Store(db).EnsureTableInstancesAsync(chain.DocumentId, period, CancellationToken.None);
        }

        var results = await Task.WhenAll(Task.Run(EnsureAsync), Task.Run(EnsureAsync));

        await using var check = builder.CreateContext();
        var instances = await check.TableInstances.AsNoTracking()
            .Where(i => i.DocumentId == chain.DocumentId && i.PeriodKeyValue == period.Value)
            .Select(i => new { i.TableDefId, i.SheetCopyId })
            .ToListAsync();

        Assert.Equal(2, instances.Count);
        Assert.Equal([null, copyId], instances.Select(i => i.SheetCopyId).Order());
        Assert.Equal(instances.Count, results.Sum());
    }

    /// <summary>Оригінал аркуша ланцюга в складі документа і його копія №2; повертає Id копії.</summary>
    internal static async Task<long> AddCopyAsync(TestDocumentBuilder builder, TestDocument chain)
    {
        await using var db = builder.CreateContext();
        var original = await db.DocumentSheets.SingleOrDefaultAsync(
            s => s.DocumentId == chain.DocumentId && s.SheetDefId == chain.SheetDefId && s.CopyOfId == null);
        if (original is null)
        {
            original = new DocumentSheet(chain.DocumentId, chain.SheetDefId);
            db.DocumentSheets.Add(original);
            await db.SaveChangesAsync();
        }

        var ordinal = (short)(1 + await db.DocumentSheets.CountAsync(s => s.CopyOfId == original.Id) + 1);
        var copy = original.CreateCopy(ordinal);
        db.DocumentSheets.Add(copy);
        await db.SaveChangesAsync();
        return copy.Id;
    }

    private RowStore Store(EcrDbContext db)
        => new(db, new BulkCellLoader(sql.ConnectionString, 1000), new TestClock(Now));

    /// <summary>Екземпляр копії: рядки — рівно Fixed-рядки шаблону, комірок немає.</summary>
    private static async Task AssertEmptyCopyAsync(EcrDbContext db, TestDocument chain, long instanceId, PeriodKey period)
    {
        var rowKeys = await db.TableRows.AsNoTracking()
            .Where(r => r.PeriodKeyValue == period.Value && r.TableInstanceId == instanceId)
            .OrderBy(r => r.Ordinal)
            .Select(r => r.RowDefId)
            .ToListAsync();
        Assert.Equal(chain.RowDefIds.Select(id => (int?)id), rowKeys);

        Assert.False(await db.CellValues.AnyAsync(
            c => c.PeriodKeyValue == period.Value
                 && db.TableRows.Any(r => r.PeriodKeyValue == period.Value && r.Id == c.TableRowId
                                          && r.TableInstanceId == instanceId)));
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
