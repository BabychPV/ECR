using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Binding;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Parsing;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Binding;

/// <summary>
/// Діапазони розкриваються **при публікації**, а не в рантаймі.
/// </summary>
/// <remarks>
/// Тест <c>Зміна_Ordinal_після_публікації_не_змінює_результат</c> — головний
/// у цьому файлі. Якби діапазон обчислювався за <c>Ordinal</c> у рантаймі,
/// презентаційна правка мовчки змінювала б числа: даних не зіпсовано,
/// а результат інший — найгірший клас помилок.
/// </remarks>
public sealed class RangeExpansionTests
{
    private static readonly RangeExpander Expander = new();

    private static (TemplateBuilder Builder, TableDef Table) FixedTable()
    {
        var builder = new TemplateBuilder();
        var sheet = builder.Sheet("Water");
        var table = builder.Table(sheet, "Main");
        builder.Column(table, "Jan");
        builder.Row(table, "7001001", 1);
        builder.Row(table, "7001002", 2);
        builder.Row(table, "7001003", 3);
        builder.Row(table, "7001004", 4);
        return (builder, table);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Діапазон_розкривається_у_список_ключів_за_Ordinal()
    {
        var (_, table) = FixedTable();

        var keys = Expander.Expand(table, "7001001", "7001003");

        Assert.Equal(["7001001", "7001002", "7001003"], keys);
        Assert.DoesNotContain("7001004", keys);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-0.2")]
    public void Зміна_Ordinal_після_публікації_НЕ_змінює_результат_формули()
    {
        var (_, table) = FixedTable();

        // Публікація: діапазон розкрито в конкретні ключі.
        var published = Expander.Expand(table, "7001001", "7001003");

        // Презентаційна правка: рядок 7001004 переїхав усередину діапазону.
        table.Rows.Single(r => r.RowKeyValue == "7001004").Reorder(2);
        table.Rows.Single(r => r.RowKeyValue == "7001002").Reorder(4);

        // ⚠ Формула вже посилається на ключі, а не на порядок. Якби діапазон
        // рахувався в рантаймі за Ordinal, у сумі з'явився б 7001004 — числа
        // змінилися б від правки, яка не чіпала даних.
        var context = new TestEvaluationContext { CurrentSheet = "Water", CurrentTable = "Main" };
        foreach (var (key, amount) in new[] { ("7001001", 10m), ("7001002", 20m), ("7001003", 30m), ("7001004", 999m) })
        {
            context.SetCell("Water", "Main", key, "Jan", ExpressionValue.Number(amount));
        }

        var total = published.Sum(key => context.Cells[TestEvaluationContext.Key("Water", "Main", key, "Jan")].AsNumber()!.Value);

        Assert.Equal(60m, total);
        Assert.Equal(["7001001", "7001002", "7001003"], published);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Розкритий_діапазон_зберігається_із_порядковим_номером_кожного_рядка()
    {
        var (builder, table) = FixedTable();
        var snapshot = builder.Build();
        var extractor = new DependencyExtractor(new ReferenceResolver(snapshot), Expander);

        var root = Expr.Parse("SUM([Main].[7001001:7001003].[Jan])").Expression!.Root;
        var dependencies = extractor.Extract(
            root, table.Id, null, new Dictionary<int, TableDef> { [table.Id] = table });

        // По одній залежності НА КОЖЕН рядок — саме вони й утворюють зворотний
        // індекс «які формули залежать від цієї комірки».
        Assert.Equal(3, dependencies.Count);
        Assert.Equal(["7001001", "7001002", "7001003"], dependencies.Select(d => d.RowKey));
        Assert.Equal([0, 1, 2], dependencies.Select(d => d.SortOrder));
        Assert.All(dependencies, d => Assert.Null(d.FilterJson));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Неіснуюча_межа_діапазону_дає_ECR_TMPL_4222_при_публікації()
    {
        var (_, table) = FixedTable();
        var diagnostics = new List<ExpressionDiagnostic>();

        var keys = Expander.Expand(table, "7001001", "7009999", diagnostics);

        Assert.Empty(keys);
        Assert.Contains(diagnostics, d => d.Code == "ECR-TMPL-4222");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-7.6")]
    public void Видалені_рядки_не_потрапляють_у_розкритий_діапазон()
    {
        var (_, table) = FixedTable();
        TemplateBuilder.Delete(table.Rows.Single(r => r.RowKeyValue == "7001002"));

        var keys = Expander.Expand(table, "7001001", "7001003");

        // Soft delete лишає рядок у таблиці заради історії, але «сюди більше
        // не входить» — це і є сенс видалення (ФВ-7.6).
        Assert.Equal(["7001001", "7001003"], keys);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-7.6")]
    public void Для_динамічної_таблиці_діапазон_записується_предикатом_а_не_переліком()
    {
        var builder = new TemplateBuilder();
        var sheet = builder.Sheet("Waste");
        var table = builder.Table(sheet, "Items", TableRowMode.Dynamic);
        builder.Column(table, "Amount");
        // ⚠ Саме Lookup, а не String: на String цей тест не ловив Q-072 —
        // резолвер відхиляв БУДЬ-ЯКЕ посилання на Lookup-колонку, і законний
        // предикат `[WHERE [WasteType] = 'W-01']` не проходив публікацію.
        builder.Column(table, "WasteType", CellDataType.Lookup);
        var snapshot = builder.Build();

        var extractor = new DependencyExtractor(new ReferenceResolver(snapshot), Expander);
        var root = Expr.Parse("SUM([Items].[WHERE [WasteType] = 'W-01'].[Amount])").Expression!.Root;

        var dependencies = extractor.Extract(root, table.Id, null);

        // Рядків динамічної таблиці на момент публікації ще НЕМАЄ — перелічити
        // їх неможливо в принципі, тому зберігається умова.
        var dependency = Assert.Single(dependencies);
        Assert.Null(dependency.RowKey);
        Assert.NotNull(dependency.FilterJson);
        Assert.Contains("W-01", dependency.FilterJson!, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Конкретний_RowKey_для_динамічної_таблиці_відхиляється()
    {
        var builder = new TemplateBuilder();
        var sheet = builder.Sheet("Waste");
        var table = builder.Table(sheet, "Items", TableRowMode.Dynamic);
        builder.Column(table, "Amount");
        var snapshot = builder.Build();

        var diagnostics = new List<ExpressionDiagnostic>();
        var reference = (CellReferenceNode)Expr.Parse("[Items].[a1b2].[Amount]").Expression!.Root;

        var resolved = new ReferenceResolver(snapshot).Resolve(reference, table.Id, null, diagnostics);

        Assert.Null(resolved);
        Assert.Contains(diagnostics, d => d.Code == "ECR-TMPL-4222");
    }
}
