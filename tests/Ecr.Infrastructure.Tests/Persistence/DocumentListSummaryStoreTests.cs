using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>Зведення переліку документів і лічильники рядка на реальному SQL Server (<c>BE-09</c>).</summary>
[Collection("SqlServer")]
public sealed class DocumentListSummaryStoreTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 1, 20, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Зведення_не_лічить_документів_чужого_проєкту()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var mine = await builder.BuildAsync(ct: CancellationToken.None);
        var foreign = await builder.BuildAsync(ct: CancellationToken.None);

        await using var db = builder.CreateContext();
        await ArrangeAsync(db, mine, mine.DocumentId, DocumentStatus.Rejected, errors: 1);
        await ArrangeAsync(db, foreign, foreign.DocumentId, DocumentStatus.Rejected, errors: 1);

        var summary = await new DocumentListSummaryStore(db).SummarizeAsync(
            projectId: null, mine.PeriodKey.Value, [mine.ProjectId], CancellationToken.None);

        // ⛔ Рівно ОДИН: чужий документ у тому самому стані й з тими самими
        // помилками існує, і без межі грантів обидва числа були б 2 (або більше).
        Assert.Equal(1, summary.Rejected);
        Assert.Equal(1, summary.WithIssues);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Стан_документа_найгірший_з_аркушів_а_зауваження_з_ОСТАННЬОГО_підсумку()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);
        await using var db = builder.CreateContext();

        // Аркуш складу без рядка стану — чернетка; перевірки не було.
        await ArrangeAsync(db, chain, chain.DocumentId, status: null, errors: null);

        // Раніше було 3 помилки, ОСТАННІЙ прогін чистий — «з зауваженнями» не є.
        var approved = await AddDocumentAsync(db, chain, "BE09-APPROVED");
        await ArrangeAsync(db, chain, approved, DocumentStatus.Approved, errors: 0, earlierErrors: 3);

        // Раніше було чисто, ОСТАННІЙ прогін знайшов 2 помилки.
        var submitted = await AddDocumentAsync(db, chain, "BE09-SUBMITTED");
        await ArrangeAsync(db, chain, submitted, DocumentStatus.Submitted, errors: 2, earlierErrors: 0);

        var summary = await new DocumentListSummaryStore(db).SummarizeAsync(
            chain.ProjectId, chain.PeriodKey.Value, visibleProjectIds: null, CancellationToken.None);

        Assert.Equal((1, 1, 1, 0, 1),
            (summary.Draft, summary.Submitted, summary.Approved, summary.Rejected, summary.WithIssues));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task У_рядку_переліку_немає_підсумку_це_null_а_не_нуль()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);
        await using var db = builder.CreateContext();

        var validated = await AddDocumentAsync(db, chain, "BE09-VALIDATED");
        await ArrangeAsync(db, chain, validated, status: null, errors: 0, earlierErrors: 5);

        var page = await new DocumentStore(db).ListAsync(
            chain.ProjectId, new PeriodKeyFilter(chain.PeriodKey.Value), default, new CursorRequest(Limit: 50),
            visibleProjectIds: null, CancellationToken.None);

        var never = page.Items.Single(d => d.Id == chain.DocumentId);
        var clean = page.Items.Single(d => d.Id == validated);

        // ⛔ Неперевірений документ — `null`; перевірений і чистий — саме нуль.
        Assert.Null(never.ErrorCount);
        Assert.Null(never.WarningCount);
        Assert.Equal(0, clean.ErrorCount);
        Assert.Equal(1, clean.WarningCount);
        Assert.NotNull(clean.ModifiedAt);
    }

    private static async Task<long> AddDocumentAsync(EcrDbContext db, TestDocument chain, string key)
    {
        var document = new Document(chain.ProjectId, key, 1, Now);
        db.Documents.Add(document);
        await db.SaveChangesAsync(CancellationToken.None);
        return document.Id;
    }

    /// <summary>Аркуш у складі, стан аркуша і до двох підсумків перевірки (раніший — на годину старший).</summary>
    private static async Task ArrangeAsync(
        EcrDbContext db, TestDocument chain, long documentId,
        DocumentStatus? status, int? errors, int? earlierErrors = null)
    {
        var period = chain.PeriodKey.Value;
        db.DocumentSheets.Add(new DocumentSheet(documentId, chain.SheetDefId));

        if (status is { } target)
        {
            var state = new ApprovalState(documentId, chain.SheetDefId, period);
            state.Submit(1, Now);
            if (target == DocumentStatus.Approved)
            {
                state.Approve(1, Now);
            }
            else if (target == DocumentStatus.Rejected)
            {
                state.Reject(1, "BE-09", Now);
            }

            db.ApprovalStates.Add(state);
        }

        if (earlierErrors is { } before)
        {
            db.ValidationResults.Add(new ValidationResult(documentId, period, Now.AddHours(-1), before, 0, 0, "[]"));
        }

        if (errors is { } last)
        {
            db.ValidationResults.Add(new ValidationResult(documentId, period, Now, last, 1, 0, "[]"));
        }

        await db.SaveChangesAsync(CancellationToken.None);
    }
}
