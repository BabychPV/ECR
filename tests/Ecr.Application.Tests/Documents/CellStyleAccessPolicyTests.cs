// tests/Ecr.Application.Tests/Documents/CellStyleAccessPolicyTests.cs
using Ecr.Application.Documents;
using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>CELL-STYLE P1A: згортка рішень на комірки в ознаку «можна фарбувати» (чиста функція).</summary>
public sealed class CellStyleAccessPolicyTests
{
    private static readonly PeriodKey Period = new(202609);

    private static CellAddress At(long row, int column) => new(Period, row, column);

    private static (Dictionary<long, string> Rows, Dictionary<int, string> Columns) Grid(int rows, int columns)
        => (
            Enumerable.Range(1, rows).ToDictionary(r => (long)r, r => $"R{r}"),
            Enumerable.Range(1, columns).ToDictionary(c => c, c => $"C{c}"));

    [Fact]
    public void Порожній_зріз_нічого_фарбувати_і_причини_немає()
    {
        var (rows, columns) = Grid(0, 0);

        var result = CellStyleAccessPolicy.Build(new Dictionary<CellAddress, EditDecision>(), rows, columns);

        Assert.False(result.CanStyle);
        Assert.Null(result.DeniedReason);
        Assert.Empty(result.StyleDeniedRowKeys);
    }

    [Fact]
    public void Усе_заборонено_одна_найчастіша_причина_без_переліків()
    {
        var (rows, columns) = Grid(2, 2);
        var decisions = new Dictionary<CellAddress, EditDecision>
        {
            [At(1, 1)] = EditDecision.Deny(EditDenyReason.DocumentSubmitted),
            [At(1, 2)] = EditDecision.Deny(EditDenyReason.DocumentSubmitted),
            [At(2, 1)] = EditDecision.Deny(EditDenyReason.DocumentSubmitted),
            [At(2, 2)] = EditDecision.Deny(EditDenyReason.RowReadOnly),
        };

        var result = CellStyleAccessPolicy.Build(decisions, rows, columns);

        Assert.False(result.CanStyle);
        Assert.Equal("DocumentSubmitted", result.DeniedReason);
        Assert.Empty(result.StyleDeniedRowKeys);
        Assert.Empty(result.StyleDeniedColumnCodes);
        Assert.Empty(result.StyleDeniedCells);
    }

    [Fact]
    public void Рядок_колонка_і_окрема_комірка_розрізняються()
    {
        var (rows, columns) = Grid(3, 3);
        var decisions = new Dictionary<CellAddress, EditDecision>();
        for (var r = 1; r <= 3; r++)
        {
            for (var c = 1; c <= 3; c++)
            {
                decisions[At(r, c)] = EditDecision.Allow();
            }
        }

        // Рядок R1 заборонений цілком, колонка C3 — цілком, комірка R2:C2 — окремо.
        for (var c = 1; c <= 3; c++)
        {
            decisions[At(1, c)] = EditDecision.Deny(EditDenyReason.RowReadOnly);
        }

        for (var r = 1; r <= 3; r++)
        {
            decisions[At(r, 3)] = EditDecision.Deny(EditDenyReason.InsufficientGrantLevel);
        }

        decisions[At(2, 2)] = EditDecision.Deny(EditDenyReason.AssignmentFilled);

        var result = CellStyleAccessPolicy.Build(decisions, rows, columns);

        Assert.True(result.CanStyle);
        Assert.Null(result.DeniedReason);
        Assert.Equal(["R1"], result.StyleDeniedRowKeys);
        Assert.Equal(["C3"], result.StyleDeniedColumnCodes);
        Assert.Equal("AssignmentFilled", Assert.Single(result.StyleDeniedCells).Value);
        Assert.Equal("R2:C2", Assert.Single(result.StyleDeniedCells).Key);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void Нерозкриття_комірки_схованої_колонки_не_потрапляють_у_згортку()
    {
        // Колонка 3 читачеві не видна (її немає в словнику видимих): її заборона не стає ні причиною, ні переліком.
        var (rows, columns) = Grid(2, 2);
        var decisions = new Dictionary<CellAddress, EditDecision>
        {
            [At(1, 1)] = EditDecision.Allow(),
            [At(1, 2)] = EditDecision.Allow(),
            [At(2, 1)] = EditDecision.Allow(),
            [At(2, 2)] = EditDecision.Allow(),
            [At(1, 3)] = EditDecision.Deny(EditDenyReason.NoGrant),
            [At(2, 3)] = EditDecision.Deny(EditDenyReason.NoGrant),
        };

        var result = CellStyleAccessPolicy.Build(decisions, rows, columns);

        Assert.True(result.CanStyle);
        Assert.Empty(result.StyleDeniedColumnCodes);
        Assert.Empty(result.StyleDeniedCells);
        Assert.Empty(result.StyleDeniedRowKeys);
    }

    [Fact]
    public void Лише_схована_колонка_заборонена_а_видима_дозволена_причина_не_розкривається()
    {
        var (rows, columns) = Grid(1, 1);
        var decisions = new Dictionary<CellAddress, EditDecision>
        {
            [At(1, 1)] = EditDecision.Allow(),
            [At(1, 9)] = EditDecision.Deny(EditDenyReason.SimulationReadOnly),
        };

        var result = CellStyleAccessPolicy.Build(decisions, rows, columns);

        Assert.True(result.CanStyle);
        Assert.Null(result.DeniedReason);
    }

    [Fact]
    public void Перелік_обрізається_до_ліміту_і_позначається_Truncated()
    {
        // 600 окремих заборонених комірок (по одній у різних рядках і колонках, без цілих рядків/колонок).
        const int n = 600;
        var (rows, columns) = Grid(n, 2);
        var decisions = new Dictionary<CellAddress, EditDecision>();
        for (var r = 1; r <= n; r++)
        {
            decisions[At(r, 1)] = r % 2 == 0 ? EditDecision.Deny(EditDenyReason.AssignmentFilled) : EditDecision.Allow();
            decisions[At(r, 2)] = r % 2 == 0 ? EditDecision.Allow() : EditDecision.Deny(EditDenyReason.AssignmentFilled);
        }

        var result = CellStyleAccessPolicy.Build(decisions, rows, columns);

        Assert.True(result.CanStyle);
        Assert.True(result.Truncated);
        Assert.Equal(CellStyleAccessPolicy.MaxKeys, result.StyleDeniedCells.Count);

        // Детермінізм: той самий вхід — той самий склад.
        var again = CellStyleAccessPolicy.Build(decisions, rows, columns);
        Assert.Equal(result.StyleDeniedCells.Keys.Order(StringComparer.Ordinal), again.StyleDeniedCells.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Без_рішень_null_дає_порожню_відмову()
    {
        var (rows, columns) = Grid(1, 1);

        var result = CellStyleAccessPolicy.Build(null, rows, columns);

        Assert.False(result.CanStyle);
        Assert.Null(result.DeniedReason);
    }
}
