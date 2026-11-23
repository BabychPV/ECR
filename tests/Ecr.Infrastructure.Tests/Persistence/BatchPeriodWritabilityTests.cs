// tests/Ecr.Infrastructure.Tests/Persistence/BatchPeriodWritabilityTests.cs
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// ✎ PERF-06b (<c>D-572</c>): пакетні читання постановки каскаду за кілька періодів документа
/// (<see cref="PeriodStore.FindPeriodStatesAsync"/>, <see cref="WorkflowStore.GetSheetsForPeriodsAsync"/>) дають рівно те саме,
/// що поодинокі (<c>FindPeriodStateAsync</c>, <c>GetSheetsAsync</c>), — і для відсутнього періоду, і з винятком «місяць відкрито
/// для документа», і за годинником (ефективний стан).
/// </summary>
/// <remarks>
/// Мутації (локально): у пакетному стані не застосовувати виняток документа — червоний; у пакетних аркушах не фільтрувати за
/// ключем періоду — червоний.
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage8)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Finding", "PERF-06")]
public sealed class BatchPeriodWritabilityTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 11, 23, 10, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Пакетні_стани_й_аркуші_дорівнюють_поодиноким(bool withClock)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync(periodKey: 209901);
        foreach (var month in new[] { 2, 3 })
        {
            await builder.AddPeriodInstanceAsync(document, new PeriodKey(209900 + month));
        }

        await using (var db = sql.CreateContext())
        {
            var periods = await db.Periods.Where(p => p.ProjectId == document.ProjectId).OrderBy(p => p.PeriodKeyValue).ToListAsync();
            periods[0].AdvanceTo(PeriodState.Open, Now);
            periods[1].AdvanceTo(PeriodState.Open, Now);
            periods[1].TransitionTo(PeriodState.Closed, Now);
            db.DocumentPeriodReopens.Add(DocumentPeriodReopen.Open(document.DocumentId, 209902, Now.AddDays(3650), "test", 1, Now));

            var approval = new ApprovalState(document.DocumentId, document.SheetDefId, 209903);
            approval.Submit(userId: 5, Now);
            db.ApprovalStates.Add(approval);
            await db.SaveChangesAsync();
        }

        int[] keys = [209901, 209902, 209903, 209904];
        await using var read = sql.CreateContext();
        var store = withClock ? new PeriodStore(read, new TestClock(Now)) : new PeriodStore(read);
        var batch = await store.FindPeriodStatesAsync(document.DocumentId, keys, CancellationToken.None);
        var workflow = new WorkflowStore(read);
        var sheets = await workflow.GetSheetsForPeriodsAsync(document.DocumentId, keys, CancellationToken.None);

        Assert.Equal(keys, batch.Keys.Order());
        Assert.Equal(keys, sheets.Keys.Order());
        foreach (var key in keys)
        {
            Assert.Equal(await store.FindPeriodStateAsync(document.DocumentId, key, CancellationToken.None), batch[key]);
            Assert.Equal(
                (await workflow.GetSheetsAsync(document.DocumentId, new PeriodKey(key), CancellationToken.None)).Select(s => s.Id),
                sheets[key].Select(s => s.Id));
        }

        Assert.Null(batch[209904]);
        Assert.Single(sheets[209903]);
        Assert.Empty(sheets[209901]);
        if (withClock)
        {
            Assert.Equal(PeriodState.Grace, batch[209902]);
        }
    }
}
