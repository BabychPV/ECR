// tests/Ecr.Expressions.Tests/Functions/MethodologyFunctionTests.cs
using Ecr.Domain.Enums;
using Ecr.Expressions;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Functions;

/// <summary>Додаткові функції діалекту методологій.</summary>
public sealed class MethodologyFunctionTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Вибір_без_збігу_дає_null_а_не_нуль()
    {
        // ⛔ Тест переписаний за `Q-082`. Стояло тут
        // `SWITCH('W-99', 'W-01', 1, 'W-02', 2)` з твердженням «без збігу і без
        // типового — null». Твердження було правдиве про НАШ рушій і хибне про
        // мову: `SWITCH` у NCalc 1.3.8 не існує, отже жодна чинна формула так
        // не написана, і перевіряти не було чого.
        //
        // ⚠ Що тест справді стеріг — і що лишилося: невідомий код не має
        // тихо ставати нулем. Нуль виглядав би як виміряне значення і
        // потрапив би в підсумок звіту як реальний викид.
        Assert.False(Expr.Parse("SWITCH('W-99', 'W-01', 1)", ExpressionDialect.Methodology).IsSuccess);

        // Заміна за `02b` §8 — вкладені `if`, і типове значення в них пише
        // автор ЯВНО. Написав `NULL` — отримав null, а не нуль.
        var value = Eval("if(@Code = 'W-01', 1, if(@Code = 'W-02', 2, NULL))");

        Assert.True(value.IsNull);
        Assert.False(value.IsError);

        // Той самий вираз із явним типовим значенням віддає його.
        Assert.Equal(-1m, Number("if(@Code = 'W-01', 1, if(@Code = 'W-02', 2, -1))"));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Вибір_бере_перший_збіг_а_не_останній()
    {
        // ⛔ Тест переписаний за `Q-082`: стояло
        // `SWITCH('W-01', 'W-01', 1, 'W-01', 2)`. Правило, яке він стеріг —
        // «перелік читається згори вниз, і порядок у ньому автор написав
        // навмисно» — нікуди не ділося; у вкладених `if` воно тримається
        // самою структурою виразу, а не реалізацією функції.
        var context = Context();
        context.Arguments["Code"] = ExpressionValue.Text("W-01");

        Assert.Equal(
            1m,
            Number("if(@Code = 'W-01', 1, if(@Code = 'W-01', 2, 0))", context));

        // ⛔ І гілка, яку не обрали, НЕ обчислюється — у чинному рушії теж
        // (NCalc віддає `if` параметри лінивими). Різниця видима не в
        // результаті: `@Mass / @Volume` дало б значення-помилку, яку ніхто не
        // взяв би. Вона видима в `Legacy`, де `7 / 0` дає **нескінченність**,
        // і крок маскування (`I.7`) записав би в трейс `MaskedZero` для гілки,
        // якою розрахунок не йшов. Тому перевіряється саме ФАКТ читання.
        var guard = new CountingContext();
        guard.Inner.Arguments["Volume"] = ExpressionValue.Number(0m);
        guard.Inner.Arguments["Mass"] = ExpressionValue.Number(7m);

        Assert.Equal(0m, Expr.Eval("if(@Volume = 0, 0, @Mass / @Volume)", guard,
            ExpressionDialect.Methodology).AsNumber());

        // Прочитано рівно `@Volume` — умову. `@Mass` не торкалися.
        Assert.Equal(["Volume"], guard.Reads);
    }

    /// <summary>
    /// Контекст, який ЗАПАМʼЯТОВУЄ, які аргументи в нього питали.
    /// </summary>
    /// <remarks>
    /// ⚠ Лічильник, а не значення: лінивість гілки неможливо довести
    /// результатом — помилка невибраної гілки все одно нікуди не потрапляє.
    /// Довести її можна лише тим, що звернення не сталося.
    /// </remarks>
    private sealed class CountingContext : IEvaluationContext
    {
        public TestEvaluationContext Inner { get; } = new();

        public List<string> Reads { get; } = [];

        public PeriodContext Period => Inner.Period;

        public ExpressionValue GetArgument(string name)
        {
            Reads.Add(name);
            return Inner.GetArgument(name);
        }

        public IReadOnlyList<ExpressionValue> Read(CellReferenceNode reference)
            => Inner.Read(reference);

        public ExpressionValue GetCell(int tableDefId, string rowKey, int columnDefId, int periodOffset)
            => Inner.GetCell(tableDefId, rowKey, columnDefId, periodOffset);

        public IReadOnlyList<ExpressionValue> GetCellsByPredicate(
            int tableDefId, string filterJson, int columnDefId)
            => Inner.GetCellsByPredicate(tableDefId, filterJson, columnDefId);

        public ExpressionValue GetConstant(string name) => Inner.GetConstant(name);

        public ExpressionValue GetFormulaResult(string name) => Inner.GetFormulaResult(name);

        public ExpressionValue GetHeader(string name) => Inner.GetHeader(name);

        public ExpressionValue Convert(ExpressionValue value, string fromUnitCode, string toUnitCode)
            => Inner.Convert(value, fromUnitCode, toUnitCode);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Формула_діалекту_B_повертає_текст()
    {
        // ⚠ У корпусі це `'В пределе норматива'` / `'Сверхнорматив'`: `if`
        // повертає тип ОБРАНОЇ гілки, і в діалекті методологій це буває текст.
        // Саме тому `FormulaDef` має `ResultType { Number, Text }`.
        var context = Context();
        context.Arguments["Ratio"] = ExpressionValue.Number(1.2m);

        var value = Eval("if(@Ratio > 1, 'Сверхнорматив', 'В пределе норматива')", context);

        Assert.Equal(ExpressionValueType.Text, value.Type);
        Assert.Equal("Сверхнорматив", value.Value);
    }

    [Theory] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [InlineData("Pow(2, 3)", 8)]
    [InlineData("Max(Max(1, 7), 3)", 7)]
    [InlineData("Min(1, 7)", 1)]
    [InlineData("Truncate(-1.7)", -1)]
    [InlineData("Floor(-1.2)", -2)]
    [InlineData("Ceiling(1.2)", 2)]
    [InlineData("Abs(-3)", 3)]
    [InlineData("Sign(-3)", -1)]
    [InlineData("Sqrt(9)", 3)]
    [InlineData("Log(8, 2)", 3)]
    [InlineData("Round(1.234, 2)", 1.23)]
    public void Виміряні_функції_обчислюються(string expression, double expected)
    {
        // ⛔ Це і є суть кроку `I.14`: імена з `DialectCatalog` мусять не лише
        // РОЗБИРАТИСЯ, а й рахуватися. Доти обчислювач звірявся з вигаданим
        // набором, де степінь звався `POWER`, — тобто `Pow(2,3)` не працював
        // ані на розборі, ані на обчисленні (`Q-082`).
        Assert.Equal((decimal)expected, Number(expression));

        // ⚠ Логарифм у `decimal` рахується рядом, і хвіст у 28-му знаку —
        // властивість ряду, а не каталогу: `Log10(1000)` дає
        // 3.0000000000000000000000000001. Подання (шість знаків,
        // `NumericPolicy.OutputScale`) його не бачить, тому тут — округлення,
        // а не точний збіг, і причина названа замість «≈».
        Assert.Equal(3m, decimal.Round(Number("Log10(1000)"), 6));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void IEEERemainder_це_не_оператор_відсотка()
    {
        // ⚠ `IEEERemainder(5,3) = -1`, тоді як `5 % 3 = 2`: остача береться від
        // НАЙБЛИЖЧОГО частого, а не від відкинутого. Сплутати їх означає
        // змінити знак у результаті — і саме на від'ємних це найважче помітити.
        Assert.Equal(-1m, Number("IEEERemainder(5, 3)"));
        Assert.Equal(2m, Number("5 % 3"));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void In_перевіряє_належність_множині_порядково()
    {
        var context = Context();
        context.Arguments["Mode"] = ExpressionValue.Text("Flare");

        Assert.True((bool)Eval("in(@Mode, 'Vent', 'Flare')", context).Value!);

        // ⛔ Регістр значущий: порівнюється значення довідника, а не текст
        // користувача. Зведення регістру перевело б рядки з однієї гілки `if`
        // в іншу — тихо і без жодної ознаки.
        Assert.False((bool)Eval("in(@Mode, 'Vent', 'FLARE')", context).Value!);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Помилка_поширюється_крізь_функцію_каталогу()
    {
        // `Sqrt(1/0)` має лишитися #DIV/0, а не стати #VALUE: інакше причину
        // видно не буде (02b §6.4).
        Assert.Equal(ExpressionErrors.DivideByZero, Eval("Sqrt(1 / 0)").ErrorCode);

        // null теж поширюється: корінь із «не заповнено» — це «не заповнено».
        Assert.True(Eval("Sqrt(@Missing)").IsNull);
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
