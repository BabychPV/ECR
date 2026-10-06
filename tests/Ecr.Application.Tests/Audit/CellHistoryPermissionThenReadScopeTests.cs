using Ecr.Application.Audit;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Audit;

/// <summary>
/// Історія комірки: СПЕРШУ проєктне право ФВ-6.14 (<c>Document.View</c> у
/// проєкті документа), ПОТІМ межі читання S6 (ФВ-6.6).
/// </summary>
/// <remarks>
/// ⛔ Два порядки дають різну відповідь на одному запиті: без права на
/// прихованій колонці правильна відповідь — 403, а не «порожньо». Якщо фільтр
/// S6 стоїть першим, запит без права отримує порожню сторінку, і відповідь
/// залежить від заборони, а не від права.
///
/// ⚠ МУТАЦІЙНИЙ ДОКАЗ: переставити в <see cref="GetCellChangesHandler"/> блок
/// ReadScopeAsync перед перевіркою <c>DocumentProjectIdAsync</c> —
/// червоніє <see cref="Без_права_в_проєкті_на_прихованій_колонці_403_і_журнал_не_читається"/>;
/// прибрати фільтр рядків — червоніє <see cref="З_правом_журнал_документа_без_рядків_прихованої_колонки"/>.
/// </remarks>
public sealed class CellHistoryPermissionThenReadScopeTests
{
    private const int Project = AccessBuilder.ProjectId;
    private const int OtherProject = 99;
    private const long DocumentId = 500;
    private const int Sheet = 101;
    private const int Table = 201;
    private const int VisibleColumn = 301;
    private const int HiddenColumn = 302;

