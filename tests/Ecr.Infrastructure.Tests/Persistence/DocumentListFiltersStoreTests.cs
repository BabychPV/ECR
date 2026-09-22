using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>Фільтри <c>state</c>/<c>mine</c> і позначка пізніх правок переліку документів (<c>BE-09b</c>).</summary>
[Collection("SqlServer")]
public sealed class DocumentListFiltersStoreTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 1, 20, 9, 0, 0, DateTimeKind.Utc);
    private static int _user = 7_300_000 + (Environment.ProcessId % 10_000) * 10;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Фільтр_стану_повертає_рівно_документи_в_цьому_стані()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);
        await using var db = builder.CreateContext();

        var byState = new Dictionary<DocumentStatus, long>
        {
            [DocumentStatus.Draft] = await DocumentAsync(db, chain, "S-DRAFT", author: 1, DocumentStatus.Draft),
            [DocumentStatus.Submitted] = await DocumentAsync(db, chain, "S-SUB", author: 1, DocumentStatus.Submitted),
            [DocumentStatus.Approved] = await DocumentAsync(db, chain, "S-APP", author: 1, DocumentStatus.Approved),
            [DocumentStatus.Rejected] = await DocumentAsync(db, chain, "S-REJ", author: 1, DocumentStatus.Rejected),
        };

        foreach (var (state, expected) in byState)
        {
            var ids = await IdsAsync(db, chain.ProjectId, chain.PeriodKey.Value, new DocumentListFilter(state, null), null);

            // Документ ланцюга без складу — теж чернетка (правило смуги: Sheets = 0).
            long[] want = state == DocumentStatus.Draft ? [chain.DocumentId, expected] : [expected];
            Assert.Equal(want.Order(), ids.Order());
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Мої_це_автор_або_той_хто_подавав_аркуш()
    {
        var me = Interlocked.Increment(ref _user);
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);
        await using var db = builder.CreateContext();

        var authored = await DocumentAsync(db, chain, "M-AUTH", author: me, DocumentStatus.Draft);
        var submitted = await DocumentAsync(db, chain, "M-SUB", author: 1, DocumentStatus.Submitted, submitter: me);
        await DocumentAsync(db, chain, "M-OTHER", author: 1, DocumentStatus.Submitted);

        var ids = await IdsAsync(db, chain.ProjectId, chain.PeriodKey.Value, new DocumentListFilter(null, me), null);

        Assert.Equal(new[] { authored, submitted }.Order(), ids.Order());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Фільтр_не_розширює_видимість_за_межі_грантів()
    {
        var me = Interlocked.Increment(ref _user);
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var mine = await builder.BuildAsync(ct: CancellationToken.None);
        var foreign = await builder.BuildAsync(ct: CancellationToken.None);
        await using var db = builder.CreateContext();

        var visible = await DocumentAsync(db, mine, "V-MINE", author: me, DocumentStatus.Rejected);
        await DocumentAsync(db, foreign, "V-FOREIGN", author: me, DocumentStatus.Rejected);

        // Усі проєкти, той самий автор і той самий стан — межа лише в гранті.
        var ids = await IdsAsync(
            db, projectId: null, mine.PeriodKey.Value,
            new DocumentListFilter(DocumentStatus.Rejected, me), visibleProjects: [mine.ProjectId]);

        Assert.Equal([visible], ids);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Пізні_правки_лише_з_позначкою_і_лише_за_свій_період_одним_запитом()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);
        var period = chain.PeriodKey.Value;

        long late, onTime, otherPeriod;
        await using (var db = builder.CreateContext())
        {
            late = await DocumentAsync(db, chain, "L-LATE", author: 1, DocumentStatus.Draft);
            onTime = await DocumentAsync(db, chain, "L-ONTIME", author: 1, DocumentStatus.Draft);
            otherPeriod = await DocumentAsync(db, chain, "L-OTHER", author: 1, DocumentStatus.Draft);
        }

        await WriteChangeAsync(late, period, isLate: false);
        await WriteChangeAsync(late, period, isLate: true);
        await WriteChangeAsync(onTime, period, isLate: false);
        await WriteChangeAsync(otherPeriod, period + 1, isLate: true);

        var counter = new DbCommandCounter();
        await using var counted = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .AddInterceptors(counter)
            .Options);

        var page = await new DocumentStore(counted).ListAsync(
            chain.ProjectId, new PeriodKeyFilter(period), default, new CursorRequest(Limit: 50),
            visibleProjectIds: null, CancellationToken.None);

        Assert.True(page.Items.Single(d => d.Id == late).HasLateEdits);
        Assert.False(page.Items.Single(d => d.Id == onTime).HasLateEdits);
        Assert.False(page.Items.Single(d => d.Id == otherPeriod).HasLateEdits);

        // Чотири документи на сторінці — чотири запити на всю сторінку
        // (документи, стани, підсумки, пізні правки), не по запиту на рядок.
        Assert.Equal(4, page.Items.Count);
        Assert.True(counter.Tally.Snapshot().Total == 4, counter.Tally.Snapshot().Format());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Фільтр_hasLateEdits_показує_лише_документи_з_позначкою()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);
        var period = chain.PeriodKey.Value;

        long late, onTime;
        await using (var db = builder.CreateContext())
        {
            late = await DocumentAsync(db, chain, "HLE-LATE", author: 1, DocumentStatus.Draft);
            onTime = await DocumentAsync(db, chain, "HLE-ONTIME", author: 1, DocumentStatus.Draft);
        }

        await WriteChangeAsync(late, period, isLate: true);

        await using var readDb = builder.CreateContext();

        var onlyLate = await IdsAsync(
            readDb, chain.ProjectId, period, new DocumentListFilter(null, null, true), null);
        Assert.Equal([late], onlyLate);

        var onlyNotLate = await IdsAsync(
            readDb, chain.ProjectId, period, new DocumentListFilter(null, null, false), null);
        Assert.Equal(new[] { chain.DocumentId, onTime }.Order(), onlyNotLate.Order());

        // Немає параметра — фільтра немає взагалі: обидва в переліку (як зараз).
        var unfiltered = await IdsAsync(
            readDb, chain.ProjectId, period, new DocumentListFilter(null, null, null), null);
        Assert.Equal(new[] { chain.DocumentId, late, onTime }.Order(), unfiltered.Order());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Фільтр_hasLateEdits_комбінується_з_state_і_mine()
    {
        var me = Interlocked.Increment(ref _user);
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);
        var period = chain.PeriodKey.Value;

        long mineLate, mineOnTime, otherLate;
        await using (var db = builder.CreateContext())
        {
            mineLate = await DocumentAsync(db, chain, "HLE-MINE-LATE", author: me, DocumentStatus.Submitted, submitter: me);
            mineOnTime = await DocumentAsync(db, chain, "HLE-MINE-ONTIME", author: me, DocumentStatus.Submitted, submitter: me);
            otherLate = await DocumentAsync(db, chain, "HLE-OTHER-LATE", author: 1, DocumentStatus.Submitted);
        }

        await WriteChangeAsync(mineLate, period, isLate: true);
        await WriteChangeAsync(otherLate, period, isLate: true);

        await using var readDb = builder.CreateContext();

        // state=Submitted + mine=me + hasLateEdits=true — рівно один документ,
        // хоч під кожен окремий фільтр підходить більше: mineOnTime випадає
        // через hasLateEdits, otherLate — через mine.
        var ids = await IdsAsync(
            readDb, chain.ProjectId, period,
            new DocumentListFilter(DocumentStatus.Submitted, me, true), null);

        Assert.Equal([mineLate], ids);
        Assert.DoesNotContain(mineOnTime, ids);
    }

    private static async Task<long[]> IdsAsync(
        EcrDbContext db, int? projectId, int periodKey, DocumentListFilter filter, int[]? visibleProjects)
    {
        var page = await new DocumentStore(db).ListAsync(
            projectId, new PeriodKeyFilter(periodKey), filter, new CursorRequest(Limit: 50),
            visibleProjects, CancellationToken.None);

        return [.. page.Items.Select(d => d.Id)];
    }

    /// <summary>Документ з одним аркушем складу в заданому стані за період ланцюга.</summary>
    private static async Task<long> DocumentAsync(
        EcrDbContext db, TestDocument chain, string key, int author, DocumentStatus status, int? submitter = null)
    {
        var document = new Document(chain.ProjectId, $"{key}-{chain.ProjectId}", author, Now);
        db.Documents.Add(document);
        await db.SaveChangesAsync(CancellationToken.None);

        db.DocumentSheets.Add(new DocumentSheet(document.Id, chain.SheetDefId));
        if (status != DocumentStatus.Draft)
        {
            var state = new ApprovalState(document.Id, chain.SheetDefId, chain.PeriodKey.Value);
            state.Submit(submitter ?? 1, Now);
            if (status == DocumentStatus.Approved)
            {
                state.Approve(1, Now);
            }
            else if (status == DocumentStatus.Rejected)
            {
                state.Reject(1, "BE-09b", Now);
            }

            db.ApprovalStates.Add(state);
        }

        await db.SaveChangesAsync(CancellationToken.None);
        return document.Id;
    }

    /// <summary>Рядок журналу — прямим ADO: <c>aud.*</c> поза моделлю EF.</summary>
    private async Task WriteChangeAsync(long documentId, int periodKey, bool isLate)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO aud.CellChange
                (ChangedAt, PeriodKey, DocumentId, TableRowId, RowKey, ColumnDefId,
                 OldValue, NewValue, ChangedByUserId, Origin, IsLateEdit)
            VALUES (@at, @period, @document, 1, N'R1', 1, N'1', N'2', 1, N'UserEdit', @late);
            """;
        command.Parameters.AddWithValue("@at", DateTime.UtcNow);
        command.Parameters.AddWithValue("@period", periodKey);
        command.Parameters.AddWithValue("@document", documentId);
        command.Parameters.AddWithValue("@late", isLate);

        await command.ExecuteNonQueryAsync();
    }
}
