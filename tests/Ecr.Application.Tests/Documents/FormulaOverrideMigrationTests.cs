// tests/Ecr.Application.Tests/Documents/FormulaOverrideMigrationTests.cs
using Ecr.Application.Documents.VersionMigration;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// ✎ Аудит 31.10 (F2, D-461): перенос версії розрізняє «цільова формула не дозволяє ручне значення» (позначка
/// знімається, комірку знову рахує формула — <c>OverrideLost</c>) і «у цільовій версії формули немає взагалі»
/// (позначка знімається, значення лишається звичайним введеним — НЕ <c>OverrideLost</c>).
/// </summary>
/// <remarks>
/// Мутаційний доказ: прибрати пропуск колонки без формули в <c>FormulaOverrideMigration.Lost</c> — червоніє
/// «колонка без формули — не втрата» (доти така позначка ставала <c>IsCalculated = 1</c> у колонці вводу).
/// </remarks>
public sealed class FormulaOverrideMigrationTests
{
    private const int Period = 202601;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Decision", "D-461")]
    public void Колонка_без_формули_не_втрата_а_введене_значення()
    {
        var builder = new TemplateBuilder { TemplateVersionId = 2 };
        var sheet = builder.Sheet("S");
        var table = builder.Table(sheet, "T");
        var input = builder.Column(table, "X", CellDataType.Decimal);
        var forbidden = builder.Column(table, "F", CellDataType.Calculated);
        var allowed = builder.Column(table, "G", CellDataType.Calculated);
        builder.Row(table, "R1", 1);
        builder.Formula(table, "1 + 1", column: forbidden);
        builder.Formula(table, "2 + 2", column: allowed).SetAllowManualOverride(true);
        var target = builder.Build();

        var marks = new List<ProjectFormulaOverride>
        {
            Mark(input.Id, "5"),
            Mark(forbidden.Id, "6"),
            Mark(allowed.Id, "7"),
            Mark(9999, "8"),
        };

        int? Same(int column) => target.ColumnsById.ContainsKey(column) ? column : null;

        var lost = FormulaOverrideMigration.Lost(marks, Same, target);
        var unformulated = FormulaOverrideMigration.Unformulated(marks, Same, target);

        // Формула не дозволяє (F) і колонки немає (9999) — втрата; G переїжджає; X — не втрата.
        Assert.Equal([forbidden.Id, 9999], lost.Select(m => m.Address.ColumnDefId).Order().ToArray());
        var kept = Assert.Single(unformulated);
        Assert.Equal(input.Id, kept.Address.ColumnDefId);
        Assert.Equal("5", kept.Value);
    }

    private static ProjectFormulaOverride Mark(int columnId, string value)
        => new(new CellAddress(new PeriodKey(Period), 1001, columnId), 1, "R1", value);
}
