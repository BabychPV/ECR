using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Binding;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Functions;
using Ecr.Expressions.Graph;
using Ecr.Expressions.Parsing;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests;

/// <summary>
/// Двадцять чотири крайові випадки з <c>02c §8</c>. Кожен — окремий тест;
/// назва відповідає ідентифікатору випадку.
/// </summary>
public sealed class EdgeCaseTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E01_SUM_порожньої_множини_дорівнює_нулю()
        => Assert.Equal(0m, TemplateFunctions.Sum([]).AsNumber());

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E02_AVERAGE_порожньої_множини_дорівнює_null()
        => Assert.True(TemplateFunctions.Average([]).IsNull);

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E03_null_помножений_на_нуль_дає_null() => Assert.True(Expr.Eval("NULL * 0").IsNull);

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E04_ділення_на_нуль_дає_помилку()
        => Assert.Equal("#DIV/0", Expr.Eval("1 / 0").ErrorCode);

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E05_ділення_на_null_дає_помилку()
        => Assert.Equal("#DIV/0", Expr.Eval("1 / NULL").ErrorCode);

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E06_помилка_поширюється()
        => Assert.Equal("#DIV/0", Expr.Eval("(1 / 0) * 100 + 5").ErrorCode);

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E07_IFERROR_перехоплює_помилку()
        => Assert.Equal(-1m, Expr.Number("IFERROR(1 / 0, -1)"));

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E08_IFERROR_не_перехоплює_null()
        => Assert.True(Expr.Eval("IFERROR(NULL, -1)").IsNull);

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E09_конкатенація_з_null_дає_другий_операнд()
        => Assert.Equal("x", Expr.Eval("NULL & 'x'").Value);

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E10_порівняння_з_null_дає_null() => Assert.True(Expr.Eval("NULL > 1").IsNull);

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E11_рівність_null_дає_TRUE() => Assert.True((bool)Expr.Eval("NULL = NULL").Value!);

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E12_округлення_двох_з_половиною_дає_три()
        => Assert.Equal(3m, Expr.Number("ROUND(2.5, 0)"));

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E13_округлення_мінус_двох_з_половиною_дає_мінус_три()
        => Assert.Equal(-3m, Expr.Number("ROUND(-2.5, 0)"));

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void E14_конверсія_різних_розмірностей_дає_помилку()
    {
        var context = UnitContext();

        // ⛔ Маса → об'єм. Не «немає коефіцієнта», а не існує в принципі:
        // перехід між ними потребує щільності, тобто властивості речовини
        // (ФВ-16.3, ФВ-16.5).
        var value = Expr.Eval("CONVERT(1, 'kg', 'm3')", context, ExpressionDialect.Methodology);

        Assert.True(value.IsError);
        Assert.Equal(ExpressionErrors.BadUnit, value.ErrorCode);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void E15_конверсія_градусів_у_Кельвіни_дає_273_15()
    {
        var context = UnitContext();

        // ⚠ Зсув — єдина причина, чому формула конверсії не «значення × k».
        // Без нього нуль Цельсія став би нулем Кельвіна, тобто абсолютним
        // нулем: помилка на 273 градуси, яку видно лише тому, хто знає фізику.
        Assert.Equal(273.15m, Expr.Number("CONVERT(0, 'degC', 'K')", context));
        Assert.Equal(373.15m, Expr.Number("CONVERT(100, 'degC', 'K')", context));

        // І назад — симетрично.
        Assert.Equal(0m, Expr.Number("CONVERT(273.15, 'K', 'degC')", context));
    }

    /// <summary>Контекст із довідником одиниць за seed-ом (`09-seed.sql`).</summary>
    private static TestEvaluationContext UnitContext()
    {
        var context = new TestEvaluationContext();

        context.SetUnit("kg", dimension: 1, factorToBase: 1m);
        context.SetUnit("t", dimension: 1, factorToBase: 1000m);
        context.SetUnit("g", dimension: 1, factorToBase: 0.001m);
        context.SetUnit("m3", dimension: 2, factorToBase: 1m);
        context.SetUnit("K", dimension: 5, factorToBase: 1m);
        context.SetUnit("degC", dimension: 5, factorToBase: 1m, offsetToBase: 273.15m);

        return context;
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E16_крос_період_за_межу_проєкту_дає_null()
    {
        var context = new TestEvaluationContext();
        context.SetCell("S", "Main", "7001001", "Total", ExpressionValue.Number(5m));

        var value = Expr.Eval("[Period:-1].[Main].[7001001].[Total]", context);

        Assert.True(value.IsNull);
        Assert.False(value.IsError);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E17_цикл_відхиляє_публікацію()
    {
        var graph = new DependencyGraph();
        graph.AddEdge(1, 2);
        graph.AddEdge(2, 1);

        var result = new TopologicalSorter().Sort(graph);

        Assert.False(result.IsSuccess);
        Assert.NotEmpty(result.CyclePath!);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E18_порівняння_числа_з_текстом_відхиляє_публікацію()
    {
        var context = new TestBindingContext();
        context.ColumnTypes["Note"] = ExpressionValueType.Text;

        var diagnostics = new List<ExpressionDiagnostic>();
        new TypeChecker().Check(Expr.Parse("[Note] > 1").Expression!.Root, context, diagnostics);

        Assert.Contains(diagnostics, d => d.Code == "ECR-TMPL-4222");
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E19_зміна_Ordinal_не_змінює_результат_діапазону()
    {
        var builder = new TemplateBuilder();
        var table = builder.Table(builder.Sheet("Water"), "Main");
        builder.Column(table, "Jan");
        builder.Row(table, "A", 1);
        builder.Row(table, "B", 2);
        builder.Row(table, "C", 3);

        var expander = new RangeExpander();
        var published = expander.Expand(table, "A", "B");

        table.Rows.Single(r => r.RowKeyValue == "C").Reorder(2);
        table.Rows.Single(r => r.RowKeyValue == "B").Reorder(3);

        // Розкриття зафіксоване публікацією; презентаційна правка не має
        // права змінювати числа.
        Assert.Equal(["A", "B"], published);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void E20_дублікат_RowKey_відхиляється()
    {
        var table = new TableDef(
            sheetDefId: 1, EcrCode.Create("TBL"), Name("Table"), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);

        table.AddRow(new RowDef(table.Id, RowKey.Create("7001001"), 1, Name("Перший"), RowKind.Item));

        // ⚠ RowKey — це ІДЕНТИЧНІСТЬ рядка, на неї посилаються вирази і дані
        // всіх минулих періодів. Два рядки з одним ключем означають, що
        // посилання перестало бути однозначним, і жоден діапазон більше не
        // розкривається передбачувано.
        var error = Assert.Throws<DomainException>(() =>
            table.AddRow(new RowDef(table.Id, RowKey.Create("7001001"), 2, Name("Другий"), RowKind.Item)));

        Assert.Equal("ECR-TMPL-0409", error.ErrorCode);
        Assert.Single(table.Rows);
    }

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void E21_агрегація_різних_одиниць_без_CONVERT_відхиляє_публікацію()
    {
        var context = new TestBindingContext();

        // Колонка `DataType = Unit`: одиниця лежить у кожній комірці окремо
        // (ФВ-16.8, R-A4). SUM склав би тонни з кілограмами і дав число, яке
        // виглядає правдоподібно.
        context.RowScopedUnitColumns.Add("Amount");
        context.UnitsByCode["kg"] = 1;
        context.Dimensions[1] = 1;

        var diagnostics = new List<ExpressionDiagnostic>();
        new UnitChecker().Check(
            Expr.Parse("SUM([Items].[Amount])").Expression!.Root, context, diagnostics);

        // ⛔ Помилка ПУБЛІКАЦІЇ, а не рантайму: у рантаймі вона вже нічого не
        // рятує — число подане.
        Assert.Contains(diagnostics, d => d.Code == "ECR-TMPL-4223");

        // Явне приведення знімає заборону — і це єдиний спосіб (D-74).
        var converted = new List<ExpressionDiagnostic>();
        new UnitChecker().Check(
            Expr.Parse("SUM(CONVERT([Items].[Amount], [AmountUnit], 'kg'))").Expression!.Root,
            context, converted);

        Assert.Empty(converted);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E22_відсутня_комірка_бере_DefaultValue()
    {
        var context = new TestEvaluationContext { CurrentRow = "R1" };
        context.Defaults["S|T|Jan"] = ExpressionValue.Number(42m);

        Assert.Equal(42m, Expr.Number("[Jan]", context));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E23_явна_порожнеча_ігнорує_DefaultValue()
    {
        var context = new TestEvaluationContext { CurrentRow = "R1" };
        context.Defaults["S|T|Jan"] = ExpressionValue.Number(42m);
        context.SetCell("S", "T", "R1", "Jan", ExpressionValue.Null);

        Assert.True(Expr.Eval("[Jan]", context).IsNull);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void E24_нуль_ціла_один_плюс_нуль_ціла_два_дорівнює_рівно_нуль_ціла_три()
    {
        // «Рівно» — ключове слово. У double тут 0.30000000000000004, і саме
        // це робить звірку з еталоном неможливою (D-30).
        Assert.Equal(0.3m, Expr.Number("0.1 + 0.2"));
        Assert.True(Expr.Number("0.1 + 0.2") == 0.3m);
    }
}
