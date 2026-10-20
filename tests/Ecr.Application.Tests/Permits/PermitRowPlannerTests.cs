// tests/Ecr.Application.Tests/Permits/PermitRowPlannerTests.cs
using Ecr.Application.Permits.Bindings;
using Ecr.Domain.Enums;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Permits;

/// <summary>Побудова рядків аркуша з рядків таблиці дозволу над знімком (PM-09).</summary>
public sealed class PermitRowPlannerTests
{
    private const string Table = "PRM_AIR_EMISSIONS";

    private static readonly PlannerColumn[] Columns =
    [
        new("SubstanceCol", "Substance", PermitColumnRole.RowKey),
        new("LimitCol", "Limit", PermitColumnRole.Limit),
        new("NoteCol", "Note", PermitColumnRole.Info),
    ];

    [Fact]
    public void Рядок_на_кожен_запис_дозволу_у_порядку_таблиці_з_ключем_P_id()
    {
        var plan = PermitRowPlanner.Plan(Snapshot(), Table, 1, Columns);

        Assert.Equal(["P201", "P200"], plan.Rows.Select(r => r.RowKey));
        Assert.Equal([201L, 200L], plan.Rows.Select(r => r.EntryId));
    }

    [Fact]
    public void Комірки_беруться_з_полів_за_кодом_колонки_аркуша()
    {
        var plan = PermitRowPlanner.Plan(Snapshot(), Table, 1, Columns);

        var row = plan.Rows[1];
        Assert.Equal("NOx", row.Cells["SubstanceCol"]);
        Assert.Equal(12.5m, row.Cells["LimitCol"]);
        Assert.Null(row.Cells["NoteCol"]);
        Assert.Equal(new DateOnly(2026, 1, 31), plan.Rows[0].Cells["NoteCol"]);
    }

    [Fact]
    public void Усі_зіставлені_колонки_заблоковані_для_користувача()
    {
        var plan = PermitRowPlanner.Plan(Snapshot(), Table, 1, Columns);

        Assert.All(plan.Rows, r => Assert.Equal(["SubstanceCol", "LimitCol", "NoteCol"], r.Locked));
    }

    [Fact]
    public void Чужі_рядки_іншого_дозволу_не_потрапляють_у_план()
    {
        var plan = PermitRowPlanner.Plan(Snapshot(), Table, 2, Columns);

        Assert.Equal(["P202"], plan.Rows.Select(r => r.RowKey));
    }

    [Fact]
    public void Дозвіл_без_рядків_дає_порожній_план_без_попереджень()
    {
        var plan = PermitRowPlanner.Plan(Snapshot(), Table, 3, Columns);

        Assert.Empty(plan.Rows);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void Невідомий_реєстр_таблиці_дає_попередження_а_не_виняток()
    {
        var plan = PermitRowPlanner.Plan(Snapshot(), "PRM_NONE_NOPE", 1, Columns);

        Assert.Empty(plan.Rows);
        Assert.Equal("tableUnavailable", Assert.Single(plan.Warnings).Case);
    }

    [Fact]
    public void Нечитане_поле_дає_порожню_комірку_і_попередження()
    {
        var columns = new[] { new PlannerColumn("X", "Missing", PermitColumnRole.Info) };
        var plan = PermitRowPlanner.Plan(Snapshot(), Table, 2, columns);

        Assert.Null(plan.Rows[0].Cells["X"]);
        Assert.Equal("fieldUnreadable", Assert.Single(plan.Warnings).Case);
    }

    [Fact]
    public void Ключ_рядка_стабільний_для_запису()
        => Assert.Equal("P42", PermitRowPlanner.RowKeyFor(42));

    [Theory]
    [InlineData("P42", true)]
    [InlineData("P0", true)]
    [InlineData("P", false)]
    [InlineData("p42", false)]
    [InlineData("P4x", false)]
    [InlineData("PX42", false)]
    [InlineData("42", false)]
    [InlineData("P-1", false)]
    public void Ключ_виду_P_число_впізнається_лише_точно(string key, bool expected)
        => Assert.Equal(expected, PermitRowPlanner.IsPermitRowKey(key));

    private static InMemoryRegistrySnapshot Snapshot()
    {
        var snapshot = new InMemoryRegistrySnapshot()
            .AddRegistry(Table, ["PERMIT", "Substance", "Limit", "Note", "Missing2"], lookupFields: ["PERMIT"]);

        // Порядок знімка - (Ordinal, Id): запис 201 має менший порядковий номер, ніж 200.
        snapshot.AddEntry(Table, 200, "r200", new()
        {
            ["PERMIT"] = ExpressionValue.Number(1),
            ["Substance"] = ExpressionValue.Text("NOx"),
            ["Limit"] = ExpressionValue.Number(12.5m),
        }, ordinal: 2);
        snapshot.AddEntry(Table, 201, "r201", new()
        {
            ["PERMIT"] = ExpressionValue.Number(1),
            ["Substance"] = ExpressionValue.Text("SO2"),
            ["Limit"] = ExpressionValue.Number(3m),
            ["Note"] = ExpressionValue.Date(new DateTime(2026, 1, 31)),
        }, ordinal: 1);
        snapshot.AddEntry(Table, 202, "r202", new() { ["PERMIT"] = ExpressionValue.Number(2) });
        return snapshot;
    }
}
