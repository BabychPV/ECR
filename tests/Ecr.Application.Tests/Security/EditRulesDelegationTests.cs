// tests/Ecr.Application.Tests/Security/EditRulesDelegationTests.cs
using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Security;

/// <summary>
/// EA-B: «передача місяця» у чистих правилах запису. Піднімає ЛИШЕ
/// <c>Read → Write</c> на причині <see cref="EditDenyReason.InsufficientGrantLevel"/>;
/// усе, що вище в черзі (стани, симуляція), — перемагає; заборона і «немає
/// гранта» не перекриваються.
/// </summary>
/// <remarks>
/// ⛔ Безпека запису. МУТАЦІЇ (локально, у detached-worktree, не в кінчику lane):
/// (1) у <see cref="EditRules.CanEdit"/> замінити <c>effective == GrantLevel.Read</c> на
/// <c>effective &lt; GrantLevel.Write</c> — червоніють NoGrant/IsDeny-рядки;
/// (2) прибрати блок <c>AssignmentLock</c> Filled/Validated — червоніють «Заповнено»/«Валідовано»;
/// (3) перенести підйом ДО перевірок стану — червоніють рядки станів.
/// </remarks>
public sealed class EditRulesDelegationTests
{
    private static AccessProfile Reader()
        => new AccessBuilder().Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Read).Build();

    private static CellAccessContext Delegated(CellAccessContext cell)
        => cell with { DelegatedWrite = true };

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "EA-B")]
    public void Читач_із_передачею_пише_а_без_передачі_отримує_InsufficientGrantLevel()
    {
        var with = EditRules.CanEdit(Reader(), Delegated(AccessBuilder.Cell()));
        var without = EditRules.CanEdit(Reader(), AccessBuilder.Cell());

        Assert.True(with.IsAllowed);
        Assert.False(without.IsAllowed);
        Assert.Equal(EditDenyReason.InsufficientGrantLevel, without.Reason);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "EA-B")]
    public void Без_гранта_взагалі_передача_нічого_не_піднімає()
    {
        var none = new AccessBuilder().Build();

        var decision = EditRules.CanEdit(none, Delegated(AccessBuilder.Cell()));

        Assert.False(decision.IsAllowed);
        Assert.Equal(EditDenyReason.NoGrant, decision.Reason);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "EA-B")]
    [InlineData(ResourceKind.Project)]
    [InlineData(ResourceKind.Sheet)]
    [InlineData(ResourceKind.Table)]
    [InlineData(ResourceKind.Column)]
    public void Явна_заборона_на_будь_якому_рівні_перекриває_передачу(ResourceKind kind)
    {
        var id = kind switch
        {
            ResourceKind.Project => AccessBuilder.ProjectId,
            ResourceKind.Sheet => AccessBuilder.SheetId,
            ResourceKind.Table => AccessBuilder.TableId,
            _ => AccessBuilder.ColumnId,
        };
        var profile = new AccessBuilder()
            .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Read)
            .Deny(kind, id)
            .Build();

        var decision = EditRules.CanEdit(profile, Delegated(AccessBuilder.Cell()));

        Assert.False(decision.IsAllowed);
        Assert.Equal(EditDenyReason.NoGrant, decision.Reason);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "EA-B")]
    public void Грант_на_аркуш_без_видимого_проєкту_не_піднімається()
    {
        // S2: грант на аркуш діє лише у видимому проєкті — Effective = None.
        var profile = new AccessBuilder().Grant(ResourceKind.Sheet, AccessBuilder.SheetId, GrantLevel.Read).Build();

        var decision = EditRules.CanEdit(profile, Delegated(AccessBuilder.Cell()));

        Assert.Equal(EditDenyReason.NoGrant, decision.Reason);
    }

    /// <summary>R2: стани вищі за передачу.</summary>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "EA-B")]
    [InlineData("closed", EditDenyReason.PeriodClosed)]
    [InlineData("scheduled", EditDenyReason.PeriodNotOpenYet)]
    [InlineData("submitted", EditDenyReason.DocumentSubmitted)]
    [InlineData("approved", EditDenyReason.DocumentApproved)]
    [InlineData("computed", EditDenyReason.CalculatedCell)]
    [InlineData("columnReadOnly", EditDenyReason.ColumnReadOnly)]
    [InlineData("rowReadOnly", EditDenyReason.RowReadOnly)]
    [InlineData("outOfWindow", EditDenyReason.OutOfAccessWindow)]
    [InlineData("archived", EditDenyReason.ProjectArchived)]
    [InlineData("archiving", EditDenyReason.ArchivingInProgress)]
    public void Стани_мають_перевагу_над_передачею(string condition, EditDenyReason expected)
    {
        var cell = condition switch
        {
            "closed" => AccessBuilder.Cell(period: PeriodState.Closed),
            "scheduled" => AccessBuilder.Cell(period: PeriodState.Scheduled),
            "submitted" => AccessBuilder.Cell(sheet: DocumentStatus.Submitted),
            "approved" => AccessBuilder.Cell(sheet: DocumentStatus.Approved),
            "computed" => AccessBuilder.Cell(computed: true),
            "columnReadOnly" => AccessBuilder.Cell(columnReadOnly: true),
            "rowReadOnly" => AccessBuilder.Cell(rowReadOnly: true),
            "outOfWindow" => AccessBuilder.Cell(outOfWindow: true),
            "archived" => AccessBuilder.Cell(project: ProjectStatus.Archived),
            "archiving" => AccessBuilder.Cell(archiving: true),
            _ => throw new ArgumentOutOfRangeException(nameof(condition)),
        };

        var decision = EditRules.CanEdit(Reader(), Delegated(cell));

        Assert.False(decision.IsAllowed);
        Assert.Equal(expected, decision.Reason);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "EA-B")]
    public void Симуляція_відхиляє_запис_попри_передачу()
    {
        var profile = new AccessBuilder()
            .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Read)
            .Build(simulation: true, simulatedFor: 8);

        var decision = EditRules.CanEdit(profile, Delegated(AccessBuilder.Cell()));

        Assert.Equal(EditDenyReason.SimulationReadOnly, decision.Reason);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "EA-B")]
    [InlineData(EditDenyReason.AssignmentFilled)]
    [InlineData(EditDenyReason.AssignmentValidated)]
    [InlineData(EditDenyReason.AssignmentExpired)]
    public void Заповнено_валідовано_і_строк_минув_замикають_навіть_власника_гранта_Write(EditDenyReason lockReason)
    {
        var writer = new AccessBuilder().Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Write).Build();

        var decision = EditRules.CanEdit(writer, AccessBuilder.Cell() with { AssignmentLock = lockReason });

        Assert.False(decision.IsAllowed);
        Assert.Equal(lockReason, decision.Reason);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "EA-B")]
    public void Заповнено_відхиляє_раніше_за_NoGrant_а_стан_періоду_раніше_за_заповнено()
    {
        var none = new AccessBuilder().Build();

        var filled = EditRules.CanEdit(none, AccessBuilder.Cell() with { AssignmentLock = EditDenyReason.AssignmentFilled });
        var closed = EditRules.CanEdit(
            none, AccessBuilder.Cell(period: PeriodState.Closed) with { AssignmentLock = EditDenyReason.AssignmentFilled });

        Assert.Equal(EditDenyReason.AssignmentFilled, filled.Reason);
        Assert.Equal(EditDenyReason.PeriodClosed, closed.Reason);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "EA-B")]
    public void LockedByOwner_відхиляє_після_гранта_а_без_гранта_видно_NoGrant()
    {
        var locked = AccessBuilder.Cell() with { AssignmentLock = EditDenyReason.LockedByOwner };
        var writer = new AccessBuilder().Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Write).Build();

        Assert.Equal(EditDenyReason.LockedByOwner, EditRules.CanEdit(writer, locked).Reason);
        Assert.Equal(EditDenyReason.InsufficientGrantLevel, EditRules.CanEdit(Reader(), locked).Reason);
        Assert.Equal(EditDenyReason.NoGrant, EditRules.CanEdit(new AccessBuilder().Build(), locked).Reason);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "EA-B")]
    public void Передача_не_піднімає_подання_аркуша()
    {
        // CanSubmit будується без фактів передачі: Read з підйомом Write не подає.
        var cell = AccessBuilder.Cell();

        var submit = EditRules.CanSubmit(Reader(), cell, hasBlockingErrors: false);

        Assert.False(submit.IsAllowed);
        Assert.Equal(EditDenyReason.InsufficientGrantLevel, submit.Reason);
        Assert.Equal(GrantLevel.Read, EditRules.Effective(Reader(), Delegated(cell)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "EA-B")]
    public void Вищий_рівень_за_Write_передачею_не_знижується()
    {
        var manager = new AccessBuilder().Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Manage).Build();

        Assert.True(EditRules.CanEdit(manager, Delegated(AccessBuilder.Cell())).IsAllowed);
        Assert.Equal(GrantLevel.Manage, EditRules.Effective(manager, Delegated(AccessBuilder.Cell())));
    }
}
