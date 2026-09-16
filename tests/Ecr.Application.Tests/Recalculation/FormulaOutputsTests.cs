// tests/Ecr.Application.Tests/Recalculation/FormulaOutputsTests.cs
using Ecr.Application.Recalculation;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Recalculation;

/// <summary>
/// Що саме обчислює формула — одне визначення на всю систему (публікація й
/// рантайм). Розбіжність тут видима лише як «баланс порахувався раніше за суми,
/// з яких складається», тобто як неправильне число, а не як помилка.
/// </summary>
public sealed class FormulaOutputsTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Формула_колонки_обчислює_свою_колонку_в_кожному_рядку()
    {
        var (table, columnA, columnB) = Table();
        var formula = Formula(table, FormulaScope.Column, columnA.Id, rowDefId: null);

        Assert.True(FormulaOutputs.Produces(formula, table, "7001001", columnA.Id));
        Assert.True(FormulaOutputs.Produces(formula, table, "7001002", columnA.Id));
        Assert.False(FormulaOutputs.Produces(formula, table, "7001001", columnB.Id));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Формула_рядка_З_явною_колонкою_не_претендує_на_весь_рядок()
    {
        // ⛔ Аудит 2026-09-16, §1.3. До фіксу `Produces` для Row-скоупу
        // ІГНОРУВАВ `ColumnDefId` (умова містила `&& formula.Scope !=
        // FormulaScope.Row`): формула звітувала, що виробляє КОЖНУ колонку свого
        // рядка. Граф залежностей отримував ребра до формул, які з неї нічого не
        // читають — зайвий перерахунок на кожній правці, не неправильне число,
        // але й не безкоштовний.
        var (table, columnA, columnB) = Table();
        var row = table.Rows.First(r => r.RowKeyValue == "7001001");
        var formula = Formula(table, FormulaScope.Row, columnA.Id, row.Id);

        // Своя комірка — так.
        Assert.True(FormulaOutputs.Produces(formula, table, "7001001", columnA.Id));

        // Сусідня колонка ТОГО САМОГО рядка — ні: саме це й було зайвим ребром.
        Assert.False(FormulaOutputs.Produces(formula, table, "7001001", columnB.Id));

        // Інший рядок — ні, як і було.
        Assert.False(FormulaOutputs.Produces(formula, table, "7001002", columnA.Id));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Формула_рядка_БЕЗ_явної_колонки_обчислює_весь_рядок()
    {
        // Зворотний бік того ж правила: коли колонки в формулі немає, «весь
        // рядок» — правильна відповідь, і звужувати її було б втратою ребра
        // (а це вже старі числа, не зайва робота).
        var (table, columnA, columnB) = Table();
        var row = table.Rows.First(r => r.RowKeyValue == "7001001");
        var formula = Formula(table, FormulaScope.Row, columnDefId: null, row.Id);

        Assert.True(FormulaOutputs.Produces(formula, table, "7001001", columnA.Id));
        Assert.True(FormulaOutputs.Produces(formula, table, "7001001", columnB.Id));
        Assert.False(FormulaOutputs.Produces(formula, table, "7001002", columnA.Id));
    }

    private static (TableDef Table, ColumnDef A, ColumnDef B) Table()
    {
        var builder = new TemplateBuilder { TemplateVersionId = 1 };
        var sheet = builder.Sheet("Water");
        var table = builder.Table(sheet, "Main");

        var a = builder.Column(table, "A");
        var b = builder.Column(table, "B");
        builder.Row(table, "7001001", 1);
        builder.Row(table, "7001002", 2);

        return (table, a, b);
    }

    private static FormulaDef Formula(
        TableDef table, FormulaScope scope, int? columnDefId, int? rowDefId)
    {
        var formula = new FormulaDef(table.Id, scope, "1", ExpressionDialect.Template);
        typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(formula, 100);

        if (columnDefId is { } column)
        {
            typeof(FormulaDef).GetProperty(nameof(FormulaDef.ColumnDefId))!.SetValue(formula, column);
        }

        if (rowDefId is { } row)
        {
            typeof(FormulaDef).GetProperty(nameof(FormulaDef.RowDefId))!.SetValue(formula, row);
        }

        return formula;
    }
}
