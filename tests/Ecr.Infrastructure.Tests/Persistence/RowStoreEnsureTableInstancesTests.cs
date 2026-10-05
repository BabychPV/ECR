using Ecr.Application.Errors;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// L6-05 (аудит 2026-10-03, 1F2): <c>RowStore.EnsureTableInstancesAsync</c> не
/// створює таблиць для періоду поза проєктом документа і не дає 500 на двох
/// одночасних перших відкриттях.
/// </summary>
/// <remarks>
/// ⚠ Гонитва відтворюється детерміновано, як у <see cref="RowStoreRaceTests"/>:
/// перехоплювач збереження «програвшого» контексту дає «переможцю» створити
/// екземпляри саме між читанням наявних і вставкою.
/// </remarks>
[Collection("SqlServer")]
public sealed class RowStoreEnsureTableInstancesTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Таблиці_за_періодом_поза_проєктом_не_створюються()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(periodKey: 202601, ct: CancellationToken.None);
        await AttachSheetAsync(builder, doc);

        // Календар проєкту має лише 202601; 202605 — валідний за форматом, але чужий.
        var outside = new PeriodKey(202605);

        await using (var db = builder.CreateContext())
        {
            var store = new RowStore(db, new BulkCellLoader(sql.ConnectionString, 1000), new FixedClock(Now));

            var thrown = await Assert.ThrowsAsync<BusinessRuleException>(
                () => store.EnsureTableInstancesAsync(doc.DocumentId, outside, CancellationToken.None));

            Assert.Equal("ECR-PRD-0422", thrown.ErrorCode);
            Assert.Equal("err.ECR-PRD-0422.periodNotInProjectOfDocument", thrown.Details!["messageKey"]);
        }

        await using var check = builder.CreateContext();
        Assert.False(await check.TableInstances.AnyAsync(
            t => t.DocumentId == doc.DocumentId && t.PeriodKeyValue == outside.Value));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Одночасне_перше_відкриття_не_дає_500()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(periodKey: 202601, ct: CancellationToken.None);
        await AttachSheetAsync(builder, doc);

        // Новий період того самого проєкту, ще не відкритий жодного разу.
        var fresh = new PeriodKey(202602);
        await using (var seed = builder.CreateContext())
        {
            seed.Periods.Add(new Period(doc.ProjectId, fresh, 2, new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28)));
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        var bulk = new BulkCellLoader(sql.ConnectionString, 1000);
        var winnerCreated = -1;

        var hook = new BeforeSave(async () =>
        {
            await using var winnerDb = builder.CreateContext();
            winnerCreated = await new RowStore(winnerDb, bulk, new FixedClock(Now))
                .EnsureTableInstancesAsync(doc.DocumentId, fresh, CancellationToken.None);
        });

        await using var loserDb = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .AddInterceptors(hook)
            .Options);
        var loser = new RowStore(loserDb, bulk, new FixedClock(Now));

        var loserCreated = await loser.EnsureTableInstancesAsync(doc.DocumentId, fresh, CancellationToken.None);

        Assert.Equal(1, winnerCreated);
        Assert.Equal(0, loserCreated);
        Assert.DoesNotContain(loserDb.ChangeTracker.Entries(), e => e.State == EntityState.Added);

        await using var check = builder.CreateContext();
        var instances = await check.TableInstances
            .Where(t => t.DocumentId == doc.DocumentId && t.PeriodKeyValue == fresh.Value)
            .ToListAsync();
        Assert.Single(instances);

        // Рядки фіксованої таблиці — лише переможця, без дублів.
        var rows = await check.TableRows
            .CountAsync(r => r.PeriodKeyValue == fresh.Value && r.TableInstanceId == instances[0].Id);
        Assert.Equal(doc.RowDefIds.Count, rows);
    }

    private static async Task AttachSheetAsync(TestDocumentBuilder builder, TestDocument doc)
    {
        await using var db = builder.CreateContext();
        db.DocumentSheets.Add(new DocumentSheet(doc.DocumentId, doc.SheetDefId));
        await db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>Одноразова дія перед першим збереженням контексту.</summary>
    private sealed class BeforeSave(Func<Task> action) : SaveChangesInterceptor
    {
        private bool _fired;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_fired)
            {
                _fired = true;
                await action();
            }

            return result;
        }
    }

    private sealed class FixedClock(DateTime utcNow) : Domain.Abstractions.IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }
}
