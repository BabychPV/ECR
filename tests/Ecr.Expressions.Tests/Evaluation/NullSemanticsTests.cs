using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Functions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Evaluation;

/// <summary>
/// **Два різні правила щодо `null`, які не можна плутати** (02b §6):
/// в агрегатах він поглинається, у бінарних операторах — поширюється.
/// Саме тут народжуються розбіжності зі старою системою.
/// </summary>
public sealed class NullSemanticsTests
{
    // ——— Поглинання в агрегатах ———

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void SUM_ігнорує_null_елементи()
    {
        // Сума трьох місяців, де один порожній, має дорівнювати сумі двох.
        Assert.Equal(3m, Expr.Number("SUM(1, NULL, 2)"));
        Assert.Equal(
            Expr.Number("SUM(1, 2)"),
            Expr.Number("SUM(1, NULL, 2)"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void SUM_порожньої_множини_дорівнює_нулю_а_не_null()
    {
        var value = TemplateFunctions.Sum([]);

        Assert.Equal(ExpressionValueType.Number, value.Type);
        Assert.Equal(0m, value.AsNumber());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void AVERAGE_не_рахує_null_ані_в_сумі_ані_в_дільнику()
    {
        // ⚠ Головна пастка: якби null рахувався нулем у дільнику, вийшло б
        // (10+20)/3 = 10, і це «майже правильне» число ніхто б не помітив.
        Assert.Equal(15m, Expr.Number("AVERAGE(10, NULL, 20)"));
        Assert.NotEqual(10m, Expr.Number("AVERAGE(10, NULL, 20)"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void AVERAGE_порожньої_множини_дорівнює_null_а_не_нулю()
    {
        var value = TemplateFunctions.Average([]);

        Assert.True(value.IsNull);
        Assert.False(value.IsError);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void PRODUCT_порожньої_множини_дорівнює_одиниці()
    {
        // Одиниця — нейтральний елемент множення; нуль занулив би все, що на
        // цей добуток помножать далі.
        var value = TemplateFunctions.Product([]);

        Assert.Equal(1m, value.AsNumber());
    }

    // ——— Поширення в бінарних операторах ———

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Додавання_до_null_дає_null()
    {
        Assert.True(Expr.Eval("NULL + 1").IsNull);
        Assert.True(Expr.Eval("1 + NULL").IsNull);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Множення_null_на_нуль_дає_null_а_НЕ_нуль()
    {
        // Нуль тут був би твердженням «добуток точно нульовий», хоча множник
        // невідомий. Це різниця між «не знаю» і «нічого».
        var value = Expr.Eval("NULL * 0");

        Assert.True(value.IsNull);
        Assert.NotEqual(0m, value.AsNumber());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Конкатенація_трактує_null_як_порожній_рядок()
    {
        // Єдиний виняток із правила поширення: інакше одна незаповнена комірка
        // стирала б увесь складений підпис.
        var value = Expr.Eval("NULL & 'x'");

        Assert.Equal(ExpressionValueType.Text, value.Type);
        Assert.Equal("x", value.Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Рівність_двох_null_дає_TRUE()
    {
        var value = Expr.Eval("NULL = NULL");

        Assert.Equal(ExpressionValueType.Boolean, value.Type);
        Assert.True((bool)value.Value!);

        // …і при цьому null = 1 дає саме FALSE, а не null: рівність порівнює
        // порожнечу як стан, а не поширює її.
        var mixed = Expr.Eval("NULL = 1");
        Assert.Equal(ExpressionValueType.Boolean, mixed.Type);
        Assert.False((bool)mixed.Value!);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Порівняння_null_з_числом_дає_null_а_не_FALSE()
    {
        // FALSE означало б «точно не більше», хоча значення невідоме.
        var greater = Expr.Eval("NULL > 1");
        var less = Expr.Eval("NULL < 1");

        Assert.True(greater.IsNull);
        Assert.True(less.IsNull);
    }

    // ——— Порожня комірка проти явної порожнечі ———

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Відсутня_комірка_бере_DefaultValue_колонки()
    {
        var context = new TestEvaluationContext { CurrentRow = "R1" };
        context.Rows["S|T"] = ["R1"];
        context.Defaults["S|T|Jan"] = ExpressionValue.Number(7m);

        // Рядка CellValue немає — беремо DefaultValue: «не заповнювали» може
        // мати дефолт.
        Assert.Equal(7m, Expr.Number("[Jan]", context));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Явна_порожнеча_ігнорує_DefaultValue_і_дає_null()
    {
        var context = new TestEvaluationContext { CurrentRow = "R1" };
        context.Defaults["S|T|Jan"] = ExpressionValue.Number(7m);
        context.SetCell("S", "T", "R1", "Jan", ExpressionValue.Null);

        // IsEmpty = 1 означає «свідомо лишили порожнім» — дефолт не діє.
        // Плутанина тут дала б у звіті число, якого ніхто не вводив.
        Assert.True(Expr.Eval("[Jan]", context).IsNull);
    }
}