    private static readonly DateTime From = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = From.AddDays(30);
    private static readonly TemplateVersionSnapshot Snapshot = BuildSnapshot();

    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IAuditReader _audit = Substitute.For<IAuditReader>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    public CellHistoryPermissionThenReadScopeTests()
    {
        _user.UserId.Returns(7);
        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), DocumentId, Arg.Any<CancellationToken>())
            .Returns(EditDecision.Allow());
        _access.DocumentProjectIdAsync(DocumentId, Arg.Any<CancellationToken>()).Returns(Project);
        _access.ReadScopeAsync(Arg.Any<AccessProfile>(), DocumentId, Arg.Any<CancellationToken>())
            .Returns(ci => DocumentReadScope.For(ci.Arg<AccessProfile>(), Project, Snapshot));
        _audit.ReadCellChangesAsync(Arg.Any<CellChangeFilter>(), Arg.Any<CursorRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<CellChangeView>([Change(VisibleColumn), Change(HiddenColumn)], null, 2));

        // Сирі лічильники вікна: 3 зміни видимої колонки і 40 — прихованої.
        _audit.CountCellChangesByColumnAsync(Arg.Any<CellChangeFilter>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(
            [
                new CellChangeColumnCount(VisibleColumn, 3, 1, 0, 0),
                new CellChangeColumnCount(HiddenColumn, 40, 9, 0, 0),
            ]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task TotalCount_журналу_документа_лічить_лише_видимі_колонки()
    {
        // R-11 / UI-38 C4: загальне число не повинно виказувати 40 змін прихованої колонки.
        Profile(Reader(deny: true).Permission(GetCellChangesHandler.Permission), scopedDocumentViewIn: null);

        var result = await Handler().HandleAsync(
            new CellChangeFilter(From, To, DocumentId: DocumentId), new CursorRequest(), CancellationToken.None);

        Assert.Equal(3, result.TotalCount);
        Assert.Equal([VisibleColumn], result.Items.Select(c => c.ColumnDefId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task TotalCount_без_заборон_і_наскрізного_журналу_лічить_усе()
    {
        Profile(Reader(deny: false).Permission(GetCellChangesHandler.Permission), scopedDocumentViewIn: null);

        var perDocument = await Handler().HandleAsync(
            new CellChangeFilter(From, To, DocumentId: DocumentId), new CursorRequest(), CancellationToken.None);
        var global = await Handler().HandleAsync(new CellChangeFilter(From, To), new CursorRequest(), CancellationToken.None);

        Assert.Equal(43, perDocument.TotalCount);

        // Наскрізний журнал за призначенням поза межами S6 (Q-177): число збігається зі сторінкою.
        Assert.Equal(43, global.TotalCount);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task TotalCount_історії_прихованої_колонки_нуль_і_лічильники_не_читаються()
    {
        Profile(Reader(deny: true), scopedDocumentViewIn: Project);

        var result = await Handler().HandleAsync(SingleCell(HiddenColumn), new CursorRequest(), CancellationToken.None);

        Assert.Equal(0, result.TotalCount);
        await _audit.DidNotReceiveWithAnyArgs().CountCellChangesByColumnAsync(default!, default, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Без_права_в_проєкті_на_прихованій_колонці_403_і_журнал_не_читається()
    {
        // `Document.View` є лише в ІНШОМУ проєкті: вхідна перевірка
        // (HasInAnyProject) пропускає, проєктна — ні. Колонка ще й під
        // забороною — і все одно 403, а не порожня сторінка.
        Profile(Reader(deny: true), scopedDocumentViewIn: OtherProject);

        var error = await Assert.ThrowsAsync<AccessDeniedException>(
            () => Handler().HandleAsync(SingleCell(HiddenColumn), new CursorRequest(), CancellationToken.None));

        Assert.Equal("ECR-AUTH-0403", error.ErrorCode);
        Assert.Equal(GetCellChangesHandler.CellHistoryPermission, error.Details!["permission"]);
        await _audit.DidNotReceiveWithAnyArgs().ReadCellChangesAsync(default!, default!, default);
        await _access.DidNotReceiveWithAnyArgs().ReadScopeAsync(default!, default, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task З_правом_історія_прихованої_колонки_порожня()
    {
        Profile(Reader(deny: true), scopedDocumentViewIn: Project);

        var result = await Handler().HandleAsync(SingleCell(HiddenColumn), new CursorRequest(), CancellationToken.None);

        Assert.Empty(result.Items);
        await _audit.DidNotReceiveWithAnyArgs().ReadCellChangesAsync(default!, default!, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task З_правом_журнал_документа_без_рядків_прихованої_колонки()
    {
        // Журнал документа (без адреси комірки) — лише за `Security.ViewAudit`.
        Profile(Reader(deny: true).Permission(GetCellChangesHandler.Permission), scopedDocumentViewIn: null);

        var result = await Handler().HandleAsync(
            new CellChangeFilter(From, To, DocumentId: DocumentId), new CursorRequest(), CancellationToken.None);

        Assert.Equal([VisibleColumn], result.Items.Select(c => c.ColumnDefId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task З_правом_без_заборон_повний_журнал()
    {
        Profile(Reader(deny: false), scopedDocumentViewIn: Project);

        var single = await Handler().HandleAsync(SingleCell(VisibleColumn), new CursorRequest(), CancellationToken.None);
        Assert.Equal(2, single.Items.Count);

        Profile(Reader(deny: false).Permission(GetCellChangesHandler.Permission), scopedDocumentViewIn: null);
        var journal = await Handler().HandleAsync(
            new CellChangeFilter(From, To, DocumentId: DocumentId), new CursorRequest(), CancellationToken.None);

        Assert.Equal([VisibleColumn, HiddenColumn], journal.Items.Select(c => c.ColumnDefId));
    }

    private GetCellChangesHandler Handler() => new(_audit, _access, _user);

    private static CellChangeFilter SingleCell(int column)
        => new(From, To, DocumentId: DocumentId, RowKey: "R1", ColumnDefId: column);

    private static AccessBuilder Reader(bool deny)
    {
        var builder = new AccessBuilder().Grant(ResourceKind.Project, Project, GrantLevel.Read);
        return deny ? builder.Deny(ResourceKind.Column, HiddenColumn) : builder;
    }

    private void Profile(AccessBuilder builder, int? scopedDocumentViewIn)
    {
        var profile = builder.Build();
        if (scopedDocumentViewIn is { } projectId)
        {
            profile = new AccessProfile
            {
                CacheKey = profile.CacheKey,
                UserId = profile.UserId,
                SecurityStamp = profile.SecurityStamp,
                Permissions = profile.Permissions,
                Grants = profile.Grants,
                Denies = profile.Denies,
                RoleIds = profile.RoleIds,
                Scoped = new Dictionary<int, ScopedProjectAccess>
                {
                    [projectId] = new(
                        new Dictionary<string, GrantLevel>(),
                        new HashSet<string>(),
                        new HashSet<int>(),
                        new HashSet<string> { GetCellChangesHandler.CellHistoryPermission }),
                },
            };
        }

        _access.BuildProfileAsync(7, Arg.Any<CancellationToken>()).Returns(profile);
    }

    private static CellChangeView Change(int column)
        => new(From.AddDays(1), 202605, DocumentId, "R1", column, "1", "2", 7, "Manual", false);

    private static TemplateVersionSnapshot BuildSnapshot()
    {
        var sheet = new SheetDef(1, EcrCode.Create("SA"), Text("A"), Sheet);
        SetId(sheet, Sheet);

        var table = new TableDef(
            Sheet, EcrCode.Create("T1"), Text("T"), Table, TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        SetId(table, Table);

        var columns = new Dictionary<int, ColumnDef>();
        foreach (var id in new[] { VisibleColumn, HiddenColumn })
        {
            var column = new ColumnDef(
                Table, EcrCode.Create("C" + id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                Text("C"), id, CellDataType.Decimal);
            SetId(column, id);
            table.AddColumn(column);
            columns[id] = column;
        }

        sheet.AddTable(table);
        return new TemplateVersionSnapshot(1, 0, [sheet], columns, new Dictionary<(int, string), RowDef>());
    }

    private static LocalizedText Text(string s) => new(new Dictionary<string, string> { ["en"] = s });

    private static void SetId<T>(T e, int id) where T : Entity<int>
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(e, id);
}
