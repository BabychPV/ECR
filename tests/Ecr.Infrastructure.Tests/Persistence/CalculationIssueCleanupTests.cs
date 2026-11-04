// tests/Ecr.Infrastructure.Tests/Persistence/CalculationIssueCleanupTests.cs
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Documents;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// AN-69: проблеми розрахунку не переживають свого власника - видалення документа забирає всі його проблеми,
/// видалення копії аркуша - лише проблеми цієї копії.
/// </summary>
/// <remarks>
/// Мутації (локально): прибрати рядок <c>CalculationIssues</c> у <c>DocumentDeletionStore.DeleteAsync</c> - червоніє
/// перший тест; у <c>DocumentSheetCopyStore.DeleteCopyAsync</c> - другий.
/// </remarks>
[Collection("SqlServer")]
public sealed class CalculationIssueCleanupTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 11, 3, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Видалення_документа_забирає_його_проблеми_розрахунку_а_чужого_документа_не_чіпає()
    {
        var (builder, chain) = await ArrangeAsync();
        await using (var db = builder.CreateContext())
        {
            db.CalculationIssues.Add(Issue(chain.DocumentId, chain, null));
            db.CalculationIssues.Add(Issue(chain.DocumentId + 1_000_000, chain, null));
            await db.SaveChangesAsync();
        }

        await using (var db = builder.CreateContext())
        {
            await new DocumentDeletionStore(db).DeleteAsync(chain.DocumentId, default);
        }

        await using var check = builder.CreateContext();
        Assert.False(await check.CalculationIssues.AnyAsync(i => i.DocumentId == chain.DocumentId));
        Assert.True(await check.CalculationIssues.AnyAsync(i => i.DocumentId == chain.DocumentId + 1_000_000));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Видалення_копії_забирає_проблеми_лише_цієї_копії()
    {
        var (builder, chain) = await ArrangeAsync();
        long copy2, copy3;
        await using (var db = builder.CreateContext())
        {
            var store = new DocumentSheetCopyStore(db);
            copy2 = (await store.AddCopyAsync(chain.DocumentId, chain.SheetDefId, default)).SheetCopyId;
            copy3 = (await store.AddCopyAsync(chain.DocumentId, chain.SheetDefId, default)).SheetCopyId;
            await new RowStore(db, new BulkCellLoader(sql.ConnectionString, 1000), new TestClock(Now))
                .EnsureTableInstancesAsync(chain.DocumentId, chain.PeriodKey, default);

            db.CalculationIssues.AddRange(
                Issue(chain.DocumentId, chain, copy2), Issue(chain.DocumentId, chain, copy3), Issue(chain.DocumentId, chain, null));
            await db.SaveChangesAsync();
        }

        await using (var db = builder.CreateContext())
        {
            await new DocumentSheetCopyStore(db).DeleteCopyAsync(chain.DocumentId, copy2, default);
        }

        await using var check = builder.CreateContext();
        var left = await check.CalculationIssues.AsNoTracking()
            .Where(i => i.DocumentId == chain.DocumentId).Select(i => i.SheetCopyId).ToListAsync();
        Assert.DoesNotContain(copy2, left);
        Assert.Contains(copy3, left);
        Assert.Contains(null, left);
    }

    private static CalculationIssue Issue(long documentId, TestDocument chain, long? copy)
        => new(documentId, chain.PeriodKey.Value, chain.TableDefId, copy, "R1", 1, "M1", 1, Now);

    private async Task<(TestDocumentBuilder Builder, TestDocument Chain)> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(periodKey: 202603, ct: CancellationToken.None);

        await using var db = builder.CreateContext();
        if (!await db.DocumentSheets.AnyAsync(s => s.DocumentId == chain.DocumentId && s.CopyOfId == null))
        {
            db.DocumentSheets.Add(new DocumentSheet(chain.DocumentId, chain.SheetDefId));
            await db.SaveChangesAsync();
        }

        return (builder, chain);
    }
}
