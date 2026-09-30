using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Functions;
using Ecr.Expressions.Parsing;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests;

/// <summary>
/// Переповнення <see cref="decimal"/> і аргумент поза областю визначення дають
/// ЗНАЧЕННЯ-помилку в комірці, а не виняток (аудит A2, `02b` §6.4).
/// </summary>
/// <remarks>
/// ⛔ Що було. <c>StrictDecimalArithmetic</c> рахував <c>a * b</c>,
/// <c>DecimalMath.Exp</c> і <c>DecimalMath.Pow</c> без перехоплення:
/// <c>1e15 * 1e15</c> кидав <see cref="OverflowException"/>, <c>Pow(0, -1)</c> —
/// <see cref="DivideByZeroException"/>, <c>Exp(70)</c> — знову
/// <see cref="OverflowException"/>. Виняток пролітав крізь обчислювач, модуль і
/// оркестратор до <c>RecalculationJob</c>, і той позначав <c>Failed</c> увесь
/// прогін — через одну комірку. У <c>Legacy</c> те саме робило звуження
/// <c>(decimal)1e30</c> в <see cref="ExpressionValue.AsNumber"/>.
///
/// ⚠ Коди — наявні: <c>#NUM</c> у контракті немає, і переповнення вже давало
/// <c>#VALUE</c> на операторі <c>^</c> шаблонів. Нуль у від'ємному степені —
/// <c>#DIV/0</c>, як там само.
/// </remarks>
public sealed class DecimalOverflowIsValueTests
{
    private const decimal E15 = 1_000_000_000_000_000m;

    /// <summary>≈ 5·10²⁸: вміщується в <see cref="decimal"/>, а подвоєне — ні.</summary>
    private const decimal HalfMax = 50_000_000_000_000_000_000_000_000_000m;

    private static ExpressionValue Methodology(
        string expression, IEvaluationArithmetic arithmetic, Action<TestEvaluationContext>? arrange = null)
    {
        var parsed = new Parser().Parse(expression, ExpressionDialect.Methodology);
        Assert.True(parsed.IsSuccess, string.Join("; ", parsed.Diagnostics.Select(d => d.Message)));

        var context = new TestEvaluationContext();
        arrange?.Invoke(context);

        return new Evaluator(new FunctionRegistry(), arithmetic)
            .Evaluate(parsed.Expression!.Root, context, ExpressionDialect.Methodology);
    }

