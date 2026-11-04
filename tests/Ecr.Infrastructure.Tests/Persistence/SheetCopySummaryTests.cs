// tests/Ecr.Infrastructure.Tests/Persistence/SheetCopySummaryTests.cs
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Workflow;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// RS (копії аркушів, людина 03.11): зведення (смуга переліку, огляд кампанії) рахують копію
/// ОКРЕМИМ аркушем зі СВОЇМ станом: документ затверджений лише коли затверджені оригінал і всі копії.
/// </summary>
/// <remarks>
/// ⛔ Мутація (локально, не комітиться): прибрати з приєднання <c>wf.ApprovalState</c> умову за <c>SheetCopyId</c> —
/// копія без стану підхоплює стан оригінала, документ стає «затвердженим», обидва тести червоні.
/// </remarks>
[Collection("SqlServer")]
public sealed class SheetCopySummaryTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 3, 20, 6, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "RS-1103")]
    public async Task Смуга_переліку_лічить_копію_окремим_аркушем_і_документ_не_затверджений_поки_копія_чернетка()
    {
        var (builder, chain) = await ArrangeAsync();
        await using var db = builder.CreateContext();

        var summary = await new DocumentListSummaryStore(db).SummarizeAsync(
            chain.ProjectId, chain.PeriodKey.Value, visibleProjectIds: null, restrictions: null, currentUserId: null, CancellationToken.None);

        // Оригінал затверджений, копія — без стану (Draft): документ Draft, аркушів 2, затверджено 1.
        Assert.Equal((1, 0, 0), (summary.Draft, summary.Submitted, summary.Approved));
        Assert.Equal((1, 2), (summary.SheetsApproved, summary.SheetsTotal));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "RS-1103")]
    public async Task Огляд_кампанії_лічить_неподані_аркуші_з_копією()
    {
        var (builder, chain) = await ArrangeAsync();
        await using var db = builder.CreateContext();

        var notSubmitted = await new CampaignSummaryStore(db)
            .NotSubmittedSheetsByPeriodAsync(chain.ProjectId, CancellationToken.None);

        // Два аркуші складу, один (оригінал) затверджений: не подано рівно один.
        Assert.Equal(1, notSubmitted[chain.PeriodKey.Value]);
    }

    private async Task<(TestDocumentBuilder Builder, TestDocument Chain)> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);

        await using var db = builder.CreateContext();
        var original = new DocumentSheet(chain.DocumentId, chain.SheetDefId);
        db.DocumentSheets.Add(original);
        await db.SaveChangesAsync();
        db.DocumentSheets.Add(original.CreateCopy(2));

        var state = new ApprovalState(chain.DocumentId, chain.SheetDefId, chain.PeriodKey.Value);
        state.Submit(1, Now);
        state.Approve(1, Now);
        db.ApprovalStates.Add(state);
        await db.SaveChangesAsync();

        return (builder, chain);
    }
}
