using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Functions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests;

/// <summary>
/// Тести, яких бракувало: кожен тут вбиває мутанта, що виживав на повному
/// наборі <c>Ecr.Expressions.Tests</c> (аудит сили тестів 2026-10-02,
/// <c>audit/expr-test-strength-2026-10-02.md</c>).
/// </summary>
/// <remarks>
/// ⚠ Це не нова поведінка, а закріплення наявної: кожне очікування нижче
/// збігається з тим, що код робить зараз, і з тим, що про нього сказано в
/// коментарях джерела. Ідентифікатор мутанта (<c>P3</c>, <c>T10</c>…) — у
/// коментарі над кожним тестом, щоб звіт і тест можна було звести.
/// </remarks>
public sealed class CalculationMutationGapTests
{
    private static readonly StrictDecimalArithmetic Strict = new();
    private static readonly LegacyDoubleArithmetic Legacy = new();

    private static ExpressionValue Num(decimal value) => ExpressionValue.Number(value);

    private static ExpressionValue Err(string code) => ExpressionValue.Error(code);

    // ── Календар ────────────────────────────────────────────────────────────

    /// <summary>P3: Fixed360 через межу року рахує місяці з урахуванням року.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Fixed360_через_межу_року_рахує_місяці_обох_років()
    {
        // Листопад 2025 — лютий 2026: чотири місяці, 120 днів. Множник року
        // 11 замість 12 дав би 108 — період «опалювального сезону» тихо
        // втратив би місяць.
        var season = new PeriodContext(
            new DateOnly(2025, 11, 1), new DateOnly(2026, 2, 28), CalendarMode.Fixed360, 2026, 1);

        Assert.Equal(120, season.Days);
        Assert.Equal(120 * 24, season.Hours);
    }

    /// <summary>E5, E6, E2: властивості періоду й різниця дат у виразі.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Кінець_і_номер_періоду_доходять_до_виразу()
    {
        var context = new TestEvaluationContext
        {
            Period = new PeriodContext(
                new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30), CalendarMode.Actual, 2026, 4),
        };

        Assert.Equal(new DateTime(2026, 4, 30), Expr.Eval("[Period].End", context, ExpressionDialect.Methodology).Value);
        Assert.Equal(4m, Expr.Eval("[Period].Sequence", context, ExpressionDialect.Methodology).AsNumber());
        Assert.Equal(2026m, Expr.Eval("[Period].Year", context, ExpressionDialect.Methodology).AsNumber());

