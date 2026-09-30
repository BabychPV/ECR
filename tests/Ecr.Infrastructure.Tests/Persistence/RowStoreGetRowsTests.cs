using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// <c>WR-04</c> п. 3: <c>RowStore.GetRowsAsync</c> — одним запитом те саме, що
/// давали <c>GetRowVersionsAsync</c> + <c>GetRowIdsAsync</c>, плюс ознака
/// осиротілості.
/// </summary>
/// <remarks>
/// ⚠ Еталон — ті самі два старі методи на тій самій базі, а не константи в
/// тесті: <c>PatchCellsHandler</c> перейшов з пари на один виклик, і
/// твердження тут рівно одне — що заміна нічого не загубила й не додала.
/// </remarks>
[Collection("SqlServer")]
public sealed class RowStoreGetRowsTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "WR-04")]
    public async Task Один_запит_віддає_ключ_Id_версію_й_осиротілість_живих_рядків()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync(
            periodKey: 202603, rowMode: TableRowMode.Dynamic, rowCount: 3, ct: CancellationToken.None);

        var now = new DateTime(2026, 3, 5, 8, 0, 0, DateTimeKind.Utc);
        var orphanId = document.RowIds[1];
        var deletedId = document.RowIds[2];

        await using (var seed = builder.CreateContext())
        {
            var orphan = await seed.TableRows
                .SingleAsync(r => r.PeriodKeyValue == document.PeriodKey.Value && r.Id == orphanId);
            orphan.SetOrphaned(true, now);

            var deleted = await seed.TableRows
                .SingleAsync(r => r.PeriodKeyValue == document.PeriodKey.Value && r.Id == deletedId);
            deleted.SoftDelete(now);

            await seed.SaveChangesAsync(CancellationToken.None);
        }

        await using var db = builder.CreateContext();
        var store = new RowStore(db, new BulkCellLoader(sql.ConnectionString, 1000), new FixedClock(now));

        var rows = await store.GetRowsAsync(document.TableInstanceId, document.PeriodKey, CancellationToken.None);
        var versions = await store.GetRowVersionsAsync(document.TableInstanceId, document.PeriodKey, CancellationToken.None);
        var ids = await store.GetRowIdsAsync(document.TableInstanceId, document.PeriodKey, CancellationToken.None);

        // Видалений рядок не живий — його немає ні там, ні там.
        Assert.Equal(2, rows.Count);
        Assert.DoesNotContain(rows, r => r.Id == deletedId);

        Assert.Equal(
            versions.OrderBy(p => p.Key, StringComparer.Ordinal),
            rows.ToDictionary(r => r.RowKey, r => r.RowVersion, StringComparer.Ordinal)
                .OrderBy(p => p.Key, StringComparer.Ordinal));
        Assert.Equal(
            ids.OrderBy(p => p.Key, StringComparer.Ordinal),
            rows.ToDictionary(r => r.RowKey, r => r.Id, StringComparer.Ordinal)
                .OrderBy(p => p.Key, StringComparer.Ordinal));

        Assert.True(rows.Single(r => r.Id == orphanId).IsOrphaned);
        Assert.False(rows.Single(r => r.Id == document.RowIds[0]).IsOrphaned);

        // Інший період того самого екземпляра — порожньо: предикат несе PeriodKey.
        Assert.Empty(await store.GetRowsAsync(
            document.TableInstanceId, new Domain.ValueObjects.PeriodKey(202604), CancellationToken.None));
    }

    private sealed class FixedClock(DateTime utcNow) : Domain.Abstractions.IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }
}
