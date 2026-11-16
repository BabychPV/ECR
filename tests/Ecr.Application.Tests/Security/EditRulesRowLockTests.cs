// tests/Ecr.Application.Tests/Security/EditRulesRowLockTests.cs
using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Security;

/// <summary>
/// ✎ B75: заборона <see cref="EditDenyReason.RowLocked"/> у чистих правилах запису — порядок відносно решти заборон
/// (диференційний: кожна заборона вище перемагає, грант — нижче).
/// </summary>
/// <remarks>
/// ⛔ Безпека запису. МУТАЦІЇ (локально): (1) прибрати блок <c>RowLockedByOther</c> у <see cref="EditRules.CanEdit"/> —
/// червоніють «Чужий_замок…» і «Менеджер…»; (2) перенести його ПІСЛЯ гранта — червоніє «…_до_гранта»; (3) перенести
/// його ДО стану аркуша/періоду — червоніють рядки станів.
/// </remarks>
public sealed class EditRulesRowLockTests
{
    private static AccessProfile Writer()
        => new AccessBuilder().Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Write).Build();

    private static CellAccessContext Locked() => AccessBuilder.Cell() with { RowLockedByOther = true };

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "B75")]
    public void Чужий_замок_рядка_відхиляє_RowLocked_а_без_нього_запис_проходить()
    {
        var locked = EditRules.CanEdit(Writer(), Locked());
        var free = EditRules.CanEdit(Writer(), AccessBuilder.Cell());

        Assert.False(locked.IsAllowed);
        Assert.Equal(EditDenyReason.RowLocked, locked.Reason);
        Assert.True(free.IsAllowed);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "B75")]
    public void Менеджер_проєкту_теж_не_пише_в_чужий_заблокований_рядок()
    {
        var manager = new AccessBuilder().Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Manage).Build();

        Assert.Equal(EditDenyReason.RowLocked, EditRules.CanEdit(manager, Locked()).Reason);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "B75")]
    public void Замок_рядка_перевіряється_до_гранта_читач_бачить_RowLocked_а_не_InsufficientGrantLevel()
    {
        var reader = new AccessBuilder().Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Read).Build();

        Assert.Equal(EditDenyReason.RowLocked, EditRules.CanEdit(reader, Locked()).Reason);
        Assert.Equal(EditDenyReason.InsufficientGrantLevel, EditRules.CanEdit(reader, AccessBuilder.Cell()).Reason);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "B75")]
    [InlineData(PeriodState.Closed, DocumentStatus.Draft, EditDenyReason.PeriodClosed)]
    [InlineData(PeriodState.Scheduled, DocumentStatus.Draft, EditDenyReason.PeriodNotOpenYet)]
    [InlineData(PeriodState.Open, DocumentStatus.Submitted, EditDenyReason.DocumentSubmitted)]
    [InlineData(PeriodState.Open, DocumentStatus.Approved, EditDenyReason.DocumentApproved)]
    public void Стани_періоду_й_аркуша_перемагають_замок_рядка(
        PeriodState period, DocumentStatus sheet, EditDenyReason expected)
    {
        var decision = EditRules.CanEdit(Writer(), Locked() with { PeriodState = period, SheetStatus = sheet });

        Assert.Equal(expected, decision.Reason);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "B75")]
    public void Архів_і_обчислювана_колонка_перемагають_замок_рядка()
    {
        Assert.Equal(
            EditDenyReason.ProjectArchived,
            EditRules.CanEdit(Writer(), Locked() with { ProjectStatus = ProjectStatus.Archived }).Reason);
        Assert.Equal(
            EditDenyReason.ColumnReadOnly,
            EditRules.CanEdit(Writer(), Locked() with { ColumnIsReadOnly = true }).Reason);
        Assert.Equal(
            EditDenyReason.CalculatedCell,
            EditRules.CanEdit(Writer(), Locked() with { ColumnIsComputed = true }).Reason);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "B75")]
    public void Ознака_наявності_замків_сама_нічого_не_забороняє()
    {
        Assert.True(EditRules.CanEdit(Writer(), AccessBuilder.Cell() with { HasRowLocks = true }).IsAllowed);
    }
}
