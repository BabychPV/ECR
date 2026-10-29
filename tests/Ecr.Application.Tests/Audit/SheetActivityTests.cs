using System.Globalization;
using System.Text;
using Ecr.Application.Audit;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Audit;

/// <summary>
/// Журнал дій аркуша (панель History): злиття двох джерел з keyset без втрат і дублів, доступ, межі читання.
/// </summary>
/// <remarks>
/// ⚠ МУТАЦІЙНІ ДОКАЗИ (виконані):
/// прибрати з <see cref="GetSheetActivityHandler"/> виклик <c>DocumentVisibility.RequireInDocumentAsync</c> -
/// червоніє <see cref="Грант_на_інший_проєкт_відповідає_як_неіснуючий_документ"/> не тут, а на рівні
/// <c>GetDocumentHandler</c>, тому окремо: замінити <c>VisibleColumnIdsOfSheet</c> на всі колонки аркуша -
/// червоніє <see cref="Колонки_закритої_таблиці_не_потрапляють_у_запит_до_журналу"/>;
/// у <see cref="SheetActivityQuery"/> замінити строгу умову курсора для джерела процесу (<c>e.Id &lt; a.Id</c>) на
/// нестрогу - червоніє <see cref="Сторінки_склеюються_без_втрат_і_дублів_на_однакових_моментах"/>.
/// </remarks>
public sealed class SheetActivityTests
{
    private const long DocumentId = 500;
    private const int SheetId = 101;
    private const int OtherSheetId = 102;
    private const int PeriodValue = 202606;
    private const int UserId = 7;
    private const int OtherProject = 99;
    private static readonly DateTime Base = new(2026, 6, 10, 8, 0, 0, DateTimeKind.Utc);