        // Дата − дата = кількість днів, і знак має значення: «кінець − початок»
        // додатний. Перестановка операндів дала б −29.
        Assert.Equal(29m, Expr.Eval("[Period].End - [Period].Start", context, ExpressionDialect.Methodology).AsNumber());
    }

    /// <summary>E1: дата мінус дні — назад у часі, а не вперед.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Дата_мінус_число_зсуває_назад()
    {
        var context = new TestEvaluationContext
        {
            Period = new PeriodContext(
                new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30), CalendarMode.Actual, 2026, 4),
        };

        Assert.Equal(new DateTime(2026, 4, 25), Expr.Eval("[Period].End - 5", context, ExpressionDialect.Methodology).Value);
        Assert.Equal(new DateTime(2026, 4, 3), Expr.Eval("[Period].Start + 2", context, ExpressionDialect.Methodology).Value);
    }

    // ── Агрегати шаблонів ───────────────────────────────────────────────────

    /// <summary>T6: COUNT не рахує зіпсовану комірку.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Count_не_рахує_помилку()
    {
        var value = TemplateFunctions.Count([Num(1), Err(ExpressionErrors.DivideByZero), ExpressionValue.Null, Num(2)]);

        Assert.Equal(2m, value.AsNumber());
    }

    /// <summary>T10: SUMIF із третім аргументом підсумовує його, а не діапазон умов.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void SumIf_підсумовує_окремий_діапазон_сум()
    {
        var range = new[] { Num(1), Num(2), Num(3) };
        var conditions = new[] { ExpressionValue.Boolean(true), ExpressionValue.Boolean(false), ExpressionValue.Boolean(true) };
        var sums = new[] { Num(100), Num(200), Num(300) };

        Assert.Equal(400m, TemplateFunctions.SumIf(range, conditions, sums).AsNumber());
        Assert.Equal(4m, TemplateFunctions.SumIf(range, conditions, null).AsNumber());
    }

    /// <summary>T11, T12: помилка в умові або у вибраному значенні поширюється.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void SumIf_поширює_помилку_умови_і_значення()
    {
        var badCondition = TemplateFunctions.SumIf(
            [Num(1), Num(2)],
            [ExpressionValue.Boolean(true), Err(ExpressionErrors.DivideByZero)],
            null);
        Assert.Equal(ExpressionErrors.DivideByZero, badCondition.ErrorCode);

        var badValue = TemplateFunctions.SumIf(
            [Num(1), Err(ExpressionErrors.BadReference)],
            [ExpressionValue.Boolean(true), ExpressionValue.Boolean(true)],
            null);
        Assert.Equal(ExpressionErrors.BadReference, badValue.ErrorCode);

        // Невибраний рядок із помилкою на суму не впливає.
        var skipped = TemplateFunctions.SumIf(
            [Num(1), Err(ExpressionErrors.BadReference)],
            [ExpressionValue.Boolean(true), ExpressionValue.Boolean(false)],
            null);
        Assert.Equal(1m, skipped.AsNumber());
    }

    /// <summary>T14: IFERROR не підміняє порожнечу запасним значенням (02c E08).</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void IfError_не_перехоплює_null()
    {
        Assert.True(TemplateFunctions.IfError(ExpressionValue.Null, Num(0)).IsNull);
        Assert.Equal(0m, TemplateFunctions.IfError(Err(ExpressionErrors.DivideByZero), Num(0)).AsNumber());
    }

    /// <summary>T15: IF з невідомою умовою — невідомо, а не гілка «інакше».</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void If_з_null_умовою_дає_null()
    {
        Assert.True(TemplateFunctions.If(ExpressionValue.Null, Num(1), Num(2)).IsNull);
    }

    /// <summary>T2: ROUND до 28 знаків — межа decimal, ще допустима.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Round_приймає_28_знаків_і_відхиляє_29()
    {
        Assert.Equal(1.5m, TemplateFunctions.Round(Num(1.5m), Num(28)).AsNumber());
        Assert.Equal(ExpressionErrors.BadValue, TemplateFunctions.Round(Num(1.5m), Num(29)).ErrorCode);
    }

    // ── Strict (decimal) ────────────────────────────────────────────────────

    /// <summary>S3: остача від ділення на нуль — #DIV/0, а не нуль.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Strict_остача_від_нуля_це_DIV0()
    {
        var value = Strict.Binary(Num(5), Num(0), Ast.BinaryOperator.Modulo);

        Assert.Equal(ExpressionErrors.DivideByZero, value.ErrorCode);
    }

    /// <summary>S4: IEEERemainder округлює частку банківськи (IEEE 754).</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Strict_IEEERemainder_на_середині_округлює_до_парного()
    {
        // 5 / 2 = 2.5 → парне 2 → 5 − 4 = 1. Від нуля дало б 3 → −1.
        Assert.Equal(1m, Strict.Binary(Num(5), Num(2), "IEEERemainder").AsNumber());
        Assert.Equal(Math.IEEERemainder(5, 2), (double)Strict.Binary(Num(5), Num(2), "IEEERemainder").AsNumber()!.Value);
    }

    /// <summary>S5: логарифм за основою 1 — #VALUE, не #DIV/0.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Strict_Log_за_основою_один_це_VALUE()
    {
        Assert.Equal(ExpressionErrors.BadValue, Strict.Binary(Num(8), Num(1), "Log").ErrorCode);
    }

    /// <summary>S7: корінь із нуля — нуль, а не помилка.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Strict_Sqrt_нуля_це_нуль()
    {
        Assert.Equal(0m, Strict.Unary(Num(0), "Sqrt").AsNumber());
    }

    /// <summary>D1: 0^0 = 1 (як Excel і чинна система).</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Strict_Pow_нуль_у_нульовому_це_один()
    {
        Assert.Equal(1m, Strict.Binary(Num(0), Num(0), "Pow").AsNumber());
    }

    /// <summary>D3, D4: межі Exp — до ±66 число, а не переповнення чи нуль.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Strict_Exp_рахує_до_межі_decimal()
    {
        // e^65 ≈ 1.69e28 — ще вміщується в decimal (макс. ≈ 7.9e28).
        var high = Strict.Unary(Num(65), "Exp").AsNumber();
        Assert.NotNull(high);
        Assert.InRange(high!.Value, 1.69e28m, 1.70e28m);

        // e^−62 ≈ 1.2e−27 — більше за найменший додатний decimal (1e−28).
        var low = Strict.Unary(Num(-62), "Exp").AsNumber();
        Assert.NotNull(low);
        Assert.True(low!.Value > 0m, $"e^-62 має бути додатним, а не {low}");
    }

    /// <summary>D5, D8: точність рядів Ln і Exp — до 20-го знака.</summary>
    /// <remarks>
    /// ⚠ Звірка йде до шостого знака на кінцевих числах, але ряд стоїть на
    /// початку ланцюга (Pow з дробовим показником = Exp(b·Ln(a))): похибка
    /// 1e−16 на Ln(1e20), помножена далі, вилазить у звірку. Еталони — з
    /// відомих констант, а не з того самого коду.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Strict_Ln_і_Exp_точні_до_двадцятого_знака()
    {
        // ln(1e20) = 20·ln(10).
        const decimal ln1e20 = 46.05170185988091368035982909368m;
        var ln = Strict.Unary(Num(1e20m), "Ln").AsNumber()!.Value;
        Assert.True(Math.Abs(ln - ln1e20) < 1e-20m, $"ln(1e20) = {ln}");

        // e^0.5 = √e.
        const decimal sqrtE = 1.6487212707001281468486507878m;
        var exp = Strict.Unary(Num(0.5m), "Exp").AsNumber()!.Value;
        Assert.True(Math.Abs(exp - sqrtE) < 1e-20m, $"e^0.5 = {exp}");
    }

    // ── Legacy (double, NCalc 1.3.8) ────────────────────────────────────────

    /// <summary>L3: Floor на від'ємному — вниз, а не до нуля.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Legacy_Floor_від_ємного_йде_вниз()
    {
        Assert.Equal(-2d, Legacy.Unary(ExpressionValue.LegacyNumber(-1.5), "Floor").AsDouble());
    }

    // ── Методології: Round, in, SUBSTANCE ───────────────────────────────────

    /// <summary>M1, M2: Round(a, digits) — 28 знаків допустимо, дробові знаки — #VALUE.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Methodology_Round_межі_кількості_знаків()
    {
        var context = new TestEvaluationContext();

        Assert.Equal(1.5m, MethodologyFunctions.Invoke("Round", [Num(1.5m), Num(28)], Strict, context).AsNumber());
        Assert.Equal(ExpressionErrors.BadValue, MethodologyFunctions.Invoke("Round", [Num(1.5m), Num(29)], Strict, context).ErrorCode);
        Assert.Equal(ExpressionErrors.BadValue, MethodologyFunctions.Invoke("Round", [Num(1.25m), Num(1.5m)], Strict, context).ErrorCode);
    }

    /// <summary>M3: в <c>in</c> два null рівні між собою (E11).</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void In_null_належить_множині_з_null()
    {
        var value = MethodologyFunctions.Invoke(
            "in", [ExpressionValue.Null, Num(1), ExpressionValue.Null], Strict, new TestEvaluationContext());

        Assert.Equal(true, value.Value);
    }

    /// <summary>M4: помилка в <c>in</c> поширюється, а не стає «не належить».</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void In_поширює_помилку()
    {
        var value = MethodologyFunctions.Invoke(
            "in", [Err(ExpressionErrors.DivideByZero), Num(1), Num(2)], Strict, new TestEvaluationContext());

        Assert.Equal(ExpressionErrors.DivideByZero, value.ErrorCode);
    }

    /// <summary>M7: відсутня речовина — #REF, а не мовчазний нуль-викид.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Substance_відсутньої_речовини_це_REF()
    {
        var context = new TestEvaluationContext();
        context.FormulaResults["CO2"] = Num(42);

        Assert.Equal(42m, MethodologyFunctions.Invoke("SUBSTANCE", [ExpressionValue.Text("CO2")], Strict, context).AsNumber());
        Assert.Equal(
            ExpressionErrors.BadReference,
            MethodologyFunctions.Invoke("SUBSTANCE", [ExpressionValue.Text("NOX")], Strict, context).ErrorCode);
    }

    // ── CONVERT ─────────────────────────────────────────────────────────────

    /// <summary>C3: помилка значення в CONVERT зберігає свій код.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Convert_зберігає_код_помилки_значення()
    {
        var value = ConvertFunction.Invoke(
            [Err(ExpressionErrors.DivideByZero), ExpressionValue.Text("kg"), ExpressionValue.Text("t")],
            new TestEvaluationContext());

        Assert.Equal(ExpressionErrors.DivideByZero, value.ErrorCode);
    }

    /// <summary>C4: одиниця, обчислена виразом, — #UNIT, а не #VALUE.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Convert_з_нерядковою_одиницею_це_UNIT()
    {
        var value = ConvertFunction.Invoke(
            [Num(1), Num(1), ExpressionValue.Text("t")], new TestEvaluationContext());

        Assert.Equal(ExpressionErrors.BadUnit, value.ErrorCode);
    }

    // ── Комірка → значення виразу ───────────────────────────────────────────

    /// <summary>V1, V2, V3: збережена комірка читається тим самим типом.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Збережена_комірка_повертається_тим_самим_значенням()
    {
        var fallback = Num(-1);

        // Помилка, збережена текстом, лишається помилкою.
        var error = CellValueMapping.ToExpressionValue(new CellValueData { ValueString = "#DIV/0" }, fallback);
        Assert.Equal(ExpressionErrors.DivideByZero, error.ErrorCode);

        var text = CellValueMapping.ToExpressionValue(new CellValueData { ValueString = "W-01" }, fallback);
        Assert.Equal("W-01", text.Value);

        // Явна порожнеча — null, а не значення за замовчуванням: формула вже
        // відпрацювала й дала «нічого».
        Assert.True(CellValueMapping.ToExpressionValue(CellValueData.Empty, fallback).IsNull);
        Assert.Equal(-1m, CellValueMapping.ToExpressionValue(null, fallback).AsNumber());

        // Lookup-комірка — Id запису як число (перший аргумент REGFIELD).
        var lookup = CellValueMapping.ToExpressionValue(new CellValueData { ValueRegistryEntryId = 101 }, fallback);
        Assert.Equal(101m, lookup.AsNumber());
    }
}
