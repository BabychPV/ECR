// tests/Ecr.Infrastructure.Tests/Persistence/DocumentSourceIssuesTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
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
/// B80 (N4, R3.2): проблеми джерела документа-споживача (<see cref="DocumentSourceMonthGate.ListSourceIssuesAsync"/>,
/// тіло — <see cref="DocumentSourceIssues"/>) на справжньому SQL Server: <c>SourceMonthNotApproved</c> для
/// незатвердженого місяця споживача, <c>SourceChangedAfterApproval</c> для затвердженого, чиє джерело відтоді
/// повернули в роботу або затвердили пізніше.
/// </summary>
/// <remarks>
/// ⚠ Правило 394: шаблон-джерело, довідник і зв'язка в кожного тесту власні; період 2099xx.
/// <para>
/// Мутації (див. звіт N4): фільтр <c>SourceState == ApprovedOnly</c> у <c>ConsumerBindingsAsync</c> → без нього — рядок
/// Any червоний; вибір споживача (<c>c.TemplateId == …</c>) → <c>true</c> — «не споживач» червоний; гілка «джерело не
/// затверджено» в <c>Classify</c> → <c>(null, null)</c> — «повернуто в роботу» червоний; порівняння
/// <c>sourceApprovedAt &gt; approvedAt</c> → <c>false</c> — «затверджено пізніше» червоний.
/// </para>
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage8)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Directive", "B80")]
public sealed class DocumentSourceIssuesTests(SqlServerFixture sql)
{
    private const int Year = 2099;
    private const int March = (Year * 100) + 3;
    private const int April = (Year * 100) + 4;
    private static readonly DateTime T0 = new(2026, 11, 18, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T1 = new(2026, 11, 18, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T2 = new(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>Пара в одному світі: за <c>Any</c> — порожньо, за <c>ApprovedOnly</c> — місяць названо.</summary>
    [Theory]
    [InlineData(DocumentSourceState.Any, 0)]
    [InlineData(DocumentSourceState.ApprovedOnly, 1)]
    public async Task Any_порожньо_ApprovedOnly_незатверджений_місяць_джерела(DocumentSourceState state, int expected)
    {
        // ⚠ Чужа зв'язка ApprovedOnly у базі є завжди: інакше рядок Any зеленів би на дрібній загальній перевірці
        // (AnyApprovedOnlyAsync), а не на фільтрі зв'язок самого документа.
        await ArrangeAsync(DocumentSourceState.ApprovedOnly);
        var world = await ArrangeAsync(state);

        var issues = await ListAsync(world.Land.DocumentId, March);

        Assert.Equal(expected, issues.Count);
    }

    [Fact]
    public async Task SourceMonthNotApproved_названо_з_документом_і_зникає_після_затвердження_джерела()
    {
        var world = await ArrangeAsync(DocumentSourceState.ApprovedOnly);
        var source = await AddStateAsync(world.Source.DocumentId, world.Source.SheetDefId, March, submittedAt: T0);

        var issue = Assert.Single(await ListAsync(world.Land.DocumentId, March));
        Assert.Equal(ISourceIssueReader.SourceMonthNotApproved, issue.Kind);
        Assert.Equal((March, 3, March), (issue.PeriodKey, issue.MonthNumber, issue.SourcePeriodKey));
        Assert.Equal(world.Source.DocumentId, issue.SourceDocumentId);
        Assert.Equal(world.SourceProjectCode, issue.SourceProjectCode);
        Assert.Equal(world.SourceDocumentKey, issue.SourceDocumentKey);
        Assert.Null(issue.SourceApprovedAt);
        Assert.Null(issue.ApprovedAt);

        await ApproveAsync(source, T1);

        Assert.Empty(await ListAsync(world.Land.DocumentId, March));
    }

    /// <summary>(a) Land затверджено після джерела, потім джерело повернули в роботу — розбіжність, причина notApproved.</summary>
    [Fact]
    public async Task SourceChangedAfterApproval_джерело_повернуто_в_роботу_після_затвердження_Land()
    {
        var world = await ArrangeAsync(DocumentSourceState.ApprovedOnly);
        var source = await AddStateAsync(world.Source.DocumentId, world.Source.SheetDefId, March, submittedAt: T0, approvedAt: T0);
        await AddStateAsync(world.Land.DocumentId, world.Land.SheetDefId, March, submittedAt: T1, approvedAt: T1);
        Assert.Empty(await ListAsync(world.Land.DocumentId, March));

        await ReopenAsync(source, T2);

        var finding = Assert.Single(await FindingsAsync(world.Land.DocumentId, March));
        Assert.Equal(ISourceIssueReader.SourceChangedAfterApproval, finding.Row.Kind);
        Assert.Equal(DocumentSourceIssues.ReasonNotApproved, finding.Reason);
        Assert.Null(finding.Row.SourceApprovedAt);
        Assert.Equal(T1, finding.Row.ApprovedAt);
        Assert.Equal(world.Source.DocumentId, finding.Row.SourceDocumentId);
    }

    /// <summary>(b) Джерело затверджено ПІЗНІШЕ за Land — розбіжність, причина reapproved (консервативно, R3.2).</summary>
    [Fact]
    public async Task SourceChangedAfterApproval_джерело_затверджено_пізніше_за_Land()
    {
        var world = await ArrangeAsync(DocumentSourceState.ApprovedOnly);
        await AddStateAsync(world.Land.DocumentId, world.Land.SheetDefId, March, submittedAt: T0, approvedAt: T1);
        await AddStateAsync(world.Source.DocumentId, world.Source.SheetDefId, March, submittedAt: T0, approvedAt: T2);

        var finding = Assert.Single(await FindingsAsync(world.Land.DocumentId, March));
        Assert.Equal(ISourceIssueReader.SourceChangedAfterApproval, finding.Row.Kind);
        Assert.Equal(DocumentSourceIssues.ReasonReapproved, finding.Reason);
        Assert.Equal(T2, finding.Row.SourceApprovedAt);
        Assert.Equal(T1, finding.Row.ApprovedAt);
    }

    [Fact]
    public async Task Land_затверджено_після_джерела_проблеми_немає()
    {
        var world = await ArrangeAsync(DocumentSourceState.ApprovedOnly);
        await AddStateAsync(world.Source.DocumentId, world.Source.SheetDefId, March, submittedAt: T0, approvedAt: T1);
        await AddStateAsync(world.Land.DocumentId, world.Land.SheetDefId, March, submittedAt: T1, approvedAt: T2);

        Assert.Empty(await ListAsync(world.Land.DocumentId, March));
    }

    /// <summary>Документ шаблону, що не є споживачем зв'язки, — порожньо; споживач того ж світу — проблема є.</summary>
    [Fact]
    public async Task Документ_не_споживача_проблем_не_має()
    {
        var world = await ArrangeAsync(DocumentSourceState.ApprovedOnly);
        var other = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey: March);

        Assert.NotEmpty(await ListAsync(world.Land.DocumentId, March));
        Assert.Empty(await ListAsync(other.DocumentId, March));
    }

    /// <summary>
    /// Кілька періодів одним викликом (річна розкладка документа — місяці року): березень джерела затверджено, квітень —
    /// ні; названо лише квітень.
    /// </summary>
    [Fact]
    public async Task Місяці_року_кожен_за_своїм_станом_джерела()
    {
        var world = await ArrangeAsync(DocumentSourceState.ApprovedOnly);
        await AddStateAsync(world.Source.DocumentId, world.Source.SheetDefId, March, submittedAt: T0, approvedAt: T0);

        var issue = Assert.Single(await ListAsync(world.Land.DocumentId, March, April));
        Assert.Equal((April, 4, April), (issue.PeriodKey, issue.MonthNumber, issue.SourcePeriodKey));
    }

    /// <summary>
    /// B182 (K2): з двох аркушів Land за період затверджено лише один — місяць споживача НЕ затверджено. Джерело не
    /// затверджено — <c>SourceMonthNotApproved</c> (не <c>SourceChangedAfterApproval</c>); джерело затверджено пізніше за
    /// затверджений аркуш — проблеми немає (не «reapproved»).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Затверджено_лише_один_з_двох_аркушів_Land_місяць_не_затверджений(bool sourceApprovedLater)
    {
        var world = await ArrangeAsync(DocumentSourceState.ApprovedOnly);
        var sheetB = await SourceIssueWorld.AddSheetAsync(sql, world.Land);
        await AddStateAsync(
            world.Source.DocumentId, world.Source.SheetDefId, March, submittedAt: T0,
            approvedAt: sourceApprovedLater ? T2 : null);
        await AddStateAsync(world.Land.DocumentId, world.Land.SheetDefId, March, submittedAt: T0, approvedAt: T1);
        await AddStateAsync(world.Land.DocumentId, sheetB, March, submittedAt: T1);

        var findings = await FindingsAsync(world.Land.DocumentId, March);

        if (sourceApprovedLater)
        {
            Assert.Empty(findings);
            return;
        }

        var finding = Assert.Single(findings);
        Assert.Equal(ISourceIssueReader.SourceMonthNotApproved, finding.Row.Kind);
        Assert.Null(finding.Reason);
        Assert.Null(finding.Row.ApprovedAt);
    }

    /// <summary>
    /// B182 (K3): два аркуші Land затверджено в T0 і T2, джерело — в T1 між ними. Момент затвердження споживача —
    /// НАЙРАНІШИЙ (T0), тож джерело затверджено пізніше — <c>reapproved</c>.
    /// </summary>
    [Fact]
    public async Task Два_аркуші_Land_момент_затвердження_найраніший()
    {
        var world = await ArrangeAsync(DocumentSourceState.ApprovedOnly);
        var sheetB = await SourceIssueWorld.AddSheetAsync(sql, world.Land);
        await AddStateAsync(world.Land.DocumentId, world.Land.SheetDefId, March, submittedAt: T0, approvedAt: T0);
        await AddStateAsync(world.Land.DocumentId, sheetB, March, submittedAt: T0, approvedAt: T2);
        await AddStateAsync(world.Source.DocumentId, world.Source.SheetDefId, March, submittedAt: T0, approvedAt: T1);

        var finding = Assert.Single(await FindingsAsync(world.Land.DocumentId, March));
        Assert.Equal(ISourceIssueReader.SourceChangedAfterApproval, finding.Row.Kind);
        Assert.Equal(DocumentSourceIssues.ReasonReapproved, finding.Reason);
        Assert.Equal(T0, finding.Row.ApprovedAt);
        Assert.Equal(T1, finding.Row.SourceApprovedAt);
    }

    /// <summary>Читання з документа вимкнено явним ключем — порожньо навіть за <c>ApprovedOnly</c>.</summary>
    [Fact]
    public async Task Явний_ключ_не_читати_з_документа_проблем_немає()
    {
        var world = await ArrangeAsync(DocumentSourceState.ApprovedOnly);

        Assert.NotEmpty(await ListAsync(world.Land.DocumentId, March));
        await using var db = sql.CreateContext();
        Assert.Empty(await new DocumentSourceMonthGate(db, readOverride: false, DependencyGraphTestKit.Provider(db))
            .ListSourceIssuesAsync(world.Land.DocumentId, [March], CancellationToken.None));
    }

    private async Task<IReadOnlyList<SourceIssueRow>> ListAsync(long documentId, params int[] periodKeys)
    {
        await using var db = sql.CreateContext();
        return await new DocumentSourceMonthGate(db, readOverride: null, DependencyGraphTestKit.Provider(db))
            .ListSourceIssuesAsync(documentId, periodKeys, CancellationToken.None);
    }

    private async Task<List<SourceIssueFinding>> FindingsAsync(long documentId, params int[] periodKeys)
    {
        await using var db = sql.CreateContext();
        return await new DocumentSourceIssues(db, readOverride: null, DependencyGraphTestKit.Provider(db))
            .ListAsync(documentId, periodKeys, CancellationToken.None);
    }

    private Task<long> AddStateAsync(
        long documentId, int sheetDefId, int periodKey, DateTime submittedAt, DateTime? approvedAt = null)
        => SourceIssueWorld.AddStateAsync(sql, documentId, sheetDefId, periodKey, submittedAt, approvedAt);

    private async Task ApproveAsync(long stateId, DateTime at)
    {
        await using var db = sql.CreateContext();
        var state = await db.ApprovalStates.SingleAsync(s => s.Id == stateId);
        state.Approve(userId: 6, at);
        await db.SaveChangesAsync();
    }

    private Task ReopenAsync(long stateId, DateTime at) => SourceIssueWorld.ReopenAsync(sql, stateId, at);

    private Task<SourceIssueWorld.World> ArrangeAsync(DocumentSourceState state) => SourceIssueWorld.ArrangeAsync(sql, state);
}

/// <summary>Світ тестів B80 N4: документ-джерело, зв'язка і документ Land (правило 394: усе власне, період 2099xx).</summary>
internal static class SourceIssueWorld
{
    /// <summary>Березень 2099 — місяць екземпляра джерела.</summary>
    public const int March = 209903;

    private static readonly DateTime Stamp = new(2026, 11, 18, 8, 0, 0, DateTimeKind.Utc);

    /// <summary>Стан оригіналу аркуша: подано, за потреби затверджено.</summary>
    public static async Task<long> AddStateAsync(
        SqlServerFixture fixture, long documentId, int sheetDefId, int periodKey, DateTime submittedAt, DateTime? approvedAt = null)
    {
        await using var db = fixture.CreateContext();
        var state = new ApprovalState(documentId, sheetDefId, periodKey);
        state.Submit(userId: 5, submittedAt);
        if (approvedAt is { } at)
        {
            state.Approve(userId: 6, at);
        }

        db.ApprovalStates.Add(state);
        await db.SaveChangesAsync();
        return state.Id;
    }

    /// <summary>Другий аркуш (оригінал) шаблону документа — для документа з кількома аркушами (B182).</summary>
    public static async Task<int> AddSheetAsync(SqlServerFixture fixture, TestDocument document)
    {
        await using var db = fixture.CreateContext();
        var sheet = new SheetDef(
            document.TemplateVersionId, EcrCode.Create($"SHEETB{document.SheetDefId}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Sheet B" }), 2);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync();
        return sheet.Id;
    }

    /// <summary>Стан КОПІЇ аркуша (<c>SheetCopyId</c> задано): подано, за потреби затверджено (B182).</summary>
    public static async Task<long> AddCopyStateAsync(
        SqlServerFixture fixture, long documentId, int sheetDefId, int periodKey, DateTime submittedAt, DateTime? approvedAt = null)
    {
        await using var db = fixture.CreateContext();
        var original = new DocumentSheet(documentId, sheetDefId);
        db.DocumentSheets.Add(original);
        await db.SaveChangesAsync();
        var copy = original.CreateCopy(2);
        db.DocumentSheets.Add(copy);
        await db.SaveChangesAsync();

        var state = new ApprovalState(documentId, sheetDefId, periodKey, copy.Id);
        state.Submit(userId: 5, submittedAt);
        if (approvedAt is { } at)
        {
            state.Approve(userId: 6, at);
        }

        db.ApprovalStates.Add(state);
        await db.SaveChangesAsync();
        return state.Id;
    }

    /// <summary>Повертає затверджений стан у роботу.</summary>
    public static async Task ReopenAsync(SqlServerFixture fixture, long stateId, DateTime at)
    {
        await using var db = fixture.CreateContext();
        var state = await db.ApprovalStates.SingleAsync(s => s.Id == stateId);
        state.Reopen(userId: 7, "B80 N4: виправлення джерела", at);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Документ-джерело (власний шаблон з таблицею потоку <c>FG_Internal</c>, екземпляр за березень 2099), власний
    /// довідник, зв'язка в заданому стані і документ Land, чий шаблон — споживач.
    /// </summary>
    public static async Task<World> ArrangeAsync(SqlServerFixture fixture, DocumentSourceState state)
    {
        var sql = fixture;
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var source = await builder.BuildAsync(periodKey: March);
        var land = await builder.BuildAsync(periodKey: March);

        await using var db = sql.CreateContext();
        var stream = new TableDef(
            source.SheetDefId, EcrCode.Create("FG_Internal"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "FG_Internal" }), 50,
            TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
        db.TableDefs.Add(stream);
        await db.SaveChangesAsync();

        var instanceId = await new BulkCellLoader(sql.ConnectionString, 1000)
            .ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);
        db.TableInstances.Add(new TableInstance(new PeriodKey(March), instanceId, source.DocumentId, stream.Id, Stamp));

        var registry = new RegistryDef(
            EcrCode.Create($"HN{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Composition" }),
            isTemporal: false);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync();

        var binding = new DocumentSourceBinding($"B80N4_{registry.Code}", source.TemplateId, registry.Id);
        db.DocumentSourceBindings.Add(binding);
        await db.SaveChangesAsync();
        await DependencyGraphTestKit.MakeConsumerAsync(db, land, registry.Code);
        await db.DocumentSourceBindings.Where(b => b.Id == binding.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.SourceState, state));

        var facts = await (
                from d in db.Documents.AsNoTracking()
                where d.Id == source.DocumentId
                join p in db.Projects.AsNoTracking() on d.ProjectId equals p.Id
                select new { ProjectCode = p.Code, d.BusinessKey })
            .SingleAsync();

        return new World(source, land, binding.Id, facts.ProjectCode, facts.BusinessKey);
    }

    /// <summary>Світ тесту.</summary>
    /// <param name="Source">Документ-джерело.</param>
    /// <param name="Land">Документ-споживач.</param>
    /// <param name="BindingId">Зв'язка.</param>
    /// <param name="SourceProjectCode">Код проєкту джерела.</param>
    /// <param name="SourceDocumentKey">Бізнес-ключ джерела.</param>
    public sealed record World(
        TestDocument Source, TestDocument Land, int BindingId, string SourceProjectCode, string SourceDocumentKey);
}
