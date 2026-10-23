// tests/Ecr.Application.Tests/Security/DocumentParticipationRulesTests.cs
using Ecr.Application.Documents.EditAssignments;
using Ecr.Application.Errors;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Security;

/// <summary>
/// D-443 (AN-130): чисті правила учасника документа — рівень надання в <see cref="EditRules.Effective"/>,
/// межі читання з наданнями, стеля «не вище власного рівня», правило передачі місяця лише для Write.
/// </summary>
/// <remarks>
/// <para>Мутаційний доказ (кожен рядок — що зламати і що червоніє):</para>
/// <list type="bullet">
/// <item>Deny-перевага: перенести <c>return context.ParticipationLevel</c> в <c>EditRules.Effective</c> ВИЩЕ циклу заборон →
/// червоніють <c>Заборона_аркуша_ролі_перемагає_надання_Approve</c> і <c>Заборона_проєкту_перемагає_надання</c>.</item>
/// <item>Видимість: повернути <c>GrantLevel.None</c> замість <c>context.ParticipationLevel</c> для невидимого проєкту →
/// червоніють <c>Без_ролі_надання_Read_дає_читання_але_не_запис</c> і <c>Межі_учасника_таблиці_…</c>.</item>
/// <item>Сховані таблиці: у <c>DocumentReadScope.ParticipationAt</c> для таблиці брати <c>OpensSheet</c> (будь-яка таблиця аркуша) →
/// червоніє <c>Межі_учасника_таблиці_відкривають_лише_її_і_її_аркуш</c>.</item>
/// <item>Подання/погодження аркуша наданням на таблицю: прибрати умову <c>tableCode is null</c> у <c>LevelAt</c> →
/// червоніє <c>Надання_на_таблицю_не_дає_рівня_на_аркуш</c>.</item>
/// <item>Стеля: у <c>EditAssignmentAccess.OwnLevelOver</c> не брати мінімум по таблицях області →
/// червоніє <c>Стеля_враховує_заборону_будь_де_в_області</c>.</item>
/// <item>Правило запису лише для Write: прибрати <c>a.Level != GrantLevel.Write</c> у <c>EditAssignmentGuard.Evaluate</c> →
/// червоніє <c>Учасник_Read_не_піднімається_і_не_замикається_правилом_передачі</c>.</item>
/// </list>
/// </remarks>
public sealed class DocumentParticipationRulesTests
{
    private const int Project = AccessBuilder.ProjectId;
    private const int SheetA = 101;
    private const int SheetB = 102;
    private const int Table1 = 201; // аркуш A
    private const int Table2 = 202; // аркуш A
    private const int Table3 = 203; // аркуш B
    private const int Col1 = 301;
    private const int Col2 = 311;
    private const int Col3 = 321;

    private static readonly DateTime Now = new(2026, 10, 23, 8, 0, 0, DateTimeKind.Utc);
    private static readonly TemplateVersionSnapshot Snapshot = Build();

    private static AccessProfile NoRole() => new AccessBuilder().Build();

