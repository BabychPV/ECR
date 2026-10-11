// tests/Ecr.Infrastructure.Tests/Persistence/CellUpsertKeyOrderTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// RC19 P3-2: комірки <c>MERGE</c> подаються в порядку первинного ключа, незалежно від порядку клієнта, — єдиний
/// порядок діапазонних замків для паралельних PATCH-ів.
/// </summary>
/// <remarks>
/// ⛔ Мутація: повернути <c>upserts</c> як є (прибрати сортування) — тест червоний.
/// </remarks>
public sealed class CellUpsertKeyOrderTests
{
    [Fact]
    public void Комірки_батча_впорядковано_за_PeriodKey_TableRowId_ColumnDefId()
    {
        var shuffled = new[] { (202610, 9L, 3), (202609, 9L, 1), (202609, 2L, 7), (202609, 2L, 4), (202610, 1L, 2) }
            .Select(k => new CellRecord(
                new CellAddress(new PeriodKey(k.Item1), k.Item2, k.Item3), 1, new CellValueData()))
            .ToList();

        var ordered = NormalizedCellStore.InKeyOrder(shuffled)
            .Select(r => (r.Address.PeriodKey.Value, r.Address.TableRowId, r.Address.ColumnDefId))
            .ToList();

        Assert.Equal(
            [(202609, 2L, 4), (202609, 2L, 7), (202609, 9L, 1), (202610, 1L, 2), (202610, 9L, 3)],
            ordered);
    }

    [Fact]
    public void Вхідний_список_не_змінюється_а_менше_двох_комірок_повертається_як_є()
    {
        var one = new List<CellRecord>
        {
            new(new CellAddress(new PeriodKey(202609), 1, 1), 1, new CellValueData()),
        };

        Assert.Same(one, NormalizedCellStore.InKeyOrder(one));
        Assert.Empty(NormalizedCellStore.InKeyOrder([]));
    }
}