    private static ExpressionValue Template(string expression)
    {
        var parsed = new Parser().Parse(expression, ExpressionDialect.Template);
        Assert.True(parsed.IsSuccess, string.Join("; ", parsed.Diagnostics.Select(d => d.Message)));

        return new Evaluator(new FunctionRegistry())
            .Evaluate(parsed.Expression!.Root, new TestEvaluationContext(), ExpressionDialect.Template);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.9")]
    [InlineData("@A * @A", "#VALUE")] // 1e15 * 1e15 — сценарій аудиту
    [InlineData("Pow(@A, 2)", "#VALUE")] // той самий добуток через цілий степінь
    [InlineData("@M + @M", "#VALUE")] // 5e28 + 5e28
    [InlineData("@M - (0 - @M)", "#VALUE")]
    [InlineData("@M / 0.1", "#VALUE")] // ділення, що ЗБІЛЬШУЄ
    [InlineData("IEEERemainder(@M, 0.1)", "#VALUE")] // a / b всередині
    [InlineData("Exp(70)", "#VALUE")] // сценарій аудиту
    [InlineData("Pow(0, -1)", "#DIV/0")] // сценарій аудиту
    [InlineData("Pow(-8, 0.5)", "#VALUE")] // дробовий степінь від'ємної основи
    [InlineData("Ln(0)", "#VALUE")] // уже було значенням — політика та сама
    [InlineData("Sqrt(-1)", "#VALUE")]
    [InlineData("1 / 0", "#DIV/0")]
    public void Strict_відмова_області_визначення_це_значення_помилка(string expression, string expected)
    {
        var value = Methodology(expression, new StrictDecimalArithmetic(), c =>
        {
            c.Arguments["A"] = ExpressionValue.Number(E15);
            c.Arguments["M"] = ExpressionValue.Number(HalfMax);
        });

        Assert.True(value.IsError, $"{expression} дав {value.Value}, а не помилку");
        Assert.Equal(expected, value.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.9")]
    public void Strict_межові_значення_без_переповнення_рахуються_як_раніше()
    {
        // ⚠ Контроль: перехоплення не сміє зачепити законні числа поруч із межею.
        var arithmetic = new StrictDecimalArithmetic();

        Assert.Equal(E15 * 1000m, Methodology("@A * 1000", arithmetic, c => c.Arguments["A"] = ExpressionValue.Number(E15)).AsNumber());
        Assert.Equal(HalfMax, Methodology("@M + 0", arithmetic, c => c.Arguments["M"] = ExpressionValue.Number(HalfMax)).AsNumber());
        Assert.Equal(0.125m, Methodology("Pow(2, -3)", arithmetic).AsNumber());
        Assert.Equal(1m, Methodology("Exp(0)", arithmetic).AsNumber());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.9")]
    public void Помилка_переповнення_поширюється_далі_як_будь_яка_помилка_значення()
    {
        // ⚠ Помилка тепер — звичайне значення §6.4: вона йде крізь наступні
        // операції, а не губиться й не перетворюється на нуль.
        var value = Methodology("@A * @A + 1", new StrictDecimalArithmetic(),
            c => c.Arguments["A"] = ExpressionValue.Number(E15));

        Assert.Equal(ExpressionErrors.BadValue, value.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.9")]
    public void Арифметика_Strict_сама_тримає_контракт_без_обчислювача()
    {
        // ⛔ Контракт класу — «помилки тут значення». Перевіряється напряму, бо
        // `MethodologyFunctions` кличе арифметику в обхід операторів
        // обчислювача, і виняток звідси не має шансу бути перехопленим вище.
        var strict = new StrictDecimalArithmetic();
        var big = ExpressionValue.Number(E15);

        Assert.Equal(ExpressionErrors.BadValue, strict.Binary(big, big, BinaryOperator.Multiply).ErrorCode);
        Assert.Equal(ExpressionErrors.BadValue, strict.Unary(ExpressionValue.Number(70m), "Exp").ErrorCode);
        Assert.Equal(
            ExpressionErrors.DivideByZero,
            strict.Binary(ExpressionValue.Number(0m), ExpressionValue.Number(-1m), "Pow").ErrorCode);
        Assert.Equal(
            ExpressionErrors.BadValue,
            strict.Binary(ExpressionValue.Number(-8m), ExpressionValue.Number(0.5m), "Pow").ErrorCode);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [InlineData("1000000000000000 * 1000000000000000")]
    [InlineData("SUM(50000000000000000000000000000, 50000000000000000000000000000)")]
    [InlineData("AVERAGE(50000000000000000000000000000, 50000000000000000000000000000)")]
    [InlineData("PRODUCT(1000000000000000, 1000000000000000)")]
    public void Шаблон_переповнення_в_операторі_й_агрегаті_дає_VALUE(string expression)
    {
        var value = Template(expression);

        Assert.True(value.IsError, $"{expression} дав {value.Value}, а не помилку");
        Assert.Equal(ExpressionErrors.BadValue, value.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Шаблон_IFERROR_перехоплює_переповнення_як_будь_яку_помилку_значення()
    {
        // ⛔ Доти переповнення не можна було перехопити НІЧИМ: виняток минав
        // IFERROR, бо той бачить лише значення.
        var value = Template("IFERROR(1000000000000000 * 1000000000000000, 7)");

        Assert.Equal(7m, value.AsNumber());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void SUMIF_переповнення_дає_VALUE()
    {
        var big = ExpressionValue.Number(HalfMax);
        var yes = ExpressionValue.Boolean(true);

        var value = TemplateFunctions.SumIf([big, big], [yes, yes], sumRange: null);

        Assert.Equal(ExpressionErrors.BadValue, value.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-16.3")]
    public void CONVERT_що_переповнює_множення_на_коефіцієнт_дає_VALUE()
    {
        // ⚠ Множить КОНТЕКСТ, а не функція: перехоплення в `ConvertFunction`
        // накриває кожну реалізацію контексту одним місцем.
        var value = Methodology("CONVERT(@A, 'kg', 'g')", new StrictDecimalArithmetic(), c =>
        {
            c.Arguments["A"] = ExpressionValue.Number(HalfMax);
            c.Conversions["kg|g"] = 1000m;
        });

        Assert.Equal(ExpressionErrors.BadValue, value.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.9")]
    public void Legacy_скінченне_число_поза_decimal_лишається_double_як_у_NCalc()
    {
        // ⛔ Сценарій аудиту для `Legacy`. NCalc 1.3.8 дає `Pow(10, 30)` =
        // `1e30` — скінченний `double`. Помилкою це не є і в нас: значення
        // живе далі, а `AsNumber()` чесно каже «у decimal не подається»
        // замість кинути.
        var legacy = new LegacyDoubleArithmetic();

        var huge = Methodology("Pow(10, 30)", legacy);
        Assert.False(huge.IsError);
        Assert.Equal(1e30, huge.AsDouble());
        Assert.Null(huge.AsNumber());

        // Проміжне значення за межею decimal не ламає подальшого обчислення:
        // `1e30 / 1e20 = 1e10`, як у чинній системі.
        Assert.Equal(1e10, Methodology("Pow(10, 30) / Pow(10, 20)", legacy).AsDouble()!.Value, precision: 3);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Legacy_унарний_мінус_над_числом_поза_decimal_не_кидає()
    {
        // ⚠ Аудит A4 закрито (`LegacyUnaryMinusKeepsDoubleTests`): мінус більше
        // не звужує, тож тут −1e30 — число, як у NCalc, а не `#VALUE`.
        // Твердження цього файла лишається тим самим — виняток не летить.
        var value = Methodology("-Pow(10, 30)", new LegacyDoubleArithmetic());

        Assert.Equal(-1e30, value.AsDouble());
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [InlineData(1e30)]
    [InlineData(-1e30)]
    [InlineData(79228162514264337593543950336d)] // = (double)decimal.MaxValue, тобто 2^96 — уже поза межею
    public void AsNumber_для_double_поза_межею_decimal_дає_null_а_не_виняток(double value)
    {
        Assert.Null(ExpressionValue.LegacyNumber(value).AsNumber());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void AsNumber_для_double_у_межах_decimal_звужує_як_раніше()
    {
        Assert.Equal(1e28m, ExpressionValue.LegacyNumber(1e28).AsNumber());
        Assert.Equal(-2.5m, ExpressionValue.LegacyNumber(-2.5).AsNumber());
    }
}