    // ───────────── злиття: keyset без втрат і дублів ─────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Сторінки_склеюються_без_втрат_і_дублів_на_однакових_моментах()
    {
        for (var seed = 1; seed <= 150; seed++)
        {
            var random = new Random(seed);
            var cells = Enumerable.Range(1, random.Next(0, 14))
                .Select(i => (Id: (long)i * 3, At: Base.AddSeconds(random.Next(0, 5))))
                .ToList();
            var events = Enumerable.Range(1, random.Next(0, 10))
                .Select(i => (Id: (long)i * 5, At: Base.AddSeconds(random.Next(0, 5))))
                .ToList();
            var limit = random.Next(1, 6);

            var query = new SheetActivityQuery(FakeAudit(cells), FakeWorkflow(events));
            var scope = new SheetActivityScope(DocumentId, PeriodValue, "SA", [301]);

            var seen = new List<string>();
            string? cursor = null;
            for (var guard = 0; guard < 100; guard++)
            {
                var page = await query.ReadAsync(scope, limit, cursor, CancellationToken.None);
                Assert.True(page.Items.Count <= limit);
                seen.AddRange(page.Items.Select(Name));
                cursor = page.NextCursor;
                if (cursor is null)
                {
                    break;
                }
            }

            // Очікуваний порядок: момент спадно, тег (правка раніше за подію), Id спадно.
            var expected = cells.Select(c => (c.At, Tag: 0, c.Id, Name: "c" + c.Id.ToString(CultureInfo.InvariantCulture)))
                .Concat(events.Select(e => (e.At, Tag: 1, e.Id, Name: "w" + e.Id.ToString(CultureInfo.InvariantCulture))))
                .OrderByDescending(x => x.At).ThenBy(x => x.Tag).ThenByDescending(x => x.Id)
                .Select(x => x.Name).ToList();

            Assert.True(expected.SequenceEqual(seen), $"seed {seed}, limit {limit}: {string.Join(',', seen)} != {string.Join(',', expected)}");
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Читач_правок_отримує_лише_людські_правки_видимих_колонок_одного_документа_і_періоду()
    {
        var audit = FakeAudit([(1, Base)]);
        var query = new SheetActivityQuery(audit, FakeWorkflow([]));

        await query.ReadAsync(new SheetActivityScope(DocumentId, PeriodValue, "SA", [301, 302], ByUserId: 9), 50, null, CancellationToken.None);

        await audit.Received(1).ReadCellChangesAsync(
            Arg.Is<CellChangeFilter>(f => f.DocumentId == DocumentId && f.PeriodKey == PeriodValue && f.HumanOnly
                && f.Sort == AuditSort.ChangedAtDesc && f.ChangedByUserId == 9 && f.VisibleColumnIds!.SequenceEqual(TwoColumns)),
            Arg.Is<CursorRequest>(p => p.Limit == 50 && p.Cursor == null),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Системні_зміни_лише_за_прапорцем_includeSystem(bool includeSystem, bool humanOnly)
    {
        var audit = FakeAudit([(1, Base)]);
        var query = new SheetActivityQuery(audit, FakeWorkflow([]));

        await query.ReadAsync(new SheetActivityScope(DocumentId, PeriodValue, "SA", [301], IncludeSystem: includeSystem), 50, null, CancellationToken.None);

        await audit.Received(1).ReadCellChangesAsync(
            Arg.Is<CellChangeFilter>(f => f.HumanOnly == humanOnly), Arg.Any<CursorRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Системна_зміна_підписується_system_а_імпорт_лишається_людиною()
    {
        var audit = Substitute.For<IAuditReader>();
        audit.ReadCellChangesAsync(Arg.Any<CellChangeFilter>(), Arg.Any<CursorRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<CellChangeView>(
            [
                new CellChangeView(Base.AddSeconds(2), PeriodValue, DocumentId, "R1", 301, "1", "2", 5, "Recalculation", false, "Bot", Id: 2),
                new CellChangeView(Base.AddSeconds(1), PeriodValue, DocumentId, "R1", 301, "1", "2", 6, "Import", false, "Olena", Id: 1),
            ], null, null));
        var query = new SheetActivityQuery(audit, FakeWorkflow([]));

        var page = await query.ReadAsync(
            new SheetActivityScope(DocumentId, PeriodValue, "SA", [301], IncludeSystem: true), 50, null, CancellationToken.None);

        Assert.Equal(["system", "Olena"], page.Items.Select(i => i.ByDisplayName));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Події_іншого_аркуша_і_іншого_користувача_не_потрапляють()
    {
        var workflow = Substitute.For<IWorkflowStore>();
        workflow.GetHistoryAsync(DocumentId, Arg.Any<PeriodKey>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([Event("SA", 1, 7, "Reject", "bad"), Event("SB", 2, 7, "Reject", "other sheet"), Event("SA", 3, 8, "Submit", null)]);
        var query = new SheetActivityQuery(FakeAudit([]), workflow);

        var page = await query.ReadAsync(
            new SheetActivityScope(DocumentId, PeriodValue, "SA", [], ByUserId: 7), 50, null, CancellationToken.None);

        var only = Assert.Single(page.Items);
        Assert.Equal(("Workflow", "Reject", "bad"), (only.Kind, only.Action, only.Reason));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Курсор_зіпсований_або_чужого_формату_це_початок()
    {
        Assert.Null(ActivityCursor.Decode("not-base64!"));
        Assert.Null(ActivityCursor.Decode(Convert.ToBase64String(Encoding.UTF8.GetBytes("1/9/2"))));
        Assert.Null(ActivityCursor.Decode(Convert.ToBase64String(Encoding.UTF8.GetBytes("abc"))));
        Assert.Equal((Base, 1, 5L), ActivityCursor.Decode(ActivityCursor.Encode(Base, 1, 5))!.Value);
    }

    // ───────────── доступ ─────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Читач_бачить_лише_свій_аркуш_і_видимі_колонки()
    {
        var audit = FakeAudit([(1, Base)]);
        var handler = Handler(Reader().Deny(ResourceKind.Table, HiddenTable), audit, FakeWorkflow([]));

        var page = await handler.HandleAsync(DocumentId, SheetId, PeriodValue, null, false, 0, null, CancellationToken.None);

        Assert.Single(page.Items);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Колонки_закритої_таблиці_не_потрапляють_у_запит_до_журналу()
    {
        var audit = FakeAudit([(1, Base)]);
        var handler = Handler(Reader().Deny(ResourceKind.Table, HiddenTable), audit, FakeWorkflow([]));

        await handler.HandleAsync(DocumentId, SheetId, PeriodValue, null, false, 0, null, CancellationToken.None);

        // Тільки колонка відкритої таблиці: 301. Закрита таблиця (302) не бачиться навіть як ідентифікатор.
        await audit.Received(1).ReadCellChangesAsync(
            Arg.Is<CellChangeFilter>(f => f.VisibleColumnIds!.SequenceEqual(OnlyVisible)),
            Arg.Any<CursorRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Закритий_цілком_аркуш_і_невідомий_аркуш_відповідають_як_неіснуючий_документ()
    {
        var audit = FakeAudit([(1, Base)]);
        var workflow = FakeWorkflow([(1, Base)]);
        var handler = Handler(Reader().Deny(ResourceKind.Sheet, SheetId), audit, workflow);

        foreach (var sheet in new[] { SheetId, 9999 })
        {
            var error = await Assert.ThrowsAsync<NotFoundException>(
                () => handler.HandleAsync(DocumentId, sheet, PeriodValue, null, false, 0, null, CancellationToken.None));
            Assert.Equal("ECR-DOC-0404", error.ErrorCode);
        }

        await audit.DidNotReceiveWithAnyArgs().ReadCellChangesAsync(default!, default!, default);
        await workflow.DidNotReceiveWithAnyArgs().GetHistoryAsync(default, default, default, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Грант_на_інший_проєкт_відповідає_як_неіснуючий_документ()
    {
        // ⚠ Document.View Є, але у ІНШОМУ проєкті; у проєкті документа гранта немає.
        var audit = FakeAudit([(1, Base)]);
        var workflow = FakeWorkflow([(1, Base)]);
        var other = new AccessBuilder { UserId = UserId }
            .Permission("Document.View").Grant(ResourceKind.Project, OtherProject, GrantLevel.Read);
        var handler = Handler(other, audit, workflow);

        var error = await Assert.ThrowsAsync<NotFoundException>(
            () => handler.HandleAsync(DocumentId, SheetId, PeriodValue, null, false, 0, null, CancellationToken.None));

        Assert.Equal("ECR-DOC-0404", error.ErrorCode);
        await audit.DidNotReceiveWithAnyArgs().ReadCellChangesAsync(default!, default!, default);
        await workflow.DidNotReceiveWithAnyArgs().GetHistoryAsync(default, default, default, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Без_права_Document_View_відмова_403_і_журнали_не_читаються()
    {
        var audit = FakeAudit([(1, Base)]);
        var workflow = FakeWorkflow([(1, Base)]);
        var handler = Handler(new AccessBuilder { UserId = UserId }, audit, workflow);

        var error = await Assert.ThrowsAsync<AccessDeniedException>(
            () => handler.HandleAsync(DocumentId, SheetId, PeriodValue, null, false, 0, null, CancellationToken.None));

        Assert.Equal(GetSheetActivityHandler.Permission, error.Details!["permission"]);
        await audit.DidNotReceiveWithAnyArgs().ReadCellChangesAsync(default!, default!, default);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Розмір_сторінки_поза_межами_422(int limit)
    {
        var handler = Handler(Reader(), FakeAudit([]), FakeWorkflow([]));

        await Assert.ThrowsAsync<BusinessRuleException>(
            () => handler.HandleAsync(DocumentId, SheetId, PeriodValue, null, false, limit, null, CancellationToken.None));
    }

    // ───────────── збірка ─────────────

    private static readonly int[] TwoColumns = [301, 302];
    private static readonly int[] OnlyVisible = [301];
    private const int VisibleTable = 201;
    private const int HiddenTable = 202;
    private const int VisibleColumn = 301;
    private const int HiddenColumn = 302;

    private static string Name(SheetActivityItem item)
        => item.Kind == SheetActivityKinds.CellEdit ? "c" + item.NewValue : "w" + item.Reason;

    private static ApprovalEventRecord Event(string sheet, long id, int by, string action, string? reason)
        => new(sheet, DocumentStatus.Submitted, DocumentStatus.Rejected, Enum.Parse<ApprovalAction>(action), by, "User " + by.ToString(CultureInfo.InvariantCulture),
            Base.AddMinutes(id), reason, null, Id: id);

    private static IAuditReader FakeAudit(IReadOnlyList<(long Id, DateTime At)> cells)
    {
        var audit = Substitute.For<IAuditReader>();
        audit.ReadCellChangesAsync(Arg.Any<CellChangeFilter>(), Arg.Any<CursorRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var page = call.Arg<CursorRequest>();
                var after = DecodeCell(page.Cursor);
                var rows = cells
                    .OrderByDescending(c => c.At).ThenByDescending(c => c.Id)
                    .Where(c => after is not { } a || c.At < a.At || (c.At == a.At && c.Id < a.Id))
                    .ToList();
                var items = rows.Take(page.Limit)
                    .Select(c => new CellChangeView(c.At, PeriodValue, DocumentId, "R1", VisibleColumn, "0", c.Id.ToString(CultureInfo.InvariantCulture), UserId, "UserEdit", false, "User 7", Id: c.Id))
                    .ToList();
                var next = rows.Count > page.Limit
                    ? ActivityCursor.EncodeCellPosition(rows[page.Limit - 1].At, rows[page.Limit - 1].Id)
                    : null;

                return Task.FromResult(new PagedResult<CellChangeView>(items, next, null));
            });
        return audit;
    }

    private static (DateTime At, long Id)? DecodeCell(string? cursor)
    {
        if (cursor is null)
        {
            return null;
        }

        // Спільний кодек курсора журналів аудиту (`AuditCursorCodec`, порядок «найновіші першими»).
        var decoded = AuditCursorCodec.TryDecode(cursor) ?? throw new InvalidOperationException("курсор не розпізнано");
        return (decoded.At, decoded.Id);
    }

    private static IWorkflowStore FakeWorkflow(IReadOnlyList<(long Id, DateTime At)> events)
    {
        var workflow = Substitute.For<IWorkflowStore>();
        workflow.GetHistoryAsync(DocumentId, Arg.Any<PeriodKey>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(events
                .OrderByDescending(e => e.At).ThenByDescending(e => e.Id)
                .Select(e => new ApprovalEventRecord(
                    "SA", DocumentStatus.Submitted, DocumentStatus.Rejected, ApprovalAction.Reject, UserId, "User 7", e.At,
                    e.Id.ToString(CultureInfo.InvariantCulture), null, Id: e.Id))
                .ToList());
        return workflow;
    }

    private static AccessBuilder Reader()
        => new AccessBuilder { UserId = UserId }
            .Permission("Document.View")
            .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Read);

    private static GetSheetActivityHandler Handler(AccessBuilder profile, IAuditReader audit, IWorkflowStore workflow)
    {
        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>()).Returns(profile.Build());

        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(UserId);

        var snapshot = BuildSnapshot();
        access.ReadScopeAsync(Arg.Any<AccessProfile>(), DocumentId, Arg.Any<CancellationToken>())
            .Returns(call => DocumentReadScope.For(call.Arg<AccessProfile>(), AccessBuilder.ProjectId, snapshot));

        var documents = Substitute.For<IDocumentStore>();
        documents.FindAsync(DocumentId, Arg.Any<PeriodKeyFilter>(), Arg.Any<CancellationToken>())
            .Returns(new DocumentSummary(
                DocumentId, AccessBuilder.ProjectId, "DOC-500", Base, 1, new Dictionary<string, string>(StringComparer.Ordinal)));

        return new GetSheetActivityHandler(
            new GetDocumentHandler(documents, access, user), new SheetActivityQuery(audit, workflow), access, user);
    }

    private static TemplateVersionSnapshot BuildSnapshot()
    {
        var columns = new Dictionary<int, ColumnDef>();
        var sheet = new SheetDef(1, EcrCode.Create("SA"), Text("A"), SheetId);
        SetId(sheet, SheetId);

        foreach (var (tableId, columnId) in new[] { (VisibleTable, VisibleColumn), (HiddenTable, HiddenColumn) })
        {
            var table = new TableDef(
                SheetId, EcrCode.Create("T" + tableId.ToString(CultureInfo.InvariantCulture)), Text("T"), tableId,
                TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
            SetId(table, tableId);
            var column = new ColumnDef(
                tableId, EcrCode.Create("C" + columnId.ToString(CultureInfo.InvariantCulture)), Text("C"), columnId, CellDataType.Decimal);
            SetId(column, columnId);
            table.AddColumn(column);
            columns[columnId] = column;
            sheet.AddTable(table);
        }

        var other = new SheetDef(1, EcrCode.Create("SB"), Text("B"), OtherSheetId);
        SetId(other, OtherSheetId);

        return new TemplateVersionSnapshot(1, 0, [sheet, other], columns, new Dictionary<(int, string), RowDef>());
    }

    private static LocalizedText Text(string s) => new(new Dictionary<string, string> { ["en"] = s });

    private static void SetId<T>(T e, int id) where T : Entity<int>
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(e, id);
}