    private static CellAccessContext Sheet(int sheetId, string code, GrantLevel participation)
        => AccessBuilder.Cell() with
        {
            SheetDefId = sheetId,
            TableDefId = 0,
            ColumnDefId = 0,
            SheetCode = code,
            ParticipationLevel = participation,
        };

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "D-443")]
    public void Без_ролі_надання_Read_дає_читання_але_не_запис()
    {
        var context = AccessBuilder.Cell() with { SheetCode = "SA", ParticipationLevel = GrantLevel.Read };

        Assert.Equal(GrantLevel.Read, EditRules.Effective(NoRole(), context));

        // Видимо, але не записується: 403 з причиною рівня, а не «немає гранта».
        var edit = EditRules.CanEdit(NoRole(), context);
        Assert.False(edit.IsAllowed);
        Assert.Equal(EditDenyReason.InsufficientGrantLevel, edit.Reason);

        // Без надання — як і доти: нічого.
        Assert.Equal(GrantLevel.None, EditRules.Effective(NoRole(), AccessBuilder.Cell()));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "D-443")]
    public void Заборона_аркуша_ролі_перемагає_надання_Approve()
    {
        var denied = new AccessBuilder().Deny(ResourceKind.Sheet, AccessBuilder.SheetId).Build();
        var context = AccessBuilder.Cell() with { ParticipationLevel = GrantLevel.Approve };

        Assert.Equal(GrantLevel.None, EditRules.Effective(denied, context));
        Assert.Equal(EditDenyReason.NoGrant, EditRules.CanEdit(denied, context).Reason);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "D-443")]
    public void Заборона_проєкту_перемагає_надання()
    {
        var denied = new AccessBuilder()
            .Grant(ResourceKind.Project, Project, GrantLevel.Read)
            .Deny(ResourceKind.Project, Project)
            .Build();

        Assert.Equal(GrantLevel.None, EditRules.Effective(denied, AccessBuilder.Cell() with { ParticipationLevel = GrantLevel.Write }));
        Assert.True(denied.DeniesProject(Project));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "D-443")]
    public void Надання_розширює_рівень_ролі_і_ніколи_не_звужує()
    {
        var reader = new AccessBuilder().Grant(ResourceKind.Project, Project, GrantLevel.Read).Build();
        var manager = new AccessBuilder().Grant(ResourceKind.Project, Project, GrantLevel.Manage).Build();

        Assert.Equal(GrantLevel.Approve, EditRules.Effective(reader, AccessBuilder.Cell() with { ParticipationLevel = GrantLevel.Approve }));
        Assert.Equal(GrantLevel.Manage, EditRules.Effective(manager, AccessBuilder.Cell() with { ParticipationLevel = GrantLevel.Read }));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "D-443")]
    public void Подання_і_погодження_за_наданням_без_ролі()
    {
        var submit = Sheet(AccessBuilder.SheetId, "SA", GrantLevel.Submit);
        Assert.True(EditRules.CanSubmit(NoRole(), submit, hasBlockingErrors: false).IsAllowed);

        var approve = Sheet(AccessBuilder.SheetId, "SA", GrantLevel.Approve) with { SheetStatus = DocumentStatus.Submitted };
        Assert.True(EditRules.CanApprove(NoRole(), approve).IsAllowed);

        // Submit не погоджує.
        var tooLow = Sheet(AccessBuilder.SheetId, "SA", GrantLevel.Submit) with { SheetStatus = DocumentStatus.Submitted };
        Assert.Equal(EditDenyReason.InsufficientGrantLevel, EditRules.CanApprove(NoRole(), tooLow).Reason);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "D-443")]
    public void Надання_на_таблицю_не_дає_рівня_на_аркуш()
    {
        IReadOnlyList<ParticipationGrant> grants = [new(GrantLevel.Approve, "SA", "T201", null)];

        Assert.Equal(GrantLevel.Approve, DocumentParticipation.LevelAt(grants, "SA", "T201", null));
        Assert.Equal(GrantLevel.None, DocumentParticipation.LevelAt(grants, "SA", "T202", null));
        Assert.Equal(GrantLevel.None, DocumentParticipation.LevelAt(grants, "SA", null, null));
        Assert.Equal(GrantLevel.None, DocumentParticipation.LevelAt(grants, "SB", "T201", null));

        // Аркуш для перегляду відкриває — щоб з'явилась сама таблиця.
        Assert.True(DocumentParticipation.OpensSheet(grants, "SA", null));
        Assert.False(DocumentParticipation.OpensSheet(grants, "SB", null));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "D-443")]
    public void Write_дає_у_рівень_лише_Read_а_період_передачі_лише_свій()
    {
        IReadOnlyList<ParticipationGrant> grants = [new(GrantLevel.Write, "SA", null, new PeriodKey(202601))];

        Assert.Equal(GrantLevel.Read, DocumentParticipation.LevelAt(grants, "SA", "T201", new PeriodKey(202601)));
        Assert.Equal(GrantLevel.None, DocumentParticipation.LevelAt(grants, "SA", "T201", new PeriodKey(202602)));
        Assert.Equal(GrantLevel.None, DocumentParticipation.LevelAt(grants, "SA", "T201", null));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "D-443")]
    public void Діють_лише_надання_виконавця_з_активним_делегатором_і_не_прострочені()
    {
        var facts = new[]
        {
            Fact(GrantLevel.Read, "SA", mine: AssigneeProgress.Assigned),
            Fact(GrantLevel.Approve, "SB", mine: null),                                          // не виконавець
            Fact(GrantLevel.Submit, "SB", mine: AssigneeProgress.Assigned, delegatorActive: false), // R7
            Fact(GrantLevel.Submit, "SB", mine: AssigneeProgress.Assigned, expiresAt: Now),         // строк минув
        };

        var active = DocumentParticipation.Active(facts, Now);

        var only = Assert.Single(active);
        Assert.Equal(GrantLevel.Read, only.Level);
        Assert.Equal("SA", only.SheetCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "D-443")]
    public void Межі_учасника_таблиці_відкривають_лише_її_і_її_аркуш()
    {
        var scope = DocumentReadScope.ForParticipant(
            NoRole(), Project, Snapshot, [new ParticipationGrant(GrantLevel.Read, "SA", "T201", null)]);

        Assert.True(scope.CanReadTable(Table1));
        Assert.True(scope.CanReadColumn(Col1));
        Assert.True(scope.CanReadSheet(SheetA));

        Assert.False(scope.CanReadTable(Table2));
        Assert.False(scope.CanReadColumn(Col2));
        Assert.False(scope.CanReadTable(Table3));
        Assert.False(scope.CanReadColumn(Col3));
        Assert.False(scope.CanReadSheet(SheetB));
        Assert.Equal(["SB"], scope.HiddenSheetCodes());

        // Без надань той самий профіль не бачить нічого.
        var none = DocumentReadScope.For(NoRole(), Project, Snapshot);
        Assert.False(none.CanReadTable(Table1));
        Assert.False(none.CanReadSheet(SheetA));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "D-443")]
    public void Межі_учасника_Deny_таблиці_ролі_ховає_навіть_надану()
    {
        var denied = new AccessBuilder().Deny(ResourceKind.Table, Table1).Build();
        var scope = DocumentReadScope.ForParticipant(
            denied, Project, Snapshot, [new ParticipationGrant(GrantLevel.Approve, "SA", null, null)]);

        Assert.False(scope.CanReadTable(Table1));
        Assert.True(scope.CanReadTable(Table2));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "D-443")]
    public void Стеля_власник_із_Write_не_видає_Approve_менеджер_видає()
    {
        var writer = new AccessBuilder().Grant(ResourceKind.Project, Project, GrantLevel.Write).Build();
        var manager = new AccessBuilder().Grant(ResourceKind.Project, Project, GrantLevel.Manage).Build();
        var sheetA = EditAssignmentSlice.Create("SA", null, null, null);

        Assert.Equal(GrantLevel.Write, EditAssignmentAccess.OwnLevelOver(writer, Project, Snapshot, sheetA));
        Assert.True(EditAssignmentAccess.OwnLevelOver(writer, Project, Snapshot, sheetA) < GrantLevel.Approve);
        Assert.True(EditAssignmentAccess.OwnLevelOver(manager, Project, Snapshot, sheetA) >= GrantLevel.Approve);

        var denied = EditAssignmentAccess.LevelAboveOwn(GrantLevel.Approve, GrantLevel.Write);
        Assert.Equal("err.ECR-ACCS-0403.editAssignmentLevelAboveOwn", denied.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "D-443")]
    public void Стеля_враховує_заборону_будь_де_в_області()
    {
        var manager = new AccessBuilder()
            .Grant(ResourceKind.Project, Project, GrantLevel.Manage)
            .Deny(ResourceKind.Table, Table2)
            .Build();

        Assert.Equal(GrantLevel.None, EditAssignmentAccess.OwnLevelOver(
            manager, Project, Snapshot, EditAssignmentSlice.Create("SA", null, null, null)));
        Assert.Equal(GrantLevel.Manage, EditAssignmentAccess.OwnLevelOver(
            manager, Project, Snapshot, EditAssignmentSlice.Create("SA", "T201", null, null)));
        Assert.Throws<BusinessRuleException>(() => EditAssignmentAccess.OwnLevelOver(
            manager, Project, Snapshot, EditAssignmentSlice.Create("SZ", null, null, null)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "D-443")]
    public void Учасник_Read_не_піднімається_і_не_замикається_правилом_передачі()
    {
        var reader = Fact(GrantLevel.Read, "SA", mine: AssigneeProgress.Assigned) with { IsExclusive = true };
        var slice = new EditSliceFacts("SA", "T201", new PeriodKey(202601), ColumnIsMonth: false, ColumnMonthNumber: null);

        var asRecipient = EditAssignmentGuard.Evaluate(7, 99, false, [reader], slice, Now);
        Assert.False(asRecipient.DelegatedWrite);
        Assert.Equal(EditDenyReason.None, asRecipient.Lock);
        Assert.False(asRecipient.IsExecutor);

        // Чужий учасник Read (навіть із позначкою ексклюзивності) не закриває зріз іншим.
        var other = EditAssignmentGuard.Evaluate(8, 99, false, [reader with { MyProgress = null }], slice, Now);
        Assert.Equal(EditDenyReason.None, other.Lock);

        // Передача місяця (Write) — як доти: підйом.
        var handoff = Fact(GrantLevel.Write, "SA", mine: AssigneeProgress.Assigned);
        Assert.True(EditAssignmentGuard.Evaluate(7, 99, false, [handoff], slice, Now).DelegatedWrite);
    }

    private static EditAssignmentFact Fact(
        GrantLevel level, string? sheet, AssigneeProgress? mine, bool delegatorActive = true, DateTime? expiresAt = null)
        => new(sheet, null, null, null, IsExclusive: false, IsValidated: false, delegatorActive, DelegatedByUserId: 99,
               expiresAt, mine, level);

    private static TemplateVersionSnapshot Build()
    {
        var sheetA = SheetDefOf(SheetA, "A");
        var sheetB = SheetDefOf(SheetB, "B");
        var columns = new Dictionary<int, ColumnDef>();

        sheetA.AddTable(TableOf(Table1, SheetA, columns, Col1));
        sheetA.AddTable(TableOf(Table2, SheetA, columns, Col2));
        sheetB.AddTable(TableOf(Table3, SheetB, columns, Col3));

        return new TemplateVersionSnapshot(1, 0, [sheetA, sheetB], columns, new Dictionary<(int, string), RowDef>());
    }

    private static SheetDef SheetDefOf(int id, string code)
    {
        var sheet = new SheetDef(1, EcrCode.Create("S" + code), Text(code), id);
        SetId(sheet, id);
        return sheet;
    }

    private static TableDef TableOf(int id, int sheetId, Dictionary<int, ColumnDef> index, params int[] columnIds)
    {
        var table = new TableDef(
            sheetId, EcrCode.Create("T" + id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            Text("T"), id, TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        SetId(table, id);

        foreach (var columnId in columnIds)
        {
            var column = new ColumnDef(
                id, EcrCode.Create("C" + columnId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                Text("C"), columnId, CellDataType.Decimal);
            SetId(column, columnId);
            table.AddColumn(column);
            index[columnId] = column;
        }

        return table;
    }

    private static LocalizedText Text(string s) => new(new Dictionary<string, string> { ["en"] = s });

    private static void SetId<T>(T e, int id) where T : Entity<int>
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(e, id);
}
