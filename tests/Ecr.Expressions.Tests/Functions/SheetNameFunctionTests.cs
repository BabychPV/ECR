// tests/Ecr.Expressions.Tests/Functions/SheetNameFunctionTests.cs
using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Functions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Functions;

/// <summary>
/// <c>SHEETNAME()</c> діалекту шаблону: показувана назва екземпляра аркуша, у якому рахується формула («Vessel 2» для
/// копії аркуша; рішення людини 08.11 — копії «&lt;Назва&gt; 2,3…»).
/// </summary>
/// <remarks>
/// Мутаційні докази (лише цей клас, локально): прибрати рядок <c>SHEETNAME</c> з каталогу <c>FunctionRegistry</c> —
/// червоніють усі тести класу; повертати з <c>TemplateFunctions.SheetName</c> порожній рядок замість <c>null</c> —
/// червоніє <see cref="Без_аркуша_значення_порожнє_а_не_помилка"/>; прибрати делегування <c>SheetName</c> у
/// <c>RegistryRowScope</c> — червоніє <see cref="Назву_видно_всередині_агрегата_довідника"/>.
/// </remarks>
public sealed class SheetNameFunctionTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Каталог_шаблону_знає_функцію_без_аргументів_з_текстовим_результатом()
    {
        var signature = new FunctionRegistry().GetSignature("SHEETNAME");

        Assert.NotNull(signature);
        Assert.Equal(0, signature.MinArgs);
        Assert.Equal(0, signature.MaxArgs);
        Assert.False(signature.AcceptsRange);
        Assert.Equal(ExpressionValueType.Text, signature.ResultType);
        Assert.Contains("SHEETNAME", FunctionRegistry.Names);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Розбір_приймає_лише_виклик_без_аргументів_і_лише_в_шаблоні()
    {
        Assert.True(Expr.Parse("SHEETNAME()", ExpressionDialect.Template).IsSuccess);
        Assert.True(Expr.Parse("sheetname()", ExpressionDialect.Template).IsSuccess);
        Assert.False(Expr.Parse("SHEETNAME(1)", ExpressionDialect.Template).IsSuccess);

        // Методологія аркуша не має (02b §3.4), звіт — теж: там функції немає.
        Assert.False(Expr.Parse("SHEETNAME()", ExpressionDialect.Methodology).IsSuccess);
        Assert.False(Expr.Parse("SHEETNAME()", ExpressionDialect.Report).IsSuccess);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Повертає_назву_екземпляра_аркуша_і_складається_з_текстом()
    {
        var context = new TestEvaluationContext { SheetName = "Vessel 2" };

        var name = Eval("SHEETNAME()", context);
        var label = Eval("SHEETNAME() + ' — total'", context);

        Assert.Equal(ExpressionValueType.Text, name.Type);
        Assert.Equal("Vessel 2", name.Value);
        Assert.Equal("Vessel 2 — total", label.Value);
        Assert.Equal(1m, Eval("IF(SHEETNAME() = 'Vessel 2', 1, 0)", context).AsNumber());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Без_аркуша_значення_порожнє_а_не_помилка()
    {
        // Правило валідації, перевірка виразу — контекст без аркуша: null (02b §7), не #REF і не порожній рядок.
        var none = Eval("SHEETNAME()", new TestEvaluationContext());
        var blank = Eval("SHEETNAME()", new TestEvaluationContext { SheetName = string.Empty });

        Assert.True(none.IsNull);
        Assert.True(blank.IsNull);
        Assert.False(none.IsError);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Назву_видно_всередині_агрегата_довідника()
    {
        // REGCOUNT відкриває область рядка (RegistryRowScope): назва аркуша мусить бути видимою і там.
        var snapshot = new InMemoryRegistrySnapshot().AddRegistry("VESSELS", ["NAME"], primaryKey: ["NAME"]);
        snapshot.AddEntry("VESSELS", 1, "V1", new() { ["NAME"] = ExpressionValue.Text("Vessel 2") }, ordinal: 1);
        snapshot.AddEntry("VESSELS", 2, "V2", new() { ["NAME"] = ExpressionValue.Text("Vessel 3") }, ordinal: 2);

        var context = snapshot.Attach(new TestEvaluationContext { SheetName = "Vessel 2" });

        Assert.Equal(1m, Eval("REGCOUNT('VESSELS', ROW.NAME = SHEETNAME())", context).AsNumber());
    }

    private static ExpressionValue Eval(string expression, IEvaluationContext context)
        => Expr.Eval(expression, context, ExpressionDialect.Template);
}
