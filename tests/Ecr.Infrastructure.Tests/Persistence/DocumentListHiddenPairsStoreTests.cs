using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// N1-01 (аудит 2026-10-09): фільтри <c>state</c> і <c>hasLateEdits</c> переліку документів застосовують межі читача
/// ПАРАМИ «проєкт, Id». Одна версія шаблону спільна для кількох проєктів, тож <c>Deny Sheet</c> лише в A не має
/// ховати аркуш (таблицю, колонку) у документах B.
/// </summary>
/// <remarks>
/// Мутація (CI): повернути плаский <c>SheetDefId IN (...)</c> у <c>DocumentStore.WhereState</c> або прибрати
/// <c>h.p = dd.ProjectId</c> у <c>LateEditDocumentIds</c> → тести червоніють.
/// </remarks>
[Collection("SqlServer")]
public sealed class DocumentListHiddenPairsStoreTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 1, 20, 9, 0, 0, DateTimeKind.Utc);
    private static int _counter;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Фільтр_state_Rejected_знаходить_документ_B_коли_аркуш_схований_лише_в_A()
    {
        var (a, b) = await TwoProjectsAsync();
        var period = a.Chain.PeriodKey.Value;

        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();

        // Аркуш відхилено в обох документах, але схований від читача лише в проєкті A.
        var filter = new DocumentListFilter(
            DocumentStatus.Rejected, null, HiddenSheetDefIds: [(a.Chain.ProjectId, a.Chain.SheetDefId)]);

        var ids = await IdsAsync(db, period, filter, [a.Chain.ProjectId, b.ProjectId]);

        Assert.Equal([b.DocumentId], ids);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Фільтр_state_Draft_лишає_документ_A_без_видимих_аркушів_і_не_бере_B()
    {
        var (a, b) = await TwoProjectsAsync();
        var period = a.Chain.PeriodKey.Value;

        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();

        var filter = new DocumentListFilter(
            DocumentStatus.Draft, null, HiddenSheetDefIds: [(a.Chain.ProjectId, a.Chain.SheetDefId)]);

        var ids = await IdsAsync(db, period, filter, [a.Chain.ProjectId, b.ProjectId]);

        // A: відхилений аркуш схований, видимого складу немає, зведений стан - чернетка; B: аркуш видимий і відхилений.
        Assert.Contains(a.RejectedDocumentId, ids);
        Assert.DoesNotContain(b.DocumentId, ids);
    }

    [Theory]
    [InlineData("sheet")]
    [InlineData("table")]
    [InlineData("column")]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Пізня_правка_схованого_в_A_не_дає_позначки_в_A_але_дає_в_B(string kind)
    {
        var (a, b) = await TwoProjectsAsync();
        var period = a.Chain.PeriodKey.Value;
        var column = a.Chain.ColumnDefIds[0];
        var projectA = a.Chain.ProjectId;

        await WriteLateEditAsync(a.RejectedDocumentId, period, column);
        await WriteLateEditAsync(b.DocumentId, period, column);

        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();

        var filter = kind switch
        {
            "sheet" => new DocumentListFilter(null, null, true, HiddenSheetDefIds: [(projectA, a.Chain.SheetDefId)]),
            "table" => new DocumentListFilter(null, null, true, HiddenTableDefIds: [(projectA, a.Chain.TableDefId)]),
            _ => new DocumentListFilter(null, null, true, HiddenColumnDefIds: [(projectA, column)]),
        };

        var ids = await IdsAsync(db, period, filter, [projectA, b.ProjectId]);

        Assert.Equal([b.DocumentId], ids);

        // Позначка в рядку — той самий предикат: A без позначки, B з позначкою.
        var page = await new DocumentStore(db).ListAsync(
            null, new PeriodKeyFilter(period), filter with { HasLateEdits = null }, new CursorRequest(Limit: 50),
            [projectA, b.ProjectId], CancellationToken.None);
        Assert.False(page.Items.Single(d => d.Id == a.RejectedDocumentId).HasLateEdits);
        Assert.True(page.Items.Single(d => d.Id == b.DocumentId).HasLateEdits);
    }

    private static async Task<long[]> IdsAsync(
        EcrDbContext db, int periodKey, DocumentListFilter filter, int[] visibleProjects)
    {
        var page = await new DocumentStore(db).ListAsync(
            null, new PeriodKeyFilter(periodKey), filter, new CursorRequest(Limit: 50),
            visibleProjects, CancellationToken.None);

        return [.. page.Items.Select(d => d.Id)];
    }

    /// <summary>Проєкт A (ланцюг будівника) і проєкт B на ТІЙ САМІЙ версії шаблону; в обох відхилено той самий аркуш.</summary>
    private async Task<(ProjectA A, ProjectB B)> TwoProjectsAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);
        await using var db = builder.CreateContext();

        var policyId = await db.PeriodPolicies.Select(p => p.Id).FirstAsync();
        var tag = Interlocked.Increment(ref _counter).ToString(System.Globalization.CultureInfo.InvariantCulture)
                  + Guid.NewGuid().ToString("N")[..6];
        var projectB = new Project(
            EcrCode.Create($"PAIRB{tag}"), new LocalizedText(new Dictionary<string, string> { ["en"] = $"B {tag}" }),
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
            chain.TemplateVersionId, PeriodKind.Monthly, policyId, "Asia/Atyrau");
        db.Projects.Add(projectB);
        await db.SaveChangesAsync();

        var rejectedA = await RejectedDocumentAsync(db, chain.ProjectId, $"PA-{tag}", chain);
        var rejectedB = await RejectedDocumentAsync(db, projectB.Id, $"PB-{tag}", chain);

        return (new ProjectA(chain, rejectedA), new ProjectB(projectB.Id, rejectedB));
    }

    private static async Task<long> RejectedDocumentAsync(EcrDbContext db, int projectId, string key, TestDocument chain)
    {
        var document = new Document(projectId, key, 1, Now);
        db.Documents.Add(document);
        await db.SaveChangesAsync();

        db.DocumentSheets.Add(new DocumentSheet(document.Id, chain.SheetDefId));
        var state = new ApprovalState(document.Id, chain.SheetDefId, chain.PeriodKey.Value);
        state.Submit(1, Now);
        state.Reject(1, "N1-01", Now);
        db.ApprovalStates.Add(state);
        await db.SaveChangesAsync();
        return document.Id;
    }

    /// <summary>Рядок журналу — прямим ADO: <c>aud.*</c> поза моделлю EF.</summary>
    private async Task WriteLateEditAsync(long documentId, int periodKey, int columnDefId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO aud.CellChange
                (ChangedAt, PeriodKey, DocumentId, TableRowId, RowKey, ColumnDefId,
                 OldValue, NewValue, ChangedByUserId, Origin, IsLateEdit)
            VALUES (@at, @period, @document, 1, N'R1', @column, N'1', N'2', 1, N'UserEdit', 1);
            """;
        command.Parameters.AddWithValue("@at", DateTime.UtcNow);
        command.Parameters.AddWithValue("@period", periodKey);
        command.Parameters.AddWithValue("@document", documentId);
        command.Parameters.AddWithValue("@column", columnDefId);

        await command.ExecuteNonQueryAsync();
    }

    private sealed record ProjectA(TestDocument Chain, long RejectedDocumentId);

    private sealed record ProjectB(int ProjectId, long DocumentId);
}
