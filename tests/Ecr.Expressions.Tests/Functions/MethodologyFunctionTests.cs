// tests/Ecr.Expressions.Tests/Functions/MethodologyFunctionTests.cs
using Ecr.Domain.Enums;
using Ecr.Expressions;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Functions;

/// <summary>Додаткові функції діалекту методологій.</summary>
public sealed class MethodologyFunctionTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void SWITCH_без_збігу_і_без_default_дає_null()
    {
        // Парна кількість аргументів після значення — типового немає.
        var value = Eval("SWITCH('W-99', 'W-01', 1, 'W-02', 2)");

        // ⛔ null, а НЕ нуль. Нуль тут виглядав би як виміряне значення і
        // потрапив би в підсумок звіту як реальний: невідома речовина стала б
        // речовиною з нульовим викидом.
        Assert.True(value.IsNull);
        Assert.False(value.IsError);

        // З типовим значенням — воно й повертається.
        Assert.Equal(-1m, Number("SWITCH('W-99', 'W-01', 1, 'W-02', 2, -1)"));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void SWITCH_повертає_перший_збіг_а_не_останній()
    {
        // Дубльований збіг — не помилка конфігурації, яку можна відкинути:
        // перелік читається згори вниз, і порядок у ньому автор написав
        // навмисно. Останній збіг мовчки перекривав би виняток, поставлений
        // першим саме тому, що він виняток.
        Assert.Equal(1m, Number("SWITCH('W-01', 'W-01', 1, 'W-01', 2)"));

        Assert.Equal(2m, Number("SWITCH(2, 1, 1, 2, 2, 3, 3)"));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void CONVERT_між_різними_розмірностями_дає_помилку()
    {
        var context = Context();

        // ⛔ м³ → кг: коефіцієнт залежить від речовини й умов, тобто це не
        // конверсія одиниць, а константа методології (ФВ-16.5). Коефіцієнта
        // немає в довіднику — і не може бути: на рівні БД це блокує
        // CK_Conv_SameDimension.
        var value = Eval("CONVERT(1000, 'm3', 'kg')", context);

        Assert.True(value.IsError);
        Assert.Equal(ExpressionErrors.BadUnit, value.ErrorCode);

        // ⚠ Це помилка-ЗНАЧЕННЯ, а не виняток: одна зіпсована комірка не валить
        // перерахунок усієї таблиці (02b §6.4).
        Assert.Equal(ExpressionErrors.BadUnit, Eval("CONVERT(1, 'm3', 'kg') + 5", context).ErrorCode);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void CONVERT_однакових_одиниць_не_змінює_значення()
    {
        var context = Context();

        // Тотожність перевіряється без арифметики: множення на одиницю з
        // подальшим діленням дало б те саме число, але на decimal із хвостом
        // у 20 знаків це вже не гарантовано.
        Assert.Equal(123.456789m, Number("CONVERT(123.456789, 'kg', 'kg')", context));

        // І реальна конверсія — рівно множник довідника, без округлення.
        Assert.Equal(2500m, Number("CONVERT(2.5, 't', 'kg')", context));
        Assert.Equal(2.5m, Number("CONVERT(2500, 'kg', 't')", context));

        // null лишається null: «не заповнено» в інших одиницях — так само
        // «не заповнено», а не нуль.
        Assert.True(Eval("CONVERT(NULL, 't', 'kg')", context).IsNull);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void PeriodContext_дає_тривалість_за_CalendarMode_методології()
    {
        var actual = Context();
        actual.Period = Month(2026, 1, CalendarMode.Actual);

        Assert.Equal(31m, Number("[Period].Days", actual));
        Assert.Equal(31m * 24m, Number("[Period].Hours", actual));
        Assert.Equal(2_678_400m, Number("[Period].Seconds", actual));

        // ⚠ Той самий вираз на тому самому періоді дає інше число, щойно
        // змінили режим версії методології. Тривалість ОБЧИСЛЮЄТЬСЯ з меж
        // періоду і CalendarMode тієї методології, що рахує (ФВ-16.11a,
        // D-112) — вона не є полем doc.Period, інакше дві методології
        // конфліктували б за одне поле, і вигравала б та, що порахувала
        // останньою.
        var fixed360 = Context();
        fixed360.Period = Month(2026, 1, CalendarMode.Fixed360);

        Assert.Equal(30m, Number("[Period].Days", fixed360));
        Assert.Equal(2_592_000m, Number("[Period].Seconds", fixed360));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Fixed365_і_Actual_дають_різні_числа_у_високосний_рік()
    {
        // 2028 — високосний. Річний період: 366 фактичних днів проти 365.
        var actual = Context();
        actual.Period = Year(2028, CalendarMode.Actual);

        var fixed365 = Context();
        fixed365.Period = Year(2028, CalendarMode.Fixed365);

        Assert.Equal(366m, Number("[Period].Days", actual));
        Assert.Equal(365m, Number("[Period].Days", fixed365));

        // Перерахунок у г/с ділить на Seconds — тобто різниця конвенції
        // потрапляє в КОЖНЕ число звіту.
        const string gsec = "1000000 / [Period].Seconds";

        var actualRate = Number(gsec, actual);
        var fixedRate = Number(gsec, fixed365);

        Assert.NotEqual(actualRate, fixedRate);

        // ⚠ 0.27 % — мало, щоб помітити, і достатньо, щоб не зійтися з
        // еталоном. Саме такі розбіжності шукають у формулі місяцями (D-78).
        var difference = (fixedRate - actualRate) / actualRate;
        Assert.Equal(0.0027m, decimal.Round(difference, 4, MidpointRounding.AwayFromZero));

        // А в невисокосному році режими збігаються — і саме тому помилку
        // конвенції неможливо знайти, поки не настане високосний рік.
        var plain = Context();
        plain.Period = Year(2026, CalendarMode.Actual);
        var plainFixed = Context();
        plainFixed.Period = Year(2026, CalendarMode.Fixed365);

        Assert.Equal(Number(gsec, plain), Number(gsec, plainFixed));
    }

    private static ExpressionValue Eval(string expression, TestEvaluationContext? context = null)
        => Expr.Eval(expression, context ?? Context(), ExpressionDialect.Methodology);

    private static decimal Number(string expression, TestEvaluationContext? context = null)
        => Eval(expression, context).AsNumber()
           ?? throw new InvalidOperationException($"Вираз '{expression}' дав не число.");

    /// <summary>Контекст із коефіцієнтами довідника одиниць за seed-ом.</summary>
    private static TestEvaluationContext Context()
    {
        var context = new TestEvaluationContext();

        context.Conversions["t|kg"] = 1000m;
        context.Conversions["kg|t"] = 0.001m;
        context.Conversions["kg|g"] = 1000m;
        context.Conversions["g|kg"] = 0.001m;

        // ⛔ Пари «m3|kg» немає і бути не може: різні розмірності.
        return context;
    }

    private static PeriodContext Month(int year, int month, CalendarMode mode)
        => new(
            new DateOnly(year, month, 1),
            new DateOnly(year, month, DateTime.DaysInMonth(year, month)),
            mode,
            year,
            (byte)month);

    private static PeriodContext Year(int year, CalendarMode mode)
        => new(new DateOnly(year, 1, 1), new DateOnly(year, 12, 31), mode, year, 1);
}
