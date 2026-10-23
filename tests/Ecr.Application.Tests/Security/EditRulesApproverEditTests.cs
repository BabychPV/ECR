using Ecr.Application.Security;
using Ecr.Application.Workflow;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Security;

/// <summary>
/// D-442 (BRIEF-1021 П.3): поданий аркуш править лише погоджувач ПОТОЧНОГО кроку маршруту
/// (<see cref="CellAccessContext.IsCurrentApprover"/>), і роль кроку знімає ЛИШЕ заборону «подано».
/// </summary>
/// <remarks>
/// ⛔ Мутації (локально, не комітяться): у <c>EditRules.CanEdit</c> прибрати гілку
/// <c>Submitted when IsCurrentApprover</c> — червоніє перший тест; поставити її ПЕРЕД перевіркою періоду чи
/// ознак колонки (повернути <c>Allow</c> одразу) — червоніють «закритий період» і «колонка лише для читання»;
/// пустити прапорець і на <c>Approved</c> — червоніє «затверджений не відкривається».
/// </remarks>
public sealed class EditRulesApproverEditTests
{
    private static AccessProfile Approver()
        => new AccessBuilder().Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Approve).Build();

    private static CellAccessContext Submitted(bool isCurrentApprover)
        => AccessBuilder.Cell(sheet: DocumentStatus.Submitted) with { IsCurrentApprover = isCurrentApprover };

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Decision", "D-442")]
    public void Погоджувач_поточного_кроку_править_поданий_аркуш_і_рішення_це_позначає()
    {
        var decision = EditRules.CanEdit(Approver(), Submitted(isCurrentApprover: true));

        Assert.True(decision.IsAllowed);
        Assert.True(decision.ByApprover);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Decision", "D-442")]
    public void Той_самий_грант_без_ролі_поточного_кроку_поданий_аркуш_не_править()
    {
        var decision = EditRules.CanEdit(Approver(), Submitted(isCurrentApprover: false));

        Assert.False(decision.IsAllowed);
        Assert.Equal(EditDenyReason.DocumentSubmitted, decision.Reason);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Decision", "D-442")]
    public void Затверджений_аркуш_прапорець_погоджувача_не_відкриває()
    {
        var approved = AccessBuilder.Cell(sheet: DocumentStatus.Approved) with { IsCurrentApprover = true };

        var decision = EditRules.CanEdit(Approver(), approved);

        Assert.False(decision.IsAllowed);
        Assert.Equal(EditDenyReason.DocumentApproved, decision.Reason);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Decision", "D-442")]
    public void Роль_кроку_не_знімає_решти_правил_період_колонка_грант_на_комірку()
    {
        var closed = AccessBuilder.Cell(period: PeriodState.Closed, sheet: DocumentStatus.Submitted)
                     with { IsCurrentApprover = true };
        var readOnlyColumn = AccessBuilder.Cell(sheet: DocumentStatus.Submitted, columnReadOnly: true)
                             with { IsCurrentApprover = true };
        var columnRead = new AccessBuilder()
            .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Approve)
            .Grant(ResourceKind.Column, AccessBuilder.ColumnId, GrantLevel.Read)
            .Build();

        Assert.Equal(EditDenyReason.PeriodClosed, EditRules.CanEdit(Approver(), closed).Reason);
        Assert.Equal(EditDenyReason.ColumnReadOnly, EditRules.CanEdit(Approver(), readOnlyColumn).Reason);
        Assert.Equal(
            EditDenyReason.InsufficientGrantLevel,
            EditRules.CanEdit(columnRead, Submitted(isCurrentApprover: true)).Reason);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Decision", "D-442")]
    public void Симуляція_погоджувача_не_пише_навіть_із_прапорцем()
    {
        var simulated = new AccessBuilder()
            .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Approve)
            .Build(simulation: true, simulatedFor: 77);

        Assert.Equal(
            EditDenyReason.SimulationReadOnly,
            EditRules.CanEdit(simulated, Submitted(isCurrentApprover: true)).Reason);
    }

    /// <summary>
    /// Зріз при затвердженні знімається лише коли змінилися комірки САМЕ цього аркуша
    /// (<see cref="ApprovalSnapshotRefresh.SheetChanged"/>).
    /// </summary>
    /// <remarks>
    /// ⛔ Мутація: <c>SheetChanged</c> завжди <c>true</c> — червоніє «зміна сусіднього аркуша не привід»;
    /// завжди <c>false</c> — червоніє «правка погоджувача — привід» (і HTTP-тест зрізу затвердження).
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Decision", "D-442")]
    public void Зріз_затвердження_лише_коли_змінилися_комірки_аркуша()
    {
        const string submitted = """[{"row":1,"column":5,"value":"10"},{"row":1,"column":9,"value":"1"}]""";
        const string approverEdit = """[{"row":1,"column":5,"value":"11"},{"row":1,"column":9,"value":"1"}]""";
        const string neighbourEdit = """[{"row":1,"column":5,"value":"10"},{"row":1,"column":9,"value":"2"}]""";
        var sheetColumns = new HashSet<int> { 5 };

        Assert.True(ApprovalSnapshotRefresh.SheetChanged(submitted, approverEdit, sheetColumns));
        Assert.False(ApprovalSnapshotRefresh.SheetChanged(submitted, neighbourEdit, sheetColumns));
        Assert.False(ApprovalSnapshotRefresh.SheetChanged(submitted, submitted, sheetColumns));
        Assert.True(ApprovalSnapshotRefresh.SheetChanged(null, submitted, sheetColumns));
    }
}
