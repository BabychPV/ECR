using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Security;

/// <summary>
/// WW-02: ознака рівня КОМІРКИ «її рахує комірочна формула» (<see cref="CellAccessContext.CellIsComputed"/>).
/// Комірка закрита (<see cref="EditDenyReason.CalculatedCell"/>), колонка й рядок — ні.
/// </summary>
public sealed class EditRulesCellComputedTests
{
    private static AccessProfile Operator()
        => new AccessBuilder().Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Write).Build();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "WW-02")]
    public void Комірка_з_комірочною_формулою_відмовляється_як_обчислювана()
    {
        var cell = AccessBuilder.Cell() with { CellIsComputed = true };

        var decision = EditRules.CanEdit(Operator(), cell);

        Assert.False(decision.IsAllowed);
        Assert.Equal(EditDenyReason.CalculatedCell, decision.Reason);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "WW-02")]
    public void Без_ознаки_комірки_та_сама_колонка_і_рядок_редагуються()
    {
        var decision = EditRules.CanEdit(Operator(), AccessBuilder.Cell());

        Assert.True(decision.IsAllowed);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "WW-02")]
    public void Ознака_комірки_не_перекриває_раніших_причин_закритий_період_першим()
    {
        var closed = AccessBuilder.Cell(period: PeriodState.Closed) with { CellIsComputed = true };
        var submitted = AccessBuilder.Cell(sheet: DocumentStatus.Submitted) with { CellIsComputed = true };

        Assert.Equal(EditDenyReason.PeriodClosed, EditRules.CanEdit(Operator(), closed).Reason);
        Assert.Equal(EditDenyReason.DocumentSubmitted, EditRules.CanEdit(Operator(), submitted).Reason);
    }
}
