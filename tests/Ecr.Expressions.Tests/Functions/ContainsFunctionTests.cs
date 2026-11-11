// tests/Ecr.Expressions.Tests/Functions/ContainsFunctionTests.cs
using Ecr.Domain.Enums;
using Ecr.Expressions;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Functions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Functions;

/// <summary>
/// <c>CONTAINS(text, fragment)</c> (AN-165): чи містить текст фрагмент. Семантика — чинного C#
/// <c>string.Contains(string)</c>: порядкове порівняння з урахуванням регістру (чинний
/// <c>ECW_C11_01.cs:109</c> перевіряє <c>"gas"</c> і <c>"Gas"</c> окремо).
/// </summary>
/// <remarks>
/// Мутаційні докази (лише цей клас): <c>StringComparison.OrdinalIgnoreCase</c> замість <c>Ordinal</c> —
/// червоніє <see cref="Регістр_значущий_як_у_string_Contains"/>; прибрати гілку <c>IsNull → FALSE</c>
/// (віддати NULL у загальне правило) — <see cref="NULL_і_порожній_текст_дають_FALSE"/>; прибрати
/// <c>text.Length &gt; 0</c> — той самий тест (порожній текст із порожнім фрагментом); приймати число
/// (<c>ToString</c>) замість #VALUE — <see cref="Нетекстовий_аргумент_дає_VALUE"/>; прибрати поширення
/// помилки аргументу — <see cref="Помилка_аргументу_поширюється"/>; прибрати «CONTAINS» з
/// <c>LegacyCompatibleRegistryFunctions</c> або змінити арність —
/// <see cref="Каталог_знає_функцію_як_розширення_з_булевим_результатом_і_двома_аргументами"/>.
/// </remarks>
public sealed class ContainsFunctionTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Каталог_знає_функцію_як_розширення_з_булевим_результатом_і_двома_аргументами()
    {
        var signature = DialectCatalog.Find("CONTAINS");

        Assert.NotNull(signature);
        Assert.Equal(2, signature.MinArgs);
        Assert.Equal(2, signature.MaxArgs);
        Assert.Equal(ExpressionValueType.Boolean, signature.ResultType);
        Assert.Equal(FunctionTier.Extension, DialectCatalog.TierOf("CONTAINS"));
        Assert.True(DialectCatalog.IsAllowedIn("CONTAINS", NumericMode.Legacy)); // ECW_C11_01 — Legacy
        Assert.True(DialectCatalog.IsAllowedIn("CONTAINS", NumericMode.Strict));

        // Регістр імені значущий, як і в решти каталогу.
        Assert.Null(DialectCatalog.Find("Contains"));

        Assert.True(Expr.Parse("CONTAINS(@S, 'gas')", ExpressionDialect.Methodology).IsSuccess);
        Assert.False(Expr.Parse("CONTAINS(@S)", ExpressionDialect.Methodology).IsSuccess);
        Assert.False(Expr.Parse("CONTAINS(@S, 'gas', 'x')", ExpressionDialect.Methodology).IsSuccess);
        Assert.False(Expr.Parse("CONTAINS('abc', 'b')", ExpressionDialect.Template).IsSuccess);
    }

    [Theory]
    [InlineData("Fuel gas (Internal)", "gas", true)]
    [InlineData("Fuel gas (Internal)", "Fuel gas (Internal)", true)] // увесь текст — теж фрагмент
    [InlineData("1-204 sludge", "sludge", true)]
    [InlineData("6646(001)", "6646", true)]
    [InlineData("6646(001)", "6647", false)]
    [InlineData("LPG", "LPG ", false)]   // пробіл — символ, обрізання немає
    [InlineData("abc", "", true)]        // string.Contains(""): порожній фрагмент є в непорожньому тексті
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Текст_містить_фрагмент(string text, string fragment, bool expected)
    {
        var context = With(ExpressionValue.Text(text));
        context.Arguments["F"] = ExpressionValue.Text(fragment);

        var value = Eval("CONTAINS(@S, @F)", context);

        Assert.Equal(ExpressionValueType.Boolean, value.Type);
        Assert.Equal(expected, value.Value);
    }

    [Theory]
    [InlineData("Fuel Gas", "Gas", true)]
    [InlineData("Fuel Gas", "gas", false)]
    [InlineData("fuel gas", "Gas", false)]
    [InlineData("FUEL GAS", "gas", false)]
    [InlineData("Diesel", "diesel", false)]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Регістр_значущий_як_у_string_Contains(string text, string fragment, bool expected)
    {
        var context = With(ExpressionValue.Text(text));
        context.Arguments["F"] = ExpressionValue.Text(fragment);

        Assert.Equal(expected, Eval("CONTAINS(@S, @F)", context).Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void NULL_і_порожній_текст_дають_FALSE()
    {
        // NULL-текст: не NULL і не помилка, а FALSE — умова вибору гілки має бути визначеною.
        var missing = Eval("CONTAINS(NULL, 'gas')", new TestEvaluationContext());
        Assert.Equal(ExpressionValueType.Boolean, missing.Type);
        Assert.Equal(false, missing.Value);

        var empty = Eval("CONTAINS(@S, 'gas')", With(ExpressionValue.Text(string.Empty)));
        Assert.Equal(ExpressionValueType.Boolean, empty.Type);
        Assert.Equal(false, empty.Value);

        // Порожній текст — FALSE навіть для порожнього фрагмента (у string.Contains було б true).
        var both = Eval("CONTAINS(@S, '')", With(ExpressionValue.Text(string.Empty)));
        Assert.Equal(false, both.Value);
    }

    [Theory]
    [InlineData("CONTAINS(6646, '66')")]     // число замість тексту
    [InlineData("CONTAINS(@N, '66')")]       // числовий аргумент
    [InlineData("CONTAINS('6646', 66)")]     // число замість фрагмента
    [InlineData("CONTAINS('6646', NULL)")]   // фрагмент обов'язковий
    [InlineData("CONTAINS(1 = 1, 'True')")]  // булеве замість тексту
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Нетекстовий_аргумент_дає_VALUE(string expression)
    {
        var context = new TestEvaluationContext();
        context.Arguments["N"] = ExpressionValue.Number(6646m);

        Assert.Equal(ExpressionErrors.BadValue, Eval(expression, context).ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Помилка_аргументу_поширюється()
    {
        var context = new TestEvaluationContext();

        Assert.Equal(ExpressionErrors.DivideByZero, Eval("CONTAINS(1 / 0, 'gas')", context).ErrorCode);
        Assert.Equal(ExpressionErrors.DivideByZero, Eval("CONTAINS('gas', 1 / 0)", context).ErrorCode);
    }

    [Theory]
    [InlineData("Fuel gas", 5)]
    [InlineData("LPG", 5)]
    [InlineData("LPG and sludge", null)] // пізніший маркер перебиває ранній — через not
    [InlineData("Diesel", null)]
    [InlineData("", null)]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void У_воротах_гілки_поєднується_з_and_or_not(string stream, int? expected)
    {
        // Форма воріт гілки (LandBranchGate.Lib.ps1): if(<умова>, <вираз>, NULL).
        const string gate =
            "if((CONTAINS(@S, 'gas') or CONTAINS(@S, 'LPG')) and not (CONTAINS(@S, 'sludge')), 5, NULL)";

        var value = Eval(gate, With(ExpressionValue.Text(stream)));

        if (expected is null)
        {
            Assert.True(value.IsNull);
        }
        else
        {
            Assert.Equal((decimal)expected.Value, Convert.ToDecimal(value.Value, System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private static ExpressionValue Eval(string expression, IEvaluationContext context)
        => Expr.Eval(expression, context, ExpressionDialect.Methodology);

    private static TestEvaluationContext With(ExpressionValue stream)
    {
        var context = new TestEvaluationContext();
        context.Arguments["S"] = stream;
        return context;
    }
}
