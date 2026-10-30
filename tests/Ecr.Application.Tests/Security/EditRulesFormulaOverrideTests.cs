using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Security;

/// <summary>
/// ✎ D-461: комірку формули можна писати як «ручне значення замість формули» лише разом — формула дозволяє
/// (<see cref="CellAccessContext.FormulaOverridable"/>) І право <see cref="EditRules.OverrideFormulaPermission"/> у
/// проєкті; решта правил (симуляція, період, стан аркуша, Deny, грант) — як для будь-якої комірки.
/// </summary>
/// <remarks>
/// Мутації (локально, не комітяться): у <c>EditRules.CanEdit</c> не перевіряти право — червоніє
/// «без права — CalculatedCell»; не перевіряти <c>FormulaOverridable</c> — «формула не дозволяє — CalculatedCell»;
/// ставити <c>viaOverride</c> ДО перевірки стану аркуша — нічого (порядок причин не змінено), але повернути
/// <c>Allow()</c> без <c>ViaFormulaOverride</c> — червоніє позначка рішення.
/// </remarks>
public sealed class EditRulesFormulaOverrideTests
{
    private static AccessProfile Writer(params string[] permissions)
    {
        var builder = new AccessBuilder().Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Write);
        foreach (var permission in permissions)
        {
            builder = builder.Permission(permission);
        }

        return builder.Build();
    }

    private static CellAccessContext FormulaCell(bool overridable, bool cellScope = false)
        => (cellScope
                ? AccessBuilder.Cell() with { CellIsComputed = true }
                : AccessBuilder.Cell() with { ColumnIsComputed = true })
            with { FormulaOverridable = overridable };

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Decision", "D-461")]
    [InlineData(false)]
    [InlineData(true)]
    public void Формула_дозволяє_і_є_право_запис_дозволено_як_ручне_значення(bool cellScope)
    {
        var decision = EditRules.CanEdit(Writer(EditRules.OverrideFormulaPermission), FormulaCell(true, cellScope));

        Assert.True(decision.IsAllowed);
        Assert.True(decision.ViaFormulaOverride);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Decision", "D-461")]
    public void Без_права_лише_запис_у_аркуш_формулу_не_замінює()
    {
        var decision = EditRules.CanEdit(Writer(), FormulaCell(true));

        Assert.False(decision.IsAllowed);
        Assert.Equal(EditDenyReason.CalculatedCell, decision.Reason);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Decision", "D-461")]
    public void Формула_не_дозволяє_право_не_допомагає()
    {
        var decision = EditRules.CanEdit(Writer(EditRules.OverrideFormulaPermission), FormulaCell(false));

        Assert.Equal(EditDenyReason.CalculatedCell, decision.Reason);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Decision", "D-461")]
    public void Звичайна_комірка_не_позначається_як_ручне_значення()
    {
        var decision = EditRules.CanEdit(Writer(EditRules.OverrideFormulaPermission), AccessBuilder.Cell());

        Assert.True(decision.IsAllowed);
        Assert.False(decision.ViaFormulaOverride);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Decision", "D-461")]
    public void Решта_правил_перемагає_симуляція_період_подано_Deny_грант()
    {
        var right = EditRules.OverrideFormulaPermission;

        var simulation = new AccessBuilder().Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Write)
            .Permission(right).Build(simulation: true, simulatedFor: 7);
        Assert.Equal(EditDenyReason.SimulationReadOnly, EditRules.CanEdit(simulation, FormulaCell(true)).Reason);

        var closed = AccessBuilder.Cell(period: PeriodState.Closed) with { ColumnIsComputed = true, FormulaOverridable = true };
        Assert.Equal(EditDenyReason.PeriodClosed, EditRules.CanEdit(Writer(right), closed).Reason);

        var submitted = AccessBuilder.Cell(sheet: DocumentStatus.Submitted) with { ColumnIsComputed = true, FormulaOverridable = true };
        Assert.Equal(EditDenyReason.DocumentSubmitted, EditRules.CanEdit(Writer(right), submitted).Reason);

        var denied = new AccessBuilder().Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Write)
            .Deny(ResourceKind.Column, AccessBuilder.Cell().ColumnDefId).Permission(right).Build();
        Assert.Equal(EditDenyReason.NoGrant, EditRules.CanEdit(denied, FormulaCell(true)).Reason);

        var reader = new AccessBuilder().Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Read)
            .Permission(right).Build();
        Assert.Equal(EditDenyReason.InsufficientGrantLevel, EditRules.CanEdit(reader, FormulaCell(true)).Reason);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Decision", "D-461")]
    public void Погоджувач_поточного_кроку_з_правом_замінює_формулу_на_поданому()
    {
        var approverCell = AccessBuilder.Cell(sheet: DocumentStatus.Submitted)
            with { ColumnIsComputed = true, FormulaOverridable = true, IsCurrentApprover = true };

        var decision = EditRules.CanEdit(Writer(EditRules.OverrideFormulaPermission), approverCell);

        Assert.True(decision.IsAllowed);
        Assert.True(decision.ByApprover);
        Assert.True(decision.ViaFormulaOverride);
    }
}
