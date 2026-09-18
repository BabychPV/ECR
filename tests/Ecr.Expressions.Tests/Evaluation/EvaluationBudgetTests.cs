using Ecr.Domain.Enums;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Functions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Evaluation;

/// <summary>
/// Бюджет одного обчислення: <see cref="EvaluationBudget"/> і
/// <see cref="Evaluator.MaxEvaluationSteps"/>.
/// </summary>
/// <remarks>
/// ⛔ **Що саме тут доводиться.** Не «межа існує» — межу видно з константи. Тут
/// доводиться, що необмежене обчислення було ДОСЯЖНЕ: виразом, який проходить
/// парсер, тайп-чекер, whitelist функцій, межу глибини
/// (<c>Parser.MaxRecursionDepth = 192</c>) і межу довжини (2000 символів,
/// <c>cfg.FormulaDef.Expression</c>), над таблицею **чинного** розміру
/// (<c>DistributionProfile.MaxRowsPerTable = 471</c>).
///
/// ⚠ Обсяг ФВ-13.6. Вимога «захист рівня 2» описує виконання КОРИСТУВАЦЬКОГО
/// C#-КОДУ (<c>reference/backend/B18</c> §14.7: whitelist <c>using</c>,
/// Roslyn-аналізатор, окремий worker-процес, «ліміти часу і пам'яті на
/// виклик»), а рівень 2 у перший реліз не входить (<c>ФВ-9.3</c>, <c>D-105</c>)
/// і формально звільнений від трасування
/// (<c>contracts/trace-exempt.md</c>). Тому ТРЕЙТА <c>ФВ-13.6</c> тут немає і
/// бути не може: <c>RequirementCensusTests.Жодна_вимога_не_звільнена_і_покрита_водночас</c>
/// почервоніє. Ці тести — про рівень 1, який у релізі Є, і про ту частину
/// «лімітів на виклик», яку можна виконати сьогодні.
/// </remarks>
public sealed class EvaluationBudgetTests
{
    /// <summary>Найважча таблиця чинного <c>.xlsm</c> (<c>DistributionProfile</c>).</summary>
    private const int HeaviestRealTable = 471;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Тридцять_предикатних_агрегатів_над_чинною_таблицею_вичерпують_бюджет()
    {
        // ⛔ ГОЛОВНЕ ТВЕРДЖЕННЯ. Вираз нижче — 1587 символів (межа 2000),
        // глибина вкладення одиниці (межа 192), усі імена з whitelist,
        // предикат проходить `PredicateValidator`. Тобто ЖОДНЕ з наявних
        // обмежень його не зупиняє. Замір до бюджету: одна така колонкова
        // формула над таблицею на 471 рядок — 16 820 мс і 5.4 ГБ алокацій
        // (`ExpressionBudgetReachabilityTests` у `Ecr.Application.Tests`).
        var context = Table(HeaviestRealTable);
        var expression = Repeat("SUM([Items].[WHERE [WasteType] = 'W-01'].[Amount])", 30);

        var value = Eval(expression, context, out var spent);

        Assert.Equal(ExpressionErrors.BudgetExceeded, value.ErrorCode);
        Assert.Equal(Evaluator.MaxEvaluationSteps, spent);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Один_предикатний_агрегат_над_чинною_таблицею_вкладається_із_названим_запасом()
    {
        // ⛔ Друга половина доказу, без якої перша нічого не варта: межа, що
        // ріже чинні форми, робить систему непрацездатною. Саме цей вираз —
        // `SUM([Items].[WHERE …].[Amount])` — директива №11 (T12) і
        // `CascadeRecalculationTests` беруть як зразок предиката, і саме він
        // мусить рахуватися над НАЙВАЖЧОЮ реальною таблицею.
        var context = Table(HeaviestRealTable);

        var value = Eval("SUM([Items].[WHERE [WasteType] = 'W-01'].[Amount])", context, out var spent);

        Assert.Equal(HeaviestRealTable, value.AsNumber());

        // ⚠ Запас названий ЧИСЛОМ, а не словом «вистачає». Якщо бюджет колись
        // опустять до цієї ціни, тест скаже це прямо, а не через плаваюче
        // падіння приймальних тестів методологій.
        Assert.True(
            spent * 8 <= Evaluator.MaxEvaluationSteps,
            $"Один предикатний агрегат над таблицею на {HeaviestRealTable} рядків коштує {spent} кроків "
            + $"із бюджету {Evaluator.MaxEvaluationSteps} — запас менший за восьмикратний. "
            + "Це форма, яку система рахує щодня; бюджет, тісний для неї, зупинить роботу, а не зловживання.");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Агрегат_над_діапазоном_усієї_чинної_таблиці_вкладається_із_названим_запасом()
    {
        // ⚠ Друга чинна форма: `SUM` над діапазоном рядків. У корпусі чинного
        // шаблону 245 викликів `SUM` на ~10 000 формул
        // (`docs/reference/as-is/01-as-is-overview.md` §3.1) — агрегати
        // поодинокі, і саме цю ціну бюджет мусить пускати з запасом.
        var context = Table(HeaviestRealTable);

        var value = Eval($"SUM([Items].[r0000:r{HeaviestRealTable - 1:D4}].[Amount])", context, out var spent);

        Assert.Equal(HeaviestRealTable, value.AsNumber());
        Assert.True(
            spent * 20 <= Evaluator.MaxEvaluationSteps,
            $"Агрегат над діапазоном у {HeaviestRealTable} рядків коштує {spent} кроків "
            + $"із бюджету {Evaluator.MaxEvaluationSteps} — запас менший за двадцятикратний.");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Вичерпаний_бюджет_не_перехоплюється_через_IFERROR()
    {
        // ⛔ Без цього бюджет був би гіршим за його відсутність. `IFERROR`
        // існує, щоб замінити помилку-значення запасним числом; якби він ловив
        // і `#BUDGET`, формула, зупинена межею, тихо давала б нуль — і в звіт
        // ішло б підроблене число замість видимої відмови.
        var context = Table(HeaviestRealTable);
        var heavy = Repeat("SUM([Items].[WHERE [WasteType] = 'W-01'].[Amount])", 30);

        var value = Eval($"IFERROR({heavy}, 0)", context, out _);

        Assert.Equal(ExpressionErrors.BudgetExceeded, value.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Предикат_що_не_відібрав_жодного_рядка_усе_одно_витрачає_бюджет()
    {
        // ⛔ Саме тут ламався б бюджет, побудований «за кількістю прочитаних
        // значень». Предикат, який не відібрав НІЧОГО, повертає порожню групу —
        // нуль значень, — а таблицю вже просканував цілком. Бюджет мусить
        // бачити сканування, а не лише його результат: інакше найдешевший
        // спосіб обійти межу — написати умову, яка ніколи не справджується.
        var context = Table(HeaviestRealTable);

        var value = Eval("SUM([Items].[WHERE [WasteType] = 'НЕМАЄ'].[Amount])", context, out var spent);

        Assert.Equal(0m, value.AsNumber());
        Assert.True(
            spent >= HeaviestRealTable,
            $"Сканування {HeaviestRealTable} рядків коштувало бюджету лише {spent} кроків: "
            + "бюджет рахує відібрані значення, а не зроблену роботу.");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Бюджет_кожного_обчислення_свій()
    {
        // ⚠ Прогін над таблицею — це сотні обчислень поспіль тим самим
        // обчислювачем (`RecalculationService.Evaluate` у циклі по рядках).
        // Спільний лічильник зупинив би таблицю після перших рядків, і межа
        // проти зловживання перетворилася б на межу проти роботи.
        var context = Table(HeaviestRealTable);
        var expression = "SUM([Items].[WHERE [WasteType] = 'W-01'].[Amount])";

        for (var i = 0; i < 50; i++)
        {
            Assert.Equal(HeaviestRealTable, Eval(expression, context, out _).AsNumber());
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Лічильник_не_переповнюється_на_величезній_групі()
    {
        // ⚠ `TryConsume(int.MaxValue)` на майже витраченому бюджеті не має дати
        // від'ємний лічильник — тобто НЕСКІНЧЕННИЙ бюджет. Арифметика межі не
        // повинна сама ставати дірою в межі.
        var budget = new EvaluationBudget(10);

        Assert.True(budget.TryConsume(5));
        Assert.False(budget.TryConsume(int.MaxValue));
        Assert.Equal(10, budget.Spent);
        Assert.True(budget.IsExhausted);
    }

    /// <summary>Повторює вираз <paramref name="times"/> разів через <c>+</c>.</summary>
    private static string Repeat(string part, int times)
        => string.Join(" + ", Enumerable.Repeat(part, times));

    /// <summary>Обчислює вираз, віддаючи витрачені кроки.</summary>
    private static ExpressionValue Eval(string expression, TestEvaluationContext context, out int spent)
    {
        var parsed = Expr.Parse(expression);
        Assert.True(parsed.IsSuccess, string.Join("; ", parsed.Diagnostics.Select(d => d.Message)));

        var budget = new EvaluationBudget(Evaluator.MaxEvaluationSteps);
        context.Budget = budget;
        try
        {
            var value = new Evaluator(new FunctionRegistry())
                .Evaluate(parsed.Expression!.Root, context, ExpressionDialect.Template, budget);
            spent = budget.Spent;
            return value;
        }
        finally
        {
            context.Budget = null;
        }
    }

    /// <summary>Динамічна таблиця <c>Items</c> на <paramref name="rows"/> рядків.</summary>
    private static TestEvaluationContext Table(int rows)
    {
        var context = new TestEvaluationContext { CurrentSheet = "Waste", CurrentTable = "Items" };
        for (var i = 0; i < rows; i++)
        {
            var rowKey = $"r{i:D4}";
            context.SetCell("Waste", "Items", rowKey, "WasteType", ExpressionValue.Text("W-01"));
            context.SetCell("Waste", "Items", rowKey, "Amount", ExpressionValue.Number(1m));
        }

        return context;
    }
}
