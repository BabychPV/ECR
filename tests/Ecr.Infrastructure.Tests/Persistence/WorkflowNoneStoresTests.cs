// tests/Ecr.Infrastructure.Tests/Persistence/WorkflowNoneStoresTests.cs
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// ✎ B138 (B75 lane 1b): версія шаблону без подання й затвердження (<c>WorkflowMode.None</c>) — старі стани
/// <c>wf.ApprovalState</c> (лишилися після переносу версії) не діють: черга погодження й «повернуті мені» їх не
/// показують, перерахунок не бачить ні станів аркушів, ні «затверджених для пропуску».
/// </summary>
/// <remarks>
/// Кожен тест має КОНТРОЛЬ: до перемикання режиму той самий рядок є. ⛔ ДОКАЗ (локально): прибрати
/// <c>ExceptWorkflowNone</c> у відповідному запиті — друга половина тесту червона.
/// </remarks>
[Collection("SqlServer")]
public sealed class WorkflowNoneStoresTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 4, 1, 10, 0, 0, DateTimeKind.Utc);
    private const int Author = 5;
    private const int Approver = 6;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B138")]
    public async Task Черга_погодження_не_показує_поданий_аркуш_версії_без_погодження()
    {
        var (chain, doc) = await ArrangeAsync(s => s.Submit(Author, Now)).ConfigureAwait(true);

        Assert.Contains(await SubmittedAsync(chain, doc).ConfigureAwait(true), r => r.DocumentId == doc.DocumentId);

        await SetNoneAsync(chain, doc).ConfigureAwait(true);

        Assert.DoesNotContain(await SubmittedAsync(chain, doc).ConfigureAwait(true), r => r.DocumentId == doc.DocumentId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B138")]
    public async Task Повернуті_мені_не_показують_відхилений_аркуш_версії_без_погодження()
    {
        var (chain, doc) = await ArrangeAsync(s =>
        {
            s.Submit(Author, Now);
            s.Reject(Approver, "Невірні значення", Now);
        }).ConfigureAwait(true);

        Assert.Contains(await ReturnedAsync(chain, doc).ConfigureAwait(true), r => r.DocumentId == doc.DocumentId);

        await SetNoneAsync(chain, doc).ConfigureAwait(true);

        Assert.DoesNotContain(await ReturnedAsync(chain, doc).ConfigureAwait(true), r => r.DocumentId == doc.DocumentId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B138")]
    public async Task Перерахунок_не_бачить_станів_і_затверджених_аркушів_версії_без_погодження()
    {
        var (chain, doc) = await ArrangeAsync(s =>
        {
            s.Submit(Author, Now);
            s.Approve(Approver, Now);
        }).ConfigureAwait(true);

        await using (var db = chain.CreateContext())
        {
            var store = new WorkflowStore(db);
            Assert.Single(await store.GetSheetsAsync(doc.DocumentId, doc.PeriodKey, CancellationToken.None).ConfigureAwait(true));
            Assert.Single(await store.ListApprovedSheetsAsync(null, doc.DocumentId, doc.PeriodKey.Value, 10, CancellationToken.None)
                .ConfigureAwait(true));
        }

        await SetNoneAsync(chain, doc).ConfigureAwait(true);

        await using (var db = chain.CreateContext())
        {
            var store = new WorkflowStore(db);
            Assert.Empty(await store.GetSheetsAsync(doc.DocumentId, doc.PeriodKey, CancellationToken.None).ConfigureAwait(true));
            Assert.Empty(await store.ListApprovedSheetsAsync(null, doc.DocumentId, doc.PeriodKey.Value, 10, CancellationToken.None)
                .ConfigureAwait(true));
        }
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private async Task<(TestDocumentBuilder Chain, TestDocument Doc)> ArrangeAsync(Action<ApprovalState> shape)
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await chain.BuildAsync().ConfigureAwait(false);

        await using var db = chain.CreateContext();
        var state = new ApprovalState(doc.DocumentId, doc.SheetDefId, doc.PeriodKey.Value);
        shape(state);
        db.ApprovalStates.Add(state);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return (chain, doc);
    }

    private static async Task SetNoneAsync(TestDocumentBuilder chain, TestDocument doc)
    {
        await using var db = chain.CreateContext();
        await db.Database.ExecuteSqlAsync(
            $"UPDATE cfg.TemplateVersion SET WorkflowMode = 1 WHERE Id = {doc.TemplateVersionId}").ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<Ecr.Application.Ports.ApprovalQueueCandidate>> SubmittedAsync(
        TestDocumentBuilder chain, TestDocument doc)
    {
        await using var db = chain.CreateContext();
        return await new ApprovalQueueStore(db).ListSubmittedAsync(
            [doc.ProjectId], requireIncludedSheet: false, after: null, take: 50, participatedDocumentIds: [],
            CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<Ecr.Application.Ports.ReturnedSheetCandidate>> ReturnedAsync(
        TestDocumentBuilder chain, TestDocument doc)
    {
        await using var db = chain.CreateContext();
        return await new ApprovalQueueStore(db).ListReturnedAsync(
            Author, [doc.ProjectId], requireIncludedSheet: false, after: null, take: 50, participatedDocumentIds: [],
            CancellationToken.None).ConfigureAwait(false);
    }
}
