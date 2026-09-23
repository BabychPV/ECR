using Ecr.Expressions;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Functions;

/// <summary>
/// <c>REGFIELD(lookup, 'код')</c> — поле запису довідника, на який показує
/// Lookup-комірка.
/// </summary>
/// <remarks>
/// ⚠ Перший аргумент — id запису, узятий зі значення Lookup-комірки ТИМ
/// САМИМ шляхом, яким комірка взагалі читається у формулі: <c>[Permit]</c>
/// тут — звичайне посилання, а не нова синтаксична форма. Код поля — рядковий
/// літерал мови, тому в single quotes (02b §1), не в подвійних лапках.
/// </remarks>
public sealed class RegistryFieldFunctionTests
{
    private static TestEvaluationContext Context(long entryId)
    {
        var context = new TestEvaluationContext();
        context.SetCell("S", "T", string.Empty, "Permit", ExpressionValue.Number(entryId));
        return context;
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Decimal_поле_обчислюється_правильно()
    {
        var context = Context(101);
        context.SetRegistryField(101, "Limit", ExpressionValue.Number(12.5m));

        var value = Expr.Eval("REGFIELD([Permit], 'Limit')", context);

        Assert.Equal(ExpressionValueType.Number, value.Type);
        Assert.Equal(12.5m, value.AsNumber());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Text_поле_обчислюється_правильно()
    {
        var context = Context(101);
        context.SetRegistryField(101, "Status", ExpressionValue.Text("Active"));

        var value = Expr.Eval("REGFIELD([Permit], 'Status')", context);

        Assert.Equal(ExpressionValueType.Text, value.Type);
        Assert.Equal("Active", value.Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Bool_поле_обчислюється_правильно()
    {
        var context = Context(101);
        context.SetRegistryField(101, "IsActive", ExpressionValue.Boolean(true));

        var value = Expr.Eval("REGFIELD([Permit], 'IsActive')", context);

        Assert.Equal(ExpressionValueType.Boolean, value.Type);
        Assert.Equal(true, value.Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Date_поле_обчислюється_правильно()
    {
        var context = Context(101);
        var expiry = new DateTime(2027, 12, 31, 0, 0, 0, DateTimeKind.Utc);
        context.SetRegistryField(101, "ExpiresAt", ExpressionValue.Date(expiry));

        var value = Expr.Eval("REGFIELD([Permit], 'ExpiresAt')", context);

        Assert.Equal(ExpressionValueType.Date, value.Type);
        Assert.Equal(expiry, value.Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Відсутній_запис_довідника_дає_REF_а_не_крах()
    {
        // Lookup-комірка показує на 999, якого в контексті немає взагалі.
        var context = Context(999);

        var value = Expr.Eval("REGFIELD([Permit], 'Limit')", context);

        Assert.True(value.IsError);
        Assert.Equal("#REF", value.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Відсутнє_поле_запису_дає_REF_а_не_крах()
    {
        // Запис 101 існує (є в знімку), але саме поля 'Missing' в нього нема.
        var context = Context(101);
        context.SetRegistryField(101, "Limit", ExpressionValue.Number(1m));

        var value = Expr.Eval("REGFIELD([Permit], 'Missing')", context);

        Assert.True(value.IsError);
        Assert.Equal("#REF", value.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Порожня_Lookup_комірка_дає_null_а_не_помилку()
    {
        // Запис іще не обрали — 02b §6.3: легітимна порожнеча, а не збій.
        var context = new TestEvaluationContext();
        context.SetCell("S", "T", string.Empty, "Permit", ExpressionValue.Null);

        var value = Expr.Eval("REGFIELD([Permit], 'Limit')", context);

        Assert.True(value.IsNull);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Нечисловий_перший_аргумент_дає_VALUE()
    {
        var context = new TestEvaluationContext();
        context.SetCell("S", "T", string.Empty, "Permit", ExpressionValue.Text("not-an-id"));

        var value = Expr.Eval("REGFIELD([Permit], 'Limit')", context);

        Assert.True(value.IsError);
        Assert.Equal("#VALUE", value.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Помилка_в_Lookup_комірці_поширюється()
    {
        // Сама Lookup-комірка вже зіпсована (наприклад, посилання, яке
        // видалили) — REGFIELD поширює цю помилку, а не намагається читати
        // довідник за нею.
        var context = new TestEvaluationContext();
        context.SetCell("S", "T", string.Empty, "Permit", ExpressionValue.Error(ExpressionErrors.BadReference));

        var value = Expr.Eval("REGFIELD([Permit], 'Limit')", context);

        Assert.True(value.IsError);
        Assert.Equal("#REF", value.ErrorCode);
    }
}
